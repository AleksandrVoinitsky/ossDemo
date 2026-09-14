# Local Hardening and Grounded AI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сделать локальный полный цикл воспроизводимым, быстрым для чек-листов на сотни пунктов и проверяемым реальным grounded-ответом Amvera LLM.

**Architecture:** Детерминированная композиция чек-листа не обращается к LLM. Основной API запуска возвращает компактный снимок, а подробные трассы загружаются страницами из отдельного endpoint. Docker-приёмка создаёт два временных подтверждённых объекта, сравнивает итоговые множества пунктов и всегда удаляет пробные данные; LLM-проверка запускается только явным флагом.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs/Razor Pages, Npgsql/PostgreSQL 17, vanilla JavaScript, PowerShell, Docker Compose, Amvera OpenAI-compatible Chat Completions.

**Spec:** `docs/requirements/rag-technical-spec.md`; `docs/superpowers/plans/2026-09-15-facility-driven-checklist-cycle.md`

## Global Constraints

- Не коммитить `.env.local`, токены или отладочные ответы модели.
- Не пушить и не деплоить без отдельной команды пользователя.
- Не передавать LLM решение о применимости требований и состав пунктов с полным нормативным покрытием.
- Каждый ответ консультанта должен быть основан на найденных фрагментах и возвращать источники.
- Обычный `Verify -RequireIndexedRag` не должен расходовать LLM-токены.

---

### Task 1: Contrastive Docker acceptance

**Files:**
- Modify: `scripts/verify-local-docker.ps1`
- Test: `tests/OssDemo.Checklists.Tests/FacilityChecklistComposerChecks.cs`

**Interfaces:**
- Consumes: `POST /api/operations/facility-profiles`, `POST /{slug}/confirm`, `POST /api/ai-checklists/runs`.
- Produces: live assertion that two confirmed profiles yield different `snapshot.selectedItemIds` and cleanup leaves zero probes.

- [x] **Step 1: Verify the existing unit contrast check passes but the live verifier has no contrast assertion**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release --no-build` and `rg "selectedItemIds|contrast" scripts/verify-local-docker.ps1`.

Expected: tests pass; `rg` finds no live comparison.

- [x] **Step 2: Add scoped probe helpers and cleanup**

Use two profiles where all required features are `absent`, then set `air.emissions=present` only for the industrial profile. Create and confirm both profiles, create runs, compare sorted selected item IDs, and delete only records bearing the generated probe prefix in a `finally` block through parameterized `psql` variables.

- [x] **Step 3: Run live verification twice**

Run: `./scripts/local-docker.ps1 Verify -RequireIndexedRag` twice.

Expected: both runs pass, item-ID sets differ, and probe counts are zero after each run.

- [x] **Step 4: Commit locally**

Run: `git add scripts/verify-local-docker.ps1 tests/OssDemo.Checklists.Tests && git commit -m "test: verify facility-specific checklists live"`.

### Task 2: Paginated checklist provenance

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistModels.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistApi.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `src/OssDemo.Web/wwwroot/js/ai-checklists.js`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces: `GET /api/ai-checklists/runs/{runId}/traces?offset=0&limit=50` returning `{items,total,offset,limit}` with `limit` clamped to `1..100`.
- Main create/get responses omit `snapshot.itemTraces` while retaining counts, IDs, decisions, gaps, and hashes.

- [x] **Step 1: Add failing checks for page boundaries and compact response projection**

Assert first page count `50`, second page begins at item `50`, excessive limit becomes `100`, and a run API projection has no embedded trace collection.

- [x] **Step 2: Run checklist checks and verify RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`.

Expected: compilation fails because trace pagination/projection does not exist.

- [x] **Step 3: Implement pagination and lazy UI loading**

Keep the complete immutable snapshot in PostgreSQL. Map API responses to a compact record and expose trace pages from `AiChecklistAgent`; render the first page on preview and append the next page only when the user presses `Показать ещё`.

- [x] **Step 4: Verify GREEN and measure payload**

Run checklist tests and measure the JSON byte length of create/get responses for a 500+ item run. Expected: no embedded traces and response remains below 512 KiB.

- [x] **Step 5: Commit locally**

Run: `git add src/OssDemo.Web/AiChecklists src/OssDemo.Web/wwwroot/js/ai-checklists.js tests/OssDemo.Checklists.Tests && git commit -m "perf: page checklist provenance traces"`.

### Task 3: Run stability and concurrency guards

**Files:**
- Modify: `tests/OssDemo.Checklists.Tests/AiChecklistRunChecks.cs`
- Modify when tests expose a defect: `src/OssDemo.Web/AiChecklists/PostgresAiChecklistRunStore.cs`
- Modify when tests expose a defect: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`

**Interfaces:**
- Verifies: duplicate queue/finalize calls are idempotent, one batch is claimed once, interrupted work resumes, and completed runs cannot be mutated.

- [x] **Step 1: Add one regression check per uncovered state transition**
- [x] **Step 2: Run the transition checks; no production defect was exposed**
- [x] **Step 3: Keep the production state machine unchanged because the new checks passed**
- [x] **Step 4: Run the full checklist suite twice**
- [x] **Step 5: Commit tests as `test: cover checklist run transitions`**

### Task 4: Explicit grounded LLM acceptance

**Files:**
- Modify: `scripts/verify-local-docker.ps1`
- Modify: `.env.local.example`
- Modify: `docs/development/local-docker.md`
- Test: `tests/OssDemo.Rag.Tests/Program.cs`

**Interfaces:**
- Consumes: `POST /api/ai/chat` with `stream=false` and the configured `AI__ApiToken`.
- Produces: optional `-RequireLlm` acceptance that requires model name, `grounded=true`, at least one source, non-empty answer, and no secret in output.

- [x] **Step 1: Add a failing static check for a missing `RequireLlm` verifier branch**
- [x] **Step 2: Add the optional live probe with a 300-second timeout and concise metrics only**
- [x] **Step 3: Recreate the local application and run `Verify -RequireIndexedRag -RequireLlm`**
- [x] **Step 4: Run full Release build, both test executables, `node --check`, `git diff --check`, and inspect application logs for unhandled failures**
- [x] **Step 5: Commit locally as `test: verify grounded llm responses locally`**
