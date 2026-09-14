# Facility-Driven Checklist Cycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a traceable, object-specific cycle from a verified structured facility profile through classifier criteria and regulatory requirements to approved checklist items.

**Architecture:** Keep the facility profile in the existing JSONB record but introduce a versioned structured schema, explicit tri-state facts, readiness validation, and a legacy migration adapter. Build a versioned checklist-item catalog offline from the approved registry and historical checklists, then use deterministic runtime selectors and a coverage report; LLM may propose unresolved links but cannot approve them.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages and minimal APIs, PostgreSQL 17/JSONB, vanilla JavaScript, Python 3 catalog builders, JSONL manifests, local Docker Compose.

**Spec:** `docs/superpowers/specs/2026-09-15-facility-driven-checklist-cycle-design.md`

## Global Constraints

- Do not change the mock flow at `/Checklists/New`.
- Do not rewrite existing checklists or history.
- Do not push or deploy; create local commits only.
- Preserve original legacy profile text and mark migrated values `needs_review`.
- Never treat `unknown` as `absent`.
- Runtime LLM calls cannot determine applicability or approve catalog links.
- A completed checklist must have no silently uncovered selected requirements.
- XLSX and DOCX files remain build inputs, not runtime dependencies.

---

## File Structure

- `src/OssDemo.Web/Facilities/FacilityProfileV2.cs`: versioned profile, tri-state feature and document types.
- `src/OssDemo.Web/Facilities/FacilityProfileMigration.cs`: legacy-to-v2 conversion without destructive writes.
- `src/OssDemo.Web/Facilities/FacilityProfileReadiness.cs`: critical-field and verification checks.
- `src/OssDemo.Web/Facilities/FacilityProfileDictionaries.cs`: stable codes exposed to the editor.
- `src/OssDemo.Web/Classifier/FacilityFacts.cs`: typed normalization consumed by applicability rules.
- `src/OssDemo.Web/Classifier/ClassifierApplicabilityMatcher.cs`: selected/rejected decisions with trace data.
- `src/OssDemo.Web/Requirements/RequirementCatalog.cs`: runtime reader for the existing registry.
- `src/OssDemo.Web/Checklists/ChecklistItemCatalog.cs`: runtime reader for approved item links.
- `src/OssDemo.Web/Checklists/FacilityChecklistComposer.cs`: requirement selection, item selection, and coverage.
- `scripts/build-checklist-item-catalog.py`: deterministic legal-reference and lexical linker.
- `data/requirements/checklist-item-catalog.jsonl`: approved production records.
- `data/requirements/checklist-item-review.jsonl`: non-production ambiguous candidates.
- `data/requirements/checklist-item-catalog.manifest.json`: source hashes, algorithm version, counts, and coverage.
- `src/OssDemo.Web/Pages/FacilityEdit.cshtml` and `wwwroot/js/facility-editor.js`: structured editor.
- `src/OssDemo.Web/Pages/FacilityCard.cshtml` and `wwwroot/js/facility-card.js`: readiness and verified facts.
- `src/OssDemo.Web/Pages/Checklists/AiNew.cshtml` and `wwwroot/js/ai-checklists.js`: preview criteria, requirements, items, and coverage.
- `tests/OssDemo.Checklists.Tests/FacilityProfileV2Checks.cs`: profile migration and readiness checks.
- `tests/OssDemo.Checklists.Tests/FacilityChecklistComposerChecks.cs`: end-to-end domain selection checks.

### Task 1: Versioned structured facility profile

**Files:**
- Create: `src/OssDemo.Web/Facilities/FacilityProfileV2.cs`
- Create: `src/OssDemo.Web/Facilities/FacilityProfileMigration.cs`
- Create: `src/OssDemo.Web/Facilities/FacilityProfileReadiness.cs`
- Create: `tests/OssDemo.Checklists.Tests/FacilityProfileV2Checks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`
- Modify: `src/OssDemo.Web/FacilityProfileService.cs`

**Interfaces:**
- Produces: `FacilityProfileV2`, `FacilityFeatureFact`, `FacilityDocumentFact`, `FacilityFactState`, `FacilityProfileMigration.FromLegacy`, and `FacilityProfileReadiness.Evaluate`.
- Preserves: the existing `FacilityProfile` API response while adding a `structuredProfile` and `readiness` member.

