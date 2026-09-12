(() => {
  const app = document.querySelector('[data-classifier-app]');
  if (!app) return;
  const q = (selector) => app.querySelector(selector);
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' })[c]);
  let tree = null;
  let selected = null;
  let creating = false;

  const request = async (url, options = {}) => {
    const response = await fetch(url, options);
    const body = response.status === 204 ? null : await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body?.error || 'Не удалось выполнить операцию.');
    return body;
  };
  const showMessage = (text, danger = false) => {
    const node = q('[data-classifier-message]');
    node.className = `alert mt-3 ${danger ? 'alert-danger' : 'alert-success'}`;
    node.textContent = text; node.hidden = false;
  };
  const ruleText = (rules) => (rules || []).map((rule) => `${rule.field} | ${rule.operator} | ${(rule.values || []).join(', ')}`).join('\n');
  const parseRules = (value) => value.split('\n').map((line) => line.trim()).filter(Boolean).map((line) => {
    const [field = '', operator = '', values = ''] = line.split('|').map((part) => part.trim());
    return { field, operator, values: values.split(',').map((part) => part.trim()).filter(Boolean) };
  });
  const allCriteria = () => tree.sections.flatMap((section) => section.criteria.map((criterion) => ({ section, criterion })));

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

  const fill = (entry, isNew = false) => {
    creating = isNew; selected = entry;
    q('[data-classifier-empty]').hidden = true; q('[data-classifier-form]').hidden = false;
    q('[data-classifier-section]').innerHTML = tree.sections.map((section) => `<option value="${section.id}">${escapeHtml(section.code)} · ${escapeHtml(section.title)}</option>`).join('');
    q('[data-classifier-section]').value = entry.section.id;
    q('[data-classifier-section]').disabled = !isNew; q('[data-classifier-code]').disabled = !isNew;
    const c = entry.criterion;
    q('[data-classifier-code]').value = c.code || '';
    q('[data-classifier-card-code]').textContent = isNew ? 'Новый критерий' : c.code;
    q('[data-classifier-card-title]').textContent = isNew ? 'Создание критерия' : entry.section.title;
    q('[data-classifier-risk]').value = c.riskText || '';
    q('[data-classifier-check]').value = c.checkText || '';
    q('[data-classifier-terms]').value = c.searchTerms || '';
    q('[data-classifier-rules]').value = ruleText(c.applicabilityRules);
    q('[data-classifier-hints]').value = (c.sourceHints || []).join('\n');
    q('[data-classifier-position]').value = c.position ?? entry.section.criteria.length;
    q('[data-classifier-active]').checked = c.isActive ?? true;
    q('[data-classifier-usage]').textContent = c.historyUsageCount || 0;
    q('[data-classifier-sources]').textContent = (c.recentSources || []).join('; ') || 'Будут собраны после запусков';
    q('[data-classifier-delete]').hidden = isNew; q('[data-classifier-message]').hidden = true;
    renderTree();
  };

  const load = async (retainId) => {
    tree = await request('/api/classifier/');
    document.querySelector('[data-classifier-version]').textContent = `v${tree.version}`;
    document.querySelector('[data-classifier-section-count]').textContent = tree.sections.length;
    document.querySelector('[data-classifier-criterion-count]').textContent = tree.sections.reduce((sum, section) => sum + section.criteria.length, 0);
    const entry = allCriteria().find((item) => item.criterion.id === retainId) || allCriteria()[0];
    renderTree(); if (entry) fill(entry);
  };

  q('[data-classifier-tree]').addEventListener('click', (event) => {
    const button = event.target.closest('[data-criterion-id]'); if (!button) return;
    const entry = allCriteria().find((item) => item.criterion.id === button.dataset.criterionId); if (entry) fill(entry);
  });
  q('[data-classifier-search]').addEventListener('input', renderTree);
  q('[data-classifier-create]').addEventListener('click', () => {
    const section = selected?.section || tree.sections[0];
    fill({ section, criterion: { code:'',riskText:'',checkText:'',searchTerms:'',applicabilityRules:[],sourceHints:[],isActive:true,position:section.criteria.length } }, true);
  });
  q('[data-classifier-form]').addEventListener('submit', async (event) => {
    event.preventDefault();
    const payload = {
      riskText:q('[data-classifier-risk]').value, checkText:q('[data-classifier-check]').value,
      searchTerms:q('[data-classifier-terms]').value, applicabilityRules:parseRules(q('[data-classifier-rules]').value),
      sourceHints:q('[data-classifier-hints]').value.split('\n').map((x) => x.trim()).filter(Boolean),
      isActive:q('[data-classifier-active]').checked, position:Number(q('[data-classifier-position]').value || 0)
    };
    try {
      const result = creating
        ? await request('/api/classifier/criteria', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ ...payload, sectionId:q('[data-classifier-section]').value, code:q('[data-classifier-code]').value }) })
        : await request(`/api/classifier/criteria/${selected.criterion.id}`, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify(payload) });
      await load(result.id); showMessage('Критерий сохранён в базе данных.');
    } catch (error) { showMessage(error.message, true); }
  });
  q('[data-classifier-delete]').addEventListener('click', async () => {
    try { await request(`/api/classifier/criteria/${selected.criterion.id}`, { method:'DELETE' }); await load(selected.criterion.id); showMessage('Критерий отключён.'); }
    catch (error) { showMessage(error.message, true); }
  });
  load().catch((error) => { q('[data-classifier-tree]').innerHTML = `<div class="alert alert-danger mb-0">${escapeHtml(error.message)}</div>`; });
})();
