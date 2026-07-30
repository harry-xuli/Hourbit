# Task 8 — Single Instance, Global Hotkey, and Resume Events

## Scope delivered

- Added a strict, pure hotkey gesture parser and an injectable global-hotkey service.
- Added a pure Win32 message-only window with a dedicated message loop. Production hotkeys use
  `RegisterHotKey`, forward `WM_HOTKEY`, report conflicts without terminating, and unregister
  during disposal.
- Added a named-mutex/named-pipe single-instance coordinator. Production identifiers are exactly:
  - `Local\Moment.ReminderApp`
  - `Moment.ReminderApp.Activation`
- Added acknowledged `show-main`, `show-quick-add`, and strict notification-argument messages.
  Pipe payloads are limited to 4,096 UTF-8 bytes and malformed, unknown, invalid UTF-8, or
  oversized messages are rejected.
- Added distinct secondary results for no primary listener, acknowledgement timeout, rejection,
  and acknowledgement. The production timeout is two seconds; tests inject unique names and
  shorter timeouts.
- Added a 500 ms debounced system-resume monitor. The production source maps:
  - WTS session unlock to `ResumeReason.Unlock`
  - `WM_POWERBROADCAST` resume to `ResumeReason.PowerResume`
  - `WM_TIMECHANGE` with unchanged zone to `ResumeReason.TimeChanged`
  - `WM_TIMECHANGE` with changed zone to `ResumeReason.TimeZoneChanged`
- Added current-user startup registration with an injectable registry adapter. The exact enabled
  value is `"{full executable path}" --background`; absence is disabled and a moved portable
  executable reports `StartupPathStatus.Stale`.
- Exposed public lifecycle/hotkey/startup interfaces and activation callbacks for Task 9
  composition. Notification activations retain Task 7's strict `NotificationArguments` payload.

The Task 8 title mentions a tray, but the brief defines no tray file, interface, or behavior.
No tray UI was invented; tray/application composition remains Task 9 scope.

## TDD evidence

The first focused run reached compilation and failed with the expected missing Task 8 namespaces
and contracts (`CS0234`/`CS0246`). Product implementation was added only after that RED result.

During GREEN:

- Interop compilation first exposed incorrect source-generated P/Invoke requirements; the
  implementation was corrected.
- Direct `NativeWindow` use caused injected hotkey tests to load `System.Windows.Forms` before fake
  behavior ran. The native host was replaced with a pure Win32 adapter and the project-level
  Windows Forms dependency was removed. Existing looped audio now uses `winmm` rather than pulling
  WindowsDesktop back into the assembly.
- The isolated oversized-message test exposed a duplex pipe deadlock: the server stopped reading
  at byte 4,097 while the client was still finishing its write. The reader now retains constant
  bounded storage, drains through the line terminator, and then rejects. Unterminated reads remain
  cancellation-safe. The isolated regression passed 1/1, followed by the coordinator suite at 7/7.

Concurrency and lifecycle coverage includes disposal during incomplete pipe reads and pending
debounce, duplicate/repeated lifecycle activity, malformed and oversized messages, secondary
no-listener and acknowledgement-timeout results, repeated start/dispose, and registration failure
followed by unregister.

## Verification

- `dotnet test tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj --filter "Hotkeys|Lifecycle"`:
  PASS, 46/46
- `dotnet test tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj`:
  PASS, 71/71
- `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj`:
  PASS, 78/78
- `dotnet test Moment.slnx`:
  PASS — Core 78/78, Infrastructure 19/19, Windows 71/71
- `dotnet build Moment.slnx`:
  PASS, 0 warnings and 0 errors

Smart App Control did not block these verification runs, so no policy retry was needed.

## Remaining manual integration checks

Automated tests use adapters and unique instance names; they do not reserve a user-global hotkey,
mutate HKCU, or depend on an interactive Windows session. Reserving the default hotkey in another
process, observing real WTS/power/time messages, and inspecting the real Run value are optional
manual checks for application composition. The production adapters compile and their resource,
protocol, and lifecycle behavior is covered through injected seams.

## Fix round 1 — Important findings

