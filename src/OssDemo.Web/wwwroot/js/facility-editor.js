(() => {
  const form = document.querySelector('[data-facility-editor]');
  if (!form) return;
  const slug = form.dataset.facilitySlug;
  const error = form.querySelector('[data-facility-editor-error]');
  const fields = Object.keys(Object.fromEntries(new FormData(form)));
  const setValues = (profile) => fields.forEach((name) => { const field = form.elements.namedItem(name); if (field) field.value = profile[name] ?? ''; });
  if (slug) fetch(`/api/operations/facility-profiles/${encodeURIComponent(slug)}`).then((response) => response.ok ? response.json() : Promise.reject()).then((data) => { setValues(data.profile); form.elements.latitude.value = data.latitude ?? ''; form.elements.longitude.value = data.longitude ?? ''; }).catch(() => { error.textContent = 'Не удалось загрузить карточку объекта.'; });
  form.addEventListener('submit', async (event) => {
    event.preventDefault(); error.textContent = '';
    const raw = Object.fromEntries(new FormData(form));
    const toNumber = (value) => value.trim() === '' ? null : Number(value.replace(',', '.'));
    const latitude = toNumber(raw.latitude); const longitude = toNumber(raw.longitude);
    if ((latitude !== null && Number.isNaN(latitude)) || (longitude !== null && Number.isNaN(longitude))) { error.textContent = 'Координаты укажите числами.'; return; }
    delete raw.latitude; delete raw.longitude;
    const response = await fetch(slug ? `/api/operations/facility-profiles/${encodeURIComponent(slug)}` : '/api/operations/facility-profiles', { method: slug ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ profile: raw, latitude, longitude }) });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) { error.textContent = data.error || 'Не удалось сохранить карточку.'; return; }
    window.location.assign(`/Facilities/Card/${encodeURIComponent(data.slug)}`);
  });
})();
