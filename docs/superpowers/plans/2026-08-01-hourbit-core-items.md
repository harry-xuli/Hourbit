# Hourbit Core Items Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to execute this plan task-by-task. Use `superpowers:test-driven-development` for each production change and `superpowers:verification-before-completion` before completion.

**Goal:** Release Hourbit 日程 0.2.0 with first-class dated/undated todos, locale-aware date entry, 24-hour presentation, bidirectional todo/reminder conversion, and one authoritative version source.

**Architecture:** Keep todos separate from scheduled reminders in domain, persistence, and services. Quick entry returns a typed draft union and the timeline composes two independent read models. Preserve internal `Moment.*` identities and the existing data path while replacing public branding.

**Tech Stack:** .NET 10, C# 14, WPF, Microsoft.Data.Sqlite, xUnit, Inno Setup 6.

## Global Constraints

- Execute after `2026-08-01-hourbit-missed-reminder-recovery.md`.
- Keep installer AppId, data folder, database filename, and `Moment.*` namespaces unchanged.
- Todos never enter scheduler queries or notification sinks.
- All displayed times use `HH:mm`; culture controls ambiguous numeric date order and first day of week.
- Each task ends with focused passing tests and a commit.

---

## Task 1: Establish Hourbit identity and one version source

**Files:**
- Create: `Version.props`
- Modify: `Directory.Build.props`
- Modify: `src/Moment.App/Moment.App.csproj`
- Modify: `src/Moment.App/Properties/AssemblyInfo.cs`
- Modify: `src/Moment.App/Settings/ReleasePageService.cs`
- Modify: `src/Moment.App/Settings/SettingsView.xaml`
- Modify: `src/Moment.App/MainWindow.xaml`
- Modify: `src/Moment.App/Shell/TrayIconController.cs`
- Modify: `src/Moment.Windows/Notifications/AppNotificationSink.cs`
- Modify: `installer/Moment.iss`
- Modify: `scripts/build-release.ps1`
- Modify: `scripts/smoke-test.ps1`
- Modify: `tests/Moment.App.Tests/Settings/SettingsViewModelTests.cs`
- Create: `tests/Moment.App.Tests/Diagnostics/ProductMetadataTests.cs`

- [ ] Add failing metadata tests asserting product `Hourbit 日程`, assembly/executable `Hourbit`, version `0.2.0`, release date `2026-08-01`, settings footer text, and no public `时刻` label.
- [ ] Run `dotnet test tests/Moment.App.Tests/Moment.App.Tests.csproj --filter "FullyQualifiedName~ProductMetadata|FullyQualifiedName~SettingsViewModel"` and confirm failure.
- [ ] Create `Version.props` with `Hourbit 日程`, `Hourbit`, `0.2.0`, and `2026-08-01`; import it from `Directory.Build.props`. Map values to `AssemblyName`, `Version`, `Product`, and `AssemblyMetadata` entries without changing `RootNamespace`.
- [ ] Change only user-visible strings in window, tray, notification, settings, installer/uninstaller, shortcuts, artifacts, and documentation. Point installer run/icon entries to `Hourbit.exe` while retaining the existing AppId and install/data location.
- [ ] Make `build-release.ps1` query evaluated MSBuild properties and validate SemVer plus ISO date before publishing. Derive `Hourbit-Portable-x64.zip` and `Hourbit-Setup-x64.exe`; pass product/version defines to Inno instead of retaining defaults.
- [ ] Run focused tests and `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -ValidateOnly`.
- [ ] Commit with `git add Version.props Directory.Build.props src installer scripts tests/Moment.App.Tests && git commit -m "feat: rename product to Hourbit and centralize version"`.

## Task 2: Return typed reminder or todo drafts from quick parsing

**Files:**
- Modify: `src/Moment.Core/Parsing/ParseResult.cs`
- Modify: `src/Moment.Core/Parsing/ChineseTimeParser.cs`
- Modify: `tests/Moment.Core.Tests/Parsing/ChineseTimeParserTests.cs`

- [ ] Add table-driven failing tests for `zh-CN`, `en-US`, and `en-GB`: ISO and Chinese dates; `/`, `-`, `.` separators; YMD/MDY/DMY order; leap days; invalid dates; `00:00` and `23:59`; invalid `24:00`; Chinese periods; title-only; date-only; date+time; and recurrence words without a time.
- [ ] Run `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~ChineseTimeParserTests"` and confirm new cases fail.
- [ ] Define `abstract record ItemDraft`, `ReminderDraft : ItemDraft`, and `TodoDraft(string Title, DateOnly? DueDate, ReminderImportance Importance) : ItemDraft`; change `ParseResult.Success` and `ParseChoice` to contain `ItemDraft`.
- [ ] Add `CultureInfo culture` to `IChineseTimeParser.Parse`. Interpret leading four-digit years as YMD; otherwise derive field order from `culture.DateTimeFormat.ShortDatePattern`. Reject impossible dates and time values outside 24-hour bounds.
- [ ] Preserve existing relative expressions and Chinese period parsing. Strip scheduling tokens only after a valid date/time is recognized; therefore `每天锻炼` remains an undated todo titled exactly `每天锻炼`.
- [ ] Run focused tests and confirm all culture cases pass.
- [ ] Commit with `git add src/Moment.Core/Parsing tests/Moment.Core.Tests/Parsing && git commit -m "feat: parse localized reminders and todos"`.

