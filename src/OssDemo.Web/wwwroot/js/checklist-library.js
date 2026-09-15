(() => {
  const root = document.querySelector('[data-checklist-library]');
  if (!root) return;
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[c]);
  const formatDate = (value) => value ? new Date(value).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' }) : '—';
  const formatDay = (value) => {
    if (!value) return '—';
    const parts = String(value).split('-');
    return parts.length === 3 ? `${parts[2]}.${parts[1]}.${parts[0]}` : escapeHtml(value);
  };
  const plural = (value, one, few, many) => {
    const number = Math.abs(Number(value)) % 100;
    const last = number % 10;
    if (number > 10 && number < 20) return many;
    if (last === 1) return one;
    return last > 1 && last < 5 ? few : many;
  };
  const emptyState = (title, text, isError = false) => `<div class="library-empty-state ${isError ? 'library-empty-state-error' : ''}"><span class="library-empty-icon" aria-hidden="true">${isError ? '!' : '○'}</span><strong>${escapeHtml(title)}</strong><span>${escapeHtml(text)}</span></div>`;
  const alert = root.querySelector('[data-library-alert]');
  const showError = (message) => { alert.textContent = message; alert.className = 'alert alert-danger mt-3'; alert.hidden = false; alert.focus(); };
  const request = async (url, options) => { const response = await fetch(url, options); if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || 'Не удалось выполнить операцию.'); } return response.status === 204 ? null : response.json(); };

  let templates = [];
  const templateBody = root.querySelector('[data-template-list]');
  const renderTemplates = () => {
    if (!templateBody) return;
    const values = templates;
    templateBody.innerHTML = values.length ? values.map((item, index) => `<article class="checklist-library-card checklist-template-card library-accent-${index % 4 + 1}">
      <div class="library-card-heading">
        <span class="library-card-symbol" aria-hidden="true"><span></span><span></span><span></span></span>
        <div class="library-card-title"><span class="library-card-kicker">Системный шаблон</span><h2>${escapeHtml(item.name)}</h2></div>
        <span class="library-card-version">v${escapeHtml(item.version)}</span>
      </div>
      <div class="library-facility"><span class="library-pin" aria-hidden="true"></span><span>${escapeHtml(item.scope === 'society' ? 'Уровень Общества' : 'Уровень филиала')}</span></div>
      <div class="library-card-metrics">
        <div><strong>${item.sectionCount}</strong><span>${plural(item.sectionCount, 'раздел', 'раздела', 'разделов')}</span></div>
        <div><strong>${item.itemCount}</strong><span>${plural(item.itemCount, 'пункт', 'пункта', 'пунктов')}</span></div>
      </div>
      <div class="library-card-footer"><span class="library-card-date">Изменён ${formatDate(item.updatedAt)}</span><div class="library-row-actions"><a class="btn btn-sm btn-primary" href="/Checklists/TemplateEditor?id=${encodeURIComponent(item.id)}">Редактировать</a></div></div>
    </article>`).join('') : emptyState('Шаблоны не найдены', 'Системные шаблоны ещё не подготовлены.');
  };
  if (templateBody) request('/api/checklist-templates').then(values => { templates = values; renderTemplates(); }).catch(error => { templateBody.innerHTML = emptyState('Не удалось загрузить шаблоны', error.message, true); showError(error.message); });

  const historyBody = root.querySelector('[data-history-list]');
  const filters = root.querySelector('[data-history-filters]');
  const loadHistory = async () => {
    if (!historyBody) return;
    historyBody.innerHTML = emptyState('Загрузка истории…', 'Получаем утверждённые чек-листы из базы данных.');
    try {
      const values = await request(`/api/checklists/history?${new URLSearchParams(new FormData(filters))}`);
      historyBody.innerHTML = values.length ? values.map((item, index) => `<article class="checklist-library-card checklist-history-card library-accent-${index % 4 + 1}">
        <div class="library-card-heading">
          <span class="library-card-symbol library-history-symbol" aria-hidden="true">✓</span>
          <div class="library-card-title"><span class="library-card-kicker">Утверждённый чек-лист</span><h2>${escapeHtml(item.name)}</h2></div>
          <span class="badge text-bg-success">История</span>
        </div>
        <div class="library-facility"><span class="library-pin" aria-hidden="true"></span><span>${escapeHtml(item.facility)}</span></div>
        <div class="library-history-details">
          <div><span>Шаблон</span><strong>${escapeHtml(item.templateName || 'Без шаблона')}</strong></div>
          <div><span>Период проверки</span><strong>${formatDay(item.inspectionStartedOn)} — ${formatDay(item.inspectionFinishedOn)}</strong></div>
          <div><span>Утверждён</span><strong>${formatDate(item.approvedAt)}</strong><small>${escapeHtml(item.approvedBy || 'Пользователь')}</small></div>
        </div>
        <div class="library-card-footer"><span class="library-item-count"><strong>${item.itemCount}</strong> ${plural(item.itemCount, 'пункт проверки', 'пункта проверки', 'пунктов проверки')}</span><a class="btn btn-sm btn-primary" href="/Checklists/Result?id=${encodeURIComponent(item.id)}">Открыть чек-лист</a></div>
      </article>`).join('') : emptyState('История пока пуста', 'Здесь появятся чек-листы после утверждения.');
    } catch (error) {
      historyBody.innerHTML = emptyState('Не удалось загрузить историю', error.message, true);
      showError(error.message);
    }
  };
  if (historyBody) {
    Promise.all([request('/api/operations/facilities'), loadHistory()]).then(([facilities]) => { const select = filters.elements.facilityId; select.innerHTML = '<option value="">Все объекты</option>' + facilities.map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}</option>`).join(''); }).catch(error => showError(error.message));
    filters.addEventListener('submit', (event) => { event.preventDefault(); loadHistory(); });
  }
})();
