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
