# Task 6 Report: Reminder Creation and Action Services

## Delivered

- Added `ReminderService` and `IReminderService` for creating, editing, and deleting reminders.
- Added `ReminderActionService` and `IReminderActionService` for complete, ignore, and snooze actions.
- All mutating paths await their repository transaction before signaling the scheduler.
- Completion and ignore use `ApplyActionAsync` and create at most one recurrence occurrence.
- Snooze uses `ApplyActionAsync`, preserves the parent occurrence ID, and enforces distinct normal/important delay policies.
- Edit and delete require `SeriesScope` and leave missing or terminal occurrences unchanged.

## TDD Evidence

1. Initial creation ordering tests were added before the service existed.
   `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter Create_signals_scheduler`
   failed with `CS0234` because `Moment.Core.Services` was absent.
2. After the minimal creation implementation, the same focused test passed (1/1).
3. Action, recurrence, snooze, and series-scope tests were then added before the action service existed.
   `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter Services`
   failed with `CS0246` because `ReminderActionService` was absent.
4. After implementation, the focused service suite passed (13/13), including after the final timing-consistency refactor.

## Verification

- Services: 13 passed, 0 failed.
- Moment.Core tests: 68 passed, 0 failed.
- Full solution tests: Moment.Core 68 passed; Moment.Infrastructure 19 passed.
- `dotnet build Moment.slnx`: 0 warnings, 0 errors.

## Smart App Control

No Smart App Control `0x800711C7` failure occurred, so no retry was needed.

## Scope

The controller ledger was not modified. No Task 7 work was started.

## Fix Round 1: Important Finding Remediation

### Root-cause investigation

1. `ReminderItem.Create` checked only title and due time. `ReminderService` forwarded `ReminderDraft.Kind`, `Importance`, and raw `RecurrenceRule` values without a service-level check, so undefined enum values and structurally invalid recurrence rules could reach the repository.
2. `ReminderActionService` discarded application scheduling context by passing `TimeZoneInfo.Local` to `IRecurrenceCalculator.NextAfter`. The next occurrence therefore depended on the machine executing the service rather than the application's configured scheduling zone.
3. `CompleteAsync` and `IgnoreAsync` already intentionally returned for missing or terminal occurrences. `SnoozeAsync` already rejected those states with `InvalidOperationException`, but the contract, stable message, and absence of mutation/scheduler signal lacked direct tests.

### RED evidence

- `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter "FullyQualifiedName~ReminderServiceTests"` failed 7 of 9 tests: every malformed draft completed without the expected `ArgumentOutOfRangeException`.
- `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter "FullyQualifiedName~ReminderActionServiceTests"` failed with `CS1729` because `ReminderActionService` did not accept the required fifth `TimeZoneInfo` constructor dependency.

### GREEN evidence

- Focused reminder-service tests: 9 passed.
- Focused action-service tests: 14 passed.
- Services group: 23 passed.
- Moment.Core tests: 78 passed.
- Full solution tests: Moment.Core 78 passed; Moment.Infrastructure 19 passed.
- `dotnet build Moment.slnx`: 0 warnings, 0 errors.

### Contract decisions and changed files

- `ReminderService` now invokes a centralized `ValidateDraft` before any repository call in both create and edit paths. It checks title/due-time validity, reminder enums, recurrence kind, day enum values, empty weekly day sets, and the required empty day sets for daily and weekday recurrence.
- `ReminderActionService` now requires a non-null `TimeZoneInfo schedulingTimeZone` constructor dependency; there is no local-machine fallback. Every existing construction site supplies the zone.
- Snooze for a missing or non-actionable occurrence throws `InvalidOperationException` with the stable message `Reminder occurrence is not actionable.` and does not refresh the scheduler or mutate records. Complete/Ignore remain idempotent no-ops.
- Changed only Task 6 service/tests/report files. The controller ledger and Task 7 were not touched.
