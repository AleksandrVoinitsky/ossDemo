(() => {
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>'"]/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
  const catalogBody = document.querySelector('[data-checklist-catalog]');
  const templateSelect = document.querySelector('[data-checklist-templates]');

  const getCatalog = async () => {
    const response = await fetch('/api/checklists/catalog');
    if (!response.ok) throw new Error('Не удалось загрузить каталог чек-листов.');
    return response.json();
  };

  const openSource = async (id) => {
    const response = await fetch(`/api/checklists/sources/${encodeURIComponent(id)}`);
    if (!response.ok) throw new Error('Не удалось открыть файл чек-листа.');
    const source = await response.json();
    document.querySelector('[data-source-title]').textContent = source.catalog.title;
    document.querySelector('[data-source-meta]').textContent = `${source.catalog.facility} · ${source.catalog.period} · ${source.items.length} пунктов`;
    document.querySelector('[data-source-items]').innerHTML = source.items.map((item) => `<tr><td>${item.number}</td><td>${escapeHtml(item.section)}</td><td>${escapeHtml(item.title)}</td><td>${escapeHtml(item.basis)}</td><td>${escapeHtml(item.result || '—')}</td></tr>`).join('');
    bootstrap.Modal.getOrCreateInstance(document.getElementById('checklistSourceModal')).show();
  };

  getCatalog().then((catalog) => {
    if (templateSelect) {
      const templates = catalog.filter((item) => item.kind === 'template');
      templateSelect.innerHTML = '<option value="">Не выбран — генерация будет заблокирована</option>' + templates.map((item, index) => `<option value="${escapeHtml(item.id)}" ${index === 0 ? 'selected' : ''}>${escapeHtml(item.facility)} · ${item.year} · ${escapeHtml(item.title)}</option>`).join('');
    }
    if (catalogBody) {
      catalogBody.innerHTML = catalog.map((item) => `<tr><td><strong>${escapeHtml(item.facility)}</strong></td><td>${escapeHtml(item.period)}</td><td><span class="badge text-bg-${item.kind === 'template' ? 'primary' : 'secondary'}">${item.kind === 'template' ? 'Шаблон' : 'Архив'}</span></td><td data-catalog-count="${escapeHtml(item.id)}">—</td><td><button class="btn btn-sm btn-outline-primary" data-open-checklist-source="${escapeHtml(item.id)}">Открыть</button></td></tr>`).join('');
      catalogBody.querySelectorAll('[data-open-checklist-source]').forEach((button) => button.addEventListener('click', () => openSource(button.dataset.openChecklistSource).catch(() => { })));
      catalog.forEach((item) => fetch(`/api/checklists/sources/${encodeURIComponent(item.id)}`).then((response) => response.ok ? response.json() : null).then((source) => {
        const cell = catalogBody.querySelector(`[data-catalog-count="${CSS.escape(item.id)}"]`);
        if (cell && source) cell.textContent = source.items.length;
      }));
    }
  }).catch(() => {
    if (catalogBody) catalogBody.innerHTML = '<tr><td colspan="5" class="text-danger">Не удалось загрузить каталог чек-листов.</td></tr>';
  });

  const checklistId = new URLSearchParams(window.location.search).get('id');
  const workingBody = document.querySelector('[data-working-checklist-body]');
  const renderWorkingChecklist = (checklist) => {
    if (!workingBody) return;
    document.querySelector('[data-working-title]').textContent = checklist.name;
    document.querySelector('[data-working-meta]').textContent = `${checklist.facility} · создан ${new Date(checklist.createdAt).toLocaleString('ru-RU')}`;
    document.querySelector('[data-working-status]').textContent = checklist.status === 'draft' ? 'Черновик сохранён' : checklist.status;
    workingBody.innerHTML = checklist.items.map((item) => `<tr class="${item.origin === 'manual' ? 'checklist-row-manual' : ''}"><td>${item.number}</td><td>${escapeHtml(item.section)}</td><td>${escapeHtml(item.title)}</td><td>${escapeHtml(item.basis)}</td><td>${escapeHtml(item.result || '—')}</td><td>${escapeHtml(item.note || '—')}</td><td>${item.origin === 'manual' ? 'Добавлено инспектором' : 'Шаблон'}</td></tr>`).join('');
  };

  if (workingBody) {
    if (!checklistId) workingBody.innerHTML = '<tr><td colspan="7" class="text-danger">Не указан рабочий чек-лист. Создайте его на странице формирования.</td></tr>';
    else fetch(`/api/checklists/${encodeURIComponent(checklistId)}`).then((response) => response.ok ? response.json() : Promise.reject()).then(renderWorkingChecklist).catch(() => { workingBody.innerHTML = '<tr><td colspan="7" class="text-danger">Рабочий чек-лист не найден.</td></tr>'; });
  }

  document.querySelector('[data-save-manual-item]')?.addEventListener('click', async () => {
    const title = document.querySelector('[data-manual-title]').value.trim();
    const basis = document.querySelector('[data-manual-basis]').value.trim();
    if (!checklistId || !title || !basis) return;
    const response = await fetch(`/api/checklists/${encodeURIComponent(checklistId)}/items`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title, basis, section: document.querySelector('[data-manual-section]').value, note: document.querySelector('[data-manual-note]').value }) });
    if (!response.ok) return;
    renderWorkingChecklist(await response.json());
    document.querySelector('[data-manual-title]').value = '';
    document.querySelector('[data-manual-basis]').value = '';
  });
})();
