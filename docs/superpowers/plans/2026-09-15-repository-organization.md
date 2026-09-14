# Repository Organization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate application code, runtime data, reference materials, documentation, and scripts without changing deployed behavior.

**Architecture:** Keep the existing solution and web project boundaries. Move repository-shipped Markdown and checklist seed records into `data/`, embed checklist JSON in the web assembly, and retain the published `knowledge-base/` layout expected by runtime services.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, MSBuild, System.Text.Json, PowerShell, Git.

**Spec:** `docs/superpowers/specs/2026-09-15-repository-organization-design.md`

**Status:** Implemented and verified on 2026-09-15 in the isolated `chore/repository-organization` branch; integration is intentionally pending owner review.

## Global Constraints

- Do not change public HTTP routes, database schema, Docker entrypoint, container port, or Amvera volume mount.
- Do not push changes or otherwise trigger autodeploy.
- Do not modify sibling repositories or model directories under `C:\OCC`.
- Preserve all currently tracked knowledge and checklist seed records exactly.
- Keep `.env.example` tracked and remove `.env` from Git tracking without deleting the developer's local secrets file.

---

### Task 1: Repository documentation and boundaries

**Files:**
- Create: `AGENTS.md`
- Create: `docs/architecture/repository-map.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: current solution, Docker and runtime paths documented in the design.
- Produces: authoritative directory rules and verification commands for future work.

- [ ] **Step 1: Write `AGENTS.md` with directory ownership, secret handling, test, and deploy rules**
- [ ] **Step 2: Write the runtime/data-flow map**
- [ ] **Step 3: Replace stale README structure and source-material paths with links to the new locations**
- [ ] **Step 4: Review documents for obsolete root paths and ambiguous ownership**
- [ ] **Step 5: Stage the documentation as one reviewable unit**

### Task 2: Move tracked data, requirements, and scripts

**Files:**
- Move: `03_📚 База знаний/` → `data/knowledge-base/`
- Move: `knowledge-inbox/` → `data/import/knowledge-inbox/`
- Move: `PROJECT_REQUIREMENTS.md` → `docs/requirements/project-requirements.md`
- Move: `RAG_TECHNICAL_SPEC.md` → `docs/requirements/rag-technical-spec.md`
- Move: `tools/convert_historical_checklists.py` → `scripts/convert_historical_checklists.py`
- Modify: `src/OssDemo.Web/OssDemo.Web.csproj`
- Modify: `.gitignore`
- Modify: `.dockerignore`

**Interfaces:**
- Consumes: MSBuild `Content` mapping from repository Markdown to published `knowledge-base/`.
- Produces: the same published runtime path and explicit source/reference data boundaries.

- [ ] **Step 1: Move tracked files with Git-aware moves**
- [ ] **Step 2: Update MSBuild to source knowledge from `data/knowledge-base/**/*.md` while keeping the published link unchanged**
- [ ] **Step 3: Update ignore files to cover generated outputs and local `data/reference/` contents**
- [ ] **Step 4: Update internal documentation links and the historical conversion script root path**
- [ ] **Step 5: Run `dotnet build OssDemo.slnx --configuration Release` and expect exit code 0**

### Task 3: Externalize checklist seed data with TDD

**Files:**
- Create: `data/seed/checklists.json`
- Create: `src/OssDemo.Web/Checklists/ChecklistSeedDataLoader.cs`
- Replace: `src/OssDemo.Web/Checklists/ChecklistSeedData.cs`
- Modify: `src/OssDemo.Web/OssDemo.Web.csproj`
- Modify: `tests/OssDemo.Checklists.Tests/Program.cs`

**Interfaces:**
- Consumes: embedded resource `OssDemo.Web.Data.checklists.json` containing `templates` and `history` arrays matching existing checklist seed records.
- Produces: `ChecklistSeedData.Templates` and `ChecklistSeedData.History` with the unchanged `IReadOnlyList` contracts.

- [ ] **Step 1: Add a test that calls `ChecklistSeedDataLoader.Deserialize` on a hand-written minimal JSON fixture and verifies nested values, date parsing, and enum parsing**
- [ ] **Step 2: Run the checklist tests and verify compilation fails because `ChecklistSeedDataLoader` does not exist**
- [ ] **Step 3: Export the existing compiled static seed records to `data/seed/checklists.json` using `System.Text.Json` with camel-case properties and string enums**
- [ ] **Step 4: Implement `ChecklistSeedDataLoader.Deserialize` with case-insensitive properties, string enum support, and a clear invalid-data exception**
- [ ] **Step 5: Replace the large literal file with a small embedded-resource facade and declare the resource in MSBuild**
- [ ] **Step 6: Run checklist tests and verify all existing seed assertions plus the new loader contract pass**

### Task 4: Local reference workspace and secret hygiene

**Files:**
- Create: `data/reference/README.md`
- Modify: `.gitignore`
- Untrack: `.env` while retaining the local file
- Move locally ignored demo/reference materials into `data/reference/` in the primary checkout

**Interfaces:**
- Consumes: existing ignored binary source folders and files.
- Produces: one documented local reference root; no runtime dependency on it.

- [ ] **Step 1: Add `/.env` to ignore rules and run `git rm --cached .env`**
- [ ] **Step 2: Add a tracked reference README and ignore every other item beneath `data/reference/`**
- [ ] **Step 3: Validate every local source path exists inside `C:\OCC\ossDemo` before moving it**
- [ ] **Step 4: Move the ignored materials into named subfolders without overwriting existing targets**
- [ ] **Step 5: Confirm sibling repositories and `C:\OCC`-level models are unchanged**

### Task 5: End-to-end verification

**Files:**
- Inspect: all changed files and Git state

**Interfaces:**
- Consumes: reorganized repository.
- Produces: evidence that build, tests, publish layout, secret tracking, and file boundaries are correct.

- [ ] **Step 1: Run `dotnet build OssDemo.slnx --configuration Release` and expect 0 warnings and 0 errors**
- [ ] **Step 2: Run both test projects in Release mode and expect their success messages**
- [ ] **Step 3: Run `dotnet publish src/OssDemo.Web/OssDemo.Web.csproj --configuration Release --output .verify-publish` and confirm 240 shipped Markdown files**
- [ ] **Step 4: Confirm `git ls-files .env` returns no path and `.env.example` remains tracked**
- [ ] **Step 5: Review `git diff --check`, `git status --short`, and changed-file statistics**
- [ ] **Step 6: Commit to the isolated branch only after all checks pass; do not push**