- [ ] **Step 1: Write failing migration and readiness checks**

```csharp
var migrated = FacilityProfileMigration.FromLegacy(new FacilityProfileFields
{
    Type = "Линейная часть магистрального газопровода",
    EnvironmentalAspects = "Выбросы в атмосферный воздух\nОбращение с отходами"
});
AssertEqual(2, migrated.SchemaVersion);
AssertEqual("needs_review", migrated.VerificationStatus);
AssertTrue(migrated.LegacySource.EnvironmentalAspects.Contains("Выбросы"));
AssertEqual(FacilityFactState.Unknown, migrated.Features["water.discharge"].State);

var empty = FacilityProfileV2.CreateEmpty("Новый объект", "new-object");
AssertTrue(empty.Features.Values.All(item => item.State == FacilityFactState.Unknown));
AssertTrue(!FacilityProfileReadiness.Evaluate(empty).CanFinalizeChecklist);
```

- [ ] **Step 2: Run the checks and verify RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: compilation fails because the v2 profile types do not exist.

- [ ] **Step 3: Implement the minimal v2 model and non-destructive adapter**

Use stable feature codes including `air.emissions`, `air.gasTreatment`, `water.intake`, `water.discharge`, `water.treatment`, `waste.generation`, `waste.disposalSite`, `land.disturbance`, `subsoil.wells`, `nature.forest`, `nature.oopt`, and `zone.waterProtection`. Store `unknown`, `present`, or `absent` separately from details. `CreateInitialProfile` must populate only known identity/category/region data and must not invent zones, impacts, equipment, permits, or people.

- [ ] **Step 4: Run all domain checks and verify GREEN**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: `Checklist domain checks passed.`

- [ ] **Step 5: Commit locally**

```powershell
git add src/OssDemo.Web/Facilities src/OssDemo.Web/FacilityProfileService.cs tests/OssDemo.Checklists.Tests
git commit -m "feat: add verified structured facility profiles"
```

### Task 2: Structured profile API and editor

**Files:**
- Create: `src/OssDemo.Web/Facilities/FacilityProfileDictionaries.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Modify: `src/OssDemo.Web/Pages/FacilityEdit.cshtml`
- Modify: `src/OssDemo.Web/Pages/FacilityCard.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/facility-editor.js`
- Modify: `src/OssDemo.Web/wwwroot/js/facility-card.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`
- Modify: `scripts/verify-local-docker.ps1`

**Interfaces:**
- Consumes: `FacilityProfileV2` and `FacilityProfileReadinessResult` from Task 1.
- Produces: `GET /api/operations/facility-profile-dictionaries`; profile save requests containing `structuredProfile`; readiness details in profile responses.

- [ ] **Step 1: Add failing HTTP contract checks**

Extend `scripts/verify-local-docker.ps1` to require dictionary entries with stable codes and to reject a finalize-ready state when a critical feature remains `unknown`:

```powershell
$dictionary = Invoke-RestMethod "$BaseUrl/api/operations/facility-profile-dictionaries" -Headers $auth
Assert-True ($dictionary.features.code -contains 'water.discharge') 'Missing stable water.discharge feature'
Assert-True ($dictionary.states.code -contains 'unknown') 'Missing tri-state unknown value'
```

- [ ] **Step 2: Run live verification and verify RED**

Run: `./scripts/local-docker.ps1 Verify -RequireIndexedRag`

Expected: verification fails with `Missing stable water.discharge feature`.

- [ ] **Step 3: Implement API controls and editor behavior**

Replace free-text switches with accessible radio/select controls. Keep detail text next to the chosen state. Mark all migrated values visibly as unverified. Saving a mapping-relevant change sets `VerificationStatus` to `needs_review`; an explicit `Подтвердить карточку` action records status `verified`, timestamp, and the current authenticated user.

- [ ] **Step 4: Rebuild Docker and verify API/page markers**

Run: `./scripts/local-docker.ps1 Up`

Run: `./scripts/local-docker.ps1 Verify -RequireIndexedRag`

Expected: live checks pass; the edit page contains `data-facility-readiness`, and new objects contain no preselected environmental features.

- [ ] **Step 5: Commit locally**

```powershell
git add src/OssDemo.Web/Facilities src/OssDemo.Web/Program.cs src/OssDemo.Web/Pages/FacilityEdit.cshtml src/OssDemo.Web/Pages/FacilityCard.cshtml src/OssDemo.Web/wwwroot scripts/verify-local-docker.ps1
git commit -m "feat: replace free-text facility applicability fields"
```

### Task 3: Explainable tri-state classifier decisions

**Files:**
- Modify: `src/OssDemo.Web/Classifier/FacilityFacts.cs`
- Modify: `src/OssDemo.Web/Classifier/ClassifierApplicabilityMatcher.cs`
- Create: `src/OssDemo.Web/Classifier/ClassifierDecision.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `tests/OssDemo.Checklists.Tests/ClassifierChecks.cs`

