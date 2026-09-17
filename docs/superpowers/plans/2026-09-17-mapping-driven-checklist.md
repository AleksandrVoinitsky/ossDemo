# Mapping Driven Checklist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the checklist flow from facility data, classifier mapping, and the requirements registry.

**Architecture:** The mapping matcher produces criterion decisions directly from profile fields. A requirement selector filters canonical registry rows by those decisions, NВОС category, and region. The wizard presents criteria and requirements as separate steps and creates deterministic checklist items without the historical template.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, PostgreSQL, vanilla JavaScript, JSONL catalogs.

**Spec:** `docs/superpowers/specs/2026-09-17-mapping-driven-checklist-design.md`

## Global Constraints

- Do not block generation on profile readiness or confirmation.
- Do not use the historical checklist template to select items.
- Do not call LLM during deterministic selection.
- Preserve requirement IDs and normative bases in every generated item.
- Do not push or deploy.

---

### Task 1: Mapping and requirement selection

**Files:**
- Modify: `src/OssDemo.Web/Classifier/ClassifierApplicabilityMatcher.cs`
- Create: `src/OssDemo.Web/Requirements/FacilityRequirementSelector.cs`
- Modify: `src/OssDemo.Web/Checklists/FacilityChecklistComposer.cs`
- Test: `tests/OssDemo.Checklists.Tests/ClassifierChecks.cs`
- Test: `tests/OssDemo.Checklists.Tests/FacilityChecklistComposerChecks.cs`

**Interfaces:**
- Consumes: `FacilityFacts`, `ClassifierTree`, `RequirementCatalog`.
- Produces: criterion decisions and category/region-filtered `RequirementCatalogItem` rows.

- [ ] Write tests proving detailed mapping fields drive criteria and whole sections are not selected.
- [ ] Run domain checks and verify the new assertions fail.
- [ ] Implement mapping evaluation and requirement filtering.
- [ ] Run domain checks and verify they pass.
- [ ] Commit the domain change.

### Task 2: Profile workflow and tooltips

**Files:**
- Modify: `src/OssDemo.Web/Pages/FacilityEdit.cshtml`
- Modify: `src/OssDemo.Web/Pages/FacilityCard.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/facility-editor.js`
- Modify: `src/OssDemo.Web/wwwroot/js/facility-card.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`
- Create: `src/OssDemo.Web/Facilities/FacilityProfileTooltips.cs`

**Interfaces:**
- Produces: profile fields with accessible `i` tooltips and no readiness/confirmation block.

- [ ] Add UI contract checks for missing readiness controls and tooltip presence.
- [ ] Verify the checks fail.
- [ ] Remove readiness UI and add tooltips from the approved DOCX.
- [ ] Verify the page contracts and build.
- [ ] Commit the profile UI change.

### Task 3: Prefilled facilities

**Files:**
- Create: `src/OssDemo.Web/Facilities/FacilityProfileSeedData.cs`
- Modify: `src/OssDemo.Web/FacilityProfileService.cs`
- Test: `tests/OssDemo.Checklists.Tests/FacilityProfileChecks.cs`

**Interfaces:**
- Produces: complete initial profiles for Bereznikovskoye, Bardymskoye, and Votkinskoye facilities.

- [ ] Add failing seed assertions for the three approved cards.
- [ ] Verify RED.
- [ ] Implement deterministic seed profiles and non-destructive initialization.
- [ ] Verify GREEN.
- [ ] Commit the seed change.

### Task 4: Criteria and requirements wizard

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistModels.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistApi.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `src/OssDemo.Web/Pages/Checklists/AiNew.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/ai-checklists.js`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces: step 4 with included criteria only and step 5 with paginated selected requirements only.

- [ ] Add failing snapshot and paging assertions.
- [ ] Verify RED.
- [ ] Expose selected requirements and deterministic checklist items.
- [ ] Replace the generation screen with the requirement review screen.
- [ ] Run domain, build, and local Docker smoke checks.
- [ ] Commit the wizard change.
