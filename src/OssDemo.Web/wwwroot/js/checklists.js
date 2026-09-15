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
      templateSelect.innerHTML = '<option value="">Выберите уровень шаблона</option>' + templates.map((item, index) => `<option value="${escapeHtml(item.id)}" ${index === 0 ? 'selected' : ''}>${escapeHtml(item.scope === 'society' ? 'Уровень Общества' : 'Уровень филиала')} · ${item.itemCount} пунктов</option>`).join('');
    }).catch(() => {
      templateSelect.innerHTML = '<option value="">Не удалось загрузить шаблоны</option>';
      const warning = document.querySelector('[data-template-warning]');
      if (warning) { warning.textContent = 'Не удалось загрузить шаблоны из базы данных.'; warning.hidden = false; }
    });
  }

  const draftList = document.querySelector('[data-draft-list]');
  let drafts = [];
  let selectedDraft = null;
  const renderDrafts = () => {
    if (!draftList) return;
    document.querySelector('[data-draft-count]').textContent = `${drafts.length} шт.`;
    draftList.innerHTML = drafts.length ? drafts.map((item) => `<article class="draft-checklist-card"><div><strong>${escapeHtml(item.name)}</strong><div class="muted-note small">${escapeHtml(item.facility)} · ${item.itemCount} пунктов · изменён ${formatDate(item.updatedAt)}</div></div><div class="draft-checklist-actions"><a class="btn btn-outline-primary" href="/Checklists/Result?id=${encodeURIComponent(item.id)}">Продолжить</a><button class="btn btn-outline-danger" type="button" data-delete-draft="${escapeHtml(item.id)}">Удалить</button></div></article>`).join('') : '<p class="muted-note mb-0">Незавершённых чек-листов нет.</p>';
  };
  if (draftList) {
    request('/api/checklists/drafts').then((values) => {
      drafts = values;
      renderDrafts();
    }).catch(() => { draftList.innerHTML = '<p class="text-danger mb-0">Не удалось загрузить черновики.</p>'; document.querySelector('[data-draft-count]').textContent = 'Ошибка'; });
    draftList.addEventListener('click', (event) => {
      const button = event.target.closest('[data-delete-draft]');
      if (!button) return;
      selectedDraft = drafts.find((item) => item.id === button.dataset.deleteDraft) || null;
      if (!selectedDraft) return;
      document.querySelector('[data-delete-draft-name]').textContent = `«${selectedDraft.name}»`;
      bootstrap.Modal.getOrCreateInstance(document.getElementById('draftChecklistDeleteModal')).show();
    });
    document.querySelector('[data-confirm-draft-delete]')?.addEventListener('click', async (event) => {
      if (!selectedDraft) return;
      event.currentTarget.disabled = true;
      try {
        await request(`/api/checklists/${encodeURIComponent(selectedDraft.id)}`, { method: 'DELETE' });
        drafts = drafts.filter((item) => item.id !== selectedDraft.id);
        selectedDraft = null;
        bootstrap.Modal.getOrCreateInstance(document.getElementById('draftChecklistDeleteModal')).hide();
        renderDrafts();
      } catch (error) { window.alert(error.message); }
      finally { event.currentTarget.disabled = false; }
    });
  }

  const checklistId = new URLSearchParams(window.location.search).get('id');
  const workingBody = document.querySelector('[data-working-checklist-body]');
  const alert = document.querySelector('[data-working-alert]');
  let currentChecklist = null;
  let selectedChecklistItem = null;
  const basisPresentation = (item) => item.needsBasisReview
    ? { rowClass: 'checklist-row-critical', badgeClass: 'text-bg-danger', label: 'Требует основания' }
    : { rowClass: 'checklist-row-success', badgeClass: 'text-bg-success', label: 'Основание найдено' };
  const originPresentation = (origin) => {
    if (origin === 'ai') return { className: 'origin-ai', label: 'ИИ + база знаний' };
    if (origin === 'manual') return { className: 'origin-manual', label: 'Добавлено инспектором' };
    return { className: 'origin-template', label: 'Шаблон' };
  };
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
    workingBody.innerHTML = checklist.items.length ? checklist.items.map((item) => {
      const basisState = basisPresentation(item);
      const origin = originPresentation(item.origin);
      const sourceLabel = item.sourceLabel || origin.label;
      return `<article class="checklist-result-item ${basisState.rowClass}" data-checklist-item="${escapeHtml(item.id)}">
        <header class="checklist-item-topline">
          <div class="checklist-item-identity"><span class="checklist-item-number">${item.position}</span><div class="checklist-top-field"><span class="checklist-field-label">Раздел</span><span class="classifier-chip">${escapeHtml(item.section || 'Без раздела')}</span></div></div>
          <div class="checklist-item-controls">
            <div class="checklist-top-field"><span class="checklist-field-label">Проверка основания</span><span class="badge ${basisState.badgeClass}">${escapeHtml(basisState.label)}</span></div>
            <div class="checklist-top-field checklist-source-field"><span class="checklist-field-label">Источник</span><span class="checklist-origin ${origin.className}">${escapeHtml(sourceLabel)}</span></div>
            ${approved ? '' : `<div class="checklist-item-actions"><button class="btn btn-sm btn-outline-secondary" type="button" data-edit-item="${escapeHtml(item.id)}">Изменить</button><button class="btn btn-sm btn-outline-danger" type="button" data-delete-item="${escapeHtml(item.id)}">Удалить</button></div>`}
          </div>
        </header>
        <div class="checklist-item-content">
          <section class="checklist-content-field checklist-content-main"><span class="checklist-field-label">Что проверить</span><strong>${escapeHtml(item.title)}</strong></section>
          <section class="checklist-content-field"><span class="checklist-field-label">Основание</span><div>${escapeHtml(item.basis || 'Основание не заполнено')}</div></section>
        </div>
      </article>`;
    }).join('') : '<div class="checklist-result-empty muted-note">В чек-листе пока нет пунктов.</div>';
  };

  const loadChecklist = () => request(`/api/checklists/${encodeURIComponent(checklistId)}`).then(renderWorkingChecklist).catch((error) => { workingBody.innerHTML = '<div class="checklist-result-empty text-danger">Чек-лист не найден.</div>'; showError(error.message); });
  if (workingBody) {
    if (!checklistId) { workingBody.innerHTML = '<div class="checklist-result-empty text-danger">Не указан чек-лист.</div>'; showError('Откройте черновик или исторический чек-лист из соответствующего списка.'); }
    else loadChecklist();
  }

  workingBody?.addEventListener('click', async (event) => {
    const editButton = event.target.closest('[data-edit-item]');
    if (editButton) {
      selectedChecklistItem = currentChecklist?.items.find((item) => item.id === editButton.dataset.editItem) || null;
      if (!selectedChecklistItem) return;
      document.querySelector('[data-edit-item-title]').value = selectedChecklistItem.title;
      document.querySelector('[data-edit-item-section]').value = selectedChecklistItem.section;
      document.querySelector('[data-edit-item-basis]').value = selectedChecklistItem.basis;
      document.querySelector('[data-edit-item-note]').value = selectedChecklistItem.note;
      bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistItemEditModal')).show();
      return;
    }
    const deleteButton = event.target.closest('[data-delete-item]');
    if (deleteButton) {
      selectedChecklistItem = currentChecklist?.items.find((item) => item.id === deleteButton.dataset.deleteItem) || null;
      if (!selectedChecklistItem) return;
      document.querySelector('[data-delete-item-name]').textContent = `«${selectedChecklistItem.title}»`;
      bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistItemDeleteModal')).show();
      return;
    }
  });
  document.querySelector('[data-edit-item-form]')?.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (!selectedChecklistItem || !checklistId) return;
    const submit = event.currentTarget.querySelector('[type="submit"]');
    submit.disabled = true;
    try {
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/items/${encodeURIComponent(selectedChecklistItem.id)}/content`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: document.querySelector('[data-edit-item-title]').value,
          section: document.querySelector('[data-edit-item-section]').value,
          basis: document.querySelector('[data-edit-item-basis]').value,
          note: document.querySelector('[data-edit-item-note]').value
        })
      });
      bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistItemEditModal')).hide();
      selectedChecklistItem = null;
      renderWorkingChecklist(checklist);
    } catch (error) { showError(error.message); }
    finally { submit.disabled = false; }
  });

  document.querySelector('[data-confirm-item-delete]')?.addEventListener('click', async (event) => {
    if (!selectedChecklistItem || !checklistId) return;
    event.currentTarget.disabled = true;
    try {
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/items/${encodeURIComponent(selectedChecklistItem.id)}`, { method: 'DELETE' });
      bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistItemDeleteModal')).hide();
      selectedChecklistItem = null;
      renderWorkingChecklist(checklist);
    } catch (error) { showError(error.message); }
    finally { event.currentTarget.disabled = false; }
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
    } catch (error) { bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistApprovalModal')).hide(); showError(error.message); }
    finally { event.currentTarget.disabled = false; }
  });
})();
