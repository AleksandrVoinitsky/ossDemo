(() => {
  const root = document.querySelector('[data-ai-checklist-wizard]');
  if (!root) return;
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
  const api = async (path, slug) => {
    const response = await fetch(`/api/ai-checklists/${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ facilitySlug: slug }) });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || 'Не удалось выполнить операцию.');
    return body;
  };
  const panels = [...root.querySelectorAll('[data-ai-step]')];
  const stages = ['Объект', 'Карточка', 'Поиск', 'ИИ-агент', 'Черновик'];
  let step = 0;
  let slug = '';
  const showStep = (next) => {
    step = next;
    panels.forEach((panel, index) => { panel.hidden = index !== step; panel.classList.toggle('active', index === step); });
    root.querySelector('[data-ai-wizard-status]').textContent = `Этап ${step + 1} из 5 · ${stages[step]}`;
    root.querySelector('[data-ai-wizard-progress]').style.width = `${(step + 1) * 20}%`;
    root.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };
  const setBusy = (button, busy, label) => { button.disabled = busy; button.textContent = busy ? label : button.dataset.idleLabel; };
  const showError = (selector, error) => { const node = root.querySelector(selector); node.textContent = error.message; node.hidden = false; node.focus(); };
  const profileLabels = { fullName: 'Полное название', type: 'Тип объекта', category: 'Категория НВОС', region: 'Регион', environmentalAspects: 'Экологические аспекты', equipment: 'Оборудование', emissionSources: 'Источники выбросов', permits: 'Разрешения', pecProgram: 'Программа ПЭК', wasteStandard: 'Нормативы отходов', treatmentFacilities: 'Очистные сооружения', waterSupply: 'Водоснабжение' };
  const renderProfile = (data) => {
    const profile = data.facility.profile;
    root.querySelector('[data-ai-profile]').innerHTML = Object.entries(profileLabels).filter(([key]) => profile[key] && profile[key] !== 'Не указано').map(([key, label]) => `<div class="col-md-6"><div class="wizard-card h-100"><div class="small text-muted mb-1">${escapeHtml(label)}</div><strong class="ai-profile-value">${escapeHtml(profile[key])}</strong></div></div>`).join('');
  };
  const renderSearch = (data) => {
    root.querySelector('[data-ai-queries]').innerHTML = data.queries.map((item) => `<span class="classifier-chip">${escapeHtml(item.label)}</span>`).join('');
    root.querySelector('[data-ai-evidence-count]').textContent = `${data.evidence.length} фрагм.`;
    root.querySelector('[data-ai-evidence]').innerHTML = data.evidence.map((item) => `<article class="wizard-card ai-evidence-card"><div class="d-flex justify-content-between gap-2"><strong>${escapeHtml(item.id)} · ${escapeHtml(item.documentTitle)}</strong><span class="badge text-bg-light">${Math.round(item.score * 100)}%</span></div><div class="small text-muted mt-1">${escapeHtml(item.sourceLabel)} · ${escapeHtml(item.queryLabel)}</div><p class="mb-0 mt-2">${escapeHtml(item.text)}</p></article>`).join('');
  };

  const select = root.querySelector('[data-ai-facility]');
  const selectedFromUrl = new URLSearchParams(location.search).get('facility');
  fetch('/api/operations/facilities').then((response) => response.ok ? response.json() : Promise.reject()).then((facilities) => {
    select.innerHTML = '<option value="">Выберите объект</option>' + facilities.map((item) => `<option value="${escapeHtml(item.slug)}" ${item.slug === selectedFromUrl ? 'selected' : ''}>${escapeHtml(item.name)} · ${escapeHtml(item.nvocCategory)}</option>`).join('');
  }).catch(() => { select.innerHTML = '<option value="">Не удалось загрузить объекты</option>'; showError('[data-ai-error]', new Error('Рабочий реестр объектов недоступен.')); });

  root.querySelector('[data-ai-analyze]').addEventListener('click', async (event) => {
    slug = select.value;
    if (!slug) { showError('[data-ai-error]', new Error('Выберите объект проверки.')); return; }
    const button = event.currentTarget; button.dataset.idleLabel = button.textContent; setBusy(button, true, 'Анализ карточки…');
    try { const data = await api('analyze', slug); renderProfile(data); showStep(1); } catch (error) { showError('[data-ai-error]', error); } finally { setBusy(button, false, ''); }
  });
  root.querySelector('[data-ai-search]').addEventListener('click', async (event) => {
    const button = event.currentTarget; button.dataset.idleLabel = button.textContent; setBusy(button, true, 'Поиск и ранжирование…');
    root.querySelector('[data-ai-search-error]').hidden = true;
    try { const data = await api('search', slug); renderSearch(data); showStep(2); } catch (error) { showError('[data-ai-search-error]', error); } finally { setBusy(button, false, ''); }
  });
  root.querySelector('[data-ai-next]').addEventListener('click', () => showStep(3));
  root.querySelectorAll('[data-ai-prev]').forEach((button) => button.addEventListener('click', () => showStep(Math.max(0, step - 1))));
  root.querySelector('[data-ai-generate]').addEventListener('click', async (event) => {
    const button = event.currentTarget; button.dataset.idleLabel = button.textContent; setBusy(button, true, 'Агент формирует проект…');
    const progress = root.querySelector('[data-ai-generation-progress]');
    root.querySelector('[data-ai-generation-title]').textContent = 'Агент сопоставляет карточку и требования';
    root.querySelector('[data-ai-generation-status]').textContent = 'Повторный поиск, синтез и проверка ссылок могут занять до минуты.';
    progress.style.width = '72%'; root.querySelector('[data-ai-generation-error]').hidden = true;
    try {
      const checklist = await api('generate', slug); progress.style.width = '100%';
      root.querySelector('[data-ai-result-name]').textContent = checklist.name;
      root.querySelector('[data-ai-result-facility]').textContent = checklist.facility;
      root.querySelector('[data-ai-result-count]').textContent = checklist.items.length;
      root.querySelector('[data-ai-result-link]').href = `/Checklists/Result?id=${encodeURIComponent(checklist.id)}`;
      showStep(4);
    } catch (error) { progress.style.width = '0'; showError('[data-ai-generation-error]', error); }
    finally { setBusy(button, false, ''); }
  });
})();