### Root causes and fixes

1. **Lifecycle broadcasts used the wrong HWND category.** The shared native host always created an
   `HWND_MESSAGE` window. Windows system broadcasts enumerate top-level windows, so the power and
   clock messages could not reach that host. Native window creation now has explicit
   `MessageOnly` and `HiddenTopLevel` modes. The lifecycle source requests a never-shown hidden
   top-level window; the hotkey host remains message-only. WTS session-unlock registration is
   unchanged. `WM_TIMECHANGE` maps clock changes, while `WM_TIMECHANGE`/`WM_SETTINGCHANGE` refresh
   and compare `TimeZoneInfo.Local` to distinguish a zone change. The Windows SDK documents
   `WM_TIMECHANGE`; there is no separate documented `WM_TIMEZONECHANGE` message.

2. **Single-instance callback disposal had a circular await.** The listener awaited the activation
   callback, and a callback awaiting `DisposeAsync` waited for that same listener. Accepted
   messages are now acknowledged before callback execution is released. Callback tasks are
   tracked and exceptions are observed through `ActivationFailed`. A disposal initiated by the
   currently active callback starts the shared cleanup but does not await itself; every external
   disposal caller awaits listener shutdown and all tracked callbacks. The active marker uses a
   scoped object so an execution context captured by child work cannot retain reentrant status
   after its callback returns.

3. **Resume recovery disposal had the same circular await.** A debounce task awaited recovery,
   while recovery awaiting `DisposeAsync` waited for the debounce task. Debounce scheduling and
   recovery callback lifetimes are now tracked separately. Recovery exceptions are observed
   through `RecoveryFailed`, current-callback disposal is self-safe, and external disposal still
   waits for in-flight recovery.

4. **Secondary timeouts were per phase rather than end to end.** Connection and acknowledgement
   each received a new timeout, and write/flush had no deadline. One injected deadline now links
   caller cancellation, coordinator lifetime, and the configured timeout. Its single token covers
   connect, write, flush, and acknowledgement read. Expiry before connection remains
   `SecondaryNoPrimary`; expiry after connection is `SecondaryTimedOut`.

### RED/GREEN evidence

- Single reentrant callback RED:
  `dotnet test ... --filter "FullyQualifiedName~Activation_callback_can_await_reentrant_disposal"`
  failed because the secondary received `SecondaryRejected` instead of `SecondaryAcknowledged`;
  the callback cleanup was cyclic. GREEN: 1/1.
- Resume reentrant callback RED:
  `dotnet test ... --filter "FullyQualifiedName~Recovery_callback_can_await_reentrant_disposal"`
  failed with the bounded one-second `TimeoutException`. GREEN: 1/1.
- Broadcast-host contract RED:
  `dotnet test ... --filter "FullyQualifiedName~Windows_source_requests_broadcast_capable_window"`
  failed to compile because the requested native lifecycle factory/window-mode contracts did not
  exist. GREEN: 1/1, asserting hidden-top-level construction and all four mapped reasons.
- End-to-end deadline RED:
  `dotnet test ... --filter "FullyQualifiedName~One_deadline|FullyQualifiedName~End_to_end_deadline"`
  failed to compile because pipe-client/deadline adapters did not exist. GREEN: 2/2. The
  deterministic fakes cover late connection followed by delayed acknowledgement and a blocked
  write after connection, and assert the same deadline token is used in every reached phase.
- External disposal tests also prove non-reentrant callers wait for in-flight activation and
  recovery callbacks.

The native construction and reason mapping are deterministic adapter tests. A direct posted
message would only prove `WindowProc` dispatch, not that Windows includes the window in a true
system broadcast; real suspend/resume, clock/zone change, and session unlock remain explicit
manual integration checks.

### Fix-round verification

- Focused `Hotkeys|Lifecycle`: PASS, 55/55
- Full Windows suite: PASS, 78/78
- Core suite: PASS, 78/78
- Full solution: PASS — Core 78/78, Infrastructure 19/19, Windows 78/78
- Full solution build: PASS, 0 warnings and 0 errors

Smart App Control did not block this fix round, so no retry was required.
