# Classifier-First AI Checklist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a database-backed classifier tree and use it as the deterministic foundation for sequential, resumable AI checklist generation from a facility card.

**Architecture:** PostgreSQL stores versioned classifier sections, criteria, applicability rules and criterion references for generated items. A deterministic matcher converts facility fields into facts and selects applicable criteria; the existing single worker searches and synthesizes one criterion at a time, then a consolidator groups compatible criteria into concise checklist rows while retaining complete criterion coverage and traceability.

**Tech Stack:** .NET 9, ASP.NET Core Razor Pages and minimal APIs, Npgsql/PostgreSQL, vanilla JavaScript, Bootstrap, existing RAGify search and Amvera OpenAI-compatible inference.

**Spec:** `docs/superpowers/specs/2026-09-13-classifier-first-ai-checklist-design.md`

## Global Constraints

- The classifier is the only source of checklist structure; AI cannot introduce criteria outside it.
- Process criteria strictly sequentially with the existing single background worker.
- Do not impose a total generation timeout.
- A failed criterion must not block finalization or navigation to the working checklist.
- Historical checklists influence wording and priority only; current legal basis comes from verified knowledge-base citations.
- The existing mock flow at `/Checklists/New` and existing templates/history remain intact.
- Runtime behavior must not depend on Excel or CSV files.

---

### Task 1: Classifier domain, seed and PostgreSQL repository

**Files:**
- Create: `src/OssDemo.Web/Classifier/ClassifierModels.cs`
- Create: `src/OssDemo.Web/Classifier/ClassifierSeedData.cs`
- Create: `src/OssDemo.Web/Classifier/ClassifierDatabaseInitializer.cs`
- Create: `src/OssDemo.Web/Classifier/IClassifierRepository.cs`
- Create: `src/OssDemo.Web/Classifier/PostgresClassifierRepository.cs`
- Create: `src/OssDemo.Web/Classifier/ClassifierApi.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Create: `tests/OssDemo.Checklists.Tests/ClassifierChecks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`

**Interfaces:**
- Produces: `ClassifierTree`, `ClassifierCriterion`, `ClassifierCriterionWrite`, `IClassifierRepository.GetTreeAsync`, `SaveCriterionAsync`, `SetCriterionActiveAsync`.
- Consumes: `ConnectionStrings:OssDatabase` and the 7 groups/49 criteria transcribed from the approved XLSX.

- [ ] **Step 1: Add failing seed and validation checks**

```csharp
internal static void RunClassifierChecks()
{
    AssertEqual(7, ClassifierSeedData.Sections.Count);
    AssertEqual(49, ClassifierSeedData.Sections.Sum(section => section.Criteria.Count));
    AssertTrue(ClassifierSeedData.Sections.SelectMany(x => x.Criteria)
        .Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 49,
        "Коды классификатора должны быть уникальны.");
    var invalid = ClassifierRules.Normalize(new ClassifierCriterionWrite("", "", "", [], [], true, 0));
    AssertTrue(!invalid.IsSuccess, "Пустой критерий должен отклоняться.");
}
```

- [ ] **Step 2: Run `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj` and confirm the new types are missing**
- [ ] **Step 3: Add domain records, all 49 seed criteria and deterministic CRUD validation**
- [ ] **Step 4: Add idempotent tables `app_classifier_versions`, `app_classifier_sections`, `app_classifier_criteria` and seed only an empty classifier**
- [ ] **Step 5: Implement repository CRUD, ordering, soft deletion and `/api/classifier` endpoints**
- [ ] **Step 6: Register and initialize classifier services in `Program.cs` after checklist initialization**
- [ ] **Step 7: Run the checklist test project and commit the passing classifier storage slice**

---

### Task 2: Facility fact normalization and criterion applicability

**Files:**
- Create: `src/OssDemo.Web/Classifier/FacilityFacts.cs`
- Create: `src/OssDemo.Web/Classifier/ClassifierApplicabilityMatcher.cs`
- Modify: `src/OssDemo.Web/Classifier/ClassifierModels.cs`
- Modify: `tests/OssDemo.Checklists.Tests/ClassifierChecks.cs`

**Interfaces:**
- Produces: `FacilityFactNormalizer.Normalize(FacilityProfileFields, string? scheduleCriteria)` and `ClassifierApplicabilityMatcher.Match(ClassifierTree, FacilityFacts, IReadOnlyList<ChecklistHistoryReference>)`.
- Returns: selected criteria with `Reason`, `MatchedField`, `MatchedValue` and priority.

- [ ] **Step 1: Add failing tests for multiline fields, yes/no values, aliases such as `КОС/ЛОС`, base criteria and water/waste/emission selection**

```csharp
var facts = FacilityFactNormalizer.Normalize(new FacilityProfileFields {
    TreatmentFacilities = "Да (локальные очистные сооружения)",
    Zones = "КОС/ЛОС",
    EnvironmentalAspects = "Сбросы в водные объекты"
}, null);
var selected = ClassifierApplicabilityMatcher.Match(ClassifierSeedData.Tree, facts, []);
AssertTrue(selected.Any(x => x.Criterion.Code == "3.9"), "Очистные сооружения должны включать 3.9.");
AssertTrue(selected.All(x => !string.IsNullOrWhiteSpace(x.Reason)), "Причина применимости обязательна.");
```

- [ ] **Step 2: Run the tests and confirm selection checks fail**
- [ ] **Step 3: Implement normalized fact sets without calling the LLM**
- [ ] **Step 4: Implement editable rule evaluation for `always`, `contains-any`, `equals` and explicit schedule criterion codes**
- [ ] **Step 5: Run tests and commit deterministic applicability**

---

### Task 3: Criterion-oriented run state and sequential processing

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistModels.cs`
- Modify: `src/OssDemo.Web/AiChecklists/IAiChecklistRunStore.cs`
- Modify: `src/OssDemo.Web/AiChecklists/PostgresAiChecklistRunStore.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistDatabaseInitializer.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Replace behavior in: `src/OssDemo.Web/AiChecklists/AiChecklistQueryPlanner.cs`
- Modify: `tests/OssDemo.Checklists.Tests/InMemoryAiChecklistRunStore.cs`
- Modify: `tests/OssDemo.Checklists.Tests/AiChecklistRunChecks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces: `AiChecklistCriterionState` containing criterion codes, applicability reason, query, status, evidence, synthesis and error.
- Consumes: `IClassifierRepository`, `FacilityFactNormalizer`, `ClassifierApplicabilityMatcher`, `IAiChecklistKnowledgeSearch`.

