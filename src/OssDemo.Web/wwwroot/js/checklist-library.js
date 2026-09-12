(() => {
  const root = document.querySelector('[data-checklist-library]');
  if (!root) return;
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[c]);
  const formatDate = (value) => value ? new Date(value).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' }) : '—';
  const alert = root.querySelector('[data-library-alert]');
  const showError = (message) => { alert.textContent = message; alert.className = 'alert alert-danger mt-3'; alert.hidden = false; alert.focus(); };
  const request = async (url, options) => { const response = await fetch(url, options); if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || 'Не удалось выполнить операцию.'); } return response.status === 204 ? null : response.json(); };

  let templates = [];
  const templateBody = root.querySelector('[data-template-list]');
  const renderTemplates = () => {
    if (!templateBody) return;
    const search = root.querySelector('[data-template-search]')?.value.trim().toLocaleLowerCase('ru-RU') || '';
    const values = templates.filter(item => !search || `${item.name} ${item.facility}`.toLocaleLowerCase('ru-RU').includes(search));
    templateBody.innerHTML = values.length ? values.map(item => `<tr><td><strong>${escapeHtml(item.name)}</strong></td><td>${escapeHtml(item.facility)}</td><td>${item.sectionCount} разделов · ${item.itemCount} пунктов</td><td>${formatDate(item.updatedAt)}</td><td><div class="library-row-actions"><a class="btn btn-sm btn-outline-primary" href="/Checklists/TemplateEditor?id=${encodeURIComponent(item.id)}">Редактировать</a><button class="btn btn-sm btn-outline-secondary" type="button" data-copy-template="${escapeHtml(item.id)}">Копировать</button><button class="btn btn-sm btn-outline-danger" type="button" data-delete-template="${escapeHtml(item.id)}">Удалить</button></div></td></tr>`).join('') : '<tr><td colspan="5" class="muted-note">Шаблоны не найдены.</td></tr>';
  };
  if (templateBody) request('/api/checklist-templates').then(values => { templates = values; renderTemplates(); }).catch(error => { templateBody.innerHTML = '<tr><td colspan="5" class="text-danger">Не удалось загрузить шаблоны.</td></tr>'; showError(error.message); });
  root.querySelector('[data-template-search]')?.addEventListener('input', renderTemplates);

  let selectedTemplate = null;
  root.addEventListener('click', (event) => {
    const copy = event.target.closest('[data-copy-template]');
    const remove = event.target.closest('[data-delete-template]');
    if (copy) { selectedTemplate = templates.find(item => item.id === copy.dataset.copyTemplate); document.querySelector('[data-copy-name]').value = `${selectedTemplate.name} — копия`; bootstrap.Modal.getOrCreateInstance(document.getElementById('templateCopyModal')).show(); }
    if (remove) { selectedTemplate = templates.find(item => item.id === remove.dataset.deleteTemplate); document.querySelector('[data-delete-name]').textContent = selectedTemplate.name; bootstrap.Modal.getOrCreateInstance(document.getElementById('templateDeleteModal')).show(); }
  });
  document.querySelector('[data-copy-form]')?.addEventListener('submit', async (event) => { event.preventDefault(); if (!selectedTemplate) return; try { const value = await request(`/api/checklist-templates/${selectedTemplate.id}/copy`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: document.querySelector('[data-copy-name]').value }) }); location.href = `/Checklists/TemplateEditor?id=${encodeURIComponent(value.id)}`; } catch (error) { showError(error.message); } });
  document.querySelector('[data-confirm-delete]')?.addEventListener('click', async () => { if (!selectedTemplate) return; try { await request(`/api/checklist-templates/${selectedTemplate.id}`, { method: 'DELETE' }); templates = templates.filter(item => item.id !== selectedTemplate.id); bootstrap.Modal.getOrCreateInstance(document.getElementById('templateDeleteModal')).hide(); renderTemplates(); } catch (error) { showError(error.message); } });

  const historyBody = root.querySelector('[data-history-list]');
  const filters = root.querySelector('[data-history-filters]');
  const loadHistory = async () => {
    if (!historyBody) return;
    historyBody.innerHTML = '<tr><td colspan="6" class="muted-note">Загрузка истории…</td></tr>';
    try { const values = await request(`/api/checklists/history?${new URLSearchParams(new FormData(filters))}`); historyBody.innerHTML = values.length ? values.map(item => `<tr><td><strong>${escapeHtml(item.name)}</strong><div class="small muted-note">${escapeHtml(item.templateName)}</div></td><td>${escapeHtml(item.facility)}</td><td>${escapeHtml(item.inspectionStartedOn || '—')} — ${escapeHtml(item.inspectionFinishedOn || '—')}</td><td>${formatDate(item.approvedAt)}<div class="small muted-note">${escapeHtml(item.approvedBy || '')}</div></td><td>${item.itemCount}</td><td><a class="btn btn-sm btn-outline-primary" href="/Checklists/Result?id=${encodeURIComponent(item.id)}">Открыть</a></td></tr>`).join('') : '<tr><td colspan="6" class="muted-note">Исторические чек-листы не найдены.</td></tr>'; } catch (error) { historyBody.innerHTML = '<tr><td colspan="6" class="text-danger">Не удалось загрузить историю.</td></tr>'; showError(error.message); }
  };
  if (historyBody) {
    Promise.all([request('/api/operations/facilities'), loadHistory()]).then(([facilities]) => { const select = filters.elements.facilityId; select.innerHTML = '<option value="">Все объекты</option>' + facilities.map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}</option>`).join(''); }).catch(error => showError(error.message));
    filters.addEventListener('submit', (event) => { event.preventDefault(); loadHistory(); });
  }
})();
