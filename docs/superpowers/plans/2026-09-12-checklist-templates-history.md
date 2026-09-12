# Шаблоны и история чек-листов — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести шаблоны, черновики и историю чек-листов в PostgreSQL, добавить CRUD-редактор шаблонов и неизменяемую историю после утверждения.

**Architecture:** `ChecklistService` реализует жизненный цикл поверх `IChecklistRepository`; `PostgresChecklistRepository` хранит нормализованные сущности и выполняет транзакции. Идемпотентный инициализатор создаёт схему и переносит шесть CSV-наборов из сгенерированных типизированных seed-данных. Razor Pages и отдельные JavaScript-контроллеры предоставляют вкладки шаблонов/истории, редактор, список черновиков и утверждение.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages и Minimal API, Npgsql 10, PostgreSQL, Bootstrap/Bootswatch, vanilla JavaScript, существующий консольный стиль тестов проекта.

**Spec:** `docs/superpowers/specs/2026-09-12-checklist-templates-history-design.md`

## Global Constraints

- После переноса приложение не читает CSV и не использует файловое хранилище рабочих чек-листов.
- Шаблоны можно создавать, редактировать, копировать и физически удалять.
- Черновик содержит снимок шаблона; изменение или удаление шаблона не меняет черновик и историю.
- В историю запись попадает только после утверждения и после этого становится неизменяемой.
- Исторические CSV импортируются как утверждённые записи, шаблонные CSV — как редактируемые шаблоны.
- Сервер записывает утверждающего как `inspector` и не принимает это имя от клиента.
- Составные операции выполняются транзакционно.
- UI сохраняет введённые данные при ошибке и управляется с клавиатуры без обязательного drag-and-drop.
- Новые frontend-фреймворки и ORM не добавляются.

---

### Task 1: Доменная модель и правила шаблонов

**Files:**
- Create: `src/OssDemo.Web/Checklists/ChecklistModels.cs`
- Create: `src/OssDemo.Web/Checklists/ChecklistRules.cs`
- Create: `tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`
- Create: `tests/OssDemo.Checklists.Tests/Program.cs`
- Modify: `src/OssDemo.Web/OssDemo.Web.csproj`
- Modify: `OssDemo.slnx`

**Interfaces:**
- Produces: `ChecklistStatus`, `ChecklistTemplateWriteRequest`, `ChecklistTemplateDetails`, `ChecklistTemplateSummary`, `CreateChecklistRequest`, `AddChecklistItemRequest`, `ChecklistHistoryFilter`, `ChecklistSummary`, `ChecklistDetails` and `ChecklistOperationResult<T>`.
- Produces: `ChecklistRules.NormalizeTemplate(ChecklistTemplateWriteRequest)` and `ChecklistRules.EnsureDraft(ChecklistDetails)`.

- [ ] **Step 1: Create the focused console test project and failing rule checks**

Create a `net10.0` executable referencing `OssDemo.Web`, add it to `OssDemo.slnx`, and expose internals to `OssDemo.Checklists.Tests`. Add:

```csharp
var invalid = ChecklistRules.NormalizeTemplate(new ChecklistTemplateWriteRequest(" ", Guid.Empty, 0, []));
AssertTrue(!invalid.IsSuccess);
AssertTrue(invalid.Errors.ContainsKey("name"));
AssertTrue(invalid.Errors.ContainsKey("facilityId"));
AssertTrue(invalid.Errors.ContainsKey("sections"));

var valid = ChecklistRules.NormalizeTemplate(new(
    "  Проверка ПЭК  ", facilityId, 3,
    [new("  Общие вопросы  ", 9, [new("  Позиция  ", "  Статья 1  ", "  Комментарий  ", 7)])]));
AssertEqual("Проверка ПЭК", valid.Value!.Name);
AssertEqual(1, valid.Value.Sections[0].Position);
AssertEqual(1, valid.Value.Sections[0].Items[0].Position);
```

- [ ] **Step 2: Run the test and verify red**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: compilation fails because `ChecklistRules` and request/result records do not exist.

- [ ] **Step 3: Add domain records and validation**

Define:

