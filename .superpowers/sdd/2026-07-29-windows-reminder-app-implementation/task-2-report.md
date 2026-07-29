# Task 2 Report: SQLite Schema, Migrations, and Repository

## Status

Implemented Task 2 and verified the infrastructure tests, solution build, and test-project build. Independent review findings were addressed and committed as `637d989 feat: persist reminders in sqlite`.

## Implementation

- Added `IReminderRepository` with the required persistence contract.
- Added the `Moment.Infrastructure` and `Moment.Infrastructure.Tests` projects to `Moment.slnx`.
- Added a SQLite v1 migration containing `items`, `occurrences`, `recurrence_rules`, `action_log`, `settings`, and `schema_info`; it enables foreign keys, runs in one transaction, provides the requested indexes, and makes `(item_id, due_at)` unique.
- Added `SqliteReminderRepository`, with ISO-8601 round-trip `DateTimeOffset` persistence, transactional save/action/delete/edit operations, and a `Scheduled`-to-`Fired` compare-and-set operation.
- Added deterministic installed/portable database-path resolution.
- Added the non-sealed, virtual-method `FakeReminderRepository`, with dictionary access guarded by a lock and atomic fake `TryMarkFiredAsync` behavior.
- Added real SQLite round-trip and path-selection tests.
- Disabled SQLite connection pooling for the repository's short-lived connections so disposal releases the database file before test temporary-directory cleanup.

## TDD Evidence

1. RED test written first: `SaveItemWithOccurrence_is_atomic_and_round_trips` in `SqliteReminderRepositoryTests`.
2. First attempted RED command:

   ```powershell
   dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --filter SaveItemWithOccurrence_is_atomic_and_round_trips
   ```

   The test could not reach compilation because restore reported NU1903 as an error for `SQLitePCLRaw.lib.e_sqlite3` 2.1.11. The project has `TreatWarningsAsErrors=true`; no product code or security setting was changed to suppress it.
3. After the user-authorized dependency remediation and initial implementation, the same focused test compiled and ran but failed at `TempDirectory.Dispose`: the database file was still in use by SQLite pooling.
4. GREEN command, after the minimal `Pooling=false` change, passed: 1/1 test, 0 failures, duration 48 ms.
5. Independent review then identified incorrect `SeriesScope` handling and non-atomic fake action updates. Two real SQLite tests were added first and failed as expected (occurrence-only edit changed the shared item; this-and-future delete removed completed history). The scope tests passed 2/2 after the repair.

The two database-path tests were added before the path resolver implementation. The first repository test is therefore the authoritative pre-implementation RED check; its initial runner result was dependency-policy blocked rather than a missing-repository compiler failure.

## NU1903 Systematic-Debugging Record

### Phase 1 — root-cause evidence

- Reproduction was deterministic with both `dotnet test` and `dotnet list ... package --include-transitive` before remediation.
- `Directory.Build.props` promotes warnings to errors.
- The generated assets graph showed:

  ```text
  Microsoft.Data.Sqlite/10.0.10
    -> SQLitePCLRaw.bundle_e_sqlite3/2.1.11
    -> SQLitePCLRaw.lib.e_sqlite3/2.1.11
  ```

