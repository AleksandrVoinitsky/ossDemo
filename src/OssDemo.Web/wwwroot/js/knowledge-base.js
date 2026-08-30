(function () {
    const root = document.querySelector('[data-knowledge-workspace]');
    if (!root) return;

    const items = root.querySelector('[data-workspace-items]');
    const breadcrumb = root.querySelector('[data-workspace-breadcrumb]');
    const empty = root.querySelector('[data-workspace-empty]');
    const search = root.querySelector('[data-workspace-search]');
    const upButton = root.querySelector('[data-workspace-up]');
    const modalElement = document.getElementById('knowledgeDocumentModal');
    const documentTitle = root.querySelector('[data-document-title]');
    const documentPath = root.querySelector('[data-document-path]');
    const documentUpdated = root.querySelector('[data-document-updated]');
    const documentContent = root.querySelector('[data-document-content]');
    const printButton = root.querySelector('[data-print-document]');
    const uploadForm = root.querySelector('[data-knowledge-upload]');
    const uploadFile = root.querySelector('[data-upload-file]');
    const uploadSubmit = root.querySelector('[data-upload-submit]');
    const uploadStatus = root.querySelector('[data-upload-status]');
    const documentModal = modalElement && window.bootstrap ? new bootstrap.Modal(modalElement) : null;

    let workspace = { name: 'База знаний', type: 'folder', children: [] };
    let current = workspace;
    let path = [];
    let viewMode = 'grid';

    function escapeHtml(value) {
        return String(value || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function icon(node) {
        return node.type === 'folder'
            ? '<span class="knowledge-folder-icon" aria-hidden="true"><svg viewBox="0 0 64 48" focusable="false"><path class="folder-tab" d="M4 10a6 6 0 0 1 6-6h15l6 7h23a6 6 0 0 1 6 6v4H4V10Z"/><path class="folder-body" d="M4 17h56v21a6 6 0 0 1-6 6H10a6 6 0 0 1-6-6V17Z"/></svg></span>'
            : '<span class="knowledge-file-icon" aria-hidden="true"><svg viewBox="0 0 48 56" focusable="false"><path class="file-sheet" d="M10 3h20l10 10v40H10V3Z"/><path class="file-corner" d="M30 3v12h10"/><path class="file-line" d="M17 25h14M17 32h14M17 39h10"/></svg></span>';
    }

    function childrenOf(node) {
        return (node.children || []).slice().sort((a, b) => a.name.localeCompare(b.name, 'ru'));
    }

    function formatDate(value) {
        return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value));
    }

    function currentPathText(fileName) {
        const names = ['База знаний'].concat(path.map(part => part.name));
        if (fileName) names.push(fileName);
        return names.join(' / ');
    }

    function openFolder(folder, nextPath) {
        current = folder;
        path = nextPath || [];
        search.value = '';
        render();
    }

    function renderBreadcrumb() {
        breadcrumb.innerHTML = '';
        const parts = [{ name: 'База знаний', node: workspace }].concat(path);
        parts.forEach((part, index) => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'knowledge-crumb' + (index === parts.length - 1 ? ' current' : '');
            button.textContent = part.name;
            button.addEventListener('click', () => openFolder(part.node, index === 0 ? [] : path.slice(0, index)));
            breadcrumb.appendChild(button);
            if (index < parts.length - 1) {
                const separator = document.createElement('span');
                separator.className = 'knowledge-crumb-separator';
                separator.textContent = '›';
                breadcrumb.appendChild(separator);
            }
        });
    }

    function renderItems() {
        const query = (search.value || '').trim().toLocaleLowerCase('ru');
        const visible = childrenOf(current).filter(node => !query || node.name.toLocaleLowerCase('ru').includes(query));
        items.classList.toggle('knowledge-items-grid', viewMode === 'grid');
        items.classList.toggle('knowledge-items-list', viewMode === 'list');
        items.innerHTML = visible.map((node, index) => {
            const meta = 'Markdown · ' + formatDate(node.updatedAt) + ' · ' + node.chunkCount + ' фр.';
            return '<button type="button" class="knowledge-item" data-item-index="' + index + '">'
                + icon(node)
                + '<span class="knowledge-item-main"><strong>' + escapeHtml(node.name) + '</strong><span class="muted-note small">' + escapeHtml(meta) + '</span></span>'
                + '</button>';
        }).join('');
        empty.classList.toggle('d-none', visible.length > 0);
        items.querySelectorAll('[data-item-index]').forEach(button => button.addEventListener('click', () => openFile(visible[Number(button.dataset.itemIndex)])));
    }

    function render() {
        upButton.disabled = path.length === 0;
        renderBreadcrumb();
        renderItems();
    }

    async function openFile(file) {
        documentTitle.textContent = file.name;
        documentPath.textContent = currentPathText(file.name);
        documentUpdated.textContent = 'Загрузка Markdown…';
        documentContent.textContent = '';
        if (documentModal) documentModal.show();
        try {
            const response = await fetch('/api/knowledge/documents/' + encodeURIComponent(file.id));
            if (!response.ok) throw new Error('Не удалось получить документ.');
            const document = await response.json();
            documentTitle.textContent = document.title;
            documentUpdated.textContent = 'Изменён ' + formatDate(document.updatedAt) + ' · ' + document.chunkCount + ' фрагментов';
            documentContent.innerHTML = markdown(document.markdown);
        } catch (error) {
            documentUpdated.textContent = 'Документ временно недоступен';
            documentContent.textContent = error.message || 'Не удалось получить Markdown.';
        }
    }

    function markdown(text) {
        return String(text || '').split('\n').map(line => {
            const safe = escapeHtml(line);
            if (line.startsWith('# ')) return '<h3>' + escapeHtml(line.slice(2)) + '</h3>';
            if (line.startsWith('## ')) return '<h4>' + escapeHtml(line.slice(3)) + '</h4>';
            if (line.startsWith('### ')) return '<h5>' + escapeHtml(line.slice(4)) + '</h5>';
            if (line.startsWith('> ')) return '<blockquote>' + escapeHtml(line.slice(2)) + '</blockquote>';
            return safe ? '<p>' + safe + '</p>' : '';
        }).join('');
    }

    function setUploadStatus(message, isError) {
        uploadStatus.textContent = message;
        uploadStatus.classList.toggle('d-none', !message);
        uploadStatus.classList.toggle('is-error', Boolean(isError));
    }

    async function loadDocuments() {
        const response = await fetch('/api/knowledge/documents');
        if (!response.ok) throw new Error('Не удалось загрузить список документов.');
        const documents = await response.json();
        workspace = {
            name: 'База знаний',
            type: 'folder',
            children: documents.map(document => ({
                id: document.id,
                name: document.title + '.md',
                updatedAt: document.updatedAt,
                chunkCount: document.chunkCount,
                type: 'file'
            }))
        };
        current = workspace;
        path = [];
        render();
    }

    search.addEventListener('input', renderItems);
    upButton.addEventListener('click', () => {
        if (path.length === 0) return;
        const nextPath = path.slice(0, -1);
        openFolder(nextPath.length === 0 ? workspace : nextPath[nextPath.length - 1].node, nextPath);
    });
    root.querySelectorAll('[data-view-mode]').forEach(button => button.addEventListener('click', () => {
        viewMode = button.dataset.viewMode;
        root.querySelectorAll('[data-view-mode]').forEach(item => item.classList.toggle('active', item === button));
        renderItems();
    }));
    printButton.addEventListener('click', () => window.print());
    uploadForm.addEventListener('submit', async event => {
        event.preventDefault();
        const file = uploadFile.files[0];
        if (!file) {
            setUploadStatus('Выберите файл PDF, DOCX или XLSX.', true);
            return;
        }
        uploadSubmit.disabled = true;
        setUploadStatus('Docling преобразует документ и добавляет его в поиск…', false);
        try {
            const formData = new FormData();
            formData.append('file', file);
            const response = await fetch('/api/knowledge/documents', { method: 'POST', body: formData });
            if (!response.ok) {
                const problem = await response.json().catch(() => null);
                throw new Error(problem && problem.title ? problem.title : 'Не удалось обработать документ.');
            }
            const document = await response.json();
            uploadForm.reset();
            setUploadStatus('Документ «' + document.title + '» преобразован в Markdown и проиндексирован.', false);
            await loadDocuments();
        } catch (error) {
            setUploadStatus(error.message || 'Не удалось обработать документ.', true);
        } finally {
            uploadSubmit.disabled = false;
        }
    });

    loadDocuments().catch(error => {
        setUploadStatus(error.message || 'Не удалось загрузить базу знаний.', true);
        render();
    });
})();
