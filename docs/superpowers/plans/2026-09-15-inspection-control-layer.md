# Inspection Control Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace one-requirement-per-item expansion with a deterministic, facility-specific catalog of 235 human-authored inspection controls.

**Architecture:** Join the complete inspector template with verified requirement links to create `InspectionControl` records. Select controls by applicable classifier section; preserve verified requirement IDs where available, mark other links as unmapped, and never emit `requirements-registry-direct-v1` rows as checklist items.

**Tech Stack:** .NET 10, ASP.NET Core, C#, PostgreSQL JSONB, PowerShell, Docker Compose, JSONL catalogs.

**Spec:** `docs/superpowers/specs/2026-09-15-inspection-control-layer-design.md`

## Global Constraints

- Keep all 235 template items; an ambiguous registry link must not remove a control.
- Unknown card facts do not block checklist creation and remain visible in classifier decisions.
- Every item retains the normative `Basis` from the inspector template.
- `requirements-registry-direct-v1` rows remain knowledge data but never become checklist items.
- LLM availability must not affect deterministic control selection.
- Do not push or deploy; create local commits only.

---

### Task 1: Build the inspection-control catalog

**Files:**
- Create: `src/OssDemo.Web/Checklists/InspectionControlCatalog.cs`
- Modify: `src/OssDemo.Web/Checklists/FacilityChecklistCatalogs.cs`
- Modify: `tests/OssDemo.Checklists.Tests/ChecklistItemCatalogChecks.cs`

**Interfaces:**
- Consumes: `InspectorChecklistTemplateItem`, `ChecklistItemCatalogItem`.
- Produces: `InspectionControlCatalog.Build(...)` and `FacilityChecklistCatalogs.Controls`.

- [ ] **Step 1: Write the failing catalog test**

```csharp
var template = InspectorChecklistTemplate.ParseLines(File.ReadLines(templatePath));
var controls = InspectionControlCatalog.Build(template, approved);
AssertEqual(235, controls.Count);
AssertEqual(34, controls.Count(item => item.LinkStatus == "verified"));
AssertEqual(201, controls.Count(item => item.LinkStatus == "unmapped"));
AssertTrue(controls.All(item => !string.IsNullOrWhiteSpace(item.Basis)),
    "Каждая процедура должна сохранять нормативное основание.");
AssertTrue(controls.All(item => !item.Provenance.StartsWith("requirements-registry-direct", StringComparison.OrdinalIgnoreCase)),
    "Прямые строки реестра не являются контрольными процедурами.");
```

- [ ] **Step 2: Verify RED**

Run `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`.
Expected: compilation fails because `InspectionControlCatalog` does not exist.

- [ ] **Step 3: Implement the catalog join**

Create this public internal contract:

```csharp
internal sealed record InspectionControl(
    string Id, int Position, string SectionCode, string Section,
    string Title, string Basis, IReadOnlyList<string> ClassifierCodes,
    IReadOnlyList<string> RequirementIds, string LinkStatus,
    string Provenance, string LinkExplanation);

internal static class InspectionControlCatalog
{
    public static IReadOnlyList<InspectionControl> Build(
        IReadOnlyList<InspectorChecklistTemplateItem> template,
        IReadOnlyList<ChecklistItemCatalogItem> linkedItems);
}
```

Index only non-direct linked items by template ID. Return every template row in position order. A linked row receives `verified` and its requirement metadata; an unlinked row receives `unmapped`, empty IDs, provenance `inspector-template-v1`, and an explanation that the exact registry link needs confirmation.

Load the template in `FacilityChecklistCatalogs`, expose `Controls`, and add `inspectorChecklistTemplate` to `CatalogHashes`.

- [ ] **Step 4: Verify GREEN**

Run `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`.
Expected: `Checklist domain checks passed.`

- [ ] **Step 5: Commit**

```powershell
git add src/OssDemo.Web/Checklists/InspectionControlCatalog.cs src/OssDemo.Web/Checklists/FacilityChecklistCatalogs.cs tests/OssDemo.Checklists.Tests/ChecklistItemCatalogChecks.cs
git commit -m "feat: build inspection controls from reference checklist"
```