```csharp
internal sealed record ChecklistTemplateWriteRequest(
    string? Name, Guid FacilityId, long Version,
    IReadOnlyList<ChecklistTemplateSectionWrite> Sections);
internal sealed record ChecklistTemplateSectionWrite(
    string? Title, int Position, IReadOnlyList<ChecklistTemplateItemWrite> Items);
internal sealed record ChecklistTemplateItemWrite(
    string? Title, string? Basis, string? Note, int Position);
internal enum ChecklistStatus { Draft, Approved }
internal sealed record CreateChecklistRequest(
    Guid TemplateId, string? Name, Guid FacilityId,
    DateOnly? InspectionStartedOn, DateOnly? InspectionFinishedOn);
internal sealed record ChecklistHistoryFilter(
    string? Search, Guid? FacilityId, DateOnly? From, DateOnly? To);
```

`NormalizeTemplate` trims strings, rejects an empty name, `Guid.Empty` facility, empty section title, empty item title/basis, and templates without at least one section and item. It rewrites positions to contiguous one-based values. `EnsureDraft` returns code `state_conflict` unless status is `draft`.

- [ ] **Step 4: Run the test and verify green**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: exit 0 and `Checklist domain checks passed.`

- [ ] **Step 5: Commit**

```powershell
git add OssDemo.slnx src/OssDemo.Web/OssDemo.Web.csproj src/OssDemo.Web/Checklists tests/OssDemo.Checklists.Tests
git commit -m "Добавить доменную модель чек-листов"
```

---

### Task 2: Seed-данные и идемпотентная схема PostgreSQL

**Files:**
- Create: `src/OssDemo.Web/Checklists/ChecklistSeedData.cs`
- Create: `src/OssDemo.Web/Checklists/ChecklistDatabaseInitializer.cs`
- Create: `tools/GenerateChecklistSeed.ps1`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`
- Modify: `src/OssDemo.Web/Program.cs`

**Interfaces:**
- Produces: `ChecklistSeedData.Templates` and `ChecklistSeedData.History` with stable GUIDs and every current CSV row.
- Produces: `ChecklistDatabaseInitializer.EnsureInitializedAsync(CancellationToken)`.
- Consumes: `ConnectionStrings:OssDatabase` and existing `app_facilities`.

- [ ] **Step 1: Add failing seed invariants**

```csharp
AssertEqual(3, ChecklistSeedData.Templates.Count);
AssertEqual(3, ChecklistSeedData.History.Count);
AssertTrue(ChecklistSeedData.History.All(x => x.Status == ChecklistStatus.Approved));
AssertTrue(ChecklistSeedData.Templates.Select(x => x.Id).Distinct().Count() == 3);
AssertTrue(ChecklistSeedData.History.SelectMany(x => x.Items)
    .All(x => !string.IsNullOrWhiteSpace(x.Title)));
```

- [ ] **Step 2: Run and verify red**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: compilation fails because `ChecklistSeedData` does not exist.

- [ ] **Step 3: Generate typed seed data**

`tools/GenerateChecklistSeed.ps1` reads `catalog.json`, parses all CSV fields, groups contiguous section names, escapes C# literals, and writes `ChecklistSeedData.cs`. It assigns fixed GUIDs derived from the six catalog IDs and preserves `result`, `nonconformity` and `note`.

Run: `pwsh -File tools/GenerateChecklistSeed.ps1`

Expected: three templates and three histories with all source rows embedded as typed records; generated code performs no file reads.

- [ ] **Step 4: Implement schema and migration**

First run the same compatible `CREATE TABLE IF NOT EXISTS app_facilities` definition used by `OperationalDataService`, because checklist initialization runs eagerly while operational initialization is lazy. Then create all checklist tables, constraints and indexes from the approved spec. Execute seed insertion once under key `checklists-postgres-v1`:

```csharp
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
if (!await MigrationExistsAsync(connection, transaction, SeedMigrationKey, cancellationToken))
{
    await EnsureSeedFacilitiesAsync(connection, transaction, cancellationToken);
    await InsertTemplatesAsync(connection, transaction, ChecklistSeedData.Templates, cancellationToken);
    await InsertHistoryAsync(connection, transaction, ChecklistSeedData.History, cancellationToken);
    await RecordMigrationAsync(connection, transaction, SeedMigrationKey, cancellationToken);
}
await transaction.CommitAsync(cancellationToken);
```

Register the singleton and call it after `builder.Build()` but before serving requests. Initialization failure aborts startup; no file fallback is allowed.

- [ ] **Step 5: Verify seed rules and Release build**

Run:

```powershell
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj
dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore -c Release
```

Expected: checks pass; build reports 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add src/OssDemo.Web/Checklists src/OssDemo.Web/Program.cs tests/OssDemo.Checklists.Tests/Program.cs tools/GenerateChecklistSeed.ps1
git commit -m "Перенести исходные чек-листы в PostgreSQL"
```

