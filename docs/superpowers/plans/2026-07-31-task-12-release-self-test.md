# Task 12 Release and Self-Test Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build deterministic installer and portable artifacts with an isolated,
packaged-executable end-to-end self-test and honest release documentation.

**Architecture:** Select self-test mode before WPF composition, use real
repository/parser/recurrence/action/scheduler/protocol components with only
deterministic clock and notification boundaries, and package the same published
files into portable and per-user installer distributions. PowerShell scripts
validate exact paths and structured output.

**Tech Stack:** .NET 10, WPF, SQLite, xUnit, PowerShell 7/Windows PowerShell,
Inno Setup 6.7.3.

## Global Constraints

- Self-test mode never calls normal or portable data-path resolution.
- Reject relative output paths and existing reparse-point ancestors.
- Emit exactly six required JSONL events once each.
- Keep only one active scheduler loop, including across restart simulation.
- Delete only exact validated `artifacts/publish` and `artifacts/portable`
  descendants during release cleanup.
- Installer runs per user without elevation and never deletes user data.
- Do not change Windows security, notification, display, accessibility, time, or
  time-zone settings.
- Mark unperformed manual matrix items pending with exact reasons.

---

### Task 1: Isolated self-test entry point

**Files:**
- Create: `tests/Moment.App.Tests/Diagnostics/SmokeTestRunnerTests.cs`
- Create: `src/Moment.App/Diagnostics/SmokeTestRunner.cs`
- Modify: `src/Moment.App/Program.cs`
- Modify: `src/Moment.App/Moment.App.csproj`

**Interfaces:**
- Produces: `SmokeTestRunner.RunAsync(string, CancellationToken) -> Task<int>`
- Produces: `<output>/self-test.jsonl`

- [ ] **Step 1: Write failing tests**

Cover rejection of relative paths, rejection of reparse paths when supported,
creation of an isolated database, exactly-once required events, persisted
restart recovery, and argument dispatch before normal application bootstrap.

- [ ] **Step 2: Verify RED**

Run:
`dotnet test tests/Moment.App.Tests/Moment.App.Tests.csproj -c Release --filter FullyQualifiedName~SmokeTestRunnerTests`

Expected: compilation fails because `SmokeTestRunner` and the dispatch API do
not exist.

- [ ] **Step 3: Implement the minimal runner and entry dispatch**

Use production `SqliteReminderRepository`, `ChineseTimeParser`,
`RecurrenceCalculator`, `ReminderActionService`, `ReminderService`,
`ReminderScheduler`, and `SingleInstanceCoordinator`. Keep clock, sink, and
event writer private to the diagnostics runner.

- [ ] **Step 4: Verify GREEN**

Run the filtered test command and require zero failures.

### Task 2: Release and smoke scripts

**Files:**
- Create: `scripts/build-release.ps1`
- Create: `scripts/smoke-test.ps1`
- Create: `installer/Moment.iss`

**Interfaces:**
- Produces: `artifacts/Moment-Setup-x64.exe`
- Produces: `artifacts/Moment-Portable-x64.zip`
- Produces: adjacent `.sha256` files

- [ ] **Step 1: Add script-facing safety tests**

Exercise release cleanup validation and smoke JSONL validation through script
parameters that avoid compiling artifacts during the focused test.

- [ ] **Step 2: Verify RED**

Run the focused script validation commands and record the missing-script
failures.

- [ ] **Step 3: Implement deterministic packaging**

Resolve all paths from the repository root, validate exact cleanup descendants,
publish once, stage portable files, invoke the explicit/known ISCC candidates,
and write adjacent hashes.

- [ ] **Step 4: Implement packaged smoke verification**

Extract the ZIP to a run-unique temporary directory, launch
`Moment.App.exe --self-test <absolute-output-directory>`, kill and fail after
30 seconds, then parse and count JSONL events.

### Task 3: User and release documentation

**Files:**
- Create: `docs/user-guide.md`
- Create: `docs/release-checklist.md`
- Create: `.superpowers/sdd/2026-07-29-windows-reminder-app-implementation/task-12-report.md`

- [ ] **Step 1: Document user-visible behavior**

Cover install/portable use, first reminder and commands, recurrence, importance,
permissions, startup, backups, updates, exit semantics, sleep limits, and
unsigned SmartScreen warnings.

- [ ] **Step 2: Record evidence without inference**

List automated gates with commands/results. Mark physical/manual scenarios
pending unless actually performed, including lock, sleep, system setting
changes, install lifecycle, scaling/high contrast, and 24-hour residency.

### Task 4: Full release gate and commit

- [ ] **Step 1: Run the full automated gate**

Run:

```powershell
dotnet test Moment.slnx -c Release
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

- [ ] **Step 2: Inspect artifacts**

Record absolute paths, sizes, and SHA-256 values. Confirm each adjacent hash file
matches a fresh `Get-FileHash`.

- [ ] **Step 3: Review release safety**

Inspect the diff for unintended cleanup, normal data-path access, duplicate
scheduler graphs, user-data uninstall directives, and unrelated changes.

- [ ] **Step 4: Write final report and verify**

Record RED/GREEN, exact commands/results, JSONL, compiler source/version,
automated/manual matrix, safety proof, and concerns. Re-run tests/build/smoke
after the final code state.

- [ ] **Step 5: Commit**

Stage only Task 12 files and commit:
`build: package and verify Windows release`

