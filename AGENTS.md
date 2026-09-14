# Правила работы с репозиторием

## Границы каталогов

- `src/` — только исполняемый код и web-ресурсы приложения.
- `tests/` — проверки, запускаемые как консольные проекты.
- `data/knowledge-base/` — Markdown, который включается в publish и индексируется приложением.
- `data/seed/` — версионируемые начальные данные приложения.
- `data/import/` — подготовительные источники; они не включаются в runtime автоматически.
- `data/reference/` — локальные бинарные и рабочие материалы. Содержимое не коммитится.
- `docs/` — архитектура, требования, дизайны и планы.
- `scripts/` — повторяемые операции конвертации и обслуживания данных.

Не добавляйте новые файлы данных в `src/` и новые исходники приложения в `data/`.

## Секреты и локальная конфигурация

- Рабочие секреты хранятся только в локальном `.env` или в секретах хостинга.
- В Git разрешён `.env.example` только с фиктивными значениями и полным перечнем переменных.
- Перед коммитом проверяйте `git diff --cached` на токены, пароли и строки подключения.

## Проверка изменений

Из корня репозитория выполните:

```powershell
dotnet restore OssDemo.slnx
dotnet build OssDemo.slnx --configuration Release --no-restore
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release --no-build
dotnet run --project tests/OssDemo.Rag.Tests/OssDemo.Rag.Tests.csproj --configuration Release --no-build
```

При изменении publish-путей дополнительно выполните:

```powershell
dotnet publish src/OssDemo.Web/OssDemo.Web.csproj --configuration Release --output .verify-publish
```

## Ветки и деплой

Автодеплой связан с удалённым репозиторием. Работайте в отдельной ветке, не выполняйте push без явного решения владельца и не меняйте `Dockerfile`, `amvera.yaml`, порт `8080` или mount `/data` попутно с несвязанной задачей.

## Документация

При изменении структуры, runtime-путей или источников данных обновляйте `README.md` и `docs/architecture/repository-map.md`. Подробные требования находятся в `docs/requirements/`; не дублируйте их целиком в README.
