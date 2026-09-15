(() => {
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
  const fetchJson = (url) => fetch(url).then((response) => response.ok ? response.json() : Promise.reject(new Error(`HTTP ${response.status}`)));
  const mapUrl = (facility) => facility.latitude == null || facility.longitude == null ? '' : `https://yandex.ru/map-widget/v1/?ll=${facility.longitude}%2C${facility.latitude}&z=10&pt=${facility.longitude}%2C${facility.latitude}%2Cpm2blm`;

  const list = document.querySelector('[data-facility-list]');
  if (list) {
    const search = document.querySelector('[data-facility-search]');
    const count = document.querySelector('[data-facility-count]');
    const status = document.querySelector('[data-facility-status]');
    let facilities = [];
    const select = (button) => {
      list.querySelectorAll('[data-facility-select]').forEach((item) => {
        const selected = item === button;
        item.closest('.facility-list-item')?.classList.toggle('active', selected);
        item.setAttribute('aria-pressed', selected ? 'true' : 'false');
      });
      const frame = document.querySelector('[data-facility-map-frame]');
      if (frame) { frame.src = button.dataset.facilityMapUrl || 'about:blank'; frame.title = `${button.dataset.facilityName} на карте`; }
      ['name', 'address', 'coordinates'].forEach((field) => { const target = document.querySelector(`[data-facility-map-${field}]`); if (target) target.textContent = button.dataset[`facility${field[0].toUpperCase()}${field.slice(1)}`]; });
    };
    const render = () => {
      const query = (search?.value || '').trim().toLocaleLowerCase('ru');
      const visible = facilities.filter((facility) => !query || `${facility.name} ${facility.address} ${facility.nvocCategory}`.toLocaleLowerCase('ru').includes(query));
      if (count) count.textContent = visible.length;
      list.innerHTML = visible.map((facility, index) => `<article class="facility-list-item ${index === 0 ? 'active' : ''}"><button class="facility-select-button text-start" type="button" aria-pressed="${index === 0}" data-facility-select data-facility-name="${escapeHtml(facility.name)}" data-facility-address="${escapeHtml(facility.address)}" data-facility-coordinates="${facility.latitude == null ? 'Координаты не указаны' : `${facility.latitude}, ${facility.longitude}`}" data-facility-map-url="${mapUrl(facility)}"><span class="facility-list-item-title">${escapeHtml(facility.name)}</span><span class="small text-muted">НВОС ${escapeHtml(facility.nvocCategory)} · цифровой профиль</span><span class="facility-list-item-meta"><span class="status-dot status-ready"></span>действующий объект</span></button><a class="btn btn-outline-primary btn-sm facility-card-action" href="/Facilities/Card/${encodeURIComponent(facility.slug)}">Открыть карточку</a></article>`).join('') || '<div class="schedule-empty-state mt-0">Объекты по запросу не найдены.</div>';
      list.querySelectorAll('[data-facility-select]').forEach((button) => button.addEventListener('click', () => select(button)));
      const first = list.querySelector('[data-facility-select]');
      if (first) select(first);
      else {
        const frame = document.querySelector('[data-facility-map-frame]');
        if (frame) { frame.src = 'about:blank'; frame.title = 'Расположение объекта на карте'; }
        const name = document.querySelector('[data-facility-map-name]'); if (name) name.textContent = 'Объекты не найдены';
        ['address', 'coordinates'].forEach((field) => { const target = document.querySelector(`[data-facility-map-${field}]`); if (target) target.textContent = ''; });
      }
      window.announceStatus?.(status, 'muted', `Показано объектов: ${visible.length} из ${facilities.length}`);
    };
    search?.addEventListener('input', render);
    fetchJson('/api/operations/facilities').then((items) => { facilities = items; render(); }).catch(() => { facilities = []; render(); list.innerHTML = '<div class="schedule-empty-state mt-0 text-danger">Не удалось загрузить реестр объектов.</div>'; window.announceStatus?.(status, 'error', 'Проверьте подключение к рабочей базе данных.'); });
  }

  const violations = document.querySelector('[data-violations-list]');
  if (violations) {
    const search = document.querySelector('[data-violation-search]');
    const statusFilter = document.querySelector('[data-violation-status]');
    const result = document.querySelector('[data-violation-result]');
    const labels = { critical: ['критический', 'danger'], review: ['требует решения', 'warning'], closed: ['закрыто', 'success'] };
    let items = [];
    const render = () => {
      const query = (search?.value || '').trim().toLocaleLowerCase('ru');
      const selectedStatus = statusFilter?.value || '';
      const visible = items.filter((item) => (!selectedStatus || item.status === selectedStatus) && (!query || `${item.facilityName} ${item.classifierSection} ${item.description} ${item.responsible}`.toLocaleLowerCase('ru').includes(query)));
      violations.innerHTML = visible.map((item) => { const status = labels[item.status] || [item.status, 'secondary']; return `<tr><td>${new Date(item.createdAt).toLocaleDateString('ru-RU')}</td><td>${escapeHtml(item.facilityName)}</td><td><span class="classifier-chip">${escapeHtml(item.classifierSection)}</span></td><td>${escapeHtml(item.description)}</td><td>${escapeHtml(item.responsible)}</td><td>${item.dueDate ? new Date(item.dueDate).toLocaleDateString('ru-RU') : '—'}</td><td><span class="badge text-bg-${status[1]}">${status[0]}</span></td></tr>`; }).join('') || '<tr><td colspan="7"><div class="schedule-empty-state mt-0">Нарушения по выбранным условиям не найдены.</div></td></tr>';
      window.announceStatus?.(result, 'muted', `Показано нарушений: ${visible.length} из ${items.length}`);
    };
    search?.addEventListener('input', render);
    statusFilter?.addEventListener('change', render);
    fetchJson('/api/operations/violations').then((data) => { items = data; render(); }).catch(() => { violations.innerHTML = '<tr><td colspan="7" class="text-danger">Не удалось загрузить реестр нарушений.</td></tr>'; window.announceStatus?.(result, 'error', 'Проверьте подключение к рабочей базе данных.'); });
  }
})();
