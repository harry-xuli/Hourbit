# Task 3 Report: Schema v3 and todo persistence

## Status

Implemented and verified. Todos now have an immutable domain model, a dedicated
repository contract, aligned SQLite/fake implementations, and an isolated schema-v3
table. Existing reminder scheduler queries remain unchanged.

## Domain and repository contract

`TodoItem` exposes exactly these get-only fields:

- `Guid Id`
- normalized `string Title`
- `DateTimeOffset CreatedAt`
- optional `DateOnly DueDate`
- `ReminderImportance Importance`
- `bool IsCompleted`
- optional `DateTimeOffset CompletedAt`

Construction trims titles and requires a normalized length from 1 through 200.
Completion state and timestamp must agree in both directions, and a completion
timestamp cannot precede creation. `DateOnly?` preserves dated and undated todos
without a sentinel value.

`ITodoRepository` defines `SaveAsync`, `GetAsync`, `GetAllAsync`, `UpdateAsync`,
`SetCompletedAsync`, and `DeleteAsync`. SQLite and fake repositories both use
dated-first ordering by due date and stable identifier, followed by undated todos.
Missing update/completion/delete targets are no-ops; duplicate saves fail without
replacing the stored item.

## Schema SQL and invariants

Schema version 3 is appended inside the existing `MigrateAsync` transaction:

```sql
CREATE TABLE IF NOT EXISTS todos (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL CHECK(length(trim(title)) BETWEEN 1 AND 200),
    created_at TEXT NOT NULL,
    due_date TEXT NULL CHECK(
        due_date IS NULL OR (
            length(due_date) = 10 AND
            due_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
        )
    ),
    importance INTEGER NOT NULL CHECK(importance IN (0, 1)),
    is_completed INTEGER NOT NULL CHECK(is_completed IN (0, 1)),
    completed_at TEXT NULL,
    CHECK(
        (is_completed = 0 AND completed_at IS NULL) OR
        (is_completed = 1 AND completed_at IS NOT NULL)
    )
);
INSERT INTO schema_info(version) VALUES (3);
```

The insert runs only when `schema_info` has no version-3 row. Reopening a v3 database
therefore leaves one table and one v3 row. The repository formats due dates with
invariant `yyyy-MM-dd`, validates them through `DateOnly`, and formats timestamps with
the existing invariant round-trip `O` representation. Reads use `ParseExact` for due
dates and `DateTimeStyles.RoundtripKind` for timestamps.

No todo table appears in reminder repository joins, scheduler due queries, or
notification paths.

## TDD evidence

### RED

Added domain, CRUD, completion, ordering, migration, preservation, idempotence,
fake parity, and scheduler-exclusion tests before the production types existed, then
ran the focused command:

```text
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~Todo|FullyQualifiedName~Migration"
```

The build failed with the expected missing todo contract (`CS0246: TodoItem could not
be found`). No unrelated test failure was involved.

### GREEN

Focused todo/migration command after implementation:

```text
Passed: 10, Failed: 0, Skipped: 0
```

Focused domain command:

```text
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj -c Release --filter "FullyQualifiedName~TodoItem"
Passed: 7, Failed: 0, Skipped: 0
```

Existing migration/backup regression command:

```text
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~Migration|FullyQualifiedName~Backup"
Passed: 26, Failed: 0, Skipped: 0
```

Fresh full Release suite:

```text
dotnet test Moment.slnx -c Release
```

Results:

- Moment.Core.Tests: 185 passed;
- Moment.Infrastructure.Tests: 58 passed;
- Moment.Windows.Tests: 88 passed;
- Moment.App.Tests: 132 passed;
- total: 463 passed, 0 failed, 0 skipped.

## Upgrade and compatibility evidence

- A handcrafted schema-v1 database containing an item, occurrence, and action-log row
  upgrades through v2 to v3; all three identifiers remain queryable afterward and a todo
  can be saved.
- A handcrafted schema-v2 database with the same reminder/action history upgrades directly
  to v3 with all rows preserved.
- Calling migration twice produces exactly one version-3 row and one `todos` table.
- Todo-only databases return no scheduled or due reminders through
  `SqliteReminderRepository`.
- Backup package format remains version 1 and still contains exactly `manifest.json` plus
  the whole `moment.db`. Its supported database-schema ceiling and manifest expectation
  are now 3, so the unchanged whole-file backup naturally includes todos.

## Files changed

- `src/Moment.Core/Domain/TodoItem.cs`
- `src/Moment.Core/Abstractions/ITodoRepository.cs`
- `src/Moment.Infrastructure/Data/DatabaseMigrator.cs`
- `src/Moment.Infrastructure/Data/SqliteTodoRepository.cs`
- `src/Moment.Infrastructure/Backup/BackupService.cs`
- `tests/Moment.TestSupport/FakeTodoRepository.cs`
- `tests/Moment.Core.Tests/Domain/TodoItemTests.cs`
- `tests/Moment.Infrastructure.Tests/Data/SqliteTodoRepositoryTests.cs`
- `tests/Moment.Infrastructure.Tests/Backup/BackupServiceTests.cs`