---

### Task 2: Select controls instead of raw requirements

**Files:**
- Modify: `src/OssDemo.Web/Checklists/FacilityChecklistComposer.cs`
- Modify: `tests/OssDemo.Checklists.Tests/FacilityChecklistComposerChecks.cs`

**Interfaces:**
- Consumes: `IReadOnlyList<InspectionControl>` and existing classifier decisions.
- Produces: `FacilityChecklistComposition.Items` as `IReadOnlyList<InspectionControl>` with run-specific classifier codes.

- [ ] **Step 1: Write failing selection tests**

Build the composer from the shipped template and links. Add literal acceptance cases:

```csharp
var noImpacts = Profile("no-impacts", FacilityFactState.Absent);
AssertEqual(31, composer.Compose(noImpacts).Items.Count);

var berezniki = Profile("bereznikovskoe", FacilityFactState.Absent);
berezniki.Type = "";
berezniki.Category = "I категория";
foreach (var code in new[] { "air.emissions", "air.gasTreatment", "water.intake",
    "water.discharge", "water.treatment", "waste.generation",
    "nature.oopt", "zone.waterProtection" })
    berezniki.StructuredProfile!.Features[code] = new(FacilityFactState.Present);
var result = composer.Compose(berezniki);
AssertEqual(179, result.Items.Count);
AssertTrue(result.Items.Select(item => item.SectionCode).Distinct().Order()
    .SequenceEqual(new[] { "1", "2", "3", "4", "7" }));
```

Also assert that no item has direct-registry provenance and every selected item has a run-specific classifier code.

- [ ] **Step 2: Verify RED**

Run the domain checks. Expected: the old composer still selects thousands of direct rows.

- [ ] **Step 3: Implement section-based selection**

Change the constructor to accept controls. Derive included section codes from included classifier codes. Select every control in those sections, and replace its classifier codes with the included decisions from that section. Derive `SelectedRequirementIds` only from verified IDs carried by selected controls. Return no `CoverageGap` for an unmapped control; its `LinkStatus` is the honest uncertainty marker. Set `CanFinalizeChecklist` when at least one control is selected.

Remove coarse requirement selection and the `historicalItems`/`historicallyCovered`/direct fallback pipeline.

- [ ] **Step 4: Verify GREEN**

Run the domain checks. Expected: literal counts 31 and 179 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/OssDemo.Web/Checklists/FacilityChecklistComposer.cs tests/OssDemo.Checklists.Tests/FacilityChecklistComposerChecks.cs
git commit -m "feat: select reference controls by facility scope"
```

---

### Task 3: Persist and execute control-based runs

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Consumes: `FacilityChecklistCatalogs.Controls` and composed controls.
- Produces: the unchanged snapshot and paged trace APIs, populated with control IDs and optional requirement IDs.

- [ ] **Step 1: Write a failing orchestration test**

For a verified profile with only emissions present, assert:

```csharp
AssertEqual(68, run.Snapshot!.ExpectedItemCount);
AssertTrue(run.Snapshot.ItemTraces.All(item =>
    !item.Provenance.StartsWith("requirements-registry-direct", StringComparison.OrdinalIgnoreCase)));
