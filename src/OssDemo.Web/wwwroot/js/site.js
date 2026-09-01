(() => {
  const updateFacilityMap = (button) => {
    const mapUrl = button.dataset.facilityMapUrl;
    if (!mapUrl) return;

    const frame = document.querySelector('[data-facility-map-frame]');
    const externalLink = document.querySelector('[data-facility-map-link-target]');
    const cardLink = document.querySelector('[data-facility-card-link-target]');
    const name = document.querySelector('[data-facility-map-name]');
    const address = document.querySelector('[data-facility-map-address]');
    const coordinates = document.querySelector('[data-facility-map-coordinates]');
    const facilityName = button.dataset.facilityName || 'Выбранный объект';

    if (frame) {
      frame.src = mapUrl;
      frame.title = `${facilityName} на карте`;
    }
    if (externalLink) externalLink.href = button.dataset.facilityMapLink || mapUrl;
    if (cardLink) cardLink.href = button.dataset.facilityCardUrl || '/Facilities';
    if (name) name.textContent = facilityName;
    if (address) address.textContent = button.dataset.facilityAddress || '';
    if (coordinates) coordinates.textContent = button.dataset.facilityCoordinates || '';
  };

  const activatePanel = (button) => {
    const group = button.dataset.panelGroup;
    const target = button.dataset.panelTarget;
    if (!group || !target) return;

    document.querySelectorAll(`[data-panel-group="${group}"]`).forEach((item) => {
      item.classList.toggle('active', item === button);
      item.setAttribute('aria-selected', item === button ? 'true' : 'false');
    });

    document.querySelectorAll(`[data-panel-owner="${group}"]`).forEach((panel) => {
      panel.hidden = panel.id !== target;
    });

    updateFacilityMap(button);
  };

  document.querySelectorAll('[data-panel-target]').forEach((button) => {
    button.addEventListener('click', () => activatePanel(button));
  });

  document.querySelectorAll('.facility-carousel').forEach((carousel) => {
    const list = carousel.querySelector('.facility-list');
    const previous = carousel.querySelector('[data-facility-carousel-prev]');
    const next = carousel.querySelector('[data-facility-carousel-next]');
    if (!list) return;

    const scrollByCard = (direction) => {
      const card = list.querySelector('.facility-list-item');
      const gap = Number.parseFloat(getComputedStyle(list).gap) || 0;
      const amount = (card?.getBoundingClientRect().width || 180) + gap;
      list.scrollBy({ left: direction * amount, behavior: 'smooth' });
    };

    previous?.addEventListener('click', () => scrollByCard(-1));
    next?.addEventListener('click', () => scrollByCard(1));
  });

  const wizard = document.querySelector('[data-checklist-wizard]');
  if (wizard) {
    const panels = Array.from(wizard.querySelectorAll('[data-step-panel]'));
    const indicators = Array.from(wizard.querySelectorAll('[data-wizard-indicator]'));
    const wizardProgress = wizard.querySelector('[data-wizard-progress]');
    const wizardStatus = wizard.querySelector('[data-wizard-status]');
    const stepNames = ['Объект', 'ОРД', 'Шаблон', 'Генерация', 'Просмотр'];
    let currentStep = 0;

    const setWizardStep = (step, shouldScroll = true) => {
      currentStep = Math.max(0, Math.min(step, panels.length - 1));
      panels.forEach((panel, index) => {
        const active = index === currentStep;
        panel.hidden = !active;
        panel.classList.toggle('active', active);
      });
      indicators.forEach((indicator, index) => {
        indicator.classList.toggle('active', index === currentStep);
        indicator.classList.toggle('complete', index < currentStep);
      });
      if (wizardProgress) wizardProgress.style.width = `${((currentStep + 1) / panels.length) * 100}%`;
      if (wizardStatus) wizardStatus.textContent = `Этап ${currentStep + 1} из ${panels.length} · ${stepNames[currentStep]}`;
      if (shouldScroll) wizard.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    wizard.querySelectorAll('[data-wizard-next]').forEach((button) => {
      button.addEventListener('click', () => setWizardStep(currentStep + 1));
    });
    wizard.querySelectorAll('[data-wizard-prev]').forEach((button) => {
      button.addEventListener('click', () => setWizardStep(currentStep - 1));
    });

    setWizardStep(0, false);
  }

  const liveChecklistItems = [
    { number: '1', title: 'Наличие утвержденной программы производственного экологического контроля', status: 'Включён', badge: 'text-bg-success', source: 'База знаний + ОРД', note: 'ФЗ-7 ст. 67; программа ПЭК' },
    { number: '8', title: 'Представлены протоколы инструментального контроля выбросов', status: 'Критический', badge: 'text-bg-danger', source: 'Реестр нарушений', note: 'Не устранено в срок по акту 22.09.2024' },
    { number: '17', title: 'Проверить устранение замечания по маркировке места накопления отходов', status: 'Контрольный', badge: 'text-bg-info', source: 'Нарушения + история', note: 'Повторяемость 2+ раза за 5 лет' },
    { number: '19', title: 'Сверить выполнение предыдущего чек-листа по плану корректирующих действий', status: 'Контрольный', badge: 'text-bg-info', source: 'Агент истории', note: 'Перенесено из архива 2025' },
    { number: '23', title: 'Проверить применимость лицензии на пользование недрами для скважины №3', status: 'Требует решения', badge: 'text-bg-warning', source: 'ОРД', note: 'Низкое качество распознавания приложения' },
    { number: '31', title: 'Наличие утвержденного ПНООЛР и соответствие фактических объемов лимитам', status: 'Включён', badge: 'text-bg-success', source: 'База знаний', note: 'ФЗ-89 ст. 11; Приказ №792' }
  ];

  const agentScripts = {
    kb: [
      [18, 'Ищет требования в 7-ФЗ, 89-ФЗ и СТО'],
      [46, 'Сопоставляет фрагменты с классификатором'],
      [78, 'Проверяет применимость к НВОС I'],
      [100, 'Передал нормативные пункты']
    ],
    ord: [
      [16, 'Распознаёт приказ ПЭК и распоряжение'],
      [48, 'Извлекает поручения, сроки и ответственных'],
      [82, 'Помечает неоднозначное приложение к лицензии'],
      [100, 'Передал требования из ОРД']
    ],
    violations: [
      [20, 'Загружает акты и предписания за 5 лет'],
      [54, 'Ищет повторяемость нарушений'],
      [86, 'Выделяет просроченное устранение'],
      [100, 'Передал контрольные и критические пункты']
    ],
    history: [
      [22, 'Открывает архивный чек-лист 2025'],
      [50, 'Сравнивает прошлые пункты с текущим объектом'],
      [84, 'Отбирает применимые корректирующие действия'],
      [100, 'Передал исторические пункты']
    ]
  };

  const setAgentState = (agent, percent, message, done = false) => {
    if (!agent) return;
    const dot = agent.querySelector('.status-dot');
    const progress = agent.querySelector('[data-agent-progress]');
    const status = agent.querySelector('[data-agent-status]');
    agent.classList.toggle('agent-card-active', !done);
    agent.classList.toggle('agent-card-done', done);
    if (dot) dot.className = `status-dot ${done ? 'status-ready' : 'status-info'}`;
    if (progress) progress.style.width = `${percent}%`;
    if (status) status.textContent = message;
  };

  const appendLiveChecklistItem = (container, item) => {
    const row = document.createElement('div');
    row.className = 'live-checklist-item';
    row.innerHTML = `<div class="live-item-number">${item.number}</div><div><strong>${item.title}</strong><div class="small text-muted">${item.note}</div><div class="small muted-note mt-1">Источник: ${item.source}</div></div><span class="badge ${item.badge}">${item.status}</span>`;
    container.appendChild(row);
  };

  document.querySelectorAll('[data-run-generation]').forEach((button) => {
    button.addEventListener('click', () => {
      const templateSelect = document.querySelector('[data-template-select]');
      const templateWarning = document.querySelector('[data-template-warning]');
      if (templateSelect && !templateSelect.value) {
        if (templateWarning) templateWarning.hidden = false;
        templateSelect.focus();
        return;
      }

      if (templateWarning) templateWarning.hidden = true;

      const progress = document.querySelector('[data-generation-progress]');
      const status = document.querySelector('[data-generation-status]');
      const result = document.querySelector('[data-generation-result]');
      const liveChecklist = document.querySelector('[data-live-checklist]');
      const liveEmpty = document.querySelector('[data-live-empty]');
      const liveCount = document.querySelector('[data-live-count]');
      const nextButton = document.querySelector('[data-generation-next]');
      if (!progress || !status || !result) return;

      button.disabled = true;
      result.hidden = true;
      if (nextButton) nextButton.disabled = true;
      progress.style.width = '0';
      status.textContent = 'Запускаем параллельных агентов...';

      if (liveChecklist) liveChecklist.replaceChildren();
      if (liveEmpty && liveChecklist) liveChecklist.appendChild(liveEmpty);
      if (liveEmpty) liveEmpty.hidden = false;
      if (liveCount) liveCount.textContent = '0 пунктов';

      document.querySelectorAll('[data-agent]').forEach((agent) => {
        agent.classList.remove('agent-card-active', 'agent-card-done');
        const dot = agent.querySelector('.status-dot');
        const agentProgress = agent.querySelector('[data-agent-progress]');
        const agentStatus = agent.querySelector('[data-agent-status]');
        if (dot) dot.className = 'status-dot status-muted';
        if (agentProgress) agentProgress.style.width = '0';
        if (agentStatus) agentStatus.textContent = 'Ожидает задачи.';
      });

      let overall = 0;
      const overallTimer = setInterval(() => {
        overall = Math.min(overall + 4, 96);
        progress.style.width = `${overall}%`;
      }, 150);

      Object.entries(agentScripts).forEach(([name, script], agentIndex) => {
        const agent = document.querySelector(`[data-agent="${name}"]`);
        script.forEach(([percent, message], scriptIndex) => {
          window.setTimeout(() => {
            setAgentState(agent, percent, message, percent === 100);
          }, 220 + agentIndex * 120 + scriptIndex * 620);
        });
      });

      liveChecklistItems.forEach((item, index) => {
        window.setTimeout(() => {
          if (!liveChecklist) return;
          if (liveEmpty) liveEmpty.hidden = true;
          appendLiveChecklistItem(liveChecklist, item);
          if (liveCount) liveCount.textContent = `${index + 1} из 42 пунктов`;
          status.textContent = `Добавлен пункт №${item.number}: ${item.source}`;
        }, 900 + index * 520);
      });

      window.setTimeout(() => {
        clearInterval(overallTimer);
        progress.style.width = '100%';
        status.textContent = 'Проект чек-листа сформирован: источники объединены и статусы рассчитаны.';
        result.hidden = false;
        button.disabled = false;
        if (liveCount) liveCount.textContent = '42 пункта';
        if (nextButton) nextButton.disabled = false;
      }, 4600);
    });
  });

  document.querySelectorAll('[data-template-select]').forEach((select) => {
    select.addEventListener('change', () => {
      const warning = document.querySelector('[data-template-warning]');
      if (warning) warning.hidden = Boolean(select.value);
    });
  });

  document.querySelectorAll('[data-resolve-item]').forEach((button) => {
    button.addEventListener('click', () => {
      const target = document.querySelector(button.dataset.resolveItem);
      if (!target) return;
      target.textContent = 'Принят инспектором';
      target.className = 'badge text-bg-success';
      button.textContent = 'Решение зафиксировано';
      button.classList.remove('btn-warning');
      button.classList.add('btn-outline-success');
      button.disabled = true;
    });
  });

  const updateChecklistState = () => {
    const unresolved = document.querySelectorAll('[data-decision-row]:not([data-resolved="true"])').length;
    const decisionCount = document.querySelector('[data-decision-count]');
    const warning = document.querySelector('[data-confirm-warning]');
    if (decisionCount) decisionCount.textContent = unresolved.toString();
    if (warning) warning.hidden = unresolved === 0;
    return unresolved;
  };

  const appendAudit = (title, text) => {
    const audit = document.querySelector('[data-local-audit]');
    if (!audit) return;
    const item = document.createElement('div');
    item.className = 'timeline-item';
    item.innerHTML = `<strong>${title}</strong><div class="small text-muted">${text}</div>`;
    audit.appendChild(item);
  };

  document.querySelectorAll('[data-resolve-action]').forEach((button) => {
    button.addEventListener('click', () => {
      const row = button.closest('[data-decision-row]');
      const status = row?.querySelector('#decision-status');
      const note = row?.querySelector('[data-decision-note]');
      if (!row || !status) return;

      const action = button.dataset.resolveAction;
      row.dataset.resolved = 'true';
      row.classList.remove('checklist-row-decision');

      if (action === 'exclude') {
        row.classList.add('text-muted');
        status.textContent = 'Исключён инспектором';
        status.className = 'badge text-bg-secondary';
        if (note) note.textContent = 'Исключено решением инспектора; в экспорт не включается';
        appendAudit('Жёлтый пункт исключён', 'Инспектор снял неоднозначное требование по лицензии.');
      } else if (action === 'edit') {
        row.classList.add('checklist-row-manual');
        const titleCell = row.children[1];
        titleCell.textContent = `${titleCell.textContent} — формулировка уточнена инспектором`;
        status.textContent = 'Включён';
        status.className = 'badge text-bg-success';
        if (note) note.textContent = 'Формулировка изменена и решение зафиксировано';
        appendAudit('Жёлтый пункт изменён', 'Инспектор уточнил формулировку и включил пункт.');
      } else {
        status.textContent = 'Включён';
        status.className = 'badge text-bg-success';
        if (note) note.textContent = 'Включено решением инспектора';
        appendAudit('Жёлтый пункт включён', 'Решение инспектора зафиксировано, пункт стал зелёным.');
      }

      row.querySelectorAll('[data-resolve-action]').forEach((item) => {
        item.disabled = true;
      });
      updateChecklistState();
    });
  });

  document.querySelectorAll('[data-add-manual-item]').forEach((button) => {
    button.addEventListener('click', () => {
      const body = document.querySelector('[data-checklist-body]');
      const total = document.querySelector('[data-total-count]');
      const title = document.querySelector('[data-manual-title]')?.value.trim();
      const section = document.querySelector('[data-manual-section]')?.value.trim();
      const basis = document.querySelector('[data-manual-basis]')?.value.trim();
      if (!body || !title || !section || !basis) return;

      const row = document.createElement('tr');
      row.className = 'checklist-row-manual';
      row.innerHTML = `<td>43</td><td>${title}</td><td>${basis}</td><td></td><td>Добавлено инспектором, дублей не найдено</td><td><span class="classifier-chip">${section}</span></td><td><span class="badge text-bg-success">Включён</span></td><td>Добавлено инспектором · ручной пункт</td><td></td>`;
      body.appendChild(row);
      if (total) total.textContent = '43';
      button.disabled = true;
      button.textContent = 'Добавлено';
      appendAudit('Ручной пункт добавлен', 'Система проверила дубли и записала источник «Добавлено инспектором».');
    });
  });

  document.querySelectorAll('[data-confirm-checklist]').forEach((button) => {
    button.addEventListener('click', () => {
      const unresolved = updateChecklistState();
      const warning = document.querySelector('[data-confirm-warning]');
      const success = document.querySelector('[data-confirm-success]');
      const status = document.querySelector('[data-checklist-status]');
      if (unresolved > 0) {
        if (warning) {
          warning.hidden = false;
          warning.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
        appendAudit('Подтверждение заблокировано', 'Остались неразрешённые жёлтые пункты.');
        return;
      }

      if (warning) warning.hidden = true;
      if (success) success.hidden = false;
      if (status) {
        status.textContent = 'Готов';
        status.className = 'badge text-bg-success align-self-center';
      }
      document.querySelectorAll('[data-export-link]').forEach((link) => {
        link.classList.remove('disabled');
        link.setAttribute('aria-disabled', 'false');
      });
      button.textContent = 'Чек-лист подтверждён';
      button.disabled = true;
      appendAudit('Чек-лист подтверждён', 'Статус «Готов», экспорт PDF/DOCX/XLSX открыт.');
    });
  });

  updateChecklistState();

  document.querySelectorAll('[data-show-target]').forEach((button) => {
    button.addEventListener('click', () => {
      const target = document.querySelector(button.dataset.showTarget);
      if (!target) return;
      target.hidden = !target.hidden;
    });
  });

  const renderClassifierChips = (container, values, chipClass) => {
    if (!container) return;
    container.replaceChildren();
    values.filter(Boolean).forEach((value) => {
      const chip = document.createElement('span');
      chip.className = chipClass;
      chip.textContent = value;
      container.appendChild(chip);
    });
  };

  document.querySelectorAll('[data-classifier-leaf]').forEach((button) => {
    button.addEventListener('click', () => {
      document.querySelectorAll('[data-classifier-leaf]').forEach((item) => {
        item.classList.toggle('active', item === button);
      });

      const title = document.querySelector('[data-classifier-title]');
      const path = document.querySelector('[data-classifier-path]');
      const description = document.querySelector('[data-classifier-description]');
      if (title) title.textContent = button.dataset.cardTitle || '';
      if (path) path.textContent = button.dataset.cardPath || '';
      if (description) description.textContent = button.dataset.cardDescription || '';

      renderClassifierChips(
        document.querySelector('[data-classifier-sources]'),
        (button.dataset.cardSources || '').split('|'),
        'source-chip'
      );
      renderClassifierChips(
        document.querySelector('[data-classifier-links]'),
        (button.dataset.cardLinks || '').split('|'),
        'classifier-chip'
      );
    });
  });

  document.querySelectorAll('[data-tree-expand]').forEach((button) => {
    button.addEventListener('click', () => {
      document.querySelectorAll('.classifier-tree details').forEach((node) => {
        node.open = true;
      });
    });
  });

  document.querySelectorAll('[data-tree-collapse]').forEach((button) => {
    button.addEventListener('click', () => {
      document.querySelectorAll('.classifier-tree details').forEach((node) => {
        node.open = false;
      });
    });
  });

  document.querySelectorAll('[data-file-action]').forEach((button) => {
    button.addEventListener('click', () => {
      const originalText = button.textContent;
      button.textContent = 'Файл подготовлен';
      button.classList.add('btn-success');
      setTimeout(() => {
        button.textContent = originalText;
        button.classList.remove('btn-success');
      }, 1600);
    });
  });

  document.querySelectorAll('[data-export-link]').forEach((link) => {
    link.addEventListener('click', (event) => {
      if (link.classList.contains('disabled')) {
        event.preventDefault();
        return;
      }
      appendAudit('Экспорт файла', `${link.textContent.trim()} подготовлен для скачивания.`);
    });
  });

  const normalizeMarkdown = (value) => {
    const lines = String(value ?? '').replace(/\r\n/g, '\n').split('\n');
    while (lines.length && !lines[0].trim()) lines.shift();
    while (lines.length && !lines[lines.length - 1].trim()) lines.pop();

    const indents = lines
      .filter((line) => line.trim())
      .map((line) => line.match(/^\s*/)?.[0].length ?? 0);
    const minIndent = indents.length ? Math.min(...indents) : 0;

    return lines.map((line) => line.slice(minIndent)).join('\n');
  };

  const renderMarkdown = (container, markdown) => {
    if (!container) return;

    const source = normalizeMarkdown(markdown);
    container.dataset.markdownRaw = source;

    if (window.marked && window.DOMPurify) {
      window.marked.setOptions({ breaks: true, gfm: true });
      const html = typeof window.marked.parse === 'function'
        ? window.marked.parse(source)
        : window.marked(source);
      container.innerHTML = window.DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
      return;
    }

    container.textContent = source;
  };

  const scrollChatToBottom = (thread) => {
    if (!thread) return;
    thread.scrollTop = thread.scrollHeight;
  };

  const renderSources = (container, sources = []) => {
    if (!container) return;

    container.replaceChildren();
    sources.filter(Boolean).forEach((source) => {
      const item = typeof source === 'string' ? { title: source } : source;
      const chip = item.url ? document.createElement('a') : document.createElement('span');
      chip.className = item.kind === 'classifier' ? 'classifier-chip' : 'source-chip';
      chip.textContent = item.title || item.name || item.label || '';

      if (item.url) {
        chip.href = item.url;
        chip.rel = 'noopener noreferrer';
        chip.target = '_blank';
      }

      if (chip.textContent) container.appendChild(chip);
    });

    container.hidden = !container.children.length;
  };

  const appendChatMessage = (thread, template, options) => {
    const message = template.content.firstElementChild.cloneNode(true);
    message.dataset.role = options.role;
    message.classList.add(options.role === 'user' ? 'chat-message-user' : 'chat-message-assistant');

    const avatar = message.querySelector('[data-chat-avatar]');
    const author = message.querySelector('[data-chat-author]');
    const markdown = message.querySelector('[data-chat-markdown]');
    const sources = message.querySelector('[data-ai-sources]');

    if (avatar) avatar.textContent = options.role === 'user' ? 'И' : 'AI';
    if (author) author.textContent = options.role === 'user' ? 'Инспектор' : 'ИИ-консультант';

    if (options.pending) {
      message.classList.add('chat-message-pending');
      if (markdown) {
        markdown.innerHTML = '<span class="chat-typing" aria-label="ИИ-консультант готовит ответ"><span></span><span></span><span></span></span>';
      }
    } else {
      renderMarkdown(markdown, options.markdown);
      renderSources(sources, options.sources);
    }

    thread.appendChild(message);
    scrollChatToBottom(thread);
    return message;
  };

  const updateChatMessage = (message, markdown, sources) => {
    message.classList.remove('chat-message-pending');
    renderMarkdown(message.querySelector('[data-chat-markdown]'), markdown);
    renderSources(message.querySelector('[data-ai-sources]'), sources);
  };

  const buildDemoAiReply = (question) => {
    const normalizedQuestion = question.toLowerCase();

    if (normalizedQuestion.includes('крит')) {
      return {
        answer: `Пункт относится к **критическим**, потому что есть незакрытое нарушение с истекшим сроком устранения.

| Проверка | Значение |
| --- | --- |
| Нарушение | протоколы инструментального контроля выбросов |
| Срок устранения | 10.10.2024 |
| Статус | не закрыто |

Рекомендуемое действие инспектора:

1. открыть карточку нарушения;
2. проверить актуальность протоколов;
3. зафиксировать источник в чек-листе перед экспортом.`,
        sources: [
          'Акт 22.09.2024',
          'СТО 16-005-2025 п. 4.2',
          { title: '2.3 Атмосфера', kind: 'classifier' }
        ]
      };
    }

    if (normalizedQuestion.includes('не найден') || normalizedQuestion.includes('санитар')) {
      return {
        answer: `В базе знаний нет подтверждённого источника по этому вопросу.

> Рабочий режим чата не должен формировать нормативный вывод без проверяемой цитаты.

Что можно сделать:

- уточнить формулировку вопроса;
- выбрать другой объект или период проверки;
- передать вопрос методологу.`,
        sources: [{ title: 'источник не найден', kind: 'classifier' }]
      };
    }

    return {
      answer: `Я подготовил черновой ответ по вопросу: **${question}**.

Для реальной интеграции подключите серверный обработчик к форме через атрибут \`data-ai-endpoint\`. Клиент уже ожидает JSON-ответ вида:

\`\`\`json
{
  "answer": "Markdown-ответ модели",
  "sources": ["Название источника", { "title": "Раздел", "kind": "classifier" }]
}
\`\`\`

До подключения API используется демонстрационный ответ с безопасным Markdown-рендерингом.`,
      sources: ['База знаний', { title: 'требует проверки', kind: 'classifier' }]
    };
  };

  const collectConversation = (thread) => Array.from(thread.querySelectorAll('[data-chat-message]')).map((message) => ({
    role: message.dataset.role || 'assistant',
    content: message.querySelector('[data-chat-markdown]')?.dataset.markdownRaw || ''
  })).filter((item) => item.content);

  const requestAiReply = async (form, thread, question) => {
    const endpoint = form.dataset.aiEndpoint;
    if (!endpoint) return buildDemoAiReply(question);

    const response = await fetch(endpoint, {
      method: 'POST',
      headers: {
        'Accept': 'application/json',
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        message: question,
        conversation: collectConversation(thread)
      })
    });

    if (!response.ok) {
      throw new Error(`AI endpoint failed: ${response.status}`);
    }

    const payload = await response.json();
    return {
      answer: payload.answer || payload.message || payload.content || 'Ответ получен, но поле answer пустое.',
      sources: Array.isArray(payload.sources) ? payload.sources : []
    };
  };

  const resizeChatInput = (textarea) => {
    textarea.style.height = 'auto';
    textarea.style.height = `${Math.min(textarea.scrollHeight, 160)}px`;
  };

  document.querySelectorAll('[data-chat-markdown]').forEach((container) => {
    if (!container.dataset.markdownRaw) renderMarkdown(container, container.textContent);
  });

  document.querySelectorAll('[data-ai-question]').forEach((form) => {
    const input = form.querySelector('[data-chat-input]');
    const sendButton = form.querySelector('[data-chat-send]');

    input?.addEventListener('input', () => resizeChatInput(input));
    input?.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
        event.preventDefault();
        form.requestSubmit();
      }
    });

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      const chatShell = form.closest('.chat-shell') || document;
      const thread = chatShell.querySelector('[data-chat-thread]');
      const messageTemplate = document.querySelector('[data-chat-message-template]');
      if (!thread || !messageTemplate || !input || !sendButton) return;

      const question = input.value.trim();
      if (!question) return;

      input.value = '';
      resizeChatInput(input);
      input.disabled = true;
      sendButton.disabled = true;

      appendChatMessage(thread, messageTemplate, { role: 'user', markdown: question });
      const pendingMessage = appendChatMessage(thread, messageTemplate, { role: 'assistant', pending: true });

      try {
        const reply = await requestAiReply(form, thread, question);
        updateChatMessage(pendingMessage, reply.answer, reply.sources);
      } catch (error) {
        updateChatMessage(
          pendingMessage,
          'Не удалось получить ответ от сервера ИИ. Проверьте подключение и повторите запрос.\n\n```text\n' + error.message + '\n```',
          [{ title: 'ошибка подключения', kind: 'classifier' }]
        );
      } finally {
        input.disabled = false;
        sendButton.disabled = false;
        input.focus();
        scrollChatToBottom(thread);
      }
    });
  });
})();
