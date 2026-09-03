(() => {
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
  const mapUrl = (facility) => `https://yandex.ru/map-widget/v1/?ll=${facility.longitude}%2C${facility.latitude}&z=10&pt=${facility.longitude}%2C${facility.latitude}%2Cpm2blm`;

  const list = document.querySelector('[data-facility-list]');
  if (list) fetch('/api/operations/facilities').then((response) => response.json()).then((facilities) => {
    document.querySelector('[data-facility-count]').textContent = facilities.length;
    list.innerHTML = facilities.map((facility, index) => `<button class="facility-list-item btn btn-light text-start ${index === 0 ? 'active' : ''}" data-facility-name="${escapeHtml(facility.name)}" data-facility-address="${escapeHtml(facility.address)}" data-facility-coordinates="${facility.latitude}, ${facility.longitude}" data-facility-map-url="${mapUrl(facility)}"><span class="facility-list-item-title">${escapeHtml(facility.name)}</span><span class="small text-muted">НВОС ${escapeHtml(facility.nvocCategory)} · цифровой профиль</span><span class="facility-list-item-meta"><span class="status-dot status-ready"></span>запись в рабочем реестре</span></button>`).join('') || '<p class="muted-note p-3">В реестре нет объектов. Добавьте первый через команду <code>!add-facility</code>.</p>';
    const select = (button) => { list.querySelectorAll('button').forEach((item) => item.classList.toggle('active', item === button)); const frame = document.querySelector('[data-facility-map-frame]'); if (frame) frame.src = button.dataset.facilityMapUrl; document.querySelector('[data-facility-map-name]').textContent = button.dataset.facilityName; document.querySelector('[data-facility-map-address]').textContent = button.dataset.facilityAddress; document.querySelector('[data-facility-map-coordinates]').textContent = button.dataset.facilityCoordinates; };
    list.querySelectorAll('button').forEach((button) => button.addEventListener('click', () => select(button)));
    if (list.querySelector('button')) select(list.querySelector('button'));
  }).catch(() => { list.innerHTML = '<p class="text-danger p-3">Не удалось загрузить реестр объектов.</p>'; });

  const violations = document.querySelector('[data-violations-list]');
  if (violations) fetch('/api/operations/violations').then((response) => response.json()).then((items) => {
    const labels = { critical: ['критический', 'danger'], review: ['требует решения', 'warning'], closed: ['закрыто', 'success'] };
    violations.innerHTML = items.map((item) => { const status = labels[item.status] || [item.status, 'secondary']; return `<tr><td>${new Date(item.createdAt).toLocaleDateString('ru-RU')}</td><td>${escapeHtml(item.facilityName)}</td><td><span class="classifier-chip">${escapeHtml(item.classifierSection)}</span></td><td>${escapeHtml(item.description)}</td><td>${escapeHtml(item.responsible)}</td><td>${item.dueDate ? new Date(item.dueDate).toLocaleDateString('ru-RU') : '—'}</td><td><span class="badge text-bg-${status[1]}">${status[0]}</span></td></tr>`; }).join('') || '<tr><td colspan="7" class="text-muted">В реестре нет нарушений.</td></tr>';
  }).catch(() => { violations.innerHTML = '<tr><td colspan="7" class="text-danger">Не удалось загрузить реестр нарушений.</td></tr>'; });
})();