AssertTrue(run.Snapshot.ItemTraces.Any(item => item.Provenance == "inspector-template-v1"));
AssertEqual(68, run.Batches.Sum(batch => batch.EvidenceIds.Count));
```

- [ ] **Step 2: Verify RED**

Run the domain checks. Expected: agent assertions fail while it still consumes `catalogs.Items`.

- [ ] **Step 3: Wire controls into run creation**

Instantiate the composer with `checklistCatalogs.Controls`. Keep snapshot JSON shape. Append `Статус нормативной связи: {LinkStatus}` to trace explanations. Build `CAT-` evidence with source `Утверждённая контрольная процедура`, preserve 50-control batches, and make no LLM calls.

- [ ] **Step 4: Verify GREEN**

Run the domain checks. Expected: `Checklist domain checks passed.`

- [ ] **Step 5: Commit**

```powershell
git add src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs
git commit -m "feat: persist control-based checklist runs"
```

---

### Task 4: Expose partial scope and verify Docker behavior

**Files:**
- Modify: `src/OssDemo.Web/wwwroot/js/ai-checklists.js`
- Modify: `src/OssDemo.Web/wwwroot/js/facility-card.js`
- Modify: `scripts/verify-local-docker.ps1`

**Interfaces:**
- Consumes: existing readiness, decisions, snapshot and trace JSON.
- Produces: non-blocking readiness wording and live assertions for reference-sized outputs.

- [ ] **Step 1: Add failing live assertions**

After the two existing probe runs, assert:

```powershell
if (@($officeIds).Count -ne 31) {
    throw "The no-impact profile must select 31 base controls; got $(@($officeIds).Count)."
}
if (@($industrialIds).Count -ne 68) {
    throw "The emissions profile must select 68 section 1+2 controls; got $(@($industrialIds).Count)."
}
```

Run `./scripts/verify-local-docker.ps1 -Live -RequireIndexedRag` against the current image.
Expected: FAIL because it still selects direct registry rows.

- [ ] **Step 2: Update wording**

Incomplete cards must say: `Чек-лист будет сформирован по известным данным. Неуточнённые признаки не войдут в область проверки.` Do not disable checklist creation based on readiness. Keep `blocked_unknown` visible as `Нужно уточнить`.

- [ ] **Step 3: Rebuild and verify GREEN**

```powershell
docker compose --project-name ossdemo-local --env-file .env.local -f compose.local.yml up -d --build application
./scripts/verify-local-docker.ps1 -Live -RequireIndexedRag
```

Expected: configuration, database, 31/68 selection, trace paging and indexed RAG checks pass.

- [ ] **Step 4: Commit**

```powershell
git add src/OssDemo.Web/wwwroot/js/ai-checklists.js src/OssDemo.Web/wwwroot/js/facility-card.js scripts/verify-local-docker.ps1
git commit -m "test: verify reference-sized checklists locally"
```

---

### Task 5: Verify Bereznikovskoye and document ownership

**Files:**
- Create: `docs/requirements/checklist-generation.md`
- Modify: `data/requirements/README.md`

**Interfaces:**
- Consumes: local `bereznikovskoe` profile and the checklist-run API.
- Produces: a verified 179-item real run and operational documentation.

- [ ] **Step 1: Create and inspect a real run**

POST `{ "facilitySlug": "bereznikovskoe" }` to the authenticated local `/api/ai-checklists/runs`. Query its snapshot:

```sql
SELECT jsonb_array_length(composition_snapshot->'selectedItemIds') AS items,
       jsonb_array_length(composition_snapshot->'itemTraces') AS traces
FROM app_ai_checklist_runs WHERE id = :'run_id';
```

Expected: `items = 179`, `traces = 179`, with no direct-registry provenance.

- [ ] **Step 2: Document responsibilities**

Document that the inspector template owns operational controls, the item catalog supplies verified links, the requirement registry supplies normative knowledge and future gap analysis, and `unmapped` does not claim exact registry coverage. State that document-derived card suggestions are the next independent plan.

- [ ] **Step 3: Run final verification**

```powershell
dotnet build OssDemo.slnx --no-restore
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --no-build
./scripts/verify-local-docker.ps1 -Live -RequireIndexedRag
git diff --check
git status --short
```

Expected: build has 0 warnings/errors; tests and Docker verification pass; only intended documentation changes remain.

- [ ] **Step 4: Commit and confirm local-only state**

```powershell
git add docs/requirements/checklist-generation.md data/requirements/README.md
git commit -m "docs: describe inspection control composition"
git status --short --branch
git log -6 --oneline
```

Expected: clean worktree on `chore/repository-organization`; no push or deployment command has run.
