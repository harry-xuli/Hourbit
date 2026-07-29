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

## Fix round 1: absolute ordering through DST fall-back

### Files changed

- `src/Moment.Core/Recurrence/RecurrenceCalculator.cs`
  - After resolving the local wall-clock candidate, compares its absolute `DateTimeOffset` instant to `after`. A candidate that is not strictly later is skipped so the search proceeds to the next occurrence.
- `tests/Moment.Core.Tests/Recurrence/RecurrenceCalculatorTests.cs`
  - Added a fall-back regression: after `2026-11-01T01:15:00-05:00`, a daily 01:30 Eastern reminder returns `2026-11-02T01:30:00-05:00`, not the already elapsed earlier 01:30 instant.
  - Added the permitted empty-weekly-day edge case, asserting the 370-day `InvalidOperationException` contract and exact message.

### TDD evidence

RED command:

```powershell
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter "Daily_skips_an_ambiguous_local_time_when_its_earlier_instant_has_already_passed|Weekly_with_no_allowed_days_throws_after_searching_370_days"
```

Result: 1 failed and 1 passed. The fall-back regression failed exactly as intended: expected `2026-11-02T01:30:00-05:00`, actual `2026-11-01T01:30:00-04:00`. The existing 370-day contract test passed before the change.

After the minimal fix, the focused GREEN command and every later test execution were blocked externally by Windows Smart App Control, not test assertions. The test host fails to load the freshly compiled unsigned `Moment.Core.dll` from the test output directory with `FileLoadException` `0x800711C7` ("application control policy blocked this file"). Windows Code Integrity event 3077 identifies `dotnet.exe`, that DLL, and the enterprise signing policy as the cause. The same error affects pre-existing core and infrastructure tests.

### Final verification for this round

```powershell
dotnet build Moment.slnx --no-restore
```

Result: passed with 0 warnings and 0 errors.

```powershell
dotnet test Moment.slnx --no-restore
```

Result: blocked by the Smart App Control policy described above: core tests 0 passed / 9 failed and infrastructure tests 2 passed / 17 failed; each failure is the identical assembly-load policy error before application behavior runs.

### Remaining concern

This round cannot provide a post-fix GREEN test result in the current environment without an external policy/trust change. No attempt was made to alter Windows security policy or bypass it. `ThisAndFuture` cutoff enforcement remains deferred as documented above.
