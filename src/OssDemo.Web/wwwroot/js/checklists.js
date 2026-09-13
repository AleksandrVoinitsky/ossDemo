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
  const autosaveTimers = new Map();
  const autosaveQueues = new Map();
  const resultOptions = (value) => ['Да', 'Нет', 'Не применяется', 'Не проверено'].map((option) => `<option ${value === option ? 'selected' : ''}>${option}</option>`).join('');
  const resultPresentation = (value, origin) => {
    if (value === 'Да') return { rowClass: 'checklist-row-success', badgeClass: 'text-bg-success', label: 'Да' };
    if (value === 'Нет') return { rowClass: 'checklist-row-critical', badgeClass: 'text-bg-danger', label: 'Нет' };
    if (value === 'Не применяется') return { rowClass: 'checklist-row-neutral', badgeClass: 'text-bg-secondary', label: 'Не применяется' };
    if (origin === 'manual') return { rowClass: 'checklist-row-manual', badgeClass: 'text-bg-secondary', label: value || 'Не проверено' };
    return { rowClass: 'checklist-row-control', badgeClass: 'text-bg-info', label: value || 'Не проверено' };
  };
  const originPresentation = (origin) => {
    if (origin === 'ai') return { className: 'origin-ai', label: 'ИИ + база знаний' };
    if (origin === 'manual') return { className: 'origin-manual', label: 'Добавлено инспектором' };
    return { className: 'origin-template', label: 'Шаблон' };
  };
  const showError = (message) => { if (!alert) return; alert.textContent = message; alert.className = 'alert alert-danger mb-3'; alert.hidden = false; alert.focus(); };
  const setAutosaveStatus = (row, text, state = '') => {
    const status = row?.querySelector('[data-item-save-status]');
    if (!status) return;
    status.textContent = text;
    status.className = `checklist-autosave-status ${state}`.trim();
  };
  const persistRow = async (row) => {
    if (!row?.isConnected || !checklistId) return currentChecklist;
    setAutosaveStatus(row, 'Сохранение…', 'is-saving');
    try {
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/items/${encodeURIComponent(row.dataset.checklistItem)}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
          result: row.querySelector('[data-item-result]').value,
          nonconformity: row.querySelector('[data-item-nonconformity]').value,
          note: row.querySelector('[data-item-note]').value
        })
      });
      currentChecklist = checklist;
      setAutosaveStatus(row, 'Сохранено', 'is-saved');
      return checklist;
    } catch (error) {
      setAutosaveStatus(row, 'Ошибка сохранения', 'is-error');
      showError(error.message);
      throw error;
    }
  };
  const enqueueRowSave = (row) => {
    const itemId = row.dataset.checklistItem;
    const previous = autosaveQueues.get(itemId) || Promise.resolve();
    const operation = previous.catch(() => {}).then(() => persistRow(row));
    autosaveQueues.set(itemId, operation);
    operation.catch(() => {});
    return operation;
  };
  const scheduleRowSave = (row, delay) => {
    const itemId = row.dataset.checklistItem;
    if (autosaveTimers.has(itemId)) window.clearTimeout(autosaveTimers.get(itemId).timer);
    setAutosaveStatus(row, 'Сохранение…', 'is-saving');
    const timer = window.setTimeout(() => {
      autosaveTimers.delete(itemId);
      enqueueRowSave(row);
    }, delay);
    autosaveTimers.set(itemId, { timer, row });
  };
  const flushAutosaves = async () => {
    for (const [itemId, pending] of autosaveTimers) {
      window.clearTimeout(pending.timer);
      autosaveTimers.delete(itemId);
      enqueueRowSave(pending.row);
    }
    const results = await Promise.allSettled([...autosaveQueues.values()]);
    return results.every((result) => result.status === 'fulfilled');
  };
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
      const result = resultPresentation(item.result, item.origin);
      const origin = originPresentation(item.origin);
      const sourceLabel = item.sourceLabel || origin.label;
      return `<tr class="checklist-result-row ${result.rowClass}" data-checklist-item="${escapeHtml(item.id)}">
        <td><span class="checklist-item-number">${item.position}</span></td>
        <td><span class="classifier-chip">${escapeHtml(item.section || 'Без раздела')}</span></td>
        <td class="checklist-item-title"><strong>${escapeHtml(item.title)}</strong></td>
        <td class="checklist-item-basis">${escapeHtml(item.basis)}</td>
        ${approved
          ? `<td><span class="badge ${result.badgeClass}">${escapeHtml(result.label)}</span></td><td>${escapeHtml(item.nonconformity || '—')}</td><td>${escapeHtml(item.note || '—')}</td>`
          : `<td><select class="form-select form-select-sm checklist-result-select" aria-label="Результат пункта ${item.position}" data-item-result><option value="">Выберите</option>${resultOptions(item.result)}</select></td><td><input class="form-control form-control-sm" value="${escapeHtml(item.nonconformity)}" aria-label="Несоответствие пункта ${item.position}" data-item-nonconformity /></td><td><input class="form-control form-control-sm" value="${escapeHtml(item.note)}" aria-label="Примечание пункта ${item.position}" data-item-note /></td>`}
        <td><span class="checklist-origin ${origin.className}">${escapeHtml(sourceLabel)}</span></td>
        <td>${approved ? '' : `<div class="checklist-item-actions"><span class="checklist-autosave-status is-saved" data-item-save-status>Сохранено</span><button class="btn btn-sm btn-outline-secondary" type="button" data-edit-item="${escapeHtml(item.id)}">Изменить</button><button class="btn btn-sm btn-outline-danger" type="button" data-delete-item="${escapeHtml(item.id)}">Удалить</button></div>`}</td>
      </tr>`;
    }).join('') : '<tr><td colspan="9" class="muted-note">В чек-листе пока нет пунктов.</td></tr>';
  };

  const loadChecklist = () => request(`/api/checklists/${encodeURIComponent(checklistId)}`).then(renderWorkingChecklist).catch((error) => { workingBody.innerHTML = '<tr><td colspan="9" class="text-danger">Чек-лист не найден.</td></tr>'; showError(error.message); });
  if (workingBody) {
    if (!checklistId) { workingBody.innerHTML = '<tr><td colspan="9" class="text-danger">Не указан чек-лист.</td></tr>'; showError('Откройте черновик или исторический чек-лист из соответствующего списка.'); }
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
  workingBody?.addEventListener('input', (event) => {
    const row = event.target.closest('[data-checklist-item]');
    if (!row || !event.target.matches('[data-item-result], [data-item-nonconformity], [data-item-note]')) return;
    if (event.target.matches('[data-item-result]')) {
      const presentation = resultPresentation(event.target.value, currentChecklist?.items.find((item) => item.id === row.dataset.checklistItem)?.origin);
      row.classList.remove('checklist-row-success', 'checklist-row-critical', 'checklist-row-neutral', 'checklist-row-manual', 'checklist-row-control');
      row.classList.add(presentation.rowClass);
    }
    scheduleRowSave(row, event.target.matches('[data-item-result]') ? 0 : 650);
  });

  document.querySelector('[data-edit-item-form]')?.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (!selectedChecklistItem || !checklistId) return;
    const submit = event.currentTarget.querySelector('[type="submit"]');
    submit.disabled = true;
    try {
      if (!await flushAutosaves()) throw new Error('Не все изменения пункта удалось сохранить. Повторите попытку.');
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/items/${encodeURIComponent(selectedChecklistItem.id)}/content`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: document.querySelector('[data-edit-item-title]').value,
          section: document.querySelector('[data-edit-item-section]').value,
          basis: document.querySelector('[data-edit-item-basis]').value
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
      await flushAutosaves();
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
      if (!await flushAutosaves()) throw new Error('Не все изменения пунктов удалось сохранить. Повторите попытку.');
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
      const saved = await flushAutosaves();
      if (!saved) throw new Error('Не все изменения удалось сохранить. Проверьте отмеченные строки и повторите попытку.');
      if ([...workingBody.querySelectorAll('[data-item-result]')].some((select) => !select.value))
        throw new Error('Выберите результат проверки для каждого пункта перед утверждением.');
      const checklist = await request(`/api/checklists/${encodeURIComponent(checklistId)}/approve`, { method: 'POST' });
      bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistApprovalModal')).hide();
      renderWorkingChecklist(checklist);
      history.replaceState(null, '', `/Checklists/Result?id=${encodeURIComponent(checklist.id)}&history=1`);
    } catch (error) { bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistApprovalModal')).hide(); showError(error.message); }
    finally { event.currentTarget.disabled = false; }
  });
})();
