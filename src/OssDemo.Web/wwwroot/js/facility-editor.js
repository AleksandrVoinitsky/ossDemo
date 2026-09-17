(() => {
  const form = document.querySelector('[data-facility-editor]'); if (!form) return;
  let slug = form.dataset.facilitySlug || '';
  const error = form.querySelector('[data-facility-editor-error]'); const success = form.querySelector('[data-facility-editor-success]');
  const fields = [...form.elements].filter((field) => field.name).map((field) => field.name);
  document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach((node) => window.bootstrap?.Tooltip && new bootstrap.Tooltip(node));
  if (slug) fetch(`/api/operations/facility-profiles/${encodeURIComponent(slug)}`).then((response) => response.ok ? response.json() : Promise.reject()).then((data) => fields.forEach((name) => { const field = form.elements.namedItem(name); if (field) field.value = name === 'latitude' ? data.latitude ?? '' : name === 'longitude' ? data.longitude ?? '' : data.profile[name] ?? ''; })).catch(() => { error.textContent = 'Не удалось загрузить карточку.'; });
  form.addEventListener('submit', async (event) => {
    event.preventDefault(); error.textContent = ''; success.textContent = '';
    const raw = Object.fromEntries(new FormData(form)); const number = (value) => String(value || '').trim() === '' ? null : Number(String(value).replace(',', '.'));
    const latitude = number(raw.latitude); const longitude = number(raw.longitude);
    if ((latitude !== null && Number.isNaN(latitude)) || (longitude !== null && Number.isNaN(longitude))) { error.textContent = 'Координаты укажите числами.'; return; }
    delete raw.latitude; delete raw.longitude;
    const response = await fetch(slug ? `/api/operations/facility-profiles/${encodeURIComponent(slug)}` : '/api/operations/facility-profiles', { method: slug ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ profile: raw, latitude, longitude, structuredProfile: null }) });
    const data = await response.json().catch(() => ({})); if (!response.ok) { error.textContent = data.error || 'Не удалось сохранить карточку.'; return; }
    slug = data.slug; form.dataset.facilitySlug = slug; history.replaceState({}, '', `/Facilities/Edit/${encodeURIComponent(slug)}`); success.textContent = 'Карточка сохранена.';
  });
})();
