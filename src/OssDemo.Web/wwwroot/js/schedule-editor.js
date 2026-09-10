(() => {
  const form = document.querySelector('[data-schedule-editor]');
  if (!form) return;
  const id = form.dataset.scheduleEventId;
  const error = form.querySelector('[data-schedule-editor-error]');
  const showError = (message) => { error.textContent = message; error.hidden = false; error.focus(); };
  const fields = ['title', 'eventType', 'status', 'facilitySlug', 'responsible', 'startDate', 'endDate', 'criteria', 'readiness', 'note'];
  const load = async () => {
    const facilities = await fetch('/api/operations/facilities').then((response) => response.ok ? response.json() : Promise.reject());
    const select = form.querySelector('[data-facility-select]');
    select.replaceChildren(new Option('Выберите объект', ''));
    facilities.forEach((facility) => select.add(new Option(facility.name, facility.slug)));
    if (id) { const item = await fetch(`/api/operations/schedule/${encodeURIComponent(id)}`).then((response) => response.ok ? response.json() : Promise.reject()); fields.forEach((name) => { form.elements[name].value = item[name] ?? ''; }); }
  };
  load().catch(() => showError('Не удалось загрузить объект или данные события.'));
  form.addEventListener('submit', async (event) => { event.preventDefault(); error.hidden = true; const payload = Object.fromEntries(new FormData(form)); if (payload.endDate < payload.startDate) { showError('Дата окончания не может быть раньше даты начала.'); return; } const response = await fetch(id ? `/api/operations/schedule/${encodeURIComponent(id)}` : '/api/operations/schedule', { method: id ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) }); const item = await response.json().catch(() => ({})); if (!response.ok) { showError(item.error || 'Не удалось сохранить событие.'); return; } window.location.assign(`/Schedule/Event/${encodeURIComponent(item.id)}`); });
  form.querySelector('[data-schedule-delete]')?.addEventListener('click', async () => { if (!window.confirm('Удалить это событие из графика?')) return; const response = await fetch(`/api/operations/schedule/${encodeURIComponent(id)}`, { method: 'DELETE' }); if (!response.ok) { showError('Не удалось удалить событие.'); return; } window.location.assign('/Schedule'); });
})();