- [ ] **Step 1: Add failing run checks that assert one queued work item is claimed at a time and carries criterion codes/reason/query**
- [ ] **Step 2: Add backward-compatible columns to AI run tables for criterion codes, applicability data and per-criterion evidence**
- [ ] **Step 3: Change run creation to select classifier criteria before any RAG or LLM call**
- [ ] **Step 4: Build one query per logical criterion candidate from criterion text, matching facility facts, document names and source hints**
- [ ] **Step 5: Move knowledge search into the single worker so criteria are searched and synthesized sequentially**
- [ ] **Step 6: Persist completion/failure after every criterion and keep already completed work untouched on resume**
- [ ] **Step 7: Run agent/run tests and commit the sequential classifier run slice**

---

### Task 4: Concise synthesis, history reference and coverage consolidation

**Files:**
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistHistoryReferenceSource.cs`
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistCriterionConsolidator.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistSynthesisClient.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistOutputParser.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistFallbackBuilder.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `src/OssDemo.Web/Checklists/IChecklistRepository.cs`
- Modify: `src/OssDemo.Web/Checklists/PostgresChecklistRepository.cs`
- Modify: `src/OssDemo.Web/Checklists/ChecklistDatabaseInitializer.cs`
- Modify: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces: concise `AiGeneratedChecklistItem` with `CriterionCodes`, verified citations and optional history style references.
- Produces: one or more `AiGeneratedDraftItem` rows whose criterion-code sets are disjoint and cover every selected criterion.

- [ ] **Step 1: Add failing tests for concise action wording, compatible `3.1+3.2` grouping, incompatible cross-section separation and fallback coverage**

```csharp
var rows = AiChecklistCriterionConsolidator.Consolidate(criteria, generated);
AssertTrue(rows.SelectMany(x => x.CriterionCodes).Order().SequenceEqual(criteria.Select(x => x.Code).Order()),
    "Каждый применимый критерий должен быть покрыт один раз.");
