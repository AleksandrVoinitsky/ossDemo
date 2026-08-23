(() => {
  const calendarEl = document.getElementById('scheduleCalendar');
  if (!calendarEl || !window.FullCalendar) return;

  const upcomingEl = document.querySelector('[data-schedule-upcoming]');
  const upcomingCountEl = document.querySelector('[data-schedule-upcoming-count]');
  const resultEl = document.querySelector('[data-schedule-result]');
  const searchEl = document.querySelector('[data-schedule-search]');
  const statusEl = document.querySelector('[data-schedule-status]');
  const categoryEl = document.querySelector('[data-schedule-category]');
  const resetEl = document.querySelector('[data-schedule-reset]');

  const statusMeta = {
    ready: { label: 'Готово к генерации', badge: 'text-bg-success', className: 'schedule-event-ready' },
    profile: { label: 'Профиль требует доработки', badge: 'text-bg-warning', className: 'schedule-event-profile' },
    missing: { label: 'Требует создания', badge: 'text-bg-danger', className: 'schedule-event-missing' },
    control: { label: 'Контрольный срок', badge: 'text-bg-info', className: 'schedule-event-control' },
    draft: { label: 'В подготовке', badge: 'text-bg-secondary', className: 'schedule-event-draft' }
  };

  const categoryMeta = {
    inspection: 'Проверка',
    documents: 'ОРД и документы',
    generation: 'Генерация чек-листа',
    control: 'Контроль нарушения'
  };

  const scheduleEvents = [
    {
      id: 'sch-18',
      title: 'Березниковское ЛПУМГ: плановая проверка',
      start: '2026-08-21T10:00:00',
      end: '2026-08-21T13:30:00',
      status: 'ready',
      category: 'inspection',
      object: 'Березниковское ЛПУМГ',
      objectType: 'Компрессорная станция, НВОС I',
      responsible: 'Дулаева Н. И.',
      criteria: 'Общие, атмосфера, вода, отходы, недра',
      match: 'Профиль подставлен автоматически, пакет ОРД найден',
      note: 'Демо-сценарий: запуск генерации чек-листа по готовому объектному профилю.',
      actionLabel: 'Создать чек-лист',
      actionUrl: '/Checklists/New'
    },
    {
      id: 'doc-ber-2026',
      title: 'Березниковское ЛПУМГ: сверка ОРД',
      start: '2026-08-20T15:00:00',
      end: '2026-08-20T16:00:00',
      status: 'ready',
      category: 'documents',
      object: 'Березниковское ЛПУМГ',
      objectType: 'Пакет приказов и лицензий',
      responsible: 'Смирнова Е. А.',
      criteria: 'Приказ ПЭК, распоряжение, приложение к лицензии',
      match: 'Документы распознаны, неоднозначность по скважине №3 вынесена в решение',
      note: 'Событие показывает подготовительный этап до проверки.',
      actionLabel: 'Открыть чек-лист',
      actionUrl: '/Checklists/New'
    },
    {
      id: 'gen-ber-2026',
      title: 'Березниковское ЛПУМГ: генерация чек-листа',
      start: '2026-08-21T14:30:00',
      end: '2026-08-21T15:15:00',
      status: 'ready',
      category: 'generation',
      object: 'Березниковское ЛПУМГ',
      objectType: 'Формирование проекта чек-листа',
      responsible: 'inspector',
      criteria: '42 пункта из базы знаний, ОРД, нарушений и архива',
      match: '4 агента завершили анализ источников',
      note: 'Быстрый переход ведет на мастер создания чек-листа.',
      actionLabel: 'Перейти к генерации',
      actionUrl: '/Checklists/New'
    },
    {
      id: 'sch-21',
      title: 'Воткинское ЛПУМГ: проверка профиля',
      start: '2026-09-09T09:30:00',
      end: '2026-09-09T12:00:00',
      status: 'profile',
      category: 'inspection',
      object: 'Воткинское ЛПУМГ',
      objectType: 'Линейное производственное управление',
      responsible: 'Каримов А. В.',
      criteria: 'Земля, отходы, общие',
      match: 'Объект найден, но профиль требует уточнения категорий НВОС',
      note: 'Перед генерацией чек-листа нужно подтвердить профиль объекта.',
      actionLabel: 'Доработать профиль',
      actionUrl: '/Facilities'
    },
    {
      id: 'doc-vot-2026',
      title: 'Воткинское ЛПУМГ: загрузить приложение к лицензии',
      start: '2026-09-05T11:00:00',
      end: '2026-09-05T12:00:00',
      status: 'profile',
      category: 'documents',
      object: 'Воткинское ЛПУМГ',
      objectType: 'Подготовка документов',
      responsible: 'Каримов А. В.',
      criteria: 'Недостающие приложения и зона ответственности',
      match: 'Найден приказ ПЭК, приложение к лицензии отсутствует',
      note: 'Событие подсвечивает причину желтого статуса в календаре.',
      actionLabel: 'Доработать профиль',
      actionUrl: '/Facilities'
    },
    {
      id: 'sch-24',
      title: 'УАВР №1: создать профиль объекта',
      start: '2026-10-06T10:00:00',
      end: '2026-10-06T11:30:00',
      status: 'missing',
      category: 'inspection',
      object: 'УАВР №1',
      objectType: 'Аварийно-восстановительное подразделение',
      responsible: 'Иванова Е. С.',
      criteria: 'Отходы, вода',
      match: 'Профиль не найден в реестре объектов',
      note: 'Красный статус показывает блокер перед плановой проверкой.',
      actionLabel: 'Создать профиль',
      actionUrl: '/Facilities'
    },
    {
      id: 'ctrl-31',
      title: 'Контроль устранения: протоколы инструментального контроля',
      start: '2026-10-14T16:00:00',
      end: '2026-10-14T17:00:00',
      status: 'control',
      category: 'control',
      object: 'Березниковское ЛПУМГ',
      objectType: 'Предписание по акту 2025',
      responsible: 'Дулаева Н. И.',
      criteria: 'Атмосфера, инструментальный контроль выбросов',
      match: 'Просроченное устранение подтянуто из реестра нарушений',
      note: 'Контрольные события позволяют видеть не только проверки, но и критичные сроки.',
      actionLabel: 'Открыть нарушение',
      actionUrl: '/Violations'
    },
    {
      id: 'sch-29',
      title: 'Пермское ЛПУМГ: подготовить проверку',
      start: '2026-11-12T09:00:00',
      end: '2026-11-12T12:00:00',
      status: 'draft',
      category: 'inspection',
      object: 'Пермское ЛПУМГ',
      objectType: 'Линейное производственное управление',
      responsible: 'Соколов П. М.',
      criteria: 'Атмосфера, недра',
      match: 'Профиль найден, пакет документов еще не проверен',
      note: 'Событие находится в подготовке, но уже доступно в календарном плане.',
      actionLabel: 'Подготовить чек-лист',
      actionUrl: '/Checklists/New'
    },
    {
      id: 'ctrl-perm-2026',
      title: 'Пермское ЛПУМГ: срок обновления ПНООЛР',
      start: '2026-11-18T15:00:00',
      end: '2026-11-18T16:00:00',
      status: 'control',
      category: 'control',
      object: 'Пермское ЛПУМГ',
      objectType: 'Контроль нормативной документации',
      responsible: 'Соколов П. М.',
      criteria: 'Отходы, лимиты и фактические объемы',
      match: 'Срок найден при анализе ОРД и прошлых чек-листов',
      note: 'Контрольный срок автоматически попадает в график подготовки.',
      actionLabel: 'Подготовить чек-лист',
      actionUrl: '/Checklists/New'
    }
  ];

  const escapeHtml = (value) => String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');

  const formatDate = (date, withTime = true) => {
    if (!date) return '—';
    return new Intl.DateTimeFormat('ru-RU', {
      day: '2-digit',
      month: 'long',
      year: 'numeric',
      ...(withTime ? { hour: '2-digit', minute: '2-digit' } : {})
    }).format(date);
  };

  const getEventUrl = (eventId) => `/Schedule/Event/${encodeURIComponent(eventId)}`;

  const toCalendarEvent = (event) => {
    const meta = statusMeta[event.status] || statusMeta.draft;
    return {
      id: event.id,
      title: event.title,
      start: event.start,
      end: event.end,
      url: getEventUrl(event.id),
      classNames: ['schedule-event', meta.className],
      extendedProps: event
    };
  };

  const getFilteredEvents = () => {
    const query = (searchEl?.value || '').trim().toLowerCase();
    const status = statusEl?.value || 'all';
    const category = categoryEl?.value || 'all';

    return scheduleEvents.filter((event) => {
      const matchesStatus = status === 'all' || event.status === status;
      const matchesCategory = category === 'all' || event.category === category;
      const searchPool = `${event.title} ${event.object} ${event.objectType} ${event.responsible} ${event.criteria} ${event.match}`.toLowerCase();
      const matchesQuery = !query || searchPool.includes(query);
      return matchesStatus && matchesCategory && matchesQuery;
    });
  };

  const renderUpcoming = (events) => {
    if (!upcomingEl) return;
    const sorted = [...events].sort((left, right) => new Date(left.start) - new Date(right.start)).slice(0, 6);
    if (upcomingCountEl) upcomingCountEl.textContent = String(events.length);

    if (sorted.length === 0) {
      upcomingEl.innerHTML = '<div class="schedule-empty-state">По текущим фильтрам событий нет.</div>';
      return;
    }

    upcomingEl.innerHTML = sorted.map((event) => {
      const meta = statusMeta[event.status] || statusMeta.draft;
      return `
        <a class="schedule-upcoming-item" href="${escapeHtml(getEventUrl(event.id))}">
          <span class="schedule-upcoming-date">${escapeHtml(formatDate(new Date(event.start), false))}</span>
          <strong>${escapeHtml(event.title)}</strong>
          <span>${escapeHtml(event.responsible)} · ${escapeHtml(categoryMeta[event.category] || event.category)}</span>
          <span class="badge ${meta.badge}">${escapeHtml(meta.label)}</span>
        </a>`;
    }).join('');
  };

  const updateStats = (events) => {
    if (resultEl) resultEl.textContent = `Показано событий: ${events.length} из ${scheduleEvents.length}`;
  };

  const applyFilters = () => {
    const filtered = getFilteredEvents();
    calendar.batchRendering(() => {
      calendar.removeAllEvents();
      calendar.addEventSource(filtered.map(toCalendarEvent));
    });
    updateStats(filtered);
    renderUpcoming(filtered);
  };

  const calendar = new FullCalendar.Calendar(calendarEl, {
    initialDate: '2026-08-21',
    initialView: 'dayGridMonth',
    locale: 'ru',
    firstDay: 1,
    height: 'auto',
    nowIndicator: true,
    selectable: true,
    navLinks: true,
    dayMaxEvents: 3,
    eventTimeFormat: { hour: '2-digit', minute: '2-digit', meridiem: false },
    headerToolbar: {
      left: 'prev,next today',
      center: 'title',
      right: 'dayGridMonth,timeGridWeek,timeGridDay,listWeek'
    },
    buttonText: {
      today: 'Сегодня',
      month: 'Месяц',
      week: 'Неделя',
      day: 'День',
      list: 'Список'
    },
    events: scheduleEvents.map(toCalendarEvent),
    eventClick: (info) => {
      info.jsEvent.preventDefault();
      window.location.assign(getEventUrl(info.event.id));
    },
    dateClick: (info) => {
      calendar.changeView('timeGridDay', info.dateStr);
    }
  });

  calendar.render();

  [searchEl, statusEl, categoryEl].forEach((control) => {
    if (!control) return;
    const eventName = control.tagName === 'INPUT' ? 'input' : 'change';
    control.addEventListener(eventName, applyFilters);
  });

  resetEl?.addEventListener('click', () => {
    if (searchEl) searchEl.value = '';
    if (statusEl) statusEl.value = 'all';
    if (categoryEl) categoryEl.value = 'all';
    applyFilters();
  });

  applyFilters();
})();
