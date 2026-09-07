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
    control: 'Контроль нарушения'
  };

  const scheduleEvents = [
    {
      id: 'sch-18',
      title: 'Березниковское ЛПУМГ: плановая проверка',
      start: '2026-08-21',
      end: '2026-08-24',
      status: 'ready',
      category: 'inspection',
      object: 'Березниковское ЛПУМГ',
      objectType: 'Компрессорная станция, НВОС I',
      responsible: 'Дулаева Н. И.',
      criteria: 'Общие, атмосфера, вода, отходы, недра',
      match: 'Профиль подставлен автоматически, пакет ОРД найден',
      note: 'Выездная плановая проверка на три календарных дня. Профиль и пакет ОРД подготовлены.',
      actionLabel: 'Создать чек-лист',
      actionUrl: '/Checklists/New'
    },
    {
      id: 'sch-21',
      title: 'Воткинское ЛПУМГ: плановая проверка',
      start: '2026-09-09',
      end: '2026-09-11',
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
      id: 'sch-24',
      title: 'УАВР №1: создать профиль объекта',
      start: '2026-10-06',
      end: '2026-10-08',
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
      start: '2026-10-14',
      end: '2026-10-15',
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
      start: '2026-11-12',
      end: '2026-11-14',
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
      start: '2026-11-18',
      end: '2026-11-19',
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

  const formatDate = (date) => {
    if (!date) return '—';
    return new Intl.DateTimeFormat('ru-RU', {
      day: '2-digit',
      month: 'long',
      year: 'numeric'
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
      allDay: true,
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
          <span class="schedule-upcoming-date">${escapeHtml(formatPeriod(event.start, event.end))}</span>
          <strong>${escapeHtml(event.title)}</strong>
          <span>${escapeHtml(event.responsible)} · ${escapeHtml(categoryMeta[event.category] || event.category)}</span>
          <span class="badge ${meta.badge}">${escapeHtml(meta.label)}</span>
        </a>`;
    }).join('');
  };

  const formatPeriod = (start, end) => {
    const startDate = new Date(`${start}T00:00:00`);
    const endDate = new Date(`${end}T00:00:00`);
    endDate.setDate(endDate.getDate() - 1);
    const startText = formatDate(startDate);
    const endText = formatDate(endDate);
    return startText === endText ? startText : `${startText} — ${endText}`;
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
    navLinks: true,
    dayMaxEvents: 3,
    headerToolbar: {
      left: 'prev,next today',
      center: 'title',
      right: 'dayGridMonth,dayGridWeek,listWeek'
    },
    buttonText: {
      today: 'Сегодня',
      month: 'Месяц',
      week: 'Неделя',
      list: 'Список'
    },
    events: scheduleEvents.map(toCalendarEvent),
    eventClick: (info) => {
      info.jsEvent.preventDefault();
      window.location.assign(getEventUrl(info.event.id));
    },
    eventDisplay: 'block'
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

  const facilityList = document.querySelector('[data-schedule-facility-list]');
  const mapFrame = document.querySelector('[data-schedule-map-frame]');
  const mapName = document.querySelector('[data-schedule-map-name]');
  const mapAddress = document.querySelector('[data-schedule-map-address]');
  const mapUrl = (facility) => `https://yandex.ru/map-widget/v1/?ll=${facility.longitude}%2C${facility.latitude}&z=10&pt=${facility.longitude}%2C${facility.latitude}%2Cpm2blm`;
  if (facilityList) {
    fetch('/api/operations/facilities').then((response) => response.ok ? response.json() : Promise.reject()).then((facilities) => {
      facilityList.innerHTML = facilities.map((facility, index) => `<button class="schedule-facility-item ${index === 0 ? 'active' : ''}" type="button" data-map-url="${mapUrl(facility)}" data-name="${escapeHtml(facility.name)}" data-address="${escapeHtml(facility.address)}"><strong>${escapeHtml(facility.name)}</strong><span>НВОС ${escapeHtml(facility.nvocCategory)}</span></button>`).join('') || '<p class="muted-note mb-0">В реестре пока нет объектов.</p>';
      const selectFacility = (button) => { facilityList.querySelectorAll('button').forEach((item) => item.classList.toggle('active', item === button)); if (mapFrame) mapFrame.src = button.dataset.mapUrl; if (mapName) mapName.textContent = button.dataset.name; if (mapAddress) mapAddress.textContent = button.dataset.address; };
      facilityList.querySelectorAll('button').forEach((button) => button.addEventListener('click', () => selectFacility(button)));
      if (facilityList.querySelector('button')) selectFacility(facilityList.querySelector('button'));
    }).catch(() => { facilityList.innerHTML = '<p class="text-danger mb-0">Не удалось загрузить объекты.</p>'; });
  }
})();
