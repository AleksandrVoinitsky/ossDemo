# Batched AI Checklist Generation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace repeated monolithic AI generation with a persisted, retryable batched workflow and an informative operation animation.

**Architecture:** PostgreSQL stores one search snapshot and independent thematic batches. Two hosted workers perform small idempotent LLM calls outside request lifetimes, then deterministic finalization creates one ordinary checklist draft. The browser polls persisted state and renders factual progress rather than model chain-of-thought.

**Tech Stack:** ASP.NET Core Razor Pages, C#/.NET 10, PostgreSQL/Npgsql, Amvera/Qwen, vanilla JavaScript, Bootstrap.

**Spec:** `docs/superpowers/specs/2026-09-13-batched-ai-checklist-generation-design.md`

## Global Constraints

- Do not repeat RAG search during generation.
- Persist run, evidence and batch progress in PostgreSQL without an execution deadline.
- Send at most 5 evidence fragments and 9,000 context characters per LLM call.
- Generate at most 20 accepted items per batch and 100 per checklist.
- Verify exact citations before storing batch items.
- Create no checklist until all batches complete and final validation succeeds.
- Show factual operations and metrics; never expose hidden model reasoning.
- Preserve `/Checklists/New` and the existing checklist lifecycle.

---

### Task 1: Run domain and batch planning

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistModels.cs`
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistBatchPlanner.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces `AiChecklistBatchPlanner.Build(IReadOnlyList<AiChecklistEvidence>)`.
- Produces run, batch and progress DTOs shared by persistence and API.

- [ ] Add a failing check proving 32 evidence fragments become deterministic batches of at most 5 sources and 9,000 characters.
- [ ] Run the checklist console tests and confirm failure because the planner is missing.
- [ ] Implement stable grouping by query topic with overflow chunks and global evidence IDs preserved.
- [ ] Add checks for empty evidence and the 20-item batch limit.
- [ ] Run tests green and commit the domain slice.

### Task 2: PostgreSQL run store

**Files:**
- Create: `src/OssDemo.Web/AiChecklists/IAiChecklistRunStore.cs`
- Create: `src/OssDemo.Web/AiChecklists/PostgresAiChecklistRunStore.cs`
- Create: `src/OssDemo.Web/AiChecklists/AiChecklistDatabaseInitializer.cs`
- Create: `tests/OssDemo.Checklists.Tests/InMemoryAiChecklistRunStore.cs`
- Modify: `src/OssDemo.Web/Program.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistRunChecks.cs`

**Interfaces:**
- Consumes planned batches from Task 1.
- Produces create/get/claim/complete/fail/finalize-safe store operations.

- [ ] Add failing in-memory lifecycle checks for create, idempotent queue/claim, failure retry, completion and restart recovery.
- [ ] Implement the store contract and in-memory test store until lifecycle checks pass.
- [ ] Add DDL for runs, evidence and batches with foreign keys, status constraints and indexes.
- [ ] Implement PostgreSQL JSON serialization, row locking and idempotent state transitions.
- [ ] Register and initialize the store, run build/tests, then commit.

### Task 3: Small synthesis calls and orchestration

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/IAiChecklistSynthesisClient.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistSynthesisClient.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistAgent.cs`
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistKnowledgeSearch.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Produces `CreateRunAsync`, `GenerateBatchAsync` and `FinalizeRunAsync`.
- Each synthesis call consumes one `AiChecklistBatch` only.

- [ ] Add failing checks proving generation uses stored evidence, calls one batch only, accepts at most 20 items and does not search again.
- [ ] Change synthesis input to batch evidence and include run/batch metadata in safe logs.
- [ ] Implement a two-worker `BackgroundService`, batch claim, synthesis, citation validation, completion/failure and retry without a provider timeout.
- [ ] Implement deterministic cross-batch deduplication and atomic draft creation on finalization.
- [ ] Reduce query plan to at most five concise searches and verify distinct bounded output.
- [ ] Run tests/build and commit orchestration.

### Task 4: Run API

**Files:**
- Modify: `src/OssDemo.Web/AiChecklists/AiChecklistApi.cs`
- Test: `tests/OssDemo.Checklists.Tests/AiChecklistAgentChecks.cs`

**Interfaces:**
- Maps create/status/batch/finalize endpoints from the spec.

- [ ] Add failing status mapping and idempotency checks.
- [ ] Implement endpoints accepting only run ID, batch index and facility slug where applicable.
- [ ] Return stable 400/404/409/410/502/503 responses and structured batch state.
- [ ] Build and commit the API slice.

### Task 5: Operation timeline and animation

**Files:**
- Modify: `src/OssDemo.Web/Pages/Checklists/AiNew.cshtml`
- Modify: `src/OssDemo.Web/wwwroot/js/ai-checklists.js`
- Modify: `src/OssDemo.Web/wwwroot/css/site.css`

**Interfaces:**
- Creates one run after search, queues batches, polls two server workers, retries failed batches and finalizes once complete.

- [ ] Replace the single fake 72% progress state with batch cards and an `aria-live` operation timeline.
- [ ] Render queued/running/verification/completed/failed states from polling actual API responses.
- [ ] Add elapsed timers, source/item counts and retry buttons without rendering any untrusted HTML.
- [ ] Add pulse, shimmer and progress transitions plus a `prefers-reduced-motion` override.
- [ ] Verify JavaScript syntax, Razor build and mobile layout, then commit.

### Task 6: Diagnostics, regression verification and delivery

**Files:**
- Modify: `README.md`

**Interfaces:**
- Documents persisted runs, batch limits and operational logs.

- [ ] Add structured duration/count logging at search, batch synthesis, validation and finalization boundaries.
- [ ] Update README with the batched flow and retry behavior.
- [ ] Run checklist and RAG tests, Release build, JavaScript syntax checks and `git diff --check`.
- [ ] Review for duplicate search, prompt size, citation integrity, idempotency, XSS and partial-draft risks.
- [ ] Merge into `main`, repeat verification, push `origin/main`, fetch and confirm divergence `0 0`.
