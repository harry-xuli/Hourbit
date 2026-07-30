# Task 11 Report: Backup, Restore, Recovery, and Release Link

## Scope

Implemented Task 11 from base commit
`ed82e4ceac114dbbaf9982ce19d0cdae8549bece` on
`feature/moment-development`.

The change adds:

- consistent SQLite `.moment-backup` export and automatic daily backup;
- strict package verification and guarded restore with rollback;
- corrupt-primary preservation and newest-valid automatic recovery;
- seven-package automatic retention and local-date deduplication;
- real create/export/confirmed-restore Settings actions while retaining the
  distinct data and backup folder actions;
- optional absolute-HTTPS release-page metadata and system-browser launch;
- a restartable boundary on the existing reminder scheduler so restore reuses
  the single repository/scheduler graph;
- persistent, non-blocking automatic-backup warnings.

No Windows security setting, unrelated Task 2 snooze behavior, Timeline styles,
or Timeline tests were changed.

## Design and Invariants

### Backup format and consistency

- A package contains exactly two case-sensitive root entries:
  `moment.db` and `manifest.json`.
- `manifest.json` is emitted as UTF-8 JSON without a BOM and uses the fields
  `formatVersion`, `schemaVersion`, `createdAt`, and `sha256`.
- `formatVersion` is `1`; supported database schema versions are `1` through
  the current schema version `2`.
- SHA-256 is computed over the completed SQLite snapshot, not the live main
  database file.
- The snapshot is created with SQLite `VACUUM INTO`, so live WAL state is read
  through SQLite rather than copied as raw database bytes.
- Snapshot and ZIP staging names are unique and live beside their final
  destination. A completed package is installed with an atomic replace/move.
  Failure preserves any existing destination and best-effort removes staging
  artifacts.

### Verification boundary

All package checks complete before the restore lifecycle is stopped:

- package exists and is no larger than 1 GiB;
- ZIP opens successfully;
- exactly two expected entries exist;
- traversal, duplicate, directory, case-variant, and unexpected entries are
  rejected by the exact-entry allowlist;
- `moment.db` is no larger than 1 GiB, with a streaming limit as defense in
  depth;
- manifest is bounded to 64 KiB and has valid format, schema, time, and SHA-256;
- manifest hash matches using a fixed-time byte comparison;
- manifest schema equals the schema actually stored in the extracted DB;
- SQLite `integrity_check` returns only `ok`.

### Guarded restore and rollback

After verification:

1. Stop and await the existing `ReminderScheduler`.
2. Create a SQLite safety snapshot of the current live database.
3. Atomically replace the database with the verified snapshot.
4. Run migrations and `PRAGMA integrity_check`.
5. Restart the same scheduler instance.
6. Refresh the scheduler and reload the existing timeline/recovery view.

If any post-stop step fails, the safety snapshot is atomically restored and
the same lifecycle is restarted and refreshed. A rollback/restart failure is
returned together with the original failure as an aggregate exception. The
restore implementation never constructs a second scheduler or repository
graph.

### Corruption recovery

- A present primary database is first checked with `PRAGMA quick_check`.
- When corrupt, its main-file bytes are copied unchanged to
  `moment.db.corrupt-{UTC timestamp}` before any replacement.
- Automatic packages are considered newest-first by their UTC filenames.
- Invalid ZIPs, hashes, schemas, integrity results, and packages which cannot
  migrate are skipped.
- The first package which verifies, migrates, and passes full integrity is
  atomically installed.
- With no usable package, the result is `RequiresUserDecision`; the corrupt
  primary remains byte-for-byte unchanged and no replacement database is
  created.

### Daily backup and retention

- Automatic names use UTC:
  `moment-yyyyMMddTHHmmssfffZ.moment-backup`.
- The last successful local calendar date is stored under
  `last_successful_local_backup_date` in the existing `settings` table.
- A second automatic backup on that local date returns the newest existing
  automatic package without creating another.
- After a successful package write, automatic packages are sorted newest-first
  and all after the seventh are removed.
- Composition opens/recover-checks and migrates the DB before attempting the
  daily backup.
- Daily failure is caught, written to the existing persistent Settings warning
  area, and does not prevent scheduler/reminder startup.

### Settings and update link

- `SettingsViewModel` receives `IBackupService` and `IReleasePageService`; it
  never opens SQLite.
- Manual export and restore receive only the paths explicitly selected by the
  user.
- Production restore is invoked only after the Open dialog returns an existing
  `.moment-backup` and a separate warning confirmation returns Yes.
- Data folder and backup folder actions remain distinct.
- `ReleasePageUrl` is assembly metadata with an empty default.
- The update button is collapsed unless the value parses as an absolute HTTPS
  URI. Its only action is `Process.Start(... UseShellExecute = true)` to open
  the system browser; there is no downloader or in-app updater.

## TDD RED/GREEN Evidence

### Initial backup contract

RED:

```powershell
dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --filter Backup --no-restore
```

After rerunning outside the sandbox because an existing `obj` directory denied
the sandbox compiler write, compilation failed as expected with missing
`Moment.Infrastructure.Backup`, `BackupService`, and
`IBackupRestoreLifecycle` (exit 1).

First GREEN run compiled and executed 9 tests: 8 passed, 1 failed. The failing
WAL test retained an external writer through atomic restore. Root-cause
analysis showed the external file handle correctly denied `File.Replace`; the
test was narrowed so the writer remains open during export, then closes before
restore. The next run passed 9/9.

