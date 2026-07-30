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

## Fix round 1 root-cause analysis (before changes)

1. `ImportantAlertController` uses `Channel.CreateUnbounded`; `TryWrite` always accepts while the controller is live, so overload has no backpressure boundary.
2. The controller owns no `CancellationTokenSource`; it passes `CancellationToken.None` to presenter, audio, and action calls, and disposal only completes the writer. An in-flight operation can therefore survive shutdown and queued callers have no deterministic disposal result.
3. `IImportantAlertAudio` has only a silent in-process default implementation. No embedded `default-alert.wav`, OS player adapter, or production looping implementation exists.
4. `WindowsAppNotificationPlatform` registers `AppNotificationManager` but never subscribes to `NotificationInvoked`. `AppNotificationSink` can parse only action-button arguments; timeline navigation arguments are neither distinguished nor routed.
5. `NotificationHealth` initially assumes `Available` and moves only after registration/show exceptions. It does not read `AppNotificationManager.Setting`, publish state changes, or support an explicit refresh after the settings app returns.

The remediation tests will assert observable outcomes at the controller and OS-adapter boundaries: bounded admission/cancellation, deterministic shutdown ownership, audio fallback/resource behavior, independent action vs navigation routing, and health state transitions.

## Fix round 1 RED/GREEN evidence

- Controller RED: new queue/lifetime tests failed because the constructor had no `queueCapacity` argument. GREEN: 7 controller tests passed after a bounded `Channel` (default capacity 32, `FullMode.Wait`) and controller-owned cancellation were added.
- Audio RED: the production audio contract was absent. The first implementation then exposed three stop calls during a failed start; cleanup was narrowed to active streams. GREEN: the custom-failure fallback/resource test passed.
- Activation/health RED: lifecycle tests failed for missing activation source, navigator, navigation payload, and health-source contracts. GREEN: both lifecycle tests passed after adding the router, Windows App SDK bridge, `Setting`-derived health refresh, and observable state changes.
- A full Windows-suite invocation initially appeared stalled because the approval/wait layer produced no output. Its completed retry result was 21/21 passing in 340 ms; no runtime deadlock was found.

Final verification:

```text
dotnet test tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj --no-restore
PASS — 21/21
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --no-restore
PASS — 78/78
dotnet test Moment.slnx --no-restore
PASS — Core 78/78, Infrastructure 19/19, Windows 21/21
dotnet build Moment.slnx --no-restore
PASS — 0 warnings, 0 errors
```

## Fix round 2

Root causes: the public controller constructor selected silent audio when no audio was supplied; activation routing had no lifecycle owner; and a failed registration could be overwritten by a setting-only refresh.

RED: runtime composition tests failed for missing `WindowsNotificationRuntime` and `ImportantAlertControllerFactory`.

GREEN: `WindowsNotificationRuntime` owns a start-once router and safe disposal; the controller factory always supplies `ImportantAlertAudio(new WindowsLoopingAudioPlayer())` in its production path while retaining injected adapters for tests. `WindowsAppNotificationPlatform` now tracks registration success and refresh retries registration before using `Setting`, retaining `RegistrationFailed` when retry fails.

Verification: Windows 23/23; Core 78/78; full solution Core 78/78, Infrastructure 19/19, Windows 23/23; build 0 warnings/errors.

Concern: direct Windows App SDK registration-failure/retry behavior remains a manual integration check because the SDK manager is static; the state gate is covered by compilation and the runtime/composition tests, but no isolated failure-injection test was added.

Amendment: extracted `INotificationRegistration`; RED was missing seam. The failure/retry/success transition test now injects registration failure then success, but its first GREEN execution was externally blocked before its body by Smart App Control (`0x800711C7` loading `Moment.Windows.dll`).

Follow-up diagnosis: the controller's executed test exposed a fixture contradiction, not a production state-machine defect. `Registration(false, true)` modeled a disabled Windows setting even after its registration retry succeeded, so `PermissionDisabled` was the correct result. The fixture now uses `Registration(true, true)`: registration initially and on first refresh fails, then succeeds after `Allow = true` while the modeled setting is enabled. The assertion remains `Available` and continues to guard against a false promotion before successful registration.

## Fix round 3

Root causes: navigation parsed a fixed argument order; runtime lifecycle used racy independent atomics; and show failures did not clear the registration gate. RED: reversed timeline fields produced no navigation. GREEN: strict structural map parsing routes either field order, runtime lifecycle is lock-protected, and show failures clear `_registered` before `RegistrationFailed`.

Verification: Windows 24/24, Core 78/78, full solution Core 78/78 + Infrastructure 19/19 + Windows 24/24, build 0 warnings/errors.

## Fix round 4

Root causes:

- Navigation used `Where` before `ToDictionary`: bare/empty segments were silently discarded, allowing malformed payloads such as a trailing `&` to route, while duplicate keys escaped as `ArgumentException`.
- Runtime disposal set its disposed flag before unsubscription completed. A concurrent later `DisposeAsync` therefore returned immediately instead of awaiting the first caller's cleanup.
- The Windows seam covered registration and setting only; `Show` still called the static SDK manager. The constructor also special-cased unauthorized registration as `PermissionDisabled`, contrary to the registration gate used by refresh and show failures.

RED evidence:

- Focused navigation/lifecycle run: 5 failed, 14 passed. Duplicate navigation keys threw, bare/trailing segments routed, and the second dispose caller completed while the controllable source still blocked `Unregister`.
- Registration/show seam test build: `CS0246` for the intentionally wished-for `IWindowsNotificationClient`.

GREEN:

- Navigation now parses every segment without throwing and accepts only exact missed or timeline schemas; action parsing remains independent.
- Runtime start/dispose state is monitor-protected, and all dispose callers await one shared completion task outside the monitor.
- `IWindowsNotificationClient` injects register, setting, and show behavior. Any register or show failure clears the gate and maps to `RegistrationFailed`; only a successful registration plus disabled setting maps to `PermissionDisabled`. `SetHealth` continues to suppress duplicate events.
- One initial post-change run was blocked before test bodies by the previously observed Smart App Control `0x800711C7`; the subsequent unchanged rebuilt suites executed successfully.

Verification:

```text
focused NotificationLifecycleTests + WindowsRuntimeTests: 22/22
Moment.Windows.Tests: 41/41
Moment.Core.Tests: 78/78
Moment.slnx tests: Core 78/78, Infrastructure 19/19, Windows 41/41
Moment.slnx build: 0 warnings, 0 errors
git diff --check: PASS
```
