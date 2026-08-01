# Hourbit Missed Reminder Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to execute this plan task-by-task. Use `superpowers:test-driven-development` for every behavior change and `superpowers:verification-before-completion` before claiming completion.

**Goal:** Make overdue reminders transition deterministically after startup/resume and keep the visible timeline synchronized, including the reported 19:00-to-20:04 case.

**Architecture:** Add compare-and-set persistence primitives, a serialized recovery coordinator, scheduler grace-period processing, and one dispatcher-aware refresh coordinator. Persisted occurrence state remains the source of truth; UI code only reloads it.

**Tech Stack:** .NET 10, C# 14, WPF, Microsoft.Data.Sqlite, xUnit.

## Global Constraints

- The five-minute boundary is inclusive: lateness `<= 00:05:00` is immediate; any greater lateness is missed.
- Important reminders never auto-transition to `Missed`.
- Delivery and summary side effects happen only after a successful compare-and-set claim.
- Keep `Moment.*` namespaces and schema compatibility.
- Complete every task with its focused tests green and a commit before continuing.

---

## Task 1: Add atomic recovery repository operations

**Files:**
- Modify: `src/Moment.Core/Abstractions/IReminderRepository.cs`
- Modify: `src/Moment.Infrastructure/Data/SqliteReminderRepository.cs`
- Modify: `tests/Moment.TestSupport/FakeReminderRepository.cs`
- Modify: `tests/Moment.Infrastructure.Tests/Data/SqliteReminderRepositoryTests.cs`
- Modify: `tests/Moment.Infrastructure.Tests/Data/FakeReminderRepositoryTests.cs`

- [ ] Add failing repository contract tests proving recovery returns due `Scheduled` plus unhandled normal `Fired` rows, and concurrent callers cannot both claim the same transition.
- [ ] Run `dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ReminderRepository"` and confirm the new tests fail for missing APIs.
- [ ] Add these exact members to `IReminderRepository`:

```csharp
Task<IReadOnlyList<ScheduledReminder>> GetRecoverableAsync(
    DateTimeOffset through, CancellationToken ct);
Task<bool> TryTransitionAsync(
    Guid occurrenceId,
    OccurrenceState expected,
    OccurrenceState next,
    DateTimeOffset handledAt,
    CancellationToken ct);
```

- [ ] Implement `TryTransitionAsync` as one parameterized `UPDATE ... WHERE id = $id AND state = $expected`; return `ExecuteNonQueryAsync(...) == 1`. Implement `GetRecoverableAsync` with deterministic due-time/id ordering and no mutation.
- [ ] Update the fake repository under its existing lock so tests exercise the same winner/loser semantics.
- [ ] Run the focused command again and confirm it passes.
- [ ] Commit with `git add src/Moment.Core/Abstractions/IReminderRepository.cs src/Moment.Infrastructure/Data/SqliteReminderRepository.cs tests/Moment.TestSupport/FakeReminderRepository.cs tests/Moment.Infrastructure.Tests/Data/SqliteReminderRepositoryTests.cs tests/Moment.Infrastructure.Tests/Data/FakeReminderRepositoryTests.cs && git commit -m "feat: add atomic reminder recovery transitions"`.

## Task 2: Implement deterministic recovery classification and delivery

**Files:**
- Create: `src/Moment.Core/Scheduling/IReminderRecoverySummarySink.cs`
- Create: `src/Moment.Core/Scheduling/ReminderRecoveryService.cs`
- Create: `tests/Moment.Core.Tests/Scheduling/ReminderRecoveryServiceTests.cs`
- Modify: `tests/Moment.TestSupport/RecordingReminderSink.cs`

- [ ] Write failing tests for lateness `4:59`, `5:00`, `5:00 + 1 tick`, the 19:00-to-20:04 case, important reminders hours late, duplicate concurrent recovery calls, and one aggregate missed summary.
- [ ] Run `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~ReminderRecoveryServiceTests"` and confirm failure.
- [ ] Define `IReminderRecoverySummarySink.SendMissedSummaryAsync(IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct)` and `ReminderRecoveryResult(int Fired, int Missed, int Failed)`.
- [ ] Implement `ReminderRecoveryService.RecoverAsync(DateTimeOffset now, CancellationToken ct)` behind a `SemaphoreSlim`. For each repository row: important `Scheduled -> Fired`; normal scheduled within grace `Scheduled -> Fired`; older normal `Scheduled -> Missed`; normal fired older than grace `Fired -> Missed`. Deliver or summarize only when `TryTransitionAsync` returns true.
- [ ] Catch and report each delivery error without abandoning later rows; send exactly one summary containing only newly claimed missed rows. Preserve cancellation.
- [ ] Run focused tests and confirm all boundary and concurrency cases pass.
- [ ] Commit with `git add src/Moment.Core/Scheduling tests/Moment.Core.Tests/Scheduling/ReminderRecoveryServiceTests.cs tests/Moment.TestSupport/RecordingReminderSink.cs && git commit -m "feat: recover overdue reminders deterministically"`.

## Task 3: Add ordinary-runtime Fired-to-Missed grace handling

