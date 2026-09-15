(() => {
  const app = document.querySelector('[data-classifier-app]');
  if (!app) return;
  const q = (selector, root = app) => root.querySelector(selector);
  const qa = (selector, root = app) => [...root.querySelectorAll(selector)];
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' })[c]);
  let tree = null;
  let selected = null;
  let creating = false;
  let requirementState = { items: [], excludedItems: [] };
  let selectedSearchRequirement = null;
  let searchTimer = null;

  const request = async (url, options = {}) => {
    const response = await fetch(url, options);
    const body = response.status === 204 ? null : await response.json().catch(() => ({}));
    if (!response.ok) {
      const error = new Error(body?.error || 'Не удалось выполнить операцию.');
      error.code = body?.code;
      error.fields = body?.fields;
      throw error;
    }
    return body;
  };
  const setStatus = (selector, text, danger = false) => {
    const node = q(selector, document);
    if (!node) return;
    node.textContent = text || '';
    node.classList.toggle('text-danger', danger);
  };
  const showMessage = (text, danger = false) => {
    const node = q('[data-classifier-message]');
    node.className = `alert mt-3 ${danger ? 'alert-danger' : 'alert-success'}`;
    node.textContent = text;
    node.hidden = false;
  };
  const ruleText = (rules) => (rules || []).map((rule) => `${rule.field} | ${rule.operator} | ${(rule.values || []).join(', ')}`).join('\n');
  const parseRules = (value) => value.split('\n').map((line) => line.trim()).filter(Boolean).map((line) => {
    const [field = '', operator = '', values = ''] = line.split('|').map((part) => part.trim());
    return { field, operator, values: values.split(',').map((part) => part.trim()).filter(Boolean) };
  });
  const allCriteria = () => tree.sections.flatMap((section) => section.criteria.map((criterion) => ({ section, criterion })));

  const switchTab = (name, focus = false) => {
    qa('[data-classifier-tab]').forEach((tab) => {
      const active = tab.dataset.classifierTab === name;
      tab.classList.toggle('active', active);
      tab.setAttribute('aria-selected', String(active));
      tab.tabIndex = active ? 0 : -1;
      if (active && focus) tab.focus();
    });
    qa('[data-classifier-panel]').forEach((panel) => { panel.hidden = panel.dataset.classifierPanel !== name; });
  };

  const renderTree = () => {
    const term = q('[data-classifier-search]').value.trim().toLowerCase();
    const html = tree.sections.map((section) => {
      const matches = section.criteria.filter((criterion) => !term || `${criterion.code} ${criterion.riskText} ${criterion.checkText} ${criterion.searchTerms}`.toLowerCase().includes(term));
      if (term && matches.length === 0 && !section.title.toLowerCase().includes(term)) return '';
      const visible = term && matches.length === 0 ? section.criteria : matches;
      return `<details class="classifier-node" ${term || selected?.section.id === section.id ? 'open' : ''}>
        <summary><span class="tree-code">${escapeHtml(section.code)}</span><span class="tree-title">${escapeHtml(section.title)}</span><span class="tree-meta">${section.criteria.filter((x) => x.isActive).length}/${section.criteria.length}</span></summary>
        <div class="classifier-children">${visible.map((criterion) => `<button class="classifier-leaf ${selected?.criterion.id === criterion.id ? 'active' : ''} ${criterion.isActive ? '' : 'classifier-leaf-disabled'}" role="treeitem" type="button" data-criterion-id="${criterion.id}"><strong>${escapeHtml(criterion.code)} ${escapeHtml(criterion.checkText)}</strong><span>${escapeHtml(criterion.searchTerms || criterion.riskText)}</span></button>`).join('')}</div>
      </details>`;
    }).join('');
    q('[data-classifier-tree]').innerHTML = html || '<div class="classifier-loading">Совпадений не найдено.</div>';
  };

  const requirementCard = (item, excluded = false) => {
    const source = excluded ? 'Исключено вручную' : item.linkSource === 'manual' ? 'Добавлено вручную' : 'Автоматическая связь';
    const sourceClass = excluded ? 'text-bg-secondary' : item.linkSource === 'manual' ? 'text-bg-info' : 'text-bg-success';
    return `<article class="requirement-card ${excluded ? 'requirement-card-excluded' : ''}" data-requirement-id="${escapeHtml(item.id)}">
      <div class="requirement-card-meta"><span class="requirement-id">${escapeHtml(item.id)}</span><span class="badge ${sourceClass}">${source}</span>${item.isRevised ? '<span class="badge text-bg-warning">Рабочая редакция</span>' : ''}</div>
      <p class="requirement-level">${escapeHtml((item.levels || []).join(' · ') || 'Уровень не указан')}</p>
      <h3>${escapeHtml(item.requirement)}</h3>
      <p class="requirement-basis"><strong>Основание:</strong> ${escapeHtml(item.basis)}</p>
      <div class="requirement-card-actions">
        ${excluded ? `<button class="btn btn-sm btn-outline-primary" type="button" data-requirement-action="restore">Восстановить</button>` : `<button class="btn btn-sm btn-outline-secondary" type="button" data-requirement-action="edit">Редактировать</button><button class="btn btn-sm ${item.linkSource === 'manual' ? 'btn-outline-secondary' : 'btn-outline-danger'}" type="button" data-requirement-action="${item.linkSource === 'manual' ? 'restore' : 'exclude'}">${item.linkSource === 'manual' ? 'Вернуть авто' : 'Исключить'}</button>`}
      </div>
      <form class="requirement-edit" data-requirement-edit hidden>
        <label class="form-label">Рабочая формулировка<textarea class="form-control" rows="4" name="requirement" required>${escapeHtml(item.requirement)}</textarea></label>
        <label class="form-label">Нормативное основание<textarea class="form-control" rows="2" name="basis" required>${escapeHtml(item.basis)}</textarea></label>
        <input type="hidden" name="version" value="${item.version}">
        <div class="d-flex gap-2"><button class="btn btn-sm btn-primary" type="submit">Сохранить редакцию</button><button class="btn btn-sm btn-outline-secondary" type="button" data-requirement-action="cancel-edit">Отмена</button></div>
      </form>
    </article>`;
  };
  const renderRequirements = () => {
    const items = requirementState.items || [];
    const excluded = requirementState.excludedItems || [];
    q('[data-requirement-count]').textContent = items.length;
    q('[data-requirement-list]').innerHTML = items.length ? items.map((item) => requirementCard(item)).join('') : '<div class="requirement-empty">Для критерия пока нет связанных требований. Добавьте строку из реестра вручную.</div>';
    q('[data-requirement-excluded-count]').textContent = excluded.length;
    q('[data-requirement-excluded-wrap]').hidden = excluded.length === 0;
    q('[data-requirement-excluded-list]').innerHTML = excluded.map((item) => requirementCard(item, true)).join('');
  };
  const loadRequirements = async () => {
    if (!selected || creating) return;
    const criterionId = selected.criterion.id;
    q('[data-requirement-list]').innerHTML = '<div class="classifier-loading">Загружаем требования…</div>';
    setStatus('[data-requirement-status]', '');
    try {
      const result = await request(`/api/classifier/criteria/${criterionId}/requirements`);
      if (selected?.criterion.id !== criterionId) return;
      requirementState = result;
      renderRequirements();
    } catch (error) {
      q('[data-requirement-list]').innerHTML = `<div class="alert alert-danger mb-0">${escapeHtml(error.message)}</div>`;
    }
  };

  const fill = (entry, isNew = false) => {
    creating = isNew;
    selected = entry;
    q('[data-classifier-empty]').hidden = true;
    q('[data-classifier-selected]').hidden = false;
    q('[data-classifier-section]').innerHTML = tree.sections.map((section) => `<option value="${section.id}">${escapeHtml(section.code)} · ${escapeHtml(section.title)}</option>`).join('');
    q('[data-classifier-section]').value = entry.section.id;
    q('[data-classifier-section]').disabled = !isNew;
    q('[data-classifier-code]').disabled = !isNew;
    const c = entry.criterion;
    q('[data-classifier-code]').value = c.code || '';
    q('[data-classifier-card-code]').textContent = isNew ? 'Новый критерий' : c.code;
    q('[data-classifier-card-title]').textContent = isNew ? 'Создание критерия' : (c.checkText || c.riskText);
    q('[data-classifier-card-section]').textContent = `${entry.section.code} · ${entry.section.title}`;
    q('[data-classifier-risk]').value = c.riskText || '';
    q('[data-classifier-check]').value = c.checkText || '';
    q('[data-classifier-terms]').value = c.searchTerms || '';
    q('[data-classifier-rules]').value = ruleText(c.applicabilityRules);
    q('[data-classifier-hints]').value = (c.sourceHints || []).join('\n');
    q('[data-classifier-position]').value = c.position ?? entry.section.criteria.length;
    q('[data-classifier-active]').checked = c.isActive ?? true;
    q('[data-classifier-usage]').textContent = c.historyUsageCount || 0;
    q('[data-classifier-sources]').textContent = (c.recentSources || []).join('; ') || 'Будут собраны после запусков';
    q('[data-classifier-delete]').hidden = isNew;
    q('[data-classifier-message]').hidden = true;
    q('[data-requirement-add]').disabled = isNew;
    renderTree();
    switchTab(isNew ? 'settings' : 'requirements');
    if (!isNew) loadRequirements();
  };
  const load = async (retainId) => {
    tree = await request('/api/classifier/');
    document.querySelector('[data-classifier-version]').textContent = `v${tree.version}`;
    document.querySelector('[data-classifier-section-count]').textContent = tree.sections.length;
    document.querySelector('[data-classifier-criterion-count]').textContent = tree.sections.reduce((sum, section) => sum + section.criteria.length, 0);
    const entry = allCriteria().find((item) => item.criterion.id === retainId) || allCriteria()[0];
    renderTree();
    if (entry) fill(entry);
  };
  const changeRequirementLink = async (id, action) => {
    const buttons = qa(`[data-requirement-id="${CSS.escape(id)}"] button`);
    buttons.forEach((button) => { button.disabled = true; });
    setStatus('[data-requirement-status]', 'Сохраняем связь…');
    try {
      requirementState = action === 'restore'
        ? await request(`/api/classifier/criteria/${selected.criterion.id}/requirements/${encodeURIComponent(id)}`, { method: 'DELETE' })
        : await request(`/api/classifier/criteria/${selected.criterion.id}/requirements/${encodeURIComponent(id)}`, { method: 'PUT', headers: { 'Content-Type':'application/json' }, body: JSON.stringify({ action }) });
      renderRequirements();
      setStatus('[data-requirement-status]', action === 'exclude' ? 'Требование исключено из критерия.' : 'Связь требования сохранена.');
      return true;
    } catch (error) {
      buttons.forEach((button) => { button.disabled = false; });
      setStatus('[data-requirement-status]', error.message, true);
      return false;
    }
  };

  q('[data-classifier-tree]').addEventListener('click', (event) => {
    const button = event.target.closest('[data-criterion-id]');
    if (!button) return;
    const entry = allCriteria().find((item) => item.criterion.id === button.dataset.criterionId);
    if (entry) fill(entry);
  });
  q('[data-classifier-search]').addEventListener('input', renderTree);
  qa('[data-classifier-tab]').forEach((tab, index, tabs) => {
    tab.addEventListener('click', () => switchTab(tab.dataset.classifierTab));
    tab.addEventListener('keydown', (event) => {
      if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
      event.preventDefault();
      const direction = event.key === 'ArrowRight' ? 1 : -1;
      const next = tabs[(index + direction + tabs.length) % tabs.length];
      switchTab(next.dataset.classifierTab, true);
    });
  });
  q('[data-classifier-create]').addEventListener('click', () => {
    const section = selected?.section || tree.sections[0];
    fill({ section, criterion: { code:'',riskText:'',checkText:'',searchTerms:'',applicabilityRules:[],sourceHints:[],isActive:true,position:section.criteria.length } }, true);
  });
  q('[data-classifier-form]').addEventListener('submit', async (event) => {
    event.preventDefault();
    const submit = q('[data-classifier-submit]');
    const payload = {
      riskText:q('[data-classifier-risk]').value, checkText:q('[data-classifier-check]').value,
      searchTerms:q('[data-classifier-terms]').value, applicabilityRules:parseRules(q('[data-classifier-rules]').value),
      sourceHints:q('[data-classifier-hints]').value.split('\n').map((x) => x.trim()).filter(Boolean),
      isActive:q('[data-classifier-active]').checked, position:Number(q('[data-classifier-position]').value || 0)
    };
    submit.disabled = true;
    try {
      const result = creating
        ? await request('/api/classifier/criteria', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ ...payload, sectionId:q('[data-classifier-section]').value, code:q('[data-classifier-code]').value }) })
        : await request(`/api/classifier/criteria/${selected.criterion.id}`, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify(payload) });
      await load(result.id);
      switchTab('settings');
      showMessage('Критерий сохранён в базе данных.');
    } catch (error) { showMessage(error.message, true); }
    finally { submit.disabled = false; }
  });
  q('[data-classifier-delete]').addEventListener('click', async () => {
    try {
      await request(`/api/classifier/criteria/${selected.criterion.id}`, { method:'DELETE' });
      await load(selected.criterion.id);
      switchTab('settings');
      showMessage('Критерий отключён.');
    } catch (error) { showMessage(error.message, true); }
  });
  app.addEventListener('click', async (event) => {
    const actionButton = event.target.closest('[data-requirement-action]');
    if (!actionButton) return;
    const card = actionButton.closest('[data-requirement-id]');
    const id = card.dataset.requirementId;
    const action = actionButton.dataset.requirementAction;
    if (action === 'edit' || action === 'cancel-edit') {
      q('[data-requirement-edit]', card).hidden = action === 'cancel-edit';
      if (action === 'edit') q('textarea', card).focus();
      return;
    }
    await changeRequirementLink(id, action);
  });
  app.addEventListener('submit', async (event) => {
    const form = event.target.closest('[data-requirement-edit]');
    if (!form) return;
    event.preventDefault();
    const id = form.closest('[data-requirement-id]').dataset.requirementId;
    const submit = q('[type="submit"]', form);
    submit.disabled = true;
    setStatus('[data-requirement-status]', 'Сохраняем рабочую редакцию…');
    try {
      await request(`/api/requirements/${encodeURIComponent(id)}/revision`, {
        method: 'PUT', headers: { 'Content-Type':'application/json' }, body: JSON.stringify({
          requirement: form.elements.requirement.value,
          basis: form.elements.basis.value,
          version: Number(form.elements.version.value)
        })
      });
      await loadRequirements();
      setStatus('[data-requirement-status]', 'Рабочая редакция сохранена.');
    } catch (error) {
      submit.disabled = false;
      setStatus('[data-requirement-status]', error.code === 'version_conflict' ? 'Требование уже изменено. Обновите список и повторите.' : error.message, true);
    }
  });

  const dialog = q('[data-requirement-dialog]', document);
  const renderSearchResults = (items) => {
    const linkedIds = new Set([...(requirementState.items || []), ...(requirementState.excludedItems || [])].map((item) => item.id));
    q('[data-requirement-search-results]', document).innerHTML = items.length ? items.map((item) => {
      const linked = linkedIds.has(item.id);
      return `<label class="requirement-search-result ${linked ? 'disabled' : ''}"><input type="radio" name="requirementResult" value="${escapeHtml(item.id)}" ${linked ? 'disabled' : ''}><span><strong>${escapeHtml(item.requirement)}</strong><small>${escapeHtml(item.basis)} · ${escapeHtml(item.id)}</small></span></label>`;
    }).join('') : '<div class="requirement-empty">Совпадений не найдено.</div>';
  };
  const searchRequirements = async () => {
    const value = q('[data-requirement-search]', document).value.trim();
    setStatus('[data-requirement-dialog-status]', 'Ищем…');
    try {
      const items = await request(`/api/requirements?search=${encodeURIComponent(value)}&limit=30`);
      renderSearchResults(items);
      setStatus('[data-requirement-dialog-status]', `Найдено: ${items.length}`);
    } catch (error) { setStatus('[data-requirement-dialog-status]', error.message, true); }
  };
  q('[data-requirement-add]').addEventListener('click', () => {
    selectedSearchRequirement = null;
    q('[data-requirement-search]', document).value = '';
    q('[data-requirement-confirm]', document).disabled = true;
    q('[data-requirement-search-results]', document).innerHTML = '';
    dialog.showModal();
    searchRequirements();
    q('[data-requirement-search]', document).focus();
  });
  q('[data-requirement-search]', document).addEventListener('input', () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(searchRequirements, 250);
  });
  q('[data-requirement-search-results]', document).addEventListener('change', (event) => {
    if (event.target.name !== 'requirementResult') return;
    selectedSearchRequirement = event.target.value;
    q('[data-requirement-confirm]', document).disabled = false;
  });
  q('[data-requirement-confirm]', document).addEventListener('click', async () => {
    if (!selectedSearchRequirement) return;
    const button = q('[data-requirement-confirm]', document);
    button.disabled = true;
    setStatus('[data-requirement-dialog-status]', 'Добавляем связь…');
    if (await changeRequirementLink(selectedSearchRequirement, 'include')) dialog.close();
    else button.disabled = false;
  });
  load().catch((error) => { q('[data-classifier-tree]').innerHTML = `<div class="alert alert-danger mb-0">${escapeHtml(error.message)}</div>`; });
})();