- NU1903 named `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 and GHSA-2m69-gcr7-jv3q. The advisory identifies versions through 2.1.11 as affected by CVE-2025-6965.

### Phase 2 — pattern and version comparison

- The plan centrally fixes `Microsoft.Data.Sqlite` at 10.0.10, and NuGet metadata for that package resolves the SQLitePCLRaw bundle at 2.1.11 under the default lowest-version resolution.
- The advisory reports no patched release in the affected 2.1.11 range. NuGet listed the current native library release as 3.53.3; the user instead explicitly authorized the compatible 2.1.12 safety pin while retaining Microsoft.Data.Sqlite 10.0.10.

### Phase 3 — single hypothesis and minimal experiment

Hypothesis: enabling central transitive pinning and centrally pinning both `SQLitePCLRaw.bundle_e_sqlite3` and `SQLitePCLRaw.lib.e_sqlite3` to 2.1.12 will retain `Microsoft.Data.Sqlite` 10.0.10 while resolving every SQLitePCLRaw component to 2.1.12 and eliminating NU1903.

Applied only those central-package changes, then ran:

```powershell
dotnet restore tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj
dotnet list tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj package --include-transitive
```

Result: restore succeeded with no NU1903. The resolved graph contains `Microsoft.Data.Sqlite` 10.0.10 and SQLitePCLRaw `bundle_e_sqlite3`, `lib.e_sqlite3`, `core`, and `provider.e_sqlite3` all at 2.1.12.

### Phase 4 — persistent fix

- Persisted `CentralPackageTransitivePinningEnabled=true` plus the two 2.1.12 central package versions.
- Kept vulnerability checks enabled; no `NoWarn`, warning downgrade, or safety-policy change was added.
- Updated the plan's Global Constraints, central package snippet, and Task 2 dependency note to prevent its fixed-version wording from reintroducing the conflict.

## Verification

| Command | Result |
| --- | --- |
| `dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --filter SaveItemWithOccurrence_is_atomic_and_round_trips` | PASS: 1/1 after the connection-release fix. |
| `dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj` | PASS: 5/5 (first final run). |
| `dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj` | PASS: 5/5 (second final run). |
| `dotnet build Moment.slnx --no-restore` | PASS: 0 warnings, 0 errors. |
| `dotnet build tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --no-restore` | PASS: 0 warnings, 0 errors. |
| `git diff --check` | PASS: exit 0; Git emitted only existing LF-to-CRLF checkout notices. |

The test runner was not blocked by Device Guard / Smart App Control during this task; all commands above ran through the xUnit runner successfully.

## Changed Files

- `Directory.Packages.props`
- `Moment.slnx`
- `docs/superpowers/plans/2026-07-29-windows-reminder-app-implementation.md`
- `src/Moment.Core/Abstractions/IReminderRepository.cs`
- `src/Moment.Infrastructure/Moment.Infrastructure.csproj`
- `src/Moment.Infrastructure/Data/DatabasePathResolver.cs`
- `src/Moment.Infrastructure/Data/DatabaseMigrator.cs`
- `src/Moment.Infrastructure/Data/SqliteReminderRepository.cs`
- `tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj`
- `tests/Moment.Infrastructure.Tests/Data/DatabasePathResolverTests.cs`
- `tests/Moment.Infrastructure.Tests/Data/SqliteReminderRepositoryTests.cs`
- `tests/Moment.TestSupport/FakeReminderRepository.cs`

## Self-review and Concerns

- Independent review found and the implementation corrected four P1 items: `OccurrenceOnly` now changes only the selected occurrence; `ThisAndFuture` removes the recurrence and scheduled occurrences at/after the cutoff while preserving completed history; the fake mirrors that behavior; and the fake action transition/insertion occurs under one lock.
- `ThisAndFuture` now splits the selected/future branch into a new item/rule and removes stale old scheduled rows at the cutoff; recurrence regeneration after that boundary remains owned by later application/scheduling work.

## Fix Round 1

### Root cause and scope

Independent review found that the initial implementation updated the shared item for series edits, stored the display timestamp as the uniqueness/sort key, and let `ApplyActionAsync` update terminal occurrences. The fake also accepted invalid dictionary states that SQLite rejects. The explicitly ledgered `snooze_parent` foreign-key minor finding was not changed.

### RED coverage and evidence

Added nine regression tests across these files:

- `tests/Moment.Infrastructure.Tests/Data/SqliteReminderRepositoryTests.cs`
- `tests/Moment.Infrastructure.Tests/Data/FakeReminderRepositoryTests.cs`

The first RED command was:

```powershell
dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj
```

It failed 6/12 as expected: occurrence-only edit did not expose the supplied item; this-and-future edit rewrote past history; repeated action overwrote the terminal state; equivalent instants with different offsets bypassed uniqueness; and the fake accepted duplicate/missing-item data and saved a next occurrence for a missing current occurrence.

The second RED command was:

```powershell
dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GetScheduledAsync_orders_occurrences_by_utc_instant_when_offsets_differ|FullyQualifiedName~EditAsync_with_a_missing_current_occurrence_does_not_create_a_replacement"
```

It failed 2/2 as expected: `due_at` text ordering ignored the instant represented by the offset, and fake edit created a replacement for an absent current occurrence.

### Implementation

- Added migration 2 with a canonical `due_at_utc` key. The original ISO-8601 `due_at` still round-trips its supplied offset; the canonical UTC key now backs uniqueness, due queries, and ordering. Migration 2 also backfills version-1 databases transactionally.
- Implemented series split edits. `OccurrenceOnly` creates a one-off item for the selected occurrence. `ThisAndFuture` creates a new item/rule, removes stale scheduled rows from the old series at the cutoff, and inserts the replacement occurrence; completed/past occurrences keep their original item and rule.
- Made `ApplyActionAsync` a conditional Scheduled/Fired transition. A terminal or missing occurrence is a no-op, so no extra next occurrence or action-log row is inserted. A constraint failure rolls back the state transition and log in the same transaction.
- Made `FakeReminderRepository` validate item ownership, occurrence IDs, foreign items, and same-instant uniqueness before mutating; it mirrors action no-op/rollback behavior and series splitting under its lock.

### GREEN and build evidence

```powershell
dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj
```

Passed 14/14, then passed 14/14 again on a second invocation. The suite covers same-file migration reopening, save/action rollback, action idempotency plus one action-log row, CAS, fake constraint parity, real SQLite constraints, offset round-trip, canonical UTC ordering/uniqueness, and both series scopes.

```powershell
dotnet build Moment.slnx --no-restore
dotnet build tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --no-restore
```

Both builds passed with 0 warnings and 0 errors.

### Remaining concern

`snooze_parent_id` remains intentionally without a foreign key because the finding is recorded as Minor in the review ledger and this fix round was directed not to change it.

## Fix Round 2

### Scope

Only the two assigned findings were changed. The deferred `snooze_parent_id` review-ledger item was not modified, and no Task 3 work was started.

### RED tests

Added focused adversarial fake-edit tests proving that an occurrence-only replacement-ID collision and a this-and-future replacement-ID collision leave the existing occurrences and history intact. Added deterministic compare-and-set tests for `TryMarkFiredAsync` in both SQLite and fake repositories (first scheduled transition succeeds; repeat and completed transitions fail). Added a true version-1 database fixture that runs the v1-to-v2 migration and asserts both the original offset-preserving `due_at` value and the canonical `due_at_utc` backfill.

The focused RED command was:

```powershell
dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --filter "FullyQualifiedName~EditAsync_with_occurrence_only_rolls_back_when_the_replacement_id_collides|FullyQualifiedName~EditAsync_with_this_and_future_rolls_back_when_the_replacement_id_collides"
```

The runner compiled all projects, then Device Guard / Smart App Control blocked loading `Moment.Infrastructure.Tests.dll` with `System.IO.FileLoadException`, `0x800711C7`, before test discovery. This is the documented environment policy failure, not a test failure. The subsequent full-suite command failed at the same assembly-load boundary.

### Minimal implementation

`FakeReminderRepository.EditAsync` now validates a replacement primary key before adding a split item or removing any scheduled rows. For occurrence-only edits it also mirrors SQLite primary-key changes by removing the old dictionary key and adding the replacement only after collision validation. The future-split validation treats the rows scheduled for deletion at the cutoff as non-conflicting and treats retained past/terminal rows as conflicts. Thus every potentially failing edit operation completes validation before it mutates a dictionary.

### GREEN/build evidence

```powershell
dotnet build tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj --no-restore
dotnet build Moment.slnx --no-restore
```

Both commands succeeded with 0 warnings and 0 errors. Runtime GREEN could not be observed because the policy block above prevented xUnit from loading the compiled assembly; no product code or security policy was changed to bypass it.
- SQLite connection pooling is intentionally disabled to preserve deterministic cleanup for the per-operation connection design. This trades pooling throughput for reliable release of the portable/local database file in the current scope.
