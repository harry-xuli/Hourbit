# Task 5 — Scheduler and Resume Recovery

## Delivered files

- `src/Moment.Core/Abstractions/IClock.cs`
- `src/Moment.Core/Abstractions/IReminderSink.cs`
- `src/Moment.Core/Scheduling/ReminderScheduler.cs`
- `src/Moment.Core/Scheduling/RecoveryClassifier.cs`
- `tests/Moment.TestSupport/FakeClock.cs`
- `tests/Moment.TestSupport/RecordingReminderSink.cs`
- `tests/Moment.TestSupport/FakeReminderRepository.cs`
- `tests/Moment.Core.Tests/Scheduling/ReminderSchedulerTests.cs`
- `tests/Moment.Core.Tests/Scheduling/RecoveryClassifierTests.cs`

## TDD evidence

1. RED: `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Refresh_interrupts_wait` failed with CS0234 because `Moment.Core.Scheduling` did not exist.
2. GREEN: the initial refresh/earlier-occurrence test passed after adding the scheduler, deterministic fake clock, recording sink, and repository helper.
3. RED: the complete Scheduling suite was run with all scheduling/recovery production types removed; it failed with CS0234 for the absent `Moment.Core.Scheduling` namespace.
4. GREEN: after reintroducing the minimal implementation, `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Scheduling` passed 11/11.

## Behavior covered

- A single scheduler loop waits on the earliest occurrence and refresh signal; the bounded semaphore coalesces duplicate refreshes and prevents a busy loop.
- Refresh interrupts a later wait so a newly persisted earlier reminder is delivered at its due time.
- Due reminders are fetched together and conditionally transitioned to `Fired` before delivery. Two schedulers sharing a repository result in one delivery through `TryMarkFiredAsync` compare-and-set.
- Simultaneously due reminders are both delivered. Tests deliberately do not impose title order because ties are deterministically ordered by occurrence ID.
- Start rejects a cancelled token and use after disposal; disposing cancels an outstanding wait.
- `FakeClock` is deterministic and wakes queued delays only when advanced. `RecordingReminderSink` records delivery and summary calls under a lock and provides a cancellation-aware wait helper.
- Recovery classifies important reminders and normal reminders up to and including five minutes late as immediate; normal reminders more than five minutes late go to the summary.

## Final verification

- `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Scheduling` — passed 11/11.
- `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj` — passed 54/54.
- `dotnet build Moment.slnx --no-restore` — succeeded with 0 warnings and 0 errors.

## Smart App Control constraint

`dotnet test Moment.slnx` was run and retried once after Windows Smart App Control blocked `Moment.Infrastructure.Tests.dll` with `0x800711C7` on both attempts. The Core suite completed successfully (54/54) on both runs; the Infrastructure suite could not be discovered. This is an external assembly-policy block, not an assertion failure.

## Concerns

- The recovery classifier is intentionally a pure boundary-policy component; invoking `IReminderSink.DeliverMissedSummaryAsync` belongs to the app resume orchestration task, outside Task 5's stated scope.

## Fix round 1 — sink failure resilience

### TDD evidence

1. RED: `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Sink_failure_is_reported` failed with CS0246/CS1061 because `SchedulerDeliveryFailure`, `ReminderScheduler.DeliveryFailed`, and `ReminderScheduler.Completion` did not exist.
2. GREEN: the same focused test passed after adding observable delivery-failure reporting and catching only non-cancellation sink exceptions around a successful `TryMarkFiredAsync` transition.

### Policy

- A non-cancellation `IReminderSink.DeliverAsync` failure raises `ReminderScheduler.DeliveryFailed` with the reminder and exception. Each subscriber is isolated so an observer failure cannot stop scheduling.
- The scheduler keeps the occurrence `Fired`: it does not invent a repository rollback or automatic retry contract. This preserves the existing atomic at-most-once state transition; an application consumer can record, surface, or remediate the reported failure.
- `OperationCanceledException` is rethrown to retain cancellation semantics. Other sink failures are handled per reminder, so the loop continues and `Dispose()` does not rethrow that already-reported sink failure.

### Verification

- Focused sink-failure test: passed 1/1.
- Scheduling: passed 12/12.
- Core: passed 55/55.
- Solution: Moment.Core.Tests 55/55 and Moment.Infrastructure.Tests 19/19.