### Scheduler lifecycle and Settings integration

RED:

```powershell
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --filter Stop_then_start --no-restore
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj --filter "FullyQualifiedName~Backup|FullyQualifiedName~Update_link|FullyQualifiedName~Release_page" --no-restore
```

Compilation failed for missing `ReminderScheduler.StopAsync`, Settings backup
injection/actions, `ReleasePageService`, restore picker/confirmation actions,
and `CompositionRoot.TryCreateDailyBackupAsync`.

The first scheduler GREEN attempt exposed a real race: `StartAsync` captured
the mutable `_runCancellation` field and an immediate stop cleared it before
the worker read it. The failure was a reproducible `NullReferenceException`.
Capturing the new CTS in a local fixed the root cause.

GREEN:

- scheduler same-instance stop/start: 1/1 passed;
- scoped Settings/backup/update/startup warning: 9/9 passed.

### Unmigratable recovery fallback

RED:

```powershell
dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --filter Corrupt_primary_uses_newest_valid --no-restore
```

A newest package was constructed with a valid ZIP, matching SHA-256, declared
schema 1, and valid SQLite integrity, but without required application tables.
The test failed with SQLite error `no such table: occurrences`, proving recovery
stopped rather than continuing to the older usable package.

GREEN:

Recovery now treats a migration `SqliteException` as an invalid automatic
candidate and continues newest-first. The full Backup filter passed 11/11.

## Safety and Rollback Evidence

The real-SQLite temporary-directory tests cover:

- export/restore marker round trip;
- tampered database entry rejected by checksum;
- exact two-entry package and literal manifest values;
- WAL-mode logical state captured while a writer connection remains open;
- corrupt ZIP, extra entry, traversal entry, duplicate entry, unsupported
  schema, and mismatched checksum rejected before lifecycle stop;
- a crafted ZIP header declaring `moment.db` as 1 GiB + 1 rejected specifically
  for the 1 GiB limit before lifecycle stop;
- failed export preserving pre-existing destination bytes and leaving no
  snapshot/package staging files;
- restore refresh failure restoring the safety snapshot and restarting the
  lifecycle in the observed order
  `stop, start, refresh, start, refresh`;
- corrupt primary recovery skipping a newest unmigratable package, restoring
  an older package, and preserving the corrupt bytes;
- no-valid-backup recovery returning `RequiresUserDecision` with original and
  corrupt-copy bytes unchanged;
- automatic daily local-date deduplication and seven-newest UTC retention.

All destructive test operations use `Moment.TestSupport.TempDirectory`.
No test resolves or touches `%LOCALAPPDATA%\Moment`.

## Integration Evidence

- Settings tests: 27/27 passed, including keyboard order, explicit restore
  confirmation cancellation, hidden invalid release link, and existing
  hotkey/startup/WAV transactional behavior.
- Daily warning test: 1/1 passed and proves a thrown backup failure sets
  `自动备份失败：disk full` without escaping the startup coordinator.
- Core suite: 79/79 passed.
- Infrastructure suite: 32/32 passed.
- Windows suite: 85/85 passed.
- Full solution excluding the one documented pointer-only environment test:
  295/295 passed:

```powershell
dotnet test Moment.slnx --no-restore --filter "FullyQualifiedName!~Simulated_dark_system_palette_reaches_timeline_surfaces"
```

- Final build:

```powershell
dotnet build Moment.slnx --no-restore
```

Result: success, 0 warnings, 0 errors.

- Diff hygiene:

```powershell
git diff --check
```

Result: no whitespace errors; only repository line-ending conversion notices.

## Full-Suite Environment Finding

The unfiltered command:

```powershell
dotnet test Moment.slnx --no-restore
```

ran 296 tests: 295 passed and the single failure was the pre-existing,
unchanged
`TimelineViewTests.Simulated_dark_system_palette_reaches_timeline_surfaces`
at line 66, where moving the Windows cursor did not update WPF
`selected.IsMouseOver`. The exact test failed again in isolation with the same
pointer assertion. There are no Task 11 diffs under Timeline or Styles, and no
Task 10 code/test was changed to mask this desktop pointer-injection issue.

## Self-Review

- Re-read the binding brief line by line against production and test behavior.
- Confirmed package output is a SQLite snapshot, not a live raw-file copy.
- Confirmed every untrusted ZIP check precedes lifecycle stop.
- Confirmed restore rollback uses the safety snapshot and the same graph.
- Confirmed recovery does not open/create the corrupt primary in
  `RequiresUserDecision`.
- Confirmed no broad delete/move target or real user data path is used in tests.
- Confirmed release-page handling has no non-HTTPS or download path.
- Scanned touched files for `TODO`, `TBD`, `NotImplemented`, accidental
  swallowing, and unrelated edits.
- `git diff --check` and the final build are clean.

## Concerns

- The only outstanding test concern is the unchanged WPF
  `IsMouseOver` desktop-pointer test described above. All 295 other tests pass
  together, and the failure reproduces independently of Task 11.
- On a corrupt primary with no usable automatic backup, startup deliberately
  stops with a clear error and the preserved-copy path. This honors
  `RequiresUserDecision` and avoids silently creating data, but the current
  application has no separate pre-composition recovery wizard; manual restore
  remains available only through the normal Settings UI when the application
  can open.
