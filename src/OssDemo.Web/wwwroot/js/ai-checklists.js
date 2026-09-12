(() => {
  const root = document.querySelector('[data-ai-checklist-wizard]');
  if (!root) return;
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
  const request = async (url, options) => {
    const response = await fetch(url, options);
    const body = response.status === 204 || response.status === 202 ? null : await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body?.error || 'Не удалось выполнить операцию.');
    return body;
  };
  const post = (url, body) => request(url, { method: 'POST', headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
  const panels = [...root.querySelectorAll('[data-ai-step]')];
  const stages = ['Объект', 'Карточка', 'Поиск', 'ИИ-агент', 'Черновик'];
  const statusText = { pending: 'Ожидает запуска', queued: 'В очереди', running: 'Модель обрабатывает источники', completed: 'Цитаты проверены', failed: 'Требует повтора' };
  let step = 0;
  let slug = '';
  let currentRun = null;
  let pollTimer = null;
  let finalizing = false;

  const showStep = (next) => {
    step = next;
    panels.forEach((panel, index) => { panel.hidden = index !== step; panel.classList.toggle('active', index === step); });
    root.querySelector('[data-ai-wizard-status]').textContent = `Этап ${step + 1} из 5 · ${stages[step]}`;
    root.querySelector('[data-ai-wizard-progress]').style.width = `${(step + 1) * 20}%`;
    root.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };
  const setBusy = (button, busy, label) => { button.disabled = busy; button.textContent = busy ? label : button.dataset.idleLabel; };
  const showError = (selector, error) => { const node = root.querySelector(selector); node.textContent = error.message; node.hidden = false; node.focus(); };
  const profileLabels = { shortName: 'Краткое название', fullName: 'Полное название', type: 'Тип объекта', category: 'Категория НВОС', region: 'Регион', specialZones: 'Специальные зоны', zones: 'Зоны объекта', environmentalAspects: 'Экологические аспекты', equipment: 'Оборудование', gasTreatment: 'Газоочистка', treatmentFacilities: 'Очистные сооружения', waterSupply: 'Водоснабжение', emissionSources: 'Источники выбросов', permits: 'Разрешения', pecProgram: 'Программа ПЭК', wasteStandard: 'Нормативы отходов', sanitaryZoneProject: 'Проект СЗЗ' };
  const renderProfile = (profile) => {
    root.querySelector('[data-ai-profile]').innerHTML = Object.entries(profileLabels).filter(([key]) => profile[key] && profile[key] !== 'Не указано').map(([key, label]) => `<div class="col-md-6"><div class="wizard-card h-100"><div class="small text-muted mb-1">${escapeHtml(label)}</div><strong class="ai-profile-value">${escapeHtml(profile[key])}</strong></div></div>`).join('');
  };
  const renderSearch = (run) => {
    root.querySelector('[data-ai-queries]').innerHTML = run.batches.map((item) => `<span class="classifier-chip">${escapeHtml(item.topic)} · ${item.evidenceIds.length}</span>`).join('');
    root.querySelector('[data-ai-evidence-count]').textContent = `${run.evidence.length} фрагм.`;
    root.querySelector('[data-ai-evidence]').innerHTML = run.evidence.map((item) => `<article class="wizard-card ai-evidence-card"><div class="d-flex justify-content-between gap-2"><strong>${escapeHtml(item.id)} · ${escapeHtml(item.documentTitle)}</strong><span class="badge text-bg-light">${Math.round(item.score * 100)}%</span></div><div class="small text-muted mt-1">${escapeHtml(item.sourceLabel)} · ${escapeHtml(item.queryLabel)}</div><p class="mb-0 mt-2">${escapeHtml(item.text)}</p></article>`).join('');
  };
  const formatElapsed = (from) => {
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(from).getTime()) / 1000));
    return `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
  };
  const renderRun = (run) => {
    currentRun = run;
    const completed = run.batches.filter((item) => item.status === 'completed').length;
    const active = run.batches.filter((item) => item.status === 'running' || item.status === 'queued').length;
    const failed = run.batches.filter((item) => item.status === 'failed').length;
    const progress = run.batches.length ? Math.round((completed / run.batches.length) * 100) : 0;
    root.querySelector('[data-ai-generation-progress]').style.width = `${progress}%`;
    root.querySelector('[data-ai-generation-title]').textContent = completed === run.batches.length ? 'Все тематические пакеты проверены' : `Обработано ${completed} из ${run.batches.length} пакетов`;
    root.querySelector('[data-ai-generation-status]').textContent = failed ? `${failed} пак. завершились ошибкой — их можно повторить отдельно.` : active ? `${active} пак. находятся в очереди или обрабатываются.` : 'Пакеты подготовлены. Ограничения по времени нет.';
    root.querySelector('[data-ai-batch-list]').innerHTML = run.batches.map((batch) => `<article class="ai-batch-card ai-batch-${escapeHtml(batch.status)}"><div class="ai-batch-card-head"><span class="ai-batch-state" aria-hidden="true"></span><strong>${escapeHtml(batch.topic)}</strong><span class="badge text-bg-light">${batch.evidenceIds.length} ист.</span></div><div class="small text-muted">${escapeHtml(statusText[batch.status] || batch.status)}${batch.status === 'running' ? ` · ${formatElapsed(batch.updatedAt)}` : batch.durationMs != null ? ` · ${(batch.durationMs / 1000).toFixed(1)} сек.` : ''}</div>${batch.status === 'completed' ? `<div class="small mt-1">Принято пунктов: <b>${batch.itemCount}</b></div>` : ''}${batch.error ? `<div class="small text-danger mt-1">${escapeHtml(batch.error)}</div>` : ''}${batch.status === 'failed' || batch.status === 'completed' && batch.itemCount === 0 ? `<button class="btn btn-sm btn-outline-primary mt-2" type="button" data-ai-retry="${batch.index}">Повторить пакет</button>` : ''}<div class="ai-batch-progress"><span></span></div></article>`).join('');
    root.querySelector('[data-ai-operation-log]').innerHTML = run.batches.map((batch) => `<li><b>${escapeHtml(batch.topic)}:</b> ${escapeHtml(statusText[batch.status] || batch.status)}${batch.status === 'completed' ? `, проверено ${batch.itemCount} пунктов` : ''}.</li>`).join('');
    root.querySelector('[data-ai-elapsed]').textContent = formatElapsed(run.createdAt);
  };
  const finish = async () => {
    if (finalizing || !currentRun || currentRun.batches.some((item) => item.status !== 'completed')) return;
    finalizing = true;
    root.querySelector('[data-ai-generation-status]').textContent = 'Объединяем пункты, удаляем дубли и сохраняем черновик…';
    try {
      const checklist = await post(`/api/ai-checklists/runs/${encodeURIComponent(currentRun.id)}/finalize`);
      root.querySelector('[data-ai-result-name]').textContent = checklist.name;
      root.querySelector('[data-ai-result-facility]').textContent = checklist.facility;
      root.querySelector('[data-ai-result-count]').textContent = checklist.items.length;
      root.querySelector('[data-ai-result-link]').href = `/Checklists/Result?id=${encodeURIComponent(checklist.id)}`;
      history.replaceState(null, '', `?run=${encodeURIComponent(currentRun.id)}`);
      showStep(4);
    } catch (error) { showError('[data-ai-generation-error]', error); finalizing = false; pollTimer = setTimeout(poll, 3000); }
  };
  const poll = async () => {
    clearTimeout(pollTimer);
    if (!currentRun || step !== 3) return;
    try {
      const run = await request(`/api/ai-checklists/runs/${encodeURIComponent(currentRun.id)}`);
      renderRun(run);
      if (run.batches.every((item) => item.status === 'completed')) { await finish(); return; }
      const waitingForRetry = run.batches.some((item) => item.status === 'failed') && !run.batches.some((item) => ['queued', 'running'].includes(item.status));
      if (!waitingForRetry) pollTimer = setTimeout(poll, 1500);
    } catch (error) { showError('[data-ai-generation-error]', error); pollTimer = setTimeout(poll, 3000); }
  };
  const queueBatches = async (batches) => {
    await post(`/api/ai-checklists/runs/${encodeURIComponent(currentRun.id)}/queue`, { batchIndexes: batches.map((batch) => batch.index) });
    poll();
  };

  const select = root.querySelector('[data-ai-facility]');
  const params = new URLSearchParams(location.search);
  const selectedFromUrl = params.get('facility');
  fetch('/api/operations/facilities').then((response) => response.ok ? response.json() : Promise.reject()).then((facilities) => {
    select.innerHTML = '<option value="">Выберите объект</option>' + facilities.map((item) => `<option value="${escapeHtml(item.slug)}" ${item.slug === selectedFromUrl ? 'selected' : ''}>${escapeHtml(item.name)} · ${escapeHtml(item.nvocCategory)}</option>`).join('');
  }).catch(() => { select.innerHTML = '<option value="">Не удалось загрузить объекты</option>'; showError('[data-ai-error]', new Error('Рабочий реестр объектов недоступен.')); });

  root.querySelector('[data-ai-analyze]').addEventListener('click', async (event) => {
    slug = select.value;
    if (!slug) { showError('[data-ai-error]', new Error('Выберите объект проверки.')); return; }
    const button = event.currentTarget; button.dataset.idleLabel = button.textContent; setBusy(button, true, 'Анализ карточки…');
    try { const data = await post('/api/ai-checklists/analyze', { facilitySlug: slug }); renderProfile(data.facility.profile); showStep(1); } catch (error) { showError('[data-ai-error]', error); } finally { setBusy(button, false, ''); }
  });
  root.querySelector('[data-ai-search]').addEventListener('click', async (event) => {
    const button = event.currentTarget; button.dataset.idleLabel = button.textContent; setBusy(button, true, 'Поиск и сохранение источников…');
    root.querySelector('[data-ai-search-error]').hidden = true;
    try { currentRun = await post('/api/ai-checklists/runs', { facilitySlug: slug }); renderSearch(currentRun); history.replaceState(null, '', `?run=${encodeURIComponent(currentRun.id)}`); showStep(2); } catch (error) { showError('[data-ai-search-error]', error); } finally { setBusy(button, false, ''); }
  });
  root.querySelector('[data-ai-next]').addEventListener('click', () => { renderRun(currentRun); showStep(3); });
  root.querySelectorAll('[data-ai-prev]').forEach((button) => button.addEventListener('click', () => showStep(Math.max(0, step - 1))));
  root.querySelector('[data-ai-generate]').addEventListener('click', async (event) => {
    const button = event.currentTarget; button.dataset.idleLabel = button.textContent; setBusy(button, true, 'Пакеты поставлены в очередь');
    root.querySelector('[data-ai-generation-error]').hidden = true;
    try { await queueBatches(currentRun.batches.filter((item) => item.status === 'pending' || item.status === 'failed')); } catch (error) { showError('[data-ai-generation-error]', error); }
    finally { setBusy(button, false, ''); }
  });
  root.querySelector('[data-ai-batch-list]').addEventListener('click', async (event) => {
    const button = event.target.closest('[data-ai-retry]'); if (!button) return;
    button.disabled = true;
    try { await queueBatches([{ index: Number(button.dataset.aiRetry) }]); } catch (error) { showError('[data-ai-generation-error]', error); button.disabled = false; }
  });
  setInterval(() => { if (currentRun && step === 3) renderRun(currentRun); }, 1000);

  const restoreRunId = params.get('run');
  if (restoreRunId) request(`/api/ai-checklists/runs/${encodeURIComponent(restoreRunId)}`).then((run) => {
    currentRun = run; slug = run.facility.slug; renderProfile(run.facility.profile); renderSearch(run); renderRun(run);
    if (run.checklistId) { root.querySelector('[data-ai-result-name]').textContent = 'ИИ-чек-лист'; root.querySelector('[data-ai-result-facility]').textContent = run.facilityName; root.querySelector('[data-ai-result-count]').textContent = run.batches.reduce((sum, item) => sum + item.itemCount, 0); root.querySelector('[data-ai-result-link]').href = `/Checklists/Result?id=${encodeURIComponent(run.checklistId)}`; showStep(4); }
    else { showStep(3); poll(); }
  }).catch((error) => showError('[data-ai-error]', error));
})();
