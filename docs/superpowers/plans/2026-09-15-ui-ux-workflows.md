# UI/UX Workflow Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Довести АИ ООС до устойчивого рабочего цикла «объект → требования → персональный чек-лист», сохранив фирменный интерфейс и текущий технологический стек.

**Architecture:** Razor Pages и Bootstrap остаются UI-слоем. Исходный JSONL реестра требований остаётся неизменным, а PostgreSQL хранит редакционные версии, ручные связи, системные шаблоны, черновики и ОРД. Генератор получает итоговые требования через один сервис разрешения связей и не вызывает LLM для детерминированного подбора.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages/Minimal API, Npgsql/PostgreSQL 17 + pgvector, Bootstrap 5, vanilla JavaScript, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-09-15-ui-ux-workflows-design.md`

## Global Constraints

- Названия и состав бокового меню не меняются.
- Фирменные градиенты и объёмные карточки сохраняются; рабочие данные должны оставаться контрастными и читаемыми.
- Моковый маршрут `Pages/Checklists/New.cshtml` не дорабатывается.
- `data/requirements/requirements-registry.jsonl` и manifest не перемещаются и не редактируются.
- ОРД опциональны, допускается несколько файлов; типы строго: `Приказ`, `Распоряжение`, `Лицензия`.
- В системе ровно два неудаляемых шаблона: `Уровень общества` и `Уровень филиала`.
- Все коммиты локальные; push и deploy запрещены.

---

### Task 1: Итоговый каталог требований для классификатора

**Files:**
- Create: `src/OssDemo.Web/Requirements/RequirementWorkspaceModels.cs`
- Create: `src/OssDemo.Web/Requirements/IRequirementWorkspace.cs`
- Create: `src/OssDemo.Web/Requirements/PostgresRequirementWorkspace.cs`
- Create: `src/OssDemo.Web/Requirements/RequirementDatabaseInitializer.cs`
- Create: `src/OssDemo.Web/Requirements/RequirementApi.cs`
- Modify: `src/OssDemo.Web/Requirements/RequirementCatalog.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Modify: `src/OssDemo.Web/Checklists/FacilityChecklistCatalogs.cs`
- Modify: `src/OssDemo.Web/Checklists/FacilityChecklistComposer.cs`
- Test: `tests/OssDemo.Checklists.Tests/RequirementWorkspaceChecks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`

**Interfaces:**
- Produces: `IRequirementWorkspace.ResolveAsync(string criterionCode, CancellationToken)` returning `ResolvedCriterionRequirements`.
- Produces: `PUT /api/classifier/criteria/{criterionId}/requirements/{requirementId}` and `DELETE` for manual links/exclusions.
- Produces: `PUT /api/requirements/{requirementId}/revision` with optimistic `Version`.
- Consumes: immutable `RequirementCatalogItem` rows and current classifier codes.

- [ ] **Step 1: Add failing domain checks**

```csharp
var resolved = RequirementWorkspaceResolver.Resolve(
    [new RequirementCatalogItem("R1", ["Федеральный"], [], ["2.1"], "ФЗ-7", "Исходное", [])],
    "2.1",
    [new RequirementLinkOverride("R1", "exclude", 1)],
    [new RequirementRevision("R1", "Рабочая формулировка", "ФЗ-7", 2)]);
AssertEqual(0, resolved.Items.Count);
AssertEqual("Рабочая формулировка", RequirementWorkspaceResolver.Resolve(
    [new RequirementCatalogItem("R1", ["Федеральный"], [], ["2.1"], "ФЗ-7", "Исходное", [])],
    "2.1", [], [new RequirementRevision("R1", "Рабочая формулировка", "ФЗ-7", 2)]).Items[0].Requirement);
```

- [ ] **Step 2: Run the checklist test executable and confirm RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: compilation failure because workspace types do not exist.

- [ ] **Step 3: Implement schema and resolver**

Create idempotent tables `app_requirement_revisions`, `app_classifier_requirement_links`, and `app_requirement_change_log`. Store `criterion_id`, stable `requirement_id`, action `include|exclude`, integer version, timestamps, and actor. Implement merge order exactly as `JSONL → automatic classifierCodes → manual include/exclude → latest revision`.