## Task 3: Add schema v3 and todo persistence

**Files:**
- Create: `src/Moment.Core/Domain/TodoItem.cs`
- Create: `src/Moment.Core/Abstractions/ITodoRepository.cs`
- Modify: `src/Moment.Infrastructure/Data/DatabaseMigrator.cs`
- Create: `src/Moment.Infrastructure/Data/SqliteTodoRepository.cs`
- Create: `tests/Moment.TestSupport/FakeTodoRepository.cs`
- Create: `tests/Moment.Core.Tests/Domain/TodoItemTests.cs`
- Create: `tests/Moment.Infrastructure.Tests/Data/SqliteTodoRepositoryTests.cs`

- [ ] Add failing tests for normalized title, optional `DateOnly`, CRUD, completion timestamp, due-date ordering inputs, schema-v1/v2 upgrade, schema-v3 idempotence, and reminder-row preservation.
- [ ] Run `dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Todo|FullyQualifiedName~Migration"` and confirm failure.
- [ ] Implement immutable `TodoItem(Guid Id, string Title, DateTimeOffset CreatedAt, DateOnly? DueDate, ReminderImportance Importance, bool IsCompleted, DateTimeOffset? CompletedAt)` with completion invariants.
- [ ] Define `ITodoRepository` members `SaveAsync`, `GetAsync`, `GetAllAsync`, `UpdateAsync`, `SetCompletedAsync`, and `DeleteAsync`.
- [ ] Add transactional, idempotent schema version 3 with `todos(id, title, created_at, due_date, importance, is_completed, completed_at)`. Store dates as `yyyy-MM-dd` and timestamps in the existing round-trip format.
- [ ] Implement the SQLite and fake repositories; never join todos into reminder scheduler queries.
- [ ] Run focused tests and existing migration/backup suites.
- [ ] Commit with `git add src/Moment.Core src/Moment.Infrastructure/Data tests/Moment.TestSupport tests/Moment.Core.Tests/Domain tests/Moment.Infrastructure.Tests/Data && git commit -m "feat: persist first-class todos"`.

## Task 4: Add todo operations and atomic type conversion

**Files:**
- Create: `src/Moment.Core/Services/TodoService.cs`
- Create: `src/Moment.Core/Services/IItemConversionStore.cs`
- Create: `src/Moment.Infrastructure/Data/SqliteItemConversionStore.cs`
- Create: `tests/Moment.Core.Tests/Services/TodoServiceTests.cs`
- Create: `tests/Moment.Infrastructure.Tests/Data/SqliteItemConversionStoreTests.cs`

- [ ] Write failing tests for create/edit/complete/delete plus todo-to-reminder and reminder-to-dated/undated-todo conversion, preserving title, importance, completion state, and rollback on injected failure.
- [ ] Define `ITodoService` CRUD methods plus `ConvertToReminderAsync(Guid todoId, ReminderDraft draft, CancellationToken ct)` and `ConvertToTodoAsync(Guid occurrenceId, TodoDraft draft, CancellationToken ct)`.
- [ ] Define store methods that accept fully constructed source/destination values and execute insert-destination/delete-source in one SQLite transaction. Completed destinations must not be scheduled.
- [ ] Implement the service with the existing clock, recurrence behavior, scheduler signal, and validation conventions.
- [ ] Run `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~TodoService"` and `dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ItemConversion"`.
- [ ] Commit with `git add src/Moment.Core/Services src/Moment.Infrastructure/Data tests/Moment.Core.Tests/Services tests/Moment.Infrastructure.Tests/Data && git commit -m "feat: add todo actions and item conversion"`.

## Task 5: Update Quick Add and edit flows

