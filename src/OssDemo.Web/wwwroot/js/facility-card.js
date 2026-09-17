(() => {
  const card = document.querySelector('[data-facility-card]'); if (!card) return; const empty = 'Не указано';
  const setText = (selector, value) => card.querySelectorAll(selector).forEach((node) => { node.textContent = value?.trim() || empty; });
  const setLines = (selector, value, chip) => { const host = card.querySelector(selector); if (!host) return; host.replaceChildren(); const values = (value || '').split(/\r?\n/).map((x) => x.trim()).filter(Boolean); (values.length ? values : [empty]).forEach((value) => { const node = document.createElement(chip ? 'span' : 'li'); node.textContent = value; if (chip) node.className = 'classifier-chip'; host.appendChild(node); }); };
  fetch(`/api/operations/facility-profiles/${encodeURIComponent(card.dataset.facilitySlug)}`).then((response) => response.ok ? response.json() : Promise.reject()).then((data) => {
    const profile = data.profile; Object.entries(profile).filter(([, value]) => typeof value === 'string').forEach(([name, value]) => setText(`[data-field="${name}"]`, value));
    setLines('[data-list="zones"]', profile.zones, true); setLines('[data-list="environmentalAspects"]', profile.environmentalAspects, true); setLines('[data-lines="equipment"]', profile.equipment); setLines('[data-lines="emissionSources"]', profile.emissionSources); setLines('[data-lines="permits"]', profile.permits); document.title = `${profile.shortName || 'Карточка объекта'} - АИ ООС`;
  }).catch(() => { const node = card.querySelector('[data-facility-card-error]'); node.textContent = 'Карточка объекта не найдена или база данных недоступна.'; node.hidden = false; });
})();