---

### Task 3: Репозиторий и жизненный цикл

**Files:**
- Create: `src/OssDemo.Web/Checklists/IChecklistRepository.cs`
- Create: `src/OssDemo.Web/Checklists/PostgresChecklistRepository.cs`
- Replace: `src/OssDemo.Web/ChecklistService.cs`
- Create: `tests/OssDemo.Checklists.Tests/InMemoryChecklistRepository.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`
- Modify: `src/OssDemo.Web/Program.cs`

**Interfaces:**
- Repository produces the exact contract below; `ChecklistService` exposes matching use-case methods and adds validation before calling it.
- `ChecklistService` validates requests and enforces state before delegating to the repository.

```csharp
internal interface IChecklistRepository
{
    Task<IReadOnlyList<ChecklistTemplateSummary>> ListTemplatesAsync(CancellationToken ct);
    Task<ChecklistTemplateDetails?> GetTemplateAsync(Guid id, CancellationToken ct);
    Task<ChecklistTemplateDetails> CreateTemplateAsync(ChecklistTemplateWriteRequest request, CancellationToken ct);
    Task<ChecklistOperationResult<ChecklistTemplateDetails>> UpdateTemplateAsync(Guid id, ChecklistTemplateWriteRequest request, CancellationToken ct);
    Task<ChecklistOperationResult<ChecklistTemplateDetails>> CopyTemplateAsync(Guid id, string name, CancellationToken ct);
    Task<bool> DeleteTemplateAsync(Guid id, CancellationToken ct);
    Task<ChecklistOperationResult<ChecklistDetails>> CreateDraftAsync(CreateChecklistRequest request, CancellationToken ct);
    Task<ChecklistDetails?> GetChecklistAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ChecklistSummary>> ListDraftsAsync(CancellationToken ct);
    Task<IReadOnlyList<ChecklistSummary>> ListHistoryAsync(ChecklistHistoryFilter filter, CancellationToken ct);
    Task<ChecklistOperationResult<ChecklistDetails>> AddDraftItemAsync(Guid id, AddChecklistItemRequest request, CancellationToken ct);
    Task<ChecklistOperationResult<ChecklistDetails>> ApproveAsync(Guid id, string approvedBy, CancellationToken ct);
}
```

- [ ] **Step 1: Write failing lifecycle checks against an in-memory repository**

```csharp
var template = RequireSuccess(await service.CreateTemplateAsync(templateRequest, ct));
var draft = RequireSuccess(await service.CreateDraftAsync(new(template.Id, "Проверка", facilityId, null, null), ct));
await service.UpdateTemplateAsync(template.Id, changedTemplate with { Version = template.Version }, ct);
AssertEqual("Исходный пункт", (await service.GetChecklistAsync(draft.Id, ct))!.Items[0].Title);
AssertTrue((await service.DeleteTemplateAsync(template.Id, ct)).IsSuccess);
AssertTrue(await service.GetChecklistAsync(draft.Id, ct) is not null);
AssertTrue((await service.ApproveAsync(draft.Id, "inspector", ct)).IsSuccess);
AssertEqual(0, (await service.ListDraftsAsync(ct)).Count);
AssertTrue((await service.ListHistoryAsync(new(null, null, null, null), ct)).Any(x => x.Id == draft.Id));
AssertEqual("state_conflict", (await service.AddItemAsync(draft.Id, manualItem, ct)).ErrorCode);
```

- [ ] **Step 2: Run and verify red**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: compilation fails because repository/service contracts do not exist.

- [ ] **Step 3: Implement repository boundary and service**

Parameterize every SQL value. Wrap every multi-table mutation in a transaction. Template update uses:

```sql
UPDATE app_checklist_templates
SET name = @name, facility_id = @facilityId,
    version = version + 1, updated_at = now()
WHERE id = @id AND version = @version;
```