**Files:**
- Modify: `src/Moment.Core/Scheduling/ReminderScheduler.cs`
- Modify: `tests/Moment.Core.Tests/Scheduling/ReminderSchedulerTests.cs`

- [ ] Add failing scheduler tests proving a normal `Fired` occurrence becomes `Missed` after five unhandled minutes, but completion, ignore, snooze, deletion, and important reminders prevent that transition.
- [ ] Add a failing test that every committed scheduler transition raises `StateChanged` once and an observer exception cannot stop the loop.
- [ ] Run `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~ReminderSchedulerTests"` and confirm failures.
- [ ] Add `public event EventHandler? StateChanged;`. In each loop iteration consider both the earliest scheduled due time and earliest normal-fired grace deadline; use `TryTransitionAsync(Fired, Missed, now, ct)` after the deadline.
- [ ] Raise `StateChanged` only after committed `Scheduled -> Fired` and `Fired -> Missed` transitions. Invoke subscribers defensively like `DeliveryFailed`.
- [ ] Run focused tests and confirm they pass without timing sleeps by using `FakeClock`.
- [ ] Commit with `git add src/Moment.Core/Scheduling/ReminderScheduler.cs tests/Moment.Core.Tests/Scheduling/ReminderSchedulerTests.cs && git commit -m "fix: expire unhandled normal reminders"`.

## Task 4: Serialize lifecycle recovery before UI refresh

**Files:**
- Create: `src/Moment.App/Timeline/TimelineRefreshCoordinator.cs`
- Create: `src/Moment.App/Startup/ReminderRecoveryCoordinator.cs`
- Modify: `src/Moment.App/CompositionRoot.cs`
- Create: `tests/Moment.App.Tests/Timeline/TimelineRefreshCoordinatorTests.cs`
- Create: `tests/Moment.App.Tests/Startup/ReminderRecoveryCoordinatorTests.cs`
- Modify: `tests/Moment.App.Tests/Startup/ApplicationBootstrapTests.cs`

- [ ] Write failing tests proving clustered lifecycle signals coalesce, recovery stops/restarts the scheduler in `finally`, persistence precedes dispatcher reload, and scheduler `StateChanged` eventually refreshes the timeline.
- [ ] Run `dotnet test tests/Moment.App.Tests/Moment.App.Tests.csproj --filter "FullyQualifiedName~RecoveryCoordinator|FullyQualifiedName~TimelineRefreshCoordinator|FullyQualifiedName~ApplicationBootstrap"` and confirm failure.
- [ ] Implement `TimelineRefreshCoordinator.RequestAsync(CancellationToken ct)` with one dispatcher-marshaled reload at a time and one trailing reload when signals arrive during a load.
- [ ] Implement `ReminderRecoveryCoordinator.RecoverAndRefreshAsync(CancellationToken ct)` in this order: acquire gate, stop scheduler, run `ReminderRecoveryService`, restart scheduler in `finally`, then await timeline refresh. Reject new work after disposal and await admitted work.
- [ ] Wire startup and `SystemResumeMonitor` through this coordinator; subscribe scheduler `StateChanged` to `TimelineRefreshCoordinator`. Keep runtime errors flowing through `CompositionRoot.OnRuntimeError`.
- [ ] Ensure startup recovery finishes before the first `Timeline.LoadAsync`, and replace the existing resume callback that calls `scheduler.Refresh()` followed by an immediate load.
- [ ] Run focused tests and confirm ordering, coalescing, cancellation, and disposal pass.
- [ ] Commit with `git add src/Moment.App/Timeline/TimelineRefreshCoordinator.cs src/Moment.App/Startup/ReminderRecoveryCoordinator.cs src/Moment.App/CompositionRoot.cs tests/Moment.App.Tests && git commit -m "fix: await reminder recovery before timeline refresh"`.

## Task 5: Verify the reported scenario and release build

**Files:**
- Modify: `src/Moment.App/Diagnostics/SmokeTestRunner.cs`
- Modify: `tests/Moment.App.Tests/Diagnostics/SmokeTestRunnerTests.cs`
- Modify: `scripts/smoke-test.ps1`

- [ ] Add a failing deterministic smoke scenario that persists a normal 19:00 reminder, advances the clock to 20:04, runs recovery, and asserts persisted `Missed`, visible `已错过`, and exactly one summary after a second recovery signal.
- [ ] Run `dotnet test tests/Moment.App.Tests/Moment.App.Tests.csproj --filter "FullyQualifiedName~SmokeTestRunnerTests"` and confirm failure before wiring the scenario.
- [ ] Implement the smoke scenario and expose it through the existing packaged self-test path without depending on wall-clock time.
- [ ] Run `dotnet test Moment.slnx --configuration Release --no-restore`.
- [ ] Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1` and then `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1`; confirm tests, publish, installer, and portable smoke checks pass.
- [ ] Manually verify on Windows 11: create a normal reminder, suspend or lock beyond five minutes, return, observe `已错过`, then trigger unlock again and verify no duplicate summary.
- [ ] Commit with `git add src/Moment.App/Diagnostics/SmokeTestRunner.cs tests/Moment.App.Tests/Diagnostics/SmokeTestRunnerTests.cs scripts/smoke-test.ps1 && git commit -m "test: cover missed reminder recovery end to end"`.