**Interfaces:**
- Consumes: verified `FacilityProfileV2` facts.
- Produces: `ClassifierDecision(string Code, string Outcome, IReadOnlyList<DecisionFact> Facts, string Reason)` where outcome is `included`, `excluded`, or `blocked_unknown`.

- [ ] **Step 1: Add failing contrast and unknown-state checks**

```csharp
var industrial = Facts.With("air.emissions", Present).With("water.discharge", Present);
var office = Facts.With("air.emissions", Absent).With("water.discharge", Absent);
var unknown = Facts.With("air.emissions", Unknown);

AssertTrue(Decide(tree, industrial).Single(x => x.Code == "2.2").Outcome == "included");
AssertTrue(Decide(tree, office).Single(x => x.Code == "2.2").Outcome == "excluded");
AssertTrue(Decide(tree, unknown).Single(x => x.Code == "2.2").Outcome == "blocked_unknown");
```

- [ ] **Step 2: Run checks and verify RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: compilation fails because `ClassifierDecision` and tri-state decisions do not exist.

- [ ] **Step 3: Implement typed rule evaluation**

Compile the published mapping into `all`, `any`, and `none` predicates. Remove bidirectional substring matching for structured fields. Retain a legacy adapter only for `needs_review` previews. History changes priority only after a criterion is included.

- [ ] **Step 4: Verify all classifier and checklist checks**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: the two contrasting profiles select different criteria, and unknown critical facts block finalization.

- [ ] **Step 5: Commit locally**

```powershell
git add src/OssDemo.Web/Classifier src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs tests/OssDemo.Checklists.Tests/ClassifierChecks.cs
git commit -m "feat: make classifier decisions explainable"
```

### Task 4: Deterministic requirement-to-checklist linking catalog

**Files:**
- Create: `scripts/build-checklist-item-catalog.py`
- Create: `data/requirements/checklist-item-links.overrides.json`
- Create: `data/requirements/checklist-item-catalog.jsonl`
- Create: `data/requirements/checklist-item-review.jsonl`
- Create: `data/requirements/checklist-item-catalog.manifest.json`
- Create: `src/OssDemo.Web/Requirements/RequirementCatalog.cs`
- Create: `src/OssDemo.Web/Checklists/ChecklistItemCatalog.cs`
- Modify: `data/requirements/README.md`
- Modify: `src/OssDemo.Web/OssDemo.Web.csproj`
- Modify: `tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj`
- Create: `tests/OssDemo.Checklists.Tests/ChecklistItemCatalogChecks.cs`

**Interfaces:**
- Produces: `RequirementCatalog.Load`, `ChecklistItemCatalog.Load`, and JSONL records with `classifierCodes`, `requirementIds`, `status`, `provenance`, `linkScore`, and `linkExplanation`.
- Input sources: `requirements-registry.jsonl`, `inspector-checklist-template.jsonl`, and explicit reviewed overrides.

- [ ] **Step 1: Add failing catalog invariants**

```csharp
var approved = ChecklistItemCatalog.Load(path).Where(item => item.Status == "approved").ToArray();
AssertTrue(approved.Length > 0);
AssertTrue(approved.All(item => item.ClassifierCodes.Count > 0));
AssertTrue(approved.All(item => item.RequirementIds.Count > 0));
AssertTrue(approved.SelectMany(item => item.RequirementIds).All(requirements.ContainsId));
AssertTrue(approved.All(item => item.LinkExplanation.Length > 0));
```