**Files:**
- Modify: `src/Moment.App/QuickAdd/QuickAddViewModel.cs`
- Modify: `src/Moment.App/QuickAdd/QuickAddWindow.xaml`
- Modify: `src/Moment.App/CompositionRoot.cs`
- Modify: `src/Moment.App/Timeline/EditReminderViewModel.cs`
- Create: `src/Moment.App/Timeline/EditTodoViewModel.cs`
- Create: `src/Moment.App/Timeline/EditTodoWindow.xaml`
- Create: `src/Moment.App/Timeline/EditTodoWindow.xaml.cs`
- Modify: `src/Moment.App/Timeline/TimelineDialogService.cs`
- Modify: `tests/Moment.App.Tests/QuickAdd/QuickAddViewModelTests.cs`
- Modify: `tests/Moment.App.Tests/Timeline/EditReminderViewModelTests.cs`
- Create: `tests/Moment.App.Tests/Timeline/EditTodoViewModelTests.cs`

- [ ] Add failing view-model tests for previews `待办 · 无日期`, `待办 · 截止 2026-08-05`, `提醒 · 2026-08-05 14:30`, service dispatch, retained invalid input, and both edit conversions.
- [ ] Run the focused QuickAdd/Edit tests and confirm failure.
- [ ] Inject the active Windows culture and both services into Quick Add; dispatch by draft runtime type only after validation. Render every reminder time with invariant `HH:mm`.
- [ ] Add todo editing controls for optional date, importance, complete/delete, and adding a time. Add removing-time conversion to the reminder editor; display persistence errors inline and keep dialogs open on failure.
- [ ] Update composition and dialog service without duplicating business logic in code-behind.
- [ ] Run focused tests and the existing WPF control tests.
- [ ] Commit with `git add src/Moment.App tests/Moment.App.Tests && git commit -m "feat: create and convert todos in the UI"`.

## Task 6: Render the split todo/reminder timeline

**Files:**
- Modify: `src/Moment.Core/Services/ITimelineQuery.cs`
- Modify: `src/Moment.Infrastructure/Data/SqliteTimelineQuery.cs`
- Modify: `src/Moment.App/Timeline/TimelineItemViewModel.cs`
- Create: `src/Moment.App/Timeline/TodoTimelineItemViewModel.cs`
- Modify: `src/Moment.App/Timeline/TimelineViewModel.cs`
- Modify: `src/Moment.App/Timeline/TimelineView.xaml`
- Modify: `tests/Moment.App.Tests/Data/SqliteTimelineQueryTests.cs`
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewModelTests.cs`
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewTests.cs`

- [ ] Add failing query/view tests for separate collections, todo ordering (overdue, today, future, undated, stable id), completed collapse, `已逾期`, section accessible names, reminder-only next count, and combined today-completed tooltip breakdown.
- [ ] Replace the single query result with `TimelineSnapshot(IReadOnlyList<TodoTimelineRow> Todos, IReadOnlyList<TimelineRow> Reminders, int TodosCompletedToday, int RemindersCompletedToday)` while retaining existing reminder rows.
- [ ] Query todos independently and expose pending/completed todo collections above the unchanged reminder groups. Dated overdue todos remain actionable and never invoke notifications.
- [ ] Bind complete/edit/delete commands and keep keyboard behaviors (`Ctrl+N`, completion shortcut, `Delete`, `Enter`) scoped to the focused item type.
- [ ] Run the three focused test classes, then `dotnet test Moment.slnx --configuration Release --no-restore`.
- [ ] Commit with `git add src/Moment.Core/Services/ITimelineQuery.cs src/Moment.Infrastructure/Data/SqliteTimelineQuery.cs src/Moment.App/Timeline tests/Moment.App.Tests && git commit -m "feat: show todos and reminders on one timeline"`.

## Task 7: Verify migration, identity, and packaged release

**Files:**
- Modify: `README.md`
- Modify: `docs/release-checklist.md` (create if absent)
- Modify: `src/Moment.App/Diagnostics/SmokeTestRunner.cs`
- Modify: `tests/Moment.App.Tests/Diagnostics/SmokeTestRunnerTests.cs`
- Modify: `scripts/smoke-test.ps1`

- [ ] Extend packaged smoke tests to upgrade a schema-v2 database, retain an existing reminder, create dated and undated todos, prove scheduler exclusion, and assert EXE/installer/settings/artifact version agreement.
- [ ] Update documentation with Hourbit branding, supported date formats, 24-hour examples, todo rules, and the requirement that future releases edit only `Version.props`.
- [ ] Run `dotnet test Moment.slnx --configuration Release --no-restore`.
- [ ] Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1` and `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1`.
- [ ] Manually verify `zh-CN`, `en-US`, and `en-GB` Windows regional formats; confirm no todo notification; confirm installer upgrades the existing AppId/data in place.
- [ ] Commit with `git add README.md docs src/Moment.App/Diagnostics tests/Moment.App.Tests/Diagnostics scripts/smoke-test.ps1 && git commit -m "docs: finalize Hourbit 0.2 core release"`.
