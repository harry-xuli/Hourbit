# Task 12 Release and Self-Test Design

- Date: 2026-07-31
- Status: approved by the existing Task 12 brief
- Scope: release packaging, packaged-executable self-test, release documentation

## Goal

Produce an unsigned, per-user Windows x64 installer and portable ZIP whose
packaged executable can verify the application's real reminder pipeline without
touching normal installed or portable data.

## Considered approaches

1. **In-process self-test entry point (selected).** `Program.Main` recognizes
   `--self-test <absolute-output-directory>` before constructing WPF
   `Application`, opening `CompositionRoot`, or registering platform services.
   The runner injects only deterministic boundaries (clock and notification
   recorder) while using the production SQLite repository, Chinese parser,
   recurrence calculator, action service, scheduler, and single-instance
   coordinator.
2. **Multiple packaged application processes.** This would exercise more launch
   plumbing, but a deterministic clock and observable reminder actions would
   require extra process-control protocols that add release-only surface area.
3. **Test-project-only integration harness.** This is fastest, but it would not
   prove that the published executable selects the isolated path or that the
   portable archive is runnable.

The selected approach is the smallest design that exercises the packaged
executable and real core operations deterministically.

## Self-test architecture

`SmokeTestRunner.RunAsync` validates that the supplied path is fully qualified,
does not traverse an existing reparse point, and is not a filesystem root. It
creates only descendants of that directory. A SQLite database and JSON Lines
result file live below that root; no call is made to `DatabasePathResolver`.

The runner parses reminders with `ChineseTimeParser`, saves them through
`ReminderService` and `SqliteReminderRepository`, delivers them with
`ReminderScheduler`, and applies completion and snooze with
`ReminderActionService` and `RecurrenceCalculator`. A controllable clock lets
the real scheduler advance without wall-clock waiting. The first scheduler is
stopped and disposed before a second scheduler reopens the same SQLite database
and delivers a persisted snoozed occurrence, proving restart recovery without a
second concurrent scheduler graph.

A real pair of `SingleInstanceCoordinator` objects uses run-unique mutex and
pipe names. The secondary sends `ShowQuickAdd`; the primary must acknowledge
and observe that activation.

The JSONL writer serializes exactly these event names once:

- `normal-delivery`
- `important-delivery`
- `completed`
- `snoozed`
- `restart-recovered`
- `single-instance-protocol`

Before returning zero, the runner rereads the result file and verifies the exact
set and count. Any exception or validation failure returns nonzero.

## Release pipeline

`scripts/build-release.ps1` resolves repository-relative paths to absolute
paths. It removes only validated descendants `artifacts/publish` and
`artifacts/portable`, publishes a self-contained single-file win-x64 app, copies
the publish directory into a portable staging directory, adds `portable.flag`,
creates the ZIP, invokes Inno Setup, and writes adjacent lowercase SHA-256
files.

The compiler resolver accepts an explicit override, then checks the two
Program Files paths required by the brief and the official per-user installation
path under LocalAppData. The current approved compiler is Inno Setup 6.7.3 at
`C:\Users\harryY7000X\AppData\Local\Programs\Inno Setup 6\ISCC.exe`.

`installer/Moment.iss` installs only published application files under the
current user's LocalAppData Programs directory. It creates Start Menu and
optional desktop shortcuts, requests no elevation, and has no directive that
removes `%LOCALAPPDATA%\Moment\data` on install, upgrade, or uninstall.

## Smoke verification

`scripts/smoke-test.ps1` extracts the portable ZIP into a unique temporary
directory, creates a separate absolute self-test output directory, launches the
extracted executable, enforces a 30-second deadline, and independently parses
the JSONL file. It fails for missing, duplicate, unknown, or malformed events.
Its cleanup targets only its run-specific temporary directory.

## Documentation and manual evidence

The user guide explains both distributions, normal usage and limitations,
backup/update/exit behavior, and the unsigned SmartScreen warning. Screenshots
must come from the Release application where feasible and be labeled honestly.

The release checklist separates automated evidence from physical/manual
Windows checks. Lock/sleep, system setting changes, install lifecycle, display
matrix, and 24-hour residency remain pending until actually performed; no
result is inferred from unit or smoke tests.