- [ ] **Step 2: Run checks and verify RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: compilation fails because the catalogs do not exist.

- [ ] **Step 3: Implement the reproducible linker**

Normalize legal references into tuples `(act type, number, article, part, paragraph)`. Restrict candidates to the historical section, score legal-reference overlap before token overlap, and accept only a unique candidate set above the fixed manifest threshold. Write every non-unique result to `checklist-item-review.jsonl`; never copy it to the approved catalog without an explicit override.

- [ ] **Step 4: Generate catalogs and validate determinism**

Run:

```powershell
python ./scripts/build-checklist-item-catalog.py `
  ./data/requirements/requirements-registry.jsonl `
  ./data/requirements/inspector-checklist-template.jsonl `
  ./data/requirements/checklist-item-links.overrides.json `
  ./data/requirements/checklist-item-catalog.jsonl
```

Run the command a second time into an exact temporary path and compare SHA-256 hashes of the catalog, review file, and manifest. Expected: all hashes match. Record approved, ambiguous, uncovered-requirement, and per-section counts in the manifest.

- [ ] **Step 5: Review the ambiguity boundary**

Inspect the generated review rows. Keep them `needs_review`. If production coverage needs those rows, prepare a bounded LLM candidate batch containing only the row, its normalized legal references, and its same-section registry candidates; save suggestions to the review file without changing approval status. Report the remaining count before requesting any LLM token.

- [ ] **Step 6: Run domain checks and commit**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

```powershell
git add scripts/build-checklist-item-catalog.py data/requirements src/OssDemo.Web/Requirements src/OssDemo.Web/Checklists/ChecklistItemCatalog.cs src/OssDemo.Web/OssDemo.Web.csproj tests/OssDemo.Checklists.Tests
git commit -m "data: link approved checks to regulatory requirements"
```

### Task 5: Requirement coverage composer

**Files:**
- Create: `src/OssDemo.Web/Checklists/FacilityChecklistComposer.cs`
- Create: `tests/OssDemo.Checklists.Tests/FacilityChecklistComposerChecks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`

**Interfaces:**
- Consumes: classifier decisions, `RequirementCatalog`, and `ChecklistItemCatalog`.
- Produces: `FacilityChecklistComposition` containing criteria decisions, selected requirements, selected items, and `CoverageGap` records.

- [ ] **Step 1: Add failing coverage checks**

```csharp
var industrial = composer.Compose(verifiedIndustrialProfile);
var office = composer.Compose(verifiedOfficeProfile);

AssertTrue(!industrial.Items.Select(x => x.Id).SequenceEqual(office.Items.Select(x => x.Id)));
AssertTrue(industrial.Items.All(x => x.ClassifierCodes.Any(industrial.IncludedCriterionCodes.Contains)));
AssertTrue(industrial.Items.SelectMany(x => x.RequirementIds).All(industrial.SelectedRequirementIds.Contains));
AssertEqual(industrial.SelectedRequirementIds.Count,
    industrial.CoveredRequirementIds.Concat(industrial.Gaps.Select(x => x.RequirementId)).Distinct().Count());
```

- [ ] **Step 2: Run checks and verify RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: compilation fails because `FacilityChecklistComposer` does not exist.

- [ ] **Step 3: Implement deterministic composition and coverage**

Select current requirements whose classifier codes intersect included decisions, apply explicit category restrictions, then select approved items whose requirements intersect the selected set and whose item conditions pass. Emit one `CoverageGap` for every selected requirement not covered by an approved item. Reject final readiness when decisions contain `blocked_unknown` or gaps exist.

- [ ] **Step 4: Run checks and verify GREEN**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: contrast, provenance, and set-equality coverage checks pass.

- [ ] **Step 5: Commit locally**

```powershell
git add src/OssDemo.Web/Checklists/FacilityChecklistComposer.cs tests/OssDemo.Checklists.Tests
git commit -m "feat: compose checklists with requirement coverage"
```

