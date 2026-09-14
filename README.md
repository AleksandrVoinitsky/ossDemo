# АИ ООС Demo

Demo-стенд системы поддержки инспекционного контроля в области охраны окружающей среды. Приложение объединяет график проверок, карточки объектов, шаблоны и рабочие чек-листы, реестр нарушений, классификатор и RAG-поиск по нормативным документам.

## Быстрый старт

Требуется .NET SDK из `global.json` и PostgreSQL. Реальные секреты задаются локально в `.env` либо через переменные окружения; перечень имён находится в `.env.example`.

```powershell
dotnet restore OssDemo.slnx
dotnet build OssDemo.slnx
dotnet run --project src/OssDemo.Web/OssDemo.Web.csproj
```

## Структура

```text
.
├── data/
│   ├── knowledge-base/      # поставляемая Markdown-база знаний
│   ├── seed/                # начальные данные пустой БД
│   ├── import/              # подготовительные источники
│   └── reference/           # локальные бинарные материалы (ignored)
├── docs/
│   ├── architecture/
│   ├── requirements/
│   └── superpowers/
├── scripts/                 # конвертация и обслуживание данных
├── src/OssDemo.Web/         # приложение ASP.NET Core
└── tests/                   # исполняемые наборы проверок
```

Подробное назначение каталогов и runtime-потоки описаны в [карте репозитория](docs/architecture/repository-map.md). Правила совместной работы находятся в [AGENTS.md](AGENTS.md).

## Проверки

```powershell
dotnet build OssDemo.slnx --configuration Release
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release --no-build
dotnet run --project tests/OssDemo.Rag.Tests/OssDemo.Rag.Tests.csproj --configuration Release --no-build
```

Полный изолированный стенд приложения и PostgreSQL описан в [руководстве по локальному Docker](docs/development/local-docker.md).

## Данные

Нормализованные документы из `data/knowledge-base/` включаются в publish как `knowledge-base/`. По команде `!check` или `!reindex` приложение индексирует их вместе с Markdown из persistent-каталога `/data/inbox` в PostgreSQL/pgvector. Встроенная ONNX-модель при первом запуске загружается в `/data/ragify-model` и затем переиспользуется.

Шаблоны, черновики, утверждённые чек-листы, классификатор и служебные состояния хранятся в PostgreSQL. JSON из `data/seed/` используется только для первоначального заполнения пустой БД.

## Конфигурация и деплой

Docker-сборка запускает `OssDemo.Web.dll` на порту `8080`. `amvera.yaml` монтирует persistent volume в `/data`. Для хостинга нужны как минимум:

- `AI__ApiToken` — серверный токен OpenAI-совместимого endpoint;
- `ConnectionStrings__OssDatabase` — строка подключения PostgreSQL.

Секреты не должны попадать в Git. Автодеплой запускается удалённым репозиторием, поэтому изменения сначала проверяются в отдельной ветке и отправляются только после просмотра.

## Подробная документация

- [Требования проекта](docs/requirements/project-requirements.md)
- [Техническая спецификация RAG](docs/requirements/rag-technical-spec.md)
- [Карта репозитория](docs/architecture/repository-map.md)
- [Аудит исходного состояния и принятые решения](docs/architecture/audit-2026-09-15.md)
- [Технический долг и следующие этапы](docs/architecture/technical-debt.md)
- [Дизайн текущей реорганизации](docs/superpowers/specs/2026-09-15-repository-organization-design.md)
