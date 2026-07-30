# Task 7 report

## TDD reset and diagnostic (2026-07-30)

Task 7's first production implementation was removed before verification. The reset deleted only these newly-created, unverified production files:

- `src/Moment.Core/Domain/ReminderAlert.cs`
- `src/Moment.Windows/Notifications/NotificationArguments.cs`
- `src/Moment.Windows/Notifications/AppNotificationSink.cs`
- `src/Moment.Windows/Alerts/IImportantAlertPresenter.cs`
- `src/Moment.Windows/Alerts/ImportantAlertController.cs`

The Windows project scaffolds, solution entries, test-first suites, and `TestData.Alert` remain. No prior commits or controller ledger files were touched.

## Root-cause evidence

The initial `dotnet test tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj --no-restore` returned no useful test/build output. An explicit diagnostic build reproduced the actual failure:

```text
error NETSDK1004: Assets file
tests/Moment.Windows.Tests/obj/project.assets.json not found. Run a NuGet package restore to generate this file.
```

`tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj` exists, is included in `Moment.slnx`, targets `net10.0-windows10.0.22621.0`, and references the intended Windows and test-support projects. The SDK is present (`10.0.302`). During SDK evaluation it emits workload resolver `MSB4276` diagnostics, but those are non-fatal; the concrete build failure is the absent NuGet restore asset file.

An approved `dotnet restore tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj --verbosity minimal` began (`正在确定要还原的项目…`) but did not complete with a restore result or produce either Windows project's `project.assets.json`. A subsequent diagnostic no-restore build still failed with `NETSDK1004`.

## Current blocker

Resolved externally: `dotnet restore tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj --verbosity normal` completed successfully with zero warnings/errors and generated the missing assets.

With all Task 7 production files still deleted, this explicit command then captured the genuine initial RED:

```text
dotnet build tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj --no-restore --verbosity minimal
error CS0246: The type or namespace name 'ReminderAlert' could not be found
```

The failure is from `TestData.Alert`, which was intentionally written before the new Core contract. Minimal production implementation resumed from that RED.

## Implemented scope

- Added `Moment.Windows` and `Moment.Windows.Tests`, both targeting `net10.0-windows10.0.22621.0`, with the Windows App SDK package resolved through the existing central package pin.
- Added strict, canonical notification action argument formatting/parsing. Missing, duplicate, unknown, malformed, and non-GUID arguments are rejected before `IReminderActionService` is called.
- Added an adapter boundary around Windows App SDK `AppNotificationManager`, normal notification payload construction, health states, test-notification and Windows-settings actions, and missed-summary delivery.
- Added Core `ReminderAlert` / `ImportantAlertAction`, plus a 25 ms coalescing single-reader Channel queue for important alerts. Batches use due time then occurrence ID ordering and present serially.
- Mapped important alert actions to `IReminderActionService`; a custom-audio failure falls back through the embedded-default-audio abstraction. Presenter exceptions raise `PresentationFailed` and deliberately do not invoke an action, preserving the scheduler-marked `Fired` occurrence for recovery.
- Added deterministic adapter/fake based Windows tests and `TestData.Alert`.

`Moment.App` settings UI was intentionally not created: later binding can observe `AppNotificationSink.Health` and invoke `SendTestNotificationAsync` / `OpenWindowsNotificationSettingsAsync`.

## Verification

### Build

```text
dotnet build tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj --no-restore --verbosity minimal
PASS — 0 warnings, 0 errors

dotnet build Moment.slnx --no-restore --verbosity minimal
PASS — 0 warnings, 0 errors

git diff --check
PASS
```

### Test execution limitation

All runtime test suites in this worktree are blocked by the external Windows Smart App Control policy before their test bodies can load `Moment.Core.dll`:

```text
System.IO.FileLoadException: application control policy has blocked this file (0x800711C7)
```

Observed commands:

```text
dotnet test tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj --no-restore --verbosity minimal
1 passed, 14 blocked by 0x800711C7 before assertions

dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --no-restore --verbosity minimal
0 passed, 37 blocked by 0x800711C7 before assertions

dotnet test Moment.slnx --no-restore --verbosity minimal
blocked across Core, Infrastructure, and Windows tests by the same policy
```

The one passing Windows test is `Arguments_round_trip_occurrence_and_action`; it exercises no runtime Core dependency. The initial test-first RED stages and clean full compilation are recorded above, but the Smart App Control policy must be lifted or the build output trusted before the complete behavioral suite can execute.
