(() => {
  const card = document.querySelector('[data-facility-card]');
  if (!card) return;
  const empty = 'Не указано';
  const setText = (selector, value) => card.querySelectorAll(selector).forEach((element) => { element.textContent = value?.trim() || empty; });
  const setLines = (selector, value, chip) => { const element = card.querySelector(selector); if (!element) return; element.replaceChildren(); const items = (value || '').split(/\r?\n/).map((item) => item.trim()).filter(Boolean); (items.length ? items : [empty]).forEach((item) => { const child = document.createElement(chip ? 'span' : 'li'); child.textContent = item; if (chip) child.className = 'classifier-chip'; element.appendChild(child); }); };
  const labels = { unknown: 'Не установлено', present: 'Есть', absent: 'Нет' };

  Promise.all([
    fetch(`/api/operations/facility-profiles/${encodeURIComponent(card.dataset.facilitySlug)}`).then((response) => response.ok ? response.json() : Promise.reject()),
    fetch('/api/operations/facility-profile-dictionaries').then((response) => response.ok ? response.json() : Promise.reject())
  ]).then(([data, dictionary]) => {
    const profile = data.profile;
    Object.entries(profile).filter(([, value]) => typeof value === 'string').forEach(([name, value]) => setText(`[data-field="${name}"]`, value));
    setLines('[data-list="zones"]', profile.zones, true); setLines('[data-list="environmentalAspects"]', profile.environmentalAspects, true); setLines('[data-lines="equipment"]', profile.equipment, false); setLines('[data-lines="emissionSources"]', profile.emissionSources, false); setLines('[data-lines="permits"]', profile.permits, false);
    const status = card.querySelector('[data-profile-verification]'); const verified = data.structuredProfile.verificationStatus === 'verified';
    status.className = `badge align-self-start ${verified ? 'text-bg-success' : 'text-bg-warning'}`; status.textContent = verified ? 'Подтверждено' : 'Требует подтверждения';
    card.querySelector('[data-profile-readiness]').textContent = data.readiness.canFinalizeChecklist ? 'Готова к формированию итогового чек-листа.' : data.readiness.reasons.join(' ');
    const host = card.querySelector('[data-profile-features]'); host.replaceChildren();
    dictionary.features.forEach((feature) => {
      const fact = data.structuredProfile.features[feature.code] || { state: 'unknown', details: '' }; const state = String(fact.state).toLowerCase();
      const row = document.createElement('div'); row.className = `facility-fact facility-fact-${state}`;
      const title = document.createElement('strong'); title.textContent = feature.label;
      const badge = document.createElement('span'); badge.className = 'badge'; badge.textContent = labels[state] || labels.unknown;
      const details = document.createElement('small'); details.textContent = fact.details || 'Без пояснения'; row.append(title, badge, details); host.appendChild(row);
    });
    document.title = `${profile.shortName || 'Карточка объекта'} - АИ ООС`;
  }).catch(() => { const error = card.querySelector('[data-facility-card-error]'); error.textContent = 'Карточка объекта не найдена или рабочая база данных недоступна.'; error.hidden = false; });
})();