```csharp
internal interface IRequirementWorkspace
{
    Task<ResolvedCriterionRequirements> ResolveAsync(string criterionCode, CancellationToken ct);
    Task<ChecklistOperationResult<ResolvedRequirement>> SaveRevisionAsync(string requirementId, RequirementRevisionWrite request, CancellationToken ct);
    Task<ChecklistOperationResult<ResolvedCriterionRequirements>> SetLinkAsync(Guid criterionId, string requirementId, string action, CancellationToken ct);
}
```

- [ ] **Step 4: Connect checklist composition to the resolver without LLM**

Replace direct `RequirementCatalog.Items` selection in `FacilityChecklistComposer` with a resolved catalog snapshot. Preserve existing behavior when there are no overrides. Add an assertion that a manual exclusion removes the requirement from newly composed items.

- [ ] **Step 5: Run focused and regression tests**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: `Checklist domain checks passed.`

- [ ] **Step 6: Commit**

```powershell
git add src/OssDemo.Web/Requirements src/OssDemo.Web/Program.cs src/OssDemo.Web/Checklists tests/OssDemo.Checklists.Tests
git commit -m "feat: manage classifier requirement links"
```

### Task 2: Рабочий экран классификатора и требования

**Files:**
- Modify: `src/OssDemo.Web/Pages/Classifier.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/classifier.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`
- Test: `tests/OssDemo.Checklists.Tests/RequirementWorkspaceChecks.cs`

**Interfaces:**
- Consumes: Task 1 requirement endpoints and `ResolvedCriterionRequirements`.
- Produces: master-detail UI with tabs `Требования` and `Настройка критерия`.

- [ ] **Step 1: Add API response contract checks**

```csharp
AssertTrue(response.Items.All(item => item.LinkSource is "automatic" or "manual"), "Источник связи обязателен.");
AssertTrue(response.Items.All(item => item.Version >= 1), "Версия требования обязательна.");
```

- [ ] **Step 2: Run tests and confirm the new response checks fail**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

- [ ] **Step 3: Replace the misleading detail heading and add tabs**

Render the selected criterion code and `CheckText` as the right-panel heading. Make `Требования (N)` the default tab. Each row exposes level, basis, working text, source badge, edit, exclude/restore, and conflict feedback. Put add-by-search in a dialog with explicit confirm action.

- [ ] **Step 4: Implement accessible interaction states**

Use real labels, `aria-live` for save/error state, keyboard-operable tabs, visible focus, disabled submit while saving, and wrapped long content. Do not remove gradients from page header or selected cards.

- [ ] **Step 5: Build and perform browser smoke check**

Run: `dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore`

Verify locally: select criteria from two different sections, edit a revision, exclude and restore one automatic link, refresh, and confirm persisted state.

- [ ] **Step 6: Commit**

```powershell
git add src/OssDemo.Web/Pages/Classifier.cshtml src/OssDemo.Web/wwwroot/js/classifier.js src/OssDemo.Web/wwwroot/css/site.css tests/OssDemo.Checklists.Tests
git commit -m "feat: edit requirements from classifier"
```

### Task 3: Два системных шаблона

**Files:**
- Modify: `src/OssDemo.Web/Checklists/ChecklistModels.cs`
- Modify: `src/OssDemo.Web/Checklists/ChecklistRules.cs`
- Modify: `src/OssDemo.Web/Checklists/ChecklistDatabaseInitializer.cs`
- Modify: `src/OssDemo.Web/Checklists/IChecklistRepository.cs`
- Modify: `src/OssDemo.Web/Checklists/PostgresChecklistRepository.cs`
- Modify: `src/OssDemo.Web/Checklists/ChecklistApi.cs`
- Modify: `src/OssDemo.Web/ChecklistService.cs`
- Modify: `src/OssDemo.Web/Pages/Checklists/Archive.cshtml`
- Modify: `src/OssDemo.Web/Pages/Checklists/TemplateEditor.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/checklist-library.js`
- Modify: `src/OssDemo.Web/wwwroot/js/checklist-template-editor.js`
- Modify: `data/seed/checklists.json`
- Test: `tests/OssDemo.Checklists.Tests/ChecklistLifecycleChecks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`

