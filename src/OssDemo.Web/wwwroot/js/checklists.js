(() => {
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
  const formatDate = (value) => value ? new Date(value).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' }) : '—';
  const request = async (url, options) => {
    const response = await fetch(url, options);
    const body = response.status === 204 ? null : await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body?.error || 'Не удалось выполнить операцию.');
    return body;
  };

  const templateSelect = document.querySelector('[data-checklist-templates]');
  if (templateSelect) {
    request('/api/checklist-templates').then((templates) => {
      templateSelect.innerHTML = '<option value="">Не выбран — генерация будет заблокирована</option>' + templates.map((item, index) => `<option value="${escapeHtml(item.id)}" data-facility-id="${escapeHtml(item.facilityId)}" ${index === 0 ? 'selected' : ''}>${escapeHtml(item.facility)} · ${escapeHtml(item.name)} · ${item.itemCount} пунктов</option>`).join('');
    }).catch(() => {
      templateSelect.innerHTML = '<option value="">Не удалось загрузить шаблоны</option>';
      const warning = document.querySelector('[data-template-warning]');
      if (warning) { warning.textContent = 'Не удалось загрузить шаблоны из базы данных.'; warning.hidden = false; }
    });
  }

  const draftList = document.querySelector('[data-draft-list]');
  if (draftList) {
    request('/api/checklists/drafts').then((drafts) => {
      document.querySelector('[data-draft-count]').textContent = `${drafts.length} шт.`;
      draftList.innerHTML = drafts.length ? drafts.map((item) => `<article class="draft-checklist-card"><div><strong>${escapeHtml(item.name)}</strong><div class="muted-note small">${escapeHtml(item.facility)} · ${item.itemCount} пунктов · изменён ${formatDate(item.updatedAt)}</div></div><a class="btn btn-outline-primary" href="/Checklists/Result?id=${encodeURIComponent(item.id)}">Продолжить</a></article>`).join('') : '<p class="muted-note mb-0">Незавершённых чек-листов нет.</p>';
    }).catch(() => { draftList.innerHTML = '<p class="text-danger mb-0">Не удалось загрузить черновики.</p>'; document.querySelector('[data-draft-count]').textContent = 'Ошибка'; });
  }

  const checklistId = new URLSearchParams(window.location.search).get('id');
  const workingBody = document.querySelector('[data-working-checklist-body]');
  const alert = document.querySelector('[data-working-alert]');
  let currentChecklist = null;
  const resultOptions = (value) => ['Да', 'Нет', 'Не применяется', 'Не проверено'].map((option) => `<option ${value === option ? 'selected' : ''}>${option}</option>`).join('');
  const showError = (message) => { if (!alert) return; alert.textContent = message; alert.className = 'alert alert-danger mb-3'; alert.hidden = false; alert.focus(); };
  const renderWorkingChecklist = (checklist) => {
    currentChecklist = checklist;
    if (!workingBody) return;
    const approved = checklist.status.toLowerCase() === 'approved';
    document.querySelector('[data-working-title]').textContent = checklist.name;
    document.querySelector('[data-working-meta]').textContent = approved
      ? `${checklist.facility} · утверждён ${formatDate(checklist.approvedAt)} · ${checklist.approvedBy || 'пользователь'}`
      : `${checklist.facility} · черновик создан ${formatDate(checklist.createdAt)}`;
    const badge = document.querySelector('[data-working-status]');
    badge.textContent = approved ? 'Утверждён · только чтение' : 'Черновик сохранён';
    badge.className = `badge text-bg-${approved ? 'success' : 'primary'} align-self-center`;
    document.querySelectorAll('[data-draft-only]').forEach((node) => { node.hidden = approved; });
    if (approved) document.querySelector('[data-draft-editor]')?.setAttribute('hidden', '');
    const exports = document.querySelector('[data-export-links]');
    if (exports) exports.hidden = false;
    document.querySelector('[data-export-xlsx]')?.setAttribute('href', `/exports/checklists/${checklist.id}.xlsx`);
    document.querySelector('[data-export-docx]')?.setAttribute('href', `/exports/checklists/${checklist.id}.docx`);
    document.querySelector('[data-export-pdf]')?.setAttribute('href', `/exports/checklists/${checklist.id}.pdf`);
    workingBody.innerHTML = checklist.items.length ? checklist.items.map((item) => `<tr class="${item.origin === 'manual' ? 'checklist-row-manual' : ''}" data-checklist-item="${escapeHtml(item.id)}"><td>${item.position}</td><td>${escapeHtml(item.section)}</td><td>${escapeHtml(item.title)}</td><td>${escapeHtml(item.basis)}</td>${approved ? `<td>${escapeHtml(item.result || '—')}</td><td>${escapeHtml(item.nonconformity || '—')}</td><td>${escapeHtml(item.note || '—')}</td>` : `<td><select class="form-select form-select-sm" aria-label="Результат пункта ${item.position}" data-item-result><option value="">Выберите</option>${resultOptions(item.result)}</select></td><td><input class="form-control form-control-sm" value="${escapeHtml(item.nonconformity)}" aria-label="Несоответствие пункта ${item.position}" data-item-nonconformity /></td><td><input class="form-control form-control-sm" value="${escapeHtml(item.note)}" aria-label="Примечание пункта ${item.position}" data-item-note /></td>`}<td>${escapeHtml(item.sourceLabel || (item.origin === 'manual' ? 'Добавлено инспектором' : item.origin === 'ai' ? 'ИИ + база знаний' : 'Шаблон'))}</td><td>${approved ? '' : '<button class="btn btn-sm btn-outline-primary" type="button" data-save-item>Сохранить</button>'}</td></tr>`).join('') : '<tr><td colspan="9" class="muted-note">В чек-листе пока нет пунктов.</td></tr>';
  };

  const loadChecklist = () => request(`/api/checklists/${encodeURIComponent(checklistId)}`).then(renderWorkingChecklist).catch((error) => { workingBody.innerHTML = '<tr><td colspan="9" class="text-danger">Чек-лист не найден.</td></tr>'; showError(error.message); });
  if (workingBody) {
    if (!checklistId) { workingBody.innerHTML = '<tr><td colspan="9" class="text-danger">Не указан чек-лист.</td></tr>'; showError('Откройте черновик или исторический чек-лист из соответствующего списка.'); }
    else loadChecklist();
  }

  workingBody?.addEventListener('click', async (event) => {
    const button = event.target.closest('[data-save-item]');
    if (!button) return;
    const row = button.closest('[data-checklist-item]');
    button.disabled = true;
    try {
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/items/${encodeURIComponent(row.dataset.checklistItem)}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
          result: row.querySelector('[data-item-result]').value,
          nonconformity: row.querySelector('[data-item-nonconformity]').value,
          note: row.querySelector('[data-item-note]').value
        })
      });
      currentChecklist = checklist;
      button.textContent = 'Сохранено';
      button.classList.remove('btn-outline-primary');
      button.classList.add('btn-outline-success');
    } catch (error) { showError(error.message); button.disabled = false; }
  });
  workingBody?.addEventListener('input', (event) => {
    const button = event.target.closest('[data-checklist-item]')?.querySelector('[data-save-item]');
    if (!button) return;
    button.textContent = 'Сохранить';
    button.classList.remove('btn-outline-success');
    button.classList.add('btn-outline-primary');
  });

  document.querySelector('[data-save-manual-item]')?.addEventListener('click', async (event) => {
    const title = document.querySelector('[data-manual-title]').value.trim();
    const basis = document.querySelector('[data-manual-basis]').value.trim();
    if (!title || !basis) { showError('Заполните наименование и основание нового пункта.'); return; }
    event.currentTarget.disabled = true;
    try {
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/items`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title, basis, section: document.querySelector('[data-manual-section]').value, note: document.querySelector('[data-manual-note]').value }) });
      renderWorkingChecklist(checklist);
      document.querySelector('[data-manual-title]').value = '';
      document.querySelector('[data-manual-basis]').value = '';
      document.getElementById('manualItem').hidden = true;
    } catch (error) { showError(error.message); }
    finally { event.currentTarget.disabled = false; }
  });

  document.querySelector('[data-open-approval]')?.addEventListener('click', () => bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistApprovalModal')).show());
  document.querySelector('[data-confirm-approval]')?.addEventListener('click', async (event) => {
    if (!currentChecklist || !checklistId) return;
    event.currentTarget.disabled = true;
    try {
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/approve`, { method: 'POST' });
      bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistApprovalModal')).hide();
      renderWorkingChecklist(checklist);
      history.replaceState(null, '', `/Checklists/Result?id=${encodeURIComponent(checklist.id)}&history=1`);
    } catch (error) { showError(error.message); }
    finally { event.currentTarget.disabled = false; }
  });
})();
