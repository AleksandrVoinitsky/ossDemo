# AI Checklist Generation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an experimental five-step workflow that generates a saved checklist draft from a facility profile and real RAG evidence.

**Architecture:** A deterministic query planner maps facility fields into bounded searches. An orchestrator retrieves evidence through an interface backed by the existing `RagService`, calls a structured Qwen synthesis client, validates citations, and persists an independent checklist snapshot through the existing repository.

**Tech Stack:** ASP.NET Core Razor Pages, C#/.NET 10, PostgreSQL/Npgsql, RAGify, pgvector, Lucene.NET, vanilla JavaScript, Bootstrap.

**Spec:** `docs/superpowers/specs/2026-09-12-ai-checklist-generation-design.md`

## Global Constraints

- Preserve `/Checklists/New` and its current APIs.
- Use facility and knowledge data from PostgreSQL-backed services.
- Never persist an AI item without a citation to retrieved evidence.
- Never create a partial draft after search, inference, validation, or persistence failure.
- Store generated drafts in the existing checklist lifecycle.
- Keep the work local until final verification, then merge and push `main`.

---

### Task 1: Agent domain and validation

**Files:**
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistModels.cs`
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistQueryPlanner.cs`
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistOutputParser.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces `AiChecklistQueryPlanner.Build(FacilityProfile)`.
- Produces `AiChecklistOutputParser.Parse(string, IReadOnlyList<AiChecklistEvidence>)`.
- Produces bounded `AiChecklistEvidence` and `AiGeneratedChecklistItem` records.

- [ ] Write checks proving profile fields produce distinct bounded queries and empty fields do not.
- [ ] Run tests and observe compilation failure for missing types.
- [ ] Implement the planner.
- [ ] Write checks proving unknown citations, empty items, duplicates and more than 100 items are rejected or bounded.
- [ ] Run red, implement strict JSON parsing and validation, then run green.
- [ ] Commit the domain slice.

### Task 2: Search and inference adapters

**Files:**
- Create: `src/OssDemo.Web/AiChecklists/IAiChecklistKnowledgeSearch.cs`
- Create: `src/OssDemo.Web/AiChecklists/RagAiChecklistKnowledgeSearch.cs`
- Create: `src/OssDemo.Web/AiChecklists/IAiChecklistSynthesisClient.cs`
- Create: `src/OssDemo.Web/AiChecklists/AmveraAiChecklistSynthesisClient.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Search adapter consumes `RagService.SearchAsync` and returns source IDs plus document, section, text and scores.
- Synthesis client consumes a profile and at most 32 evidence fragments and returns provider content.

- [ ] Add a failing prompt check for citation rules, untrusted document instructions and JSON-only output.
- [ ] Implement the RAG adapter with cross-query deduplication.
- [ ] Implement the Amvera client using named `AmveraInference`, bearer token, low temperature and non-streaming response.
- [ ] Register both interfaces and run tests/build.
- [ ] Commit the adapter slice.

### Task 3: Orchestration and atomic draft persistence

**Files:**
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `src/OssDemo.Web/Checklists/ChecklistModels.cs`
- Modify: `src/OssDemo.Web/Checklists/IChecklistRepository.cs`
- Modify: `src/OssDemo.Web/Checklists/PostgresChecklistRepository.cs`
- Modify: `tests/OssDemo.Checklists.Tests/InMemoryChecklistRepository.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces `AnalyzeAsync`, `SearchAsync` and `GenerateAsync`.
- Produces `CreateGeneratedDraftAsync(CreateGeneratedChecklistRequest)`.

- [ ] Add failing orchestration checks with deterministic fake search/synthesis dependencies.
- [ ] Implement facility loading, query planning, retrieval, synthesis and validation without accepting client evidence.
- [ ] Add failing persistence check for AI metadata and snapshot items.
- [ ] Implement a single PostgreSQL transaction inserting header and all generated items.
- [ ] Verify no draft is created for empty evidence or invalid synthesis.
- [ ] Commit the orchestration slice.

### Task 4: Experimental API

**Files:**
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistApi.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Maps `POST /api/ai-checklists/analyze`.
- Maps `POST /api/ai-checklists/search`.
- Maps `POST /api/ai-checklists/generate`.

- [ ] Add response mapping checks for not found, no evidence, invalid model output and provider failure.
- [ ] Implement endpoints that accept only `facilitySlug`.
- [ ] Return stable Russian error messages and appropriate 400/404/502 responses.
- [ ] Build and commit the API slice.

### Task 5: Five-step UI and navigation

**Files:**
- Create: `src/OssDemo.Web/Pages/Checklists/AiNew.cshtml`
- Create: `src/OssDemo.Web/wwwroot/js/ai-checklist.js`
- Modify: `src/OssDemo.Web/Pages/Shared/_Layout.cshtml`
- Modify: `src/OssDemo.Web/Pages/FacilityCard.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`

**Interfaces:**
- Adds menu entry «ИИ-формирование».
- Accepts optional `facility` query parameter.
- Drives analyze, search and generate endpoints sequentially and opens `/Checklists/Result?id=...`.

- [ ] Create the page with five accessible wizard panels and real empty/loading/error states.
- [ ] Implement facility loading and optional preselection from the object card.
- [ ] Render mapped fields, search directions and escaped evidence.
- [ ] Run actual generation, render generated items, and link the saved draft.
- [ ] Add responsive styling, keyboard focus and reduced-motion compatibility.
- [ ] Run JavaScript syntax check and build, then commit the UI slice.

### Task 6: Review, verification and delivery

**Files:**
- Modify: `README.md`

- [ ] Document the experimental workflow, limits and required `AI__ApiToken`.
- [ ] Run checklist tests and RAG adapter tests.
- [ ] Run `dotnet build OssDemo.slnx -c Release --no-restore`.
- [ ] Run `node --check` for the new and changed scripts and `git diff --check`.
- [ ] Review the complete diff for prompt injection, XSS, data consistency and current-flow regressions.
- [ ] Commit documentation, fast-forward into `main`, repeat verification, push `origin main`, fetch and confirm divergence `0 0`.