**Interfaces:**
- Produces: `ChecklistTemplateScope` values `society` and `branch`.
- Produces: exactly two stable template IDs and update-only API.
- Consumes: existing template sections/items and checklist creation.

- [ ] **Step 1: Replace legacy seed expectations with failing system-template checks**

```csharp
AssertEqual(2, ChecklistSeedData.Templates.Count);
AssertEqual(2, ChecklistSeedData.Templates.Select(x => x.Scope).Distinct().Count());
AssertTrue(ChecklistSeedData.Templates.All(x => !x.Name.Contains("ЛПУМГ")), "Шаблоны не должны содержать объект.");
```

- [ ] **Step 2: Run tests and confirm RED against the current three facility templates**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

- [ ] **Step 3: Migrate schema and seed data**

Add `scope`, `header_json`, and `is_system` to `app_checklist_templates`; make facility optional for legacy history only. Upsert two known IDs, migrate their sections, and remove the three old seed templates only after checklist foreign keys are set to null. Enforce unique active scope with a database index.

- [ ] **Step 4: Restrict API and repository operations**

Remove create/copy/delete controls from the system-template UI. Return `409 system_template` if those endpoints target a system template. Update requests accept `Name`, `Header`, `Version`, and sections; they cannot change scope.

- [ ] **Step 5: Update template UI and checklist creation**

Show exactly two cards. Editor exposes universal header fields and sections, never facility/city fields. Checklist creation chooses scope and substitutes facility data only into the created checklist/export.

- [ ] **Step 6: Run tests and commit**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

```powershell
git add src/OssDemo.Web/Checklists src/OssDemo.Web/ChecklistService.cs src/OssDemo.Web/Pages/Checklists src/OssDemo.Web/wwwroot/js data/seed/checklists.json tests/OssDemo.Checklists.Tests
git commit -m "feat: enforce two system checklist templates"
```

### Task 4: Черновик формирования и несколько ОРД

