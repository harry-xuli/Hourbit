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