When affected rows are zero, check existence to distinguish `not_found` from `version_conflict`. Draft creation locks the template, copies ordered sections/items, stores snapshot names, and commits atomically. Approval updates only `WHERE id = @id AND status = 'draft'`.

- [ ] **Step 4: Implement fake repository and verify green**

The fake clones nested records on read/write so snapshot tests cannot pass through shared references.

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: domain, seed, CRUD, snapshot and approval checks pass.

- [ ] **Step 5: Commit**

```powershell
git add src/OssDemo.Web/Checklists src/OssDemo.Web/ChecklistService.cs src/OssDemo.Web/Program.cs tests/OssDemo.Checklists.Tests
git commit -m "Добавить хранение и жизненный цикл чек-листов"
```

---

### Task 4: HTTP API и ошибки

**Files:**
- Create: `src/OssDemo.Web/Checklists/ChecklistApi.cs`
- Create: `src/OssDemo.Web/Checklists/ChecklistApiResponses.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`

**Interfaces:**
- Produces endpoint groups `/api/checklist-templates` and `/api/checklists`.
- Produces JSON `{ error, code, fields }` with `validation=400`, `not_found=404` and state/version conflicts `=409`.

- [ ] **Step 1: Add failing result-mapping checks**

```csharp
AssertEqual(400, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("validation", "Проверьте поля.", errors)));
AssertEqual(404, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("not_found", "Не найдено.")));
AssertEqual(409, ChecklistApiResponses.StatusCode(ChecklistOperationResult<object>.Fail("version_conflict", "Версия изменилась.")));
```

- [ ] **Step 2: Run and verify red**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: compilation fails because `ChecklistApiResponses` is absent.

- [ ] **Step 3: Map all specified endpoints**

Move checklist routes out of `Program.cs` into `ChecklistApi.MapChecklistApi(WebApplication)`. Remove catalog/source endpoints. Include CRUD/copy/delete, drafts, filtered history, get, create draft, add item and approve.

```csharp
app.MapPost("/api/checklists/{id:guid}/approve",
    async (Guid id, ChecklistService service, CancellationToken ct) =>
        ChecklistApiResponses.ToResult(
            await service.ApproveAsync(id, LoginModel.UserName, ct)));
```

- [ ] **Step 4: Verify mappings and build**

Run:

```powershell
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj
dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore -c Release
```

Expected: checks pass; build reports 0 errors.

- [ ] **Step 5: Commit**

```powershell
git add src/OssDemo.Web/Checklists/ChecklistApi.cs src/OssDemo.Web/Checklists/ChecklistApiResponses.cs src/OssDemo.Web/Program.cs tests/OssDemo.Checklists.Tests/Program.cs
git commit -m "Добавить API шаблонов и истории чек-листов"
```

---

### Task 5: Вкладка шаблонов и редактор

**Files:**
- Replace: `src/OssDemo.Web/Pages/Checklists/Archive.cshtml`
- Create: `src/OssDemo.Web/Pages/Checklists/TemplateEditor.cshtml`
- Create: `src/OssDemo.Web/wwwroot/js/checklist-library.js`
- Create: `src/OssDemo.Web/wwwroot/js/checklist-template-editor.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`
- Modify: `src/OssDemo.Web/Pages/Checklists/New.cshtml`

**Interfaces:**
- Consumes template CRUD/copy/delete endpoints and `/api/operations/facilities`.
- Produces `[data-checklist-library]` with template/history tabs.
- Produces editor payload matching `ChecklistTemplateWriteRequest`.

- [ ] **Step 1: Generate focused UI guidance**

Run:

```powershell
python C:/Users/Maany/.agents/skills/ui-ux-pro-max/scripts/search.py "enterprise checklist template editor dense accessible" --design-system -p "АИ ООС"
python C:/Users/Maany/.agents/skills/ui-ux-pro-max/scripts/search.py "inline validation keyboard focus destructive confirmation" --domain ux
```

Use only recommendations compatible with existing `site.css` tokens and Bootstrap.

- [ ] **Step 2: Build separate accessible tabs**

Use query routes `?tab=templates` and `?tab=history` with visible selected state. The templates pane includes search, loading/empty/error states, «Создать шаблон», and a table with object, counts, update time and actions.

- [ ] **Step 3: Implement list/copy/delete**

`checklist-library.js` escapes server text, filters by name/object, and uses an `aria-live` alert. Delete confirmation names the template. Copy uses a labeled modal and navigates to the new editor after success.

