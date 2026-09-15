(() => {
  const form = document.querySelector('[data-facility-editor]');
  if (!form) return;

  let slug = form.dataset.facilitySlug || '';
  let structuredProfile = null;
  let dictionary = null;
  const error = form.querySelector('[data-facility-editor-error]');
  const success = form.querySelector('[data-facility-editor-success]');
  const readiness = form.querySelector('[data-facility-readiness]');
  const verification = form.querySelector('[data-facility-verification]');
  const confirmButton = form.querySelector('[data-facility-confirm]');
  const featuresHost = form.querySelector('[data-structured-features]');
  const staticFields = [...form.elements].filter((field) => field.name).map((field) => field.name);

  const stateCode = (value) => String(value || 'unknown').toLowerCase();
  const setValues = (profile) => staticFields.forEach((name) => {
    const field = form.elements.namedItem(name);
    if (field && name !== 'latitude' && name !== 'longitude') field.value = profile[name] ?? '';
  });

  const currentFacts = () => Object.fromEntries(dictionary.features.map((feature) => {
    const selected = form.querySelector(`input[name="feature.${CSS.escape(feature.code)}"]:checked`);
    const details = form.querySelector(`[name="details.${CSS.escape(feature.code)}"]`);
    return [feature.code, { state: selected?.value || 'unknown', details: details?.value.trim() || '' }];
  }));

  const updateReadiness = () => {
    if (!dictionary) return;
    const unknown = dictionary.features.filter((feature) => {
      const checked = form.querySelector(`input[name="feature.${CSS.escape(feature.code)}"]:checked`);
      return !checked || checked.value === 'unknown';
    });
    const verified = structuredProfile?.verificationStatus === 'verified';
    readiness.className = `alert mb-0 ${verified && unknown.length === 0 ? 'alert-success' : 'alert-warning'}`;
    readiness.textContent = unknown.length
      ? `Нужно уточнить признаков: ${unknown.length}. Чек-лист можно сформировать по известным данным; эти признаки не войдут в область проверки.`
      : verified ? 'Карточка подтверждена и готова к формированию чек-листа.' : 'Все признаки заполнены. Сохраните и подтвердите карточку.';
    verification.className = `badge align-self-start ${verified ? 'text-bg-success' : 'text-bg-warning'}`;
    verification.textContent = verified ? `Подтверждено${structuredProfile.verifiedBy ? `: ${structuredProfile.verifiedBy}` : ''}` : 'Не подтверждено';
    confirmButton.disabled = !slug || unknown.length > 0 || verified;
  };

  const renderFeatures = () => {
    featuresHost.replaceChildren();
    dictionary.features.forEach((feature) => {
      const fact = structuredProfile?.features?.[feature.code] || { state: 'unknown', details: '' };
      const section = document.createElement('fieldset'); section.className = 'facility-feature';
      const legend = document.createElement('legend'); legend.className = 'facility-feature-title'; legend.textContent = feature.label;
      const hint = document.createElement('p'); hint.className = 'facility-feature-hint'; hint.textContent = feature.description;
      const choices = document.createElement('div'); choices.className = 'facility-state-options';
      dictionary.states.forEach((state) => {
        const label = document.createElement('label'); label.className = 'facility-state-option';
        const input = document.createElement('input'); input.type = 'radio'; input.name = `feature.${feature.code}`; input.value = state.code;
        input.checked = stateCode(fact.state) === state.code; input.addEventListener('change', updateReadiness);
        label.append(input, document.createTextNode(state.label)); choices.appendChild(label);
      });
      const details = document.createElement('textarea'); details.className = 'form-control form-control-sm mt-2'; details.rows = 2;
      details.name = `details.${feature.code}`; details.value = fact.details || ''; details.placeholder = 'Источник, оборудование, документ или пояснение';
      section.append(legend, hint, choices, details); featuresHost.appendChild(section);
    });
    updateReadiness();
  };

  const load = async () => {
    try {
      const dictionaryResponse = await fetch('/api/operations/facility-profile-dictionaries');
      if (!dictionaryResponse.ok) throw new Error();
      dictionary = await dictionaryResponse.json();
      if (slug) {
        const response = await fetch(`/api/operations/facility-profiles/${encodeURIComponent(slug)}`);
        if (!response.ok) throw new Error();
        const data = await response.json(); setValues(data.profile);
        form.elements.latitude.value = data.latitude ?? ''; form.elements.longitude.value = data.longitude ?? ''; structuredProfile = data.structuredProfile;
      } else structuredProfile = { schemaVersion: 2, verificationStatus: 'needs_review', features: {} };
      renderFeatures();
    } catch { error.textContent = 'Не удалось загрузить карточку или справочники.'; }
  };

  form.addEventListener('submit', async (event) => {
    event.preventDefault(); error.textContent = ''; success.textContent = '';
    const raw = Object.fromEntries(new FormData(form));
    Object.keys(raw).filter((key) => key.startsWith('feature.') || key.startsWith('details.')).forEach((key) => delete raw[key]);
    const toNumber = (value) => String(value || '').trim() === '' ? null : Number(String(value).replace(',', '.'));
    const latitude = toNumber(raw.latitude); const longitude = toNumber(raw.longitude);
    if ((latitude !== null && Number.isNaN(latitude)) || (longitude !== null && Number.isNaN(longitude))) { error.textContent = 'Координаты укажите числами.'; return; }
    delete raw.latitude; delete raw.longitude;
    const outgoingStructured = { ...(structuredProfile || {}), features: currentFacts(), verificationStatus: 'needs_review', verifiedAt: null, verifiedBy: '' };
    const response = await fetch(slug ? `/api/operations/facility-profiles/${encodeURIComponent(slug)}` : '/api/operations/facility-profiles', { method: slug ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ profile: raw, latitude, longitude, structuredProfile: outgoingStructured }) });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) { error.textContent = data.error || 'Не удалось сохранить карточку.'; return; }
    slug = data.slug; form.dataset.facilitySlug = slug; structuredProfile = outgoingStructured;
    window.history.replaceState({}, '', `/Facilities/Edit/${encodeURIComponent(slug)}`); success.textContent = 'Карточка сохранена. Изменения нужно подтвердить.'; updateReadiness();
  });

  confirmButton.addEventListener('click', async () => {
    error.textContent = ''; success.textContent = '';
    const response = await fetch(`/api/operations/facility-profiles/${encodeURIComponent(slug)}/confirm`, { method: 'POST' });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) { error.textContent = data.error || 'Не удалось подтвердить карточку.'; return; }
    structuredProfile.verificationStatus = 'verified'; structuredProfile.verifiedBy = 'inspector'; structuredProfile.verifiedAt = new Date().toISOString();
    success.textContent = 'Карточка подтверждена и готова для подбора требований.'; updateReadiness();
  });

  load();
})();