## Concerns / handoff

- No SQLite compatibility blocker was found with the bundled provider.
- Calendar validity is guaranteed at the repository boundary by `DateOnly`; the table
  check additionally enforces the invariant textual shape for raw SQLite writes.
- Schema v4 soft deletion and analytics changes were deliberately not implemented.
- Task 4 can use `ITodoRepository` for todo operations; transactional cross-domain
  conversion remains outside this task.

## Fix Round 1: Reject incomplete logical schema v3

### Finding and root cause

The original v3 branch treated `COUNT(version = 3) > 0` as proof that migration had
completed. It therefore accepted a marker with no `todos` table, a malformed table, and
duplicate v3 markers. When no marker existed, `CREATE TABLE IF NOT EXISTS` silently kept a
pre-existing malformed table before inserting the marker. Backup validation independently
trusted only `MAX(version)` plus `integrity_check`; SQLite physical integrity does not prove
the application-level table contract.

### RED

Added deterministic cases for:

- one v3 marker with no `todos` table;
- ten individually malformed table definitions covering a missing column, nullability,
  a column default, primary-key/index shape, declared type, and each required CHECK family;
- a malformed pre-existing table with no v3 marker and transactional marker rollback;
- a canonical pre-existing table with no marker;
- duplicate v3 markers and unchanged marker count after rejection;
- backup export of v3 databases with a missing or malformed `todos` table.

The initial corrected focused run reported 15 expected failures and 11 passes. Every
failure was acceptance of logical schema corruption; fixture setup and compilation were
clean.

### Implementation

`DatabaseSchemaValidator` is now the single v3 contract used by both paths:

- `PRAGMA table_info(todos)` validates exactly seven ordered columns, declared types,
  nullability, absence of defaults, and the `id` primary-key flag;
- `PRAGMA index_list(todos)` plus `PRAGMA index_info(...)` requires exactly one non-partial
  primary-key index containing only `id`;
- normalized `sqlite_master.sql` validates CHECK clauses that SQLite does not expose via a
  structural PRAGMA. Comparison ignores case, whitespace, a trailing semicolon, and the
  optional `IF NOT EXISTS` phrase, but requires the canonical table body;
- migration requires zero or one v3 marker. With one marker it validates the table. With
  no marker it validates any pre-existing table before inserting the marker, or creates
  the canonical table, then validates the completed schema before commit;
- any failure unwinds the existing migration transaction, leaving marker rows unchanged;
- backup schema reading invokes the same validator against the read-only snapshot whenever
  `MAX(version)` is 3, so export and restore validation cannot drift from migration.

### GREEN and regression evidence

Targeted reproduction command:

```text
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~Todo|FullyQualifiedName~Migration|FullyQualifiedName~Export_rejects_a_logically"
Passed: 26, Failed: 0, Skipped: 0
```

Complete todo/migration/backup slice:

```text
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~Todo|FullyQualifiedName~Migration|FullyQualifiedName~Backup"
Passed: 49, Failed: 0, Skipped: 0
```

Fresh full Release suite:

```text
dotnet test Moment.slnx -c Release
```

Results:

- Moment.Core.Tests: 185 passed;
- Moment.Infrastructure.Tests: 74 passed;
- Moment.Windows.Tests: 88 passed;
- Moment.App.Tests: 132 passed;
- total: 479 passed, 0 failed, 0 skipped.

### Files changed in this round

- `src/Moment.Infrastructure/Data/DatabaseSchemaValidator.cs`
- `src/Moment.Infrastructure/Data/DatabaseMigrator.cs`
- `src/Moment.Infrastructure/Backup/BackupService.cs`
- `tests/Moment.Infrastructure.Tests/Data/SqliteTodoRepositoryTests.cs`
- `tests/Moment.Infrastructure.Tests/Backup/BackupServiceTests.cs`

### Remaining concerns

No SQLite compatibility or locking blocker was found. The normalized SQL fallback is
deliberately limited to CHECK validation because SQLite has no CHECK-list PRAGMA; column and
index validation remains structural. Schema v4 remains out of scope.

One intermediate full-solution run timed out after ten seconds in the unchanged
`ReminderSchedulerTests.Each_committed_transition_raises_state_changed_once_and_observer_failure_does_not_stop_loop`.
The exact test immediately passed alone in 70 ms, and a fresh full Release rerun passed all
479 tests. No Core scheduling source or test file changed in this round; this is recorded as
an existing timing-flake signal rather than attributed to schema validation.