- [ ] **Step 4: Build the standalone editor**

Render name, facility and dynamic sections. Add rename/delete/up/down for sections and title/basis/note plus delete/up/down for items. Keep state after errors and map server `fields` to inputs.

```javascript
const payload = {
  name: nameInput.value,
  facilityId: facilitySelect.value,
  version: state.version,
  sections: state.sections.map((section, sectionIndex) => ({
    title: section.title,
    position: sectionIndex + 1,
    items: section.items.map((item, itemIndex) => ({ ...item, position: itemIndex + 1 }))
  }))
};
```

- [ ] **Step 5: Add responsive accessible styling**

Reuse existing tokens and breakpoints. Use 4/8px spacing, visible focus, 44px action targets, collapsible metadata columns, scroll-safe content, and reduced-motion handling.

- [ ] **Step 6: Verify static contracts and build**

Run:

```powershell
rg -n "data-checklist-library|data-template-editor|Создать шаблон|История" src/OssDemo.Web/Pages/Checklists src/OssDemo.Web/wwwroot/js
dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore -c Release
```

Expected: contracts are found; build reports 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add src/OssDemo.Web/Pages/Checklists src/OssDemo.Web/wwwroot/js/checklist-library.js src/OssDemo.Web/wwwroot/js/checklist-template-editor.js src/OssDemo.Web/wwwroot/css/site.css
git commit -m "Добавить редактор шаблонов чек-листов"
```

---

### Task 6: Черновики, утверждение и история

**Files:**
- Modify: `src/OssDemo.Web/Pages/Checklists/New.cshtml`
- Modify: `src/OssDemo.Web/Pages/Checklists/Result.cshtml`
- Modify: `src/OssDemo.Web/Pages/Checklists/Archive.cshtml`
- Replace: `src/OssDemo.Web/wwwroot/js/checklists.js`
- Modify: `src/OssDemo.Web/wwwroot/js/checklist-library.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`

**Interfaces:**
- Consumes draft/history/get/approve APIs and template list.
- Produces draft continuation list, read-only historical detail and status-aware result page.

- [ ] **Step 1: Load templates and drafts from PostgreSQL**

Replace catalog filtering with `GET /api/checklist-templates`. Render «Незавершённые чек-листы» with name, object, timestamps and «Продолжить». Creating a checklist sends the template UUID and opens `/Checklists/Result?id=<uuid>`.

- [ ] **Step 2: Make Result status-aware**

For `draft` show manual item controls and approval action. For `approved` hide all mutation controls, show approval metadata and label «Утверждён». Render API failures in an alert.

- [ ] **Step 3: Implement approval confirmation**

The modal names the checklist and warns that the action is irreversible. Disable confirmation during the request. On success navigate to `/Checklists/Archive?tab=history&open=<id>`; on `409` reload actual state.

- [ ] **Step 4: Render filtered history**

Query with `search`, `facilityId`, `from` and `to`. Render name, object, period, approval time and approver. Open the existing Result page in read-only mode to reuse table and export controls.

- [ ] **Step 5: Verify contracts, lifecycle tests and build**

Run:

```powershell
rg -n "Незавершённые чек-листы|Утвердить чек-лист|/api/checklists/history" src/OssDemo.Web/Pages/Checklists src/OssDemo.Web/wwwroot/js
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj
dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore -c Release
```

Expected: contracts are found, checks pass, build reports 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add src/OssDemo.Web/Pages/Checklists src/OssDemo.Web/wwwroot/js/checklists.js src/OssDemo.Web/wwwroot/js/checklist-library.js src/OssDemo.Web/wwwroot/css/site.css
git commit -m "Разделить черновики и историю чек-листов"
```

---

### Task 7: Экспорт сохранённого чек-листа

