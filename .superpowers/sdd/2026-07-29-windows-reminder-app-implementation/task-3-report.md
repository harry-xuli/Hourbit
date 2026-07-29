# Task 3 Report: Recurrence Calculation

## Files changed

- `src/Moment.Core/Recurrence/RecurrenceCalculator.cs`
  - Added `IRecurrenceCalculator` and `RecurrenceCalculator`.
  - Calculates the strictly next permitted occurrence in the supplied time zone using local wall-clock values.
  - Supports daily, weekday, and multiple-day weekly rules.
  - Rolls invalid DST local times forward one minute at a time to the first valid minute.
  - Resolves ambiguous DST local times to the earlier UTC instant.
- `tests/Moment.Core.Tests/Recurrence/RecurrenceCalculatorTests.cs`
  - Added real-behavior tests for weekday/weekend transitions, multiple weekly days, the December-to-January boundary, invalid DST time handling, and ambiguous DST time handling.

## TDD evidence

### RED

Command:

```powershell
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter "Weekdays_skip_weekends|Weekly_supports_more_than_one_day"
```

Result: failed during test-project compilation with `CS0234`: `Moment.Core.Recurrence` did not exist. This is the expected failure before `RecurrenceCalculator` was added.

### GREEN

Command:

```powershell
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter "Weekdays_skip_weekends|Weekly_supports_more_than_one_day"
```

Result: passed, 3 tests passed, 0 failed.

Command:

```powershell
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter Recurrence
```

Result: passed, 6 tests passed, 0 failed.

## Final verification

Command:

```powershell
dotnet test Moment.slnx
```

Result: passed. `Moment.Core.Tests`: 7 passed, 0 failed. `Moment.Infrastructure.Tests`: 19 passed, 0 failed.

Command:

```powershell
dotnet build Moment.slnx --no-restore
```

Result: passed with 0 warnings and 0 errors.

## Concerns and deferred integration work

`IRecurrenceCalculator.NextAfter` takes only a rule, reference instant, and time zone. It intentionally has no series or cutoff state, so adding a cutoff to this calculator would mix occurrence arithmetic with persistence/generation policy.

For a `ThisAndFuture` split, later occurrence-generation/repository work must associate the old branch with an exclusive end boundary (or equivalent deletion/tombstone rule) and never regenerate rows after that boundary. The new branch starts at the split point. The existing `RecurrenceRule` model does not prevent implementing that policy later, but the calculator alone cannot enforce it and no broad generator/repository changes were made in Task 3.
