# Карта репозитория и потоков данных

## Компоненты

| Путь | Ответственность | Попадает в publish |
| --- | --- | --- |
| `src/OssDemo.Web/` | Razor Pages, API, сервисы, доступ к PostgreSQL и RAG | Да |
| `tests/OssDemo.Checklists.Tests/` | Доменные и интеграционные проверки чек-листов | Нет |
| `tests/OssDemo.Rag.Tests/` | Проверки адаптеров RAG и Markdown | Нет |
| `data/knowledge-base/` | Нормализованные Markdown-документы для встроенной базы знаний | Да, как `knowledge-base/` |
| `data/seed/` | Начальные записи для пустой БД | Да, как embedded resources |
| `data/import/` | Подготовленные материалы и промежуточные наборы | Нет |
| `data/reference/` | Локальные DOCX, PDF, XLSX, изображения и рабочие материалы | Нет, содержимое ignored |
| `scripts/` | Одноразовые и обслуживающие конвертации | Нет |
| `docs/requirements/` | Зафиксированные требования и RAG-спецификация | Нет |

## Runtime-потоки

```text
data/knowledge-base/*.md
  -> MSBuild Content + Link
  -> publish/knowledge-base/*.md
  -> KnowledgeImportService
  -> RAGify + PostgreSQL/pgvector

data/seed/checklists.json
  -> embedded resource OssDemo.Web.Data.checklists.json
  -> ChecklistSeedDataLoader
  -> ChecklistDatabaseInitializer
  -> PostgreSQL app_checklist_*

/data/inbox (persistent volume)
  -> KnowledgeImportService
  -> тот же RAG-индекс
```

`data/import/knowledge-inbox/` — репозиторный подготовительный набор. Он намеренно не подключён к runtime: только утверждённые документы из `data/knowledge-base/` поставляются с приложением, а оперативный импорт выполняется через `/data/inbox`.

## Конфигурация

- `appsettings.json` содержит безопасные значения по умолчанию.
- локальный `.env` и секреты Amvera содержат реальные ключи и строки подключения;
- `.env.example` документирует имена переменных;
- `/data` хранит модель, Lucene-индекс, диагностические данные и импортируемые документы между перезапусками контейнера.

## Правило добавления данных

Новый файл сначала помещается в `data/reference/` или `data/import/`, преобразуется скриптом из `scripts/`, проверяется, и только нормализованный Markdown переносится в `data/knowledge-base/`. Начальные структурированные записи должны храниться в `data/seed/`, а не в C#-литералах.