**Files:**
- Create: `src/OssDemo.Web/Checklists/ChecklistExportService.cs`
- Modify: `src/OssDemo.Web/Checklists/ChecklistApi.cs`
- Modify: `src/OssDemo.Web/Pages/Checklists/Result.cshtml`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`
- Modify: `src/OssDemo.Web/Program.cs`

**Interfaces:**
- Produces `CreateXlsx(ChecklistDetails)`, `CreateDocx(ChecklistDetails)` and `CreatePdf(ChecklistDetails)` returning `ExportedChecklist`.
- Produces routes `/exports/checklists/{id}.xlsx`, `.docx` and `.pdf`.

- [ ] **Step 1: Add failing export checks**

```csharp
var xlsx = ChecklistExportService.CreateXlsx(approvedChecklist);
AssertTrue(xlsx.Content.Length > 0);
AssertTrue(ReadZipEntry(xlsx.Content, "xl/worksheets/sheet1.xml").Contains("Проверка ПЭК"));
AssertTrue(ReadZipEntry(ChecklistExportService.CreateDocx(approvedChecklist).Content, "word/document.xml").Contains("Статья 1"));
AssertTrue(ChecklistExportService.CreatePdf(approvedChecklist).Content.AsSpan(0, 5).SequenceEqual("%PDF-"u8));
```

- [ ] **Step 2: Run and verify red**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`

Expected: compilation fails because `ChecklistExportService` does not exist.

- [ ] **Step 3: Adapt existing builders to actual data**

Move ZIP/XML/PDF helpers out of `Program.cs`. XML-escape all fields, generate every item row, and create a safe filename from checklist name/ID. Export endpoints load the requested saved checklist and return `404` when absent.

- [ ] **Step 4: Link exports from Result**

Show XLSX/DOCX/PDF links after load and insert the encoded checklist ID. Exports remain available for draft review and approved history.

- [ ] **Step 5: Verify exports and build**

Run:

```powershell
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj
dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore -c Release
```

Expected: export checks pass; build reports 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add src/OssDemo.Web/Checklists/ChecklistExportService.cs src/OssDemo.Web/Checklists/ChecklistApi.cs src/OssDemo.Web/Pages/Checklists/Result.cshtml src/OssDemo.Web/Program.cs tests/OssDemo.Checklists.Tests/Program.cs
git commit -m "Подключить экспорт сохранённых чек-листов"
```

---

### Task 8: Удаление CSV-контура и сквозная проверка

**Files:**
- Delete: `src/OssDemo.Web/Data/Checklists/catalog.json`
- Delete: `src/OssDemo.Web/Data/Checklists/*.csv`
- Delete: `tools/GenerateChecklistSeed.ps1`
- Modify: `src/OssDemo.Web/OssDemo.Web.csproj`
- Modify: `README.md`

**Interfaces:**
- Removes catalog/source APIs, `Checklists:Directory`, CSV parsing, JSON persistence and publish-copy declarations.
- Preserves PostgreSQL templates, drafts, approval, history and exports.

- [ ] **Step 1: Remove file-backed runtime**

Delete catalog/CSV files, the one-time seed generator and their `Content` entries. Remove old source directory, working directory, parser, JSON persistence, records and endpoints.

- [ ] **Step 2: Prove no file-backed path remains**

Run:

```powershell
rg -n "Data[/\\]Checklists|catalog\\.json|ParseCsv|Checklists:Directory|/api/checklists/catalog|/api/checklists/sources" src README.md
```

Expected: no matches.

- [ ] **Step 3: Document the workflow**

Update `README.md` with PostgreSQL tables, idempotent initialization, template CRUD, drafts, approval and immutable history.

- [ ] **Step 4: Run fresh verification**

```powershell
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj
dotnet build OssDemo.slnx --no-restore -c Release
git diff --check
git status --short
```

Expected: checks exit 0; build reports 0 errors; diff is clean; status contains only intended files or is clean after commits.

- [ ] **Step 5: Smoke-test only with an identified non-production PostgreSQL connection**

Start the app with an explicit test connection and verify: three seed templates, three histories, template create/edit/copy/delete, draft snapshot after template edit, approval, history appearance and immutable mutation response. Do not run this against an unidentified database.

- [ ] **Step 6: Commit cleanup**

```powershell
git add -A src/OssDemo.Web/Data/Checklists src/OssDemo.Web/OssDemo.Web.csproj tools/GenerateChecklistSeed.ps1 README.md
git commit -m "Удалить файловое хранение чек-листов"
```

- [ ] **Step 7: Review and push**

```powershell
git log --oneline origin/main..HEAD
git diff --stat origin/main...HEAD
git push origin main
git fetch origin
git rev-list --left-right --count origin/main...HEAD
```

Expected: push succeeds and final divergence is `0 0`.