### Task 6: Persist snapshots and integrate the automated run

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistDatabaseInitializer.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistModels.cs`
- Modify: `src/OssDemo.Web/AiChecklists/PostgresAiChecklistRunStore.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistApi.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Modify: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`
- Modify: `tests/OssDemo.Checklists.Tests/AiChecklistRunChecks.cs`

**Interfaces:**
- Consumes: `FacilityChecklistComposer`.
- Produces: run snapshots with profile schema/version, mapping hash, registry hash, item-catalog hash, decisions, selected requirements, and coverage.

- [ ] **Step 1: Add failing run snapshot checks**

```csharp
var run = await agent.CreateRunAsync("industrial", CancellationToken.None);
AssertEqual(2, run.Value!.Snapshot.ProfileSchemaVersion);
AssertTrue(run.Value.Snapshot.ClassifierDecisions.Count == 49);
AssertTrue(run.Value.Snapshot.SelectedRequirementIds.Count > 0);
AssertTrue(run.Value.Snapshot.CatalogHashes.Values.All(value => value.Length == 64));
AssertTrue(run.Value.Batches.All(batch => batch.RequirementIds.Count > 0));
```

- [ ] **Step 2: Run checks and verify RED**

Run: `dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release`

Expected: compilation fails because the snapshot fields do not exist.

- [ ] **Step 3: Implement storage migration and agent integration**

Add JSONB snapshot and coverage columns with `ADD COLUMN IF NOT EXISTS`. Replace section-wide template selection with the composer output. Create deterministic batches from selected approved items. Return HTTP 409 with structured missing-field details for unverified profiles and HTTP 422 with coverage gaps when finalization would silently omit requirements.

- [ ] **Step 4: Verify persistence, resume, and no-LLM behavior**

Run checklist checks and assert the synthesis/search fakes receive zero calls for a fully covered profile. Restart the local application container and verify the stored run still exposes identical hashes and selected item IDs.

- [ ] **Step 5: Commit locally**

```powershell
git add src/OssDemo.Web/AiChecklists src/OssDemo.Web/Program.cs tests/OssDemo.Checklists.Tests
git commit -m "feat: persist traceable checklist compositions"
```

### Task 7: Explainability UI and full local acceptance

**Files:**
- Modify: `src/OssDemo.Web/Pages/Checklists/AiNew.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/ai-checklists.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`
- Modify: `scripts/verify-local-docker.ps1`
- Modify: `docs/operations/local-docker.md`

**Interfaces:**
- Consumes: readiness, decisions, requirement counts, item provenance, and gaps returned by Task 6.
- Produces: a user-verifiable preview and a complete local acceptance test.

- [ ] **Step 1: Add failing live acceptance assertions**

Require the authenticated page/API to expose `Готовность карточки`, included/excluded criterion counts, selected requirement count, expected item count, and `Разрывы покрытия`. Add a script assertion that two seeded contrasting profiles return different item-ID sets.

- [ ] **Step 2: Run Docker verification and verify RED**

Run: `./scripts/local-docker.ps1 Verify -RequireIndexedRag`

Expected: failure on the first missing explainability marker.

- [ ] **Step 3: Implement preview and blocking states**

Show missing profile fields as links to the editor. For every included item, expose a compact expandable trace `fact → criterion → requirement → check`. Keep excluded criteria collapsed. Disable the final action when readiness or coverage is incomplete and provide the exact next action instead of a generic error.

- [ ] **Step 4: Execute the complete verification matrix**

Run:

```powershell
dotnet build OssDemo.slnx --configuration Release
dotnet run --project tests/OssDemo.Checklists.Tests/OssDemo.Checklists.Tests.csproj --configuration Release --no-build
dotnet run --project tests/OssDemo.Rag.Tests/OssDemo.Rag.Tests.csproj --configuration Release --no-build
./scripts/local-docker.ps1 Up
./scripts/local-docker.ps1 Verify -RequireIndexedRag
git diff --check
```

Expected: zero build warnings/errors, both test executables pass, 240 RAG documents and 28,590 chunks remain indexed, contrasting profiles produce different checklists, and no probe records remain in PostgreSQL.

- [ ] **Step 5: Commit locally**

```powershell
git add src/OssDemo.Web/Pages/Checklists/AiNew.cshtml src/OssDemo.Web/wwwroot scripts/verify-local-docker.ps1 docs/operations/local-docker.md
git commit -m "feat: expose checklist provenance and coverage"
```