**Files:**
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistDraftModels.cs`
- Create: `src/OssDemo.Web/AiChecklists/IAiChecklistDraftStore.cs`
- Create: `src/OssDemo.Web/AiChecklists/PostgresAiChecklistDraftStore.cs`
- Create: `src/OssDemo.Web/AiChecklists/InspectionBasisDocumentStore.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistDatabaseInitializer.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistApi.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistModels.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistDraftChecks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`

**Interfaces:**
- Produces: `POST /api/ai-checklists/drafts`, `GET/PUT /drafts/{id}`, multipart `POST /drafts/{id}/documents`, and `DELETE /drafts/{id}/documents/{documentId}`.
- Produces: `InspectionBasisDocumentType` with only `order`, `directive`, `license`.
- Produces: `AiChecklistDraftState(Guid Id, string FacilitySlug, Guid? TemplateId, string Step, long Version, IReadOnlyList<InspectionBasisDocument> Documents)`.
- Produces: `CreateAiChecklistRunRequest(Guid DraftId)`; created runs expose `DraftId`, while their snapshot fixes `TemplateId` and resolved requirement IDs.
- Consumes: facility slug, selected template ID, and existing `CreateRunAsync`.

- [ ] **Step 1: Add failing validation and persistence checks**

```csharp
AssertTrue(InspectionBasisDocumentType.TryParse("order", out _), "Приказ должен приниматься.");
AssertTrue(!InspectionBasisDocumentType.TryParse("report", out _), "Отчёт не входит в справочник ОРД.");
AssertTrue(AiChecklistDraftRules.CanContinue([]), "ОРД не должны блокировать процесс.");
```

- [ ] **Step 2: Run tests and confirm RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

- [ ] **Step 3: Add draft/document schema and file store**

Create `app_ai_checklist_drafts` and `app_inspection_basis_documents`. Store binaries under `/data/ord/<draft-id>/<document-id>` using generated names; persist original name, type, media type, byte length and SHA-256. Validate extension and size before moving the temporary upload. Delete only the requested file and clean expired unattached drafts.

- [ ] **Step 4: Link draft conversion to run creation**

Create the run and mark the draft converted in one database transaction boundary. Copy document links to `run_id`. Do not include document text or metadata in evidence, prompts, queries, or batch planning.

- [ ] **Step 5: Run persistence tests and commit**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

```powershell
git add src/OssDemo.Web/AiChecklists src/OssDemo.Web/Program.cs tests/OssDemo.Checklists.Tests
git commit -m "feat: persist checklist drafts and ord files"
```

### Task 5: Основной шестишаговый пользовательский путь

**Files:**
- Modify: `src/OssDemo.Web/Pages/Checklists/AiNew.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/ai-checklists.js`
- Modify: `src/OssDemo.Web/Pages/Shared/_Layout.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistRunChecks.cs`

**Interfaces:**
- Consumes: Tasks 1, 3 and 4 APIs.
- Produces: resumable six-step wizard and makes `/Checklists/AiNew` the sidebar checklist target.

- [ ] **Step 1: Add run snapshot checks for template and resolved requirements**

```csharp
AssertEqual(draftId, run.DraftId);
AssertEqual(selectedTemplateId, run.Snapshot.TemplateId);
AssertTrue(run.Snapshot.ItemTraces.All(x => resolvedRequirementIds.Contains(x.RequirementId)), "Запуск должен фиксировать итоговый каталог.");
```

- [ ] **Step 2: Run tests and confirm new snapshot expectations fail**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

- [ ] **Step 3: Implement the six steps and autosave**

Persist after object/template selection and successful document upload. Restore the current step from the draft. Show `Сохранение`, `Сохранено`, or a retryable error through `aria-live`. Keep `Назад` and `Продолжить` in a stable footer; enable generation without documents.

- [ ] **Step 4: Improve applicability and progress screens**

Before generation show included criteria, resolved requirement count, exclusions and reasons. During generation show deterministic catalog work separately from optional AI refinement. Do not represent the process as a fixed number of AI agents.

- [ ] **Step 5: Switch navigation and run smoke flow**

Change only the target of the existing sidebar item `Чек-лист` to `/Checklists/AiNew`. Complete a flow with zero OРД and a flow with two typed files; refresh between steps and verify recovery.

- [ ] **Step 6: Commit**

```powershell
git add src/OssDemo.Web/Pages/Checklists/AiNew.cshtml src/OssDemo.Web/Pages/Shared/_Layout.cshtml src/OssDemo.Web/wwwroot/js/ai-checklists.js src/OssDemo.Web/wwwroot/css/site.css tests/OssDemo.Checklists.Tests
git commit -m "feat: add resumable checklist workflow"
```

### Task 6: Реорганизация базы знаний без повторной индексации

**Files:**
- Create: `src/OssDemo.Web/KnowledgePathMigration.cs`
- Modify: `src/OssDemo.Web/KnowledgeImportService.cs`
- Modify: `src/OssDemo.Web/KnowledgeDocumentMetadata.cs`
- Modify: `src/OssDemo.Web/Pages/KnowledgeBase.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/knowledge-base.js`
- Move: files under `data/import/knowledge-inbox/Документы ГТЧ` and `Документы ПАО`
- Move: files under `data/knowledge-base/Документы ГТЧ` and `Документы ПАО`
- Move: root requirement registries in both knowledge trees
- Create: `data/import/knowledge-inbox/Региональные документы/.gitkeep`
- Create: `data/knowledge-base/Региональные документы/.gitkeep`
- Test: `tests/OssDemo.Rag.Tests/Program.cs`

**Interfaces:**
- Produces: canonical relative path mapping and hash-based path reconciliation.
- Consumes: existing `knowledge_documents` hashes and `ragify_vectors` document IDs.

- [ ] **Step 1: Add failing canonical path checks**

```csharp
AssertEqual("Корпоративные документы/file.md", KnowledgePathMigration.Canonicalize("Документы ПАО/file.md"));
AssertEqual("Прочие нормативные документы/Реестр требований.md", KnowledgePathMigration.Canonicalize("Реестр требований.md"));
```

- [ ] **Step 2: Run RAG tests and confirm RED**

Run: `dotnet run --project tests/OssDemo.Rag.Tests/OssDemo.Rag.Tests.csproj`

- [ ] **Step 3: Implement idempotent path reconciliation**

For each moved file, calculate the existing import hash, locate one matching `knowledge_documents` row, and update only its path/title metadata. Keep the same document ID so vector rows remain attached. Abort on zero or multiple hash matches; never delete vectors to resolve ambiguity.

- [ ] **Step 4: Move filesystem content with collision checks**

Before each move, compare hashes when a destination name exists. Move identical content once; fail on different content with the same name. Place the three root registry Markdown files under `Прочие нормативные документы`. Remove old directories only after source counts and destination counts reconcile.

- [ ] **Step 5: Verify index invariants**

Run before and after migration:

```sql
SELECT count(*) FROM knowledge_documents;
SELECT count(*) FROM ragify_vectors;
SELECT count(*) FROM ragify_vectors v LEFT JOIN knowledge_documents d ON d.id=v.document_id WHERE d.id IS NULL;
```

Expected on the current dataset: 240 documents, 28590 chunks, 0 orphan chunks.

- [ ] **Step 6: Run tests and commit**

Run: `dotnet run --project tests/OssDemo.Rag.Tests/OssDemo.Rag.Tests.csproj`

```powershell
git add src/OssDemo.Web data/import/knowledge-inbox data/knowledge-base tests/OssDemo.Rag.Tests
git commit -m "feat: organize knowledge document categories"
```

### Task 7: Общий UX остальных страниц и финальная стабилизация

**Files:**
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`
- Modify: `src/OssDemo.Web/wwwroot/js/site.js`
- Modify: `src/OssDemo.Web/Pages/Schedule.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/schedule-calendar.js`
- Modify: `src/OssDemo.Web/Pages/Facilities.cshtml`
- Modify: `src/OssDemo.Web/Pages/FacilityCard.cshtml`
- Modify: `src/OssDemo.Web/Pages/Violations.cshtml`
- Modify: `src/OssDemo.Web/Pages/AuditLog.cshtml`
- Modify: `src/OssDemo.Web/Pages/Profile.cshtml`
- Modify: `src/OssDemo.Web/Pages/Login.cshtml`
- Modify: `src/OssDemo.Web/Pages/Index.cshtml`

