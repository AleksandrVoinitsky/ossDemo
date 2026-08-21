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
      const progress = document.querySelector('[data-generation-progress]');
      const status = document.querySelector('[data-generation-status]');
      const result = document.querySelector('[data-generation-result]');
      if (!progress || !status || !result) return;

      button.disabled = true;
      result.hidden = true;
      const steps = [
        ['15%', 'Сопоставляем объект с графиком проверок'],
        ['38%', 'Проверяем профиль, классификатор и шаблон'],
        ['64%', 'Подбираем требования из базы знаний и ОРД'],
        ['86%', 'Поднимаем исторические нарушения и спорные пункты'],
        ['100%', 'Проект чек-листа сформирован в demo-режиме']
      ];
      let index = 0;
      progress.style.width = '0';
      status.textContent = 'Запуск demo workflow...';

      const tick = () => {
        const [width, message] = steps[index];
        progress.style.width = width;
        status.textContent = message;
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

  document.querySelectorAll('[data-show-target]').forEach((button) => {
    button.addEventListener('click', () => {
      const target = document.querySelector(button.dataset.showTarget);
      if (!target) return;
      target.hidden = !target.hidden;
    });
  });

  document.querySelectorAll('[data-export-demo]').forEach((button) => {
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

  document.querySelectorAll('[data-ai-question]').forEach((form) => {
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      const answer = document.querySelector('[data-ai-answer]');
      const input = form.querySelector('input');
      if (!answer || !input) return;
      answer.hidden = false;
      answer.querySelector('[data-ai-question-text]').textContent = input.value || 'Какие документы нужны по ПНООЛР?';
      input.value = '';
    });
  });
})();
