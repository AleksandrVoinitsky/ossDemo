(() => {
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
  };

  document.querySelectorAll('[data-panel-target]').forEach((button) => {
    button.addEventListener('click', () => activatePanel(button));
  });

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
      if (!progress || !status || !result) return;

      button.disabled = true;
      result.hidden = true;
      const steps = [
        ['12%', 'Проверяем запись графика, профиль объекта и шаблон филиала', null],
        ['32%', 'Агент базы знаний ищет требования в 7-ФЗ, 89-ФЗ и СТО', 'kb'],
        ['52%', 'Агент ОРД извлекает требования из приказа, распоряжения и программы ПЭК', 'ord'],
        ['72%', 'Агент нарушений анализирует повторы и просроченные сроки за последние 5 лет', 'violations'],
        ['90%', 'Агент истории переносит применимые пункты из архивного чек-листа', 'history'],
        ['100%', 'Проект чек-листа сформирован: источники объединены и статусы рассчитаны', null]
      ];
      let index = 0;
      progress.style.width = '0';
      status.textContent = 'Запуск формирования...';
      document.querySelectorAll('[data-agent]').forEach((agent) => {
        agent.classList.remove('agent-card-active', 'agent-card-done');
        const dot = agent.querySelector('.status-dot');
        if (dot) dot.className = 'status-dot status-muted';
      });

      const tick = () => {
        const [width, message, agentName] = steps[index];
        progress.style.width = width;
        status.textContent = message;
        if (agentName) {
          const agent = document.querySelector(`[data-agent="${agentName}"]`);
          const dot = agent?.querySelector('.status-dot');
          agent?.classList.add('agent-card-active');
          if (dot) dot.className = 'status-dot status-info';
          setTimeout(() => {
            agent?.classList.remove('agent-card-active');
            agent?.classList.add('agent-card-done');
            if (dot) dot.className = 'status-dot status-ready';
          }, 420);
        }
        index += 1;
        if (index < steps.length) {
          setTimeout(tick, 520);
          return;
        }
        setTimeout(() => {
          result.hidden = false;
          button.disabled = false;
        }, 350);
      };

      setTimeout(tick, 250);
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

  document.querySelectorAll('[data-ai-question]').forEach((form) => {
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      const thread = document.querySelector('[data-chat-thread]');
      const userTemplate = document.querySelector('[data-user-question-template]');
      const answerTemplate = document.querySelector('[data-ai-answer-template]');
      const input = form.querySelector('input');
      if (!thread || !userTemplate || !answerTemplate || !input) return;

      const question = input.value.trim();
      if (!question) return;

      const userMessage = userTemplate.content.firstElementChild.cloneNode(true);
      userMessage.querySelector('[data-user-question-text]').textContent = question;
      thread.appendChild(userMessage);

      const answerMessage = answerTemplate.content.firstElementChild.cloneNode(true);
      const questionTarget = answerMessage.querySelector('[data-ai-question-text]');
      const answerText = answerMessage.querySelector('[data-ai-answer-text]');
      const sources = answerMessage.querySelector('[data-ai-sources]');
      const normalizedQuestion = question.toLowerCase();
      if (questionTarget) questionTarget.textContent = question;
      if (answerText && sources) {
        if (normalizedQuestion.includes('крит')) {
          answerText.textContent = 'Пункт считается критическим, потому что в реестре нарушений есть неустранённое в срок нарушение по протоколам инструментального контроля выбросов. Источник: акт от 22.09.2024 и СТО 16-005-2025 п. 4.2.';
          sources.innerHTML = '<span class="source-chip">Акт 22.09.2024</span><span class="source-chip">СТО 16-005-2025 п. 4.2</span><span class="classifier-chip">2.3 Атмосфера</span>';
        } else if (normalizedQuestion.includes('не найден') || normalizedQuestion.includes('санитар')) {
          answerText.textContent = 'В базе знаний нет релевантного источника по этому вопросу. Система не формирует ответ без подтверждённой цитаты и предлагает обратиться к методологу.';
          sources.innerHTML = '<span class="classifier-chip">источник не найден</span>';
        }
      }
      thread.appendChild(answerMessage);

      input.value = '';
      thread.scrollTop = thread.scrollHeight;
    });
  });
})();