AssertTrue(rows.All(x => x.Title.StartsWith("Проверить ", StringComparison.OrdinalIgnoreCase)),
    "Формулировка должна быть проверочным действием.");
```

- [ ] **Step 2: Change the prompt to summarize only the supplied classifier criterion and return compact basis metadata with exact citations**
- [ ] **Step 3: Read approved history by facility/type/section for style and priority without reusing its legal basis**
- [ ] **Step 4: Implement deterministic consolidation by section and inspection subject, retaining every criterion code exactly once**
- [ ] **Step 5: Store criterion codes and full trace metadata separately from the compact checklist title/basis**
- [ ] **Step 6: Ensure failed/empty synthesis falls back to the classifier working wording and still finalizes**
- [ ] **Step 7: Run lifecycle and agent tests and commit the quality/coverage slice**

---

### Task 5: Dynamic classifier tree editor

**Files:**
- Replace static markup in: `src/OssDemo.Web/Pages/Classifier.cshtml`
- Create: `src/OssDemo.Web/wwwroot/js/classifier.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`

**Interfaces:**
- Consumes: `GET/POST/PUT/DELETE /api/classifier/...`.
- Produces: accessible expandable tree, search, criterion detail/editor and applicability rule editor.

- [ ] **Step 1: Replace hard-coded nodes with loading, empty and error states plus semantic tree/detail containers**
- [ ] **Step 2: Render section counts, active state, selection, automatic expansion and highlighted search matches**
- [ ] **Step 3: Render the criterion detail with risk, check wording, rules, source hints, usage count and recent sources**
- [ ] **Step 4: Add create/edit/activate/deactivate flows with inline validation and refresh-after-save**
- [ ] **Step 5: Add responsive styling for a wide two-column workspace and stacked mobile layout**
- [ ] **Step 6: Build the web project and commit the classifier UI**

---

### Task 6: Experimental navigation, real progress and standard result

**Files:**
- Modify: `src/OssDemo.Web/Pages/Shared/_Layout.cshtml`
- Modify: `src/OssDemo.Web/Pages/Privacy.cshtml`
- Modify: `src/OssDemo.Web/Pages/Checklists/AiNew.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/ai-checklists.js`
- Modify: `src/OssDemo.Web/wwwroot/js/checklists.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`

**Interfaces:**
- Consumes: criterion-oriented run state from `/api/ai-checklists/runs/{id}`.
- Produces: real progress labels and links to `/Checklists/Result?id={checklistId}`.

- [ ] **Step 1: Remove the experimental sidebar item and add the «Эксперимент» card to «О системе»**
- [ ] **Step 2: Replace batch-centric copy with stages for card analysis, classifier matching, current criterion, search, citation verification, summarization and save**
- [ ] **Step 3: Show selected criterion count, current code/title, applicability reason and completed/failed counts without a fake timer**
- [ ] **Step 4: Permit finalization after all criteria reach completed or failed and link to the ordinary working checklist**
- [ ] **Step 5: Display the compact source as `Классификатор <codes> + база знаний` in the standard result table**
- [ ] **Step 6: Build and commit navigation/progress integration**

---

### Task 7: Full verification and delivery

**Files:**
- Modify only files required by failures found during verification.

**Interfaces:**
- Consumes all previous tasks.
- Produces a deployable main branch commit series.

- [ ] **Step 1: Run `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`**
- [ ] **Step 2: Run `dotnet run --project tests/OssDemo.Rag.Tests/OssDemo.Rag.Tests.csproj`**
- [ ] **Step 3: Run `dotnet build src/OssDemo.Web/OssDemo.Web.csproj --no-restore`**
- [ ] **Step 4: Run `git diff --check` and inspect the complete diff for accidental generated files or secrets**
- [ ] **Step 5: Verify the repository is on `main`, fetch origin and resolve any divergence without discarding local work**
- [ ] **Step 6: Push the tested commits to `origin/main` and verify local HEAD equals `origin/main`**