**Interfaces:**
- Consumes: existing page APIs and shared visual language.
- Produces: consistent page headers, feedback, filters, focus, density and responsive behavior without menu renames.

- [ ] **Step 1: Add shared UI primitives**

Define CSS classes for page header, toolbar, status message, empty state, sticky action row, compact data row and focus ring. Add one `announceStatus(element, kind, message)` helper in `site.js`; remove page-specific duplicate notification styling only where touched.

- [ ] **Step 2: Correct the schedule workflow**

Configure FullCalendar for month/multi-day inspection ranges without required time input. Preserve start/end dates and show status, object and checklist state in event cards. Keep event editing routes unchanged.

- [ ] **Step 3: Apply the shared flow to the remaining pages**

Objects: make list → card → edit/profile readiness explicit. Violations: keep filters and status near the result list. Audit/profile/login/home: align headers, primary actions and feedback. Preserve all current routes and labels.

- [ ] **Step 4: Run full automated verification**

```powershell
dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj
dotnet run --project tests/OssDemo.Rag.Tests/OssDemo.Rag.Tests.csproj
docker compose --env-file .env.local -f compose.local.yml up -d --build
```

Expected: build exit 0, both test executables exit 0, database healthy, application running.

- [ ] **Step 5: Run responsive and end-to-end browser verification**

At 375, 768, 1024 and 1440 px verify Login, Schedule, KnowledgeBase, Classifier, Facilities, Violations, system templates and AiNew. Complete checklist formation for Березниковское ЛПУМГ, confirm resolved requirements appear in traces, and confirm a refresh restores the draft.

- [ ] **Step 6: Verify repository and runtime invariants**

Run: `git diff --check`, `git status --short`, Docker health check, HTTP `/Login`, and the three SQL counts from Task 6. Confirm no secret or `.env.local` is staged.

- [ ] **Step 7: Commit**

```powershell
git add src/OssDemo.Web tests
git commit -m "feat: improve application workflow usability"
```
