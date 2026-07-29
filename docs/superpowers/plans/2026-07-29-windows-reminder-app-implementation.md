# “时刻” Windows Reminder App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a reliable, local-first Windows 11 reminder app with countdowns, alarms, daily planning, recurring items, native notifications, important-alert windows, tray operation, backup, installer, and portable distribution.

**Architecture:** A WPF shell consumes application services and never accesses SQLite directly. A domain/core library owns reminder semantics, parsing, recurrence, and scheduling; an infrastructure library owns SQLite and backups; a Windows integration library owns notifications, global hotkeys, power/session events, startup, and single-instance behavior. Every reminder is persisted before the single scheduler is signaled.

**Tech Stack:** C# 14, .NET 10 LTS, WPF, Windows App SDK 2.3.1, Microsoft.Data.Sqlite 10.0.10, xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.8.1, Inno Setup 6.

## Global Constraints

- Target Windows 11 x64. Keep `Moment.Core`, `Moment.Infrastructure`, `Moment.TestSupport`, and their corresponding test projects on `net10.0`; target `Moment.Windows` and `Moment.App` with `net10.0-windows10.0.22621.0`. Do not add cross-platform abstractions that the specification does not require.
- Use WPF for UI and Windows App SDK `AppNotificationManager` for normal notifications.
- Keep the app local-only: no account, telemetry, cloud sync, AI service, or network time parser.
- Persist a reminder occurrence before signaling the scheduler; UI and notification handlers never access SQLite directly.
- Run exactly one scheduler per Windows user session.
- Default global shortcut is `Ctrl+Alt+Space`; startup is disabled by default.
- Installed data path is `%LOCALAPPDATA%\Moment\data\moment.db`; portable data path is `Data\moment.db` beside `portable.flag`.
- Normal missed reminders older than five minutes are grouped; important missed reminders remain individual.
- Use high-contrast text, keyboard-accessible controls, non-color status indicators, 100%–200% scaling, and reduced-motion support.
- Pin package versions centrally: `Microsoft.WindowsAppSDK` 2.3.1, `Microsoft.Data.Sqlite` 10.0.10, `xunit` 2.9.3, and `Microsoft.NET.Test.Sdk` 18.8.1.
- Do not begin implementation until the .NET 10 SDK is installed; packaging tasks additionally require Inno Setup 6.

---

## Planned File Structure

```text
Moment.slnx
Directory.Build.props
Directory.Packages.props
src/
  Moment.Core/
    Domain/ReminderItem.cs
    Domain/ReminderOccurrence.cs
    Domain/RecurrenceRule.cs
    Domain/ReminderEnums.cs
    Abstractions/IClock.cs
    Abstractions/IReminderRepository.cs
    Abstractions/IReminderSink.cs
    Parsing/ChineseTimeParser.cs
    Parsing/ParseResult.cs
    Recurrence/RecurrenceCalculator.cs
    Scheduling/ReminderScheduler.cs
    Scheduling/RecoveryClassifier.cs
    Services/ReminderService.cs
    Services/ReminderActionService.cs
  Moment.Infrastructure/
    Data/DatabasePathResolver.cs
    Data/DatabaseMigrator.cs
    Data/SqliteReminderRepository.cs
    Backup/BackupService.cs
    Backup/BackupManifest.cs
  Moment.Windows/
    Notifications/AppNotificationSink.cs
    Notifications/NotificationArguments.cs
    Alerts/ImportantAlertController.cs
    Hotkeys/GlobalHotkeyService.cs
    Lifecycle/SingleInstanceCoordinator.cs
    Lifecycle/SystemResumeMonitor.cs
    Startup/StartupRegistrationService.cs
  Moment.App/
    App.xaml
    App.xaml.cs
    CompositionRoot.cs
    MainWindow.xaml
    Timeline/TimelineView.xaml
    Timeline/TimelineViewModel.cs
    Timeline/TimelineItemViewModel.cs
    QuickAdd/QuickAddWindow.xaml
    QuickAdd/QuickAddViewModel.cs
    Alerts/ImportantAlertWindow.xaml
    Alerts/ImportantAlertWindow.xaml.cs
    Settings/SettingsView.xaml
    Settings/SettingsViewModel.cs
    Shell/TrayIconController.cs
    Shell/WindowPlacementService.cs
    Styles/Colors.xaml
    Styles/Controls.xaml
    Assets/default-alert.wav
tests/
  Moment.TestSupport/
    TempDirectory.cs
    FakeClock.cs
    FakeReminderRepository.cs
    RecordingReminderSink.cs
    TestData.cs
  Moment.Core.Tests/
  Moment.Infrastructure.Tests/
  Moment.Windows.Tests/
  Moment.App.Tests/
installer/
  Moment.iss
scripts/
  build-release.ps1
  smoke-test.ps1
docs/
  user-guide.md
```

`Moment.Core` contains no WPF, Windows App SDK, or SQLite references. `Moment.Infrastructure` references Core and SQLite. `Moment.Windows` references Core and Windows APIs. `Moment.App` composes all three and contains only presentation and composition code.

---

### Task 1: Solution Skeleton and Domain Model

**Files:**
- Create: `Moment.slnx`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `src/Moment.Core/Moment.Core.csproj`
- Create: `src/Moment.Core/Domain/ReminderEnums.cs`
- Create: `src/Moment.Core/Domain/ReminderItem.cs`
- Create: `src/Moment.Core/Domain/ReminderOccurrence.cs`
- Create: `src/Moment.Core/Domain/RecurrenceRule.cs`
- Create: `src/Moment.Core/Domain/ScheduledReminder.cs`
- Create: `tests/Moment.TestSupport/Moment.TestSupport.csproj`
- Create: `tests/Moment.TestSupport/TempDirectory.cs`
- Create: `tests/Moment.TestSupport/TestData.cs`
- Create: `tests/Moment.Core.Tests/Moment.Core.Tests.csproj`
- Create: `tests/Moment.Core.Tests/Domain/ReminderItemTests.cs`

**Interfaces:**
- Produces: `ReminderItem`, `ReminderOccurrence`, `RecurrenceRule`, `ScheduledReminder`, `ReminderKind`, `ReminderImportance`, `OccurrenceState`, `RecurrenceKind`, and `SeriesScope`.
- Time fields use `DateTimeOffset`; identifiers use `Guid`; user-visible titles are trimmed and limited to 200 characters.

- [ ] **Step 1: Install and verify the required SDK**

Install the x64 .NET 10 SDK from Microsoft, then run:

```powershell
dotnet --list-sdks
```

Expected: at least one line beginning with `10.0.`. Stop this task if the SDK is absent.

- [ ] **Step 2: Scaffold the solution and projects**

```powershell
dotnet new sln --format slnx --name Moment
dotnet new classlib --name Moment.Core --output src/Moment.Core --framework net10.0
dotnet new classlib --name Moment.TestSupport --output tests/Moment.TestSupport --framework net10.0
dotnet new xunit --name Moment.Core.Tests --output tests/Moment.Core.Tests --framework net10.0
dotnet sln Moment.slnx add src/Moment.Core/Moment.Core.csproj
dotnet sln Moment.slnx add tests/Moment.TestSupport/Moment.TestSupport.csproj
dotnet sln Moment.slnx add tests/Moment.Core.Tests/Moment.Core.Tests.csproj
dotnet add tests/Moment.TestSupport/Moment.TestSupport.csproj reference src/Moment.Core/Moment.Core.csproj
dotnet add tests/Moment.Core.Tests/Moment.Core.Tests.csproj reference src/Moment.Core/Moment.Core.csproj
dotnet add tests/Moment.Core.Tests/Moment.Core.Tests.csproj reference tests/Moment.TestSupport/Moment.TestSupport.csproj
```

Delete the generated `Class1.cs` and `UnitTest1.cs` with the repository’s normal patch workflow.

- [ ] **Step 3: Pin build and package settings**

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

Create `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.10" />
    <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.3.1" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
</Project>
```

Open every generated test `.csproj`, remove `Version` attributes from `PackageReference` elements because central package management owns versions, and keep `PrivateAssets=all` plus `IncludeAssets=runtime;build;native;contentfiles;analyzers;buildtransitive` on `xunit.runner.visualstudio`.

- [ ] **Step 4: Write the failing domain test**

```csharp
[Fact]
public void Create_trims_title_and_rejects_due_time_before_creation()
{
    var created = new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));
    var due = created.AddMinutes(20);

    var item = ReminderItem.Create("  起来活动  ", ReminderKind.Countdown,
        ReminderImportance.Normal, created, due);

    Assert.Equal("起来活动", item.Title);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        ReminderItem.Create("错误", ReminderKind.Alarm,
            ReminderImportance.Normal, created, created.AddSeconds(-1)));
}
```

- [ ] **Step 5: Run the test and verify failure**

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Create_trims_title_and_rejects_due_time_before_creation
```

Expected: FAIL because `ReminderItem` does not exist.

- [ ] **Step 6: Implement the domain records**

Use immutable records with factory validation:

```csharp
public sealed record ReminderItem(
    Guid Id,
    string Title,
    ReminderKind Kind,
    ReminderImportance Importance,
    DateTimeOffset CreatedAt,
    RecurrenceRule? Recurrence)
{
    public static ReminderItem Create(
        string title,
        ReminderKind kind,
        ReminderImportance importance,
        DateTimeOffset createdAt,
        DateTimeOffset firstDueAt,
        RecurrenceRule? recurrence = null)
    {
        var normalized = title.Trim();
        if (normalized.Length is 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(title));
        if (firstDueAt < createdAt)
            throw new ArgumentOutOfRangeException(nameof(firstDueAt));
        return new(Guid.NewGuid(), normalized, kind, importance, createdAt, recurrence);
    }
}
```

Define `ReminderOccurrence` with `Id`, `ItemId`, `DueAt`, `State`, `HandledAt`, and `SnoozeParentId`, plus factory `Schedule(Guid itemId, DateTimeOffset dueAt, Guid? snoozeParentId = null)`. Define `ScheduledReminder(ReminderItem Item, ReminderOccurrence Occurrence)`. Define `RecurrenceRule` factories `Daily(TimeOnly)`, `Weekdays(TimeOnly)`, and `Weekly(IEnumerable<DayOfWeek>, TimeOnly)`; the weekly factory normalizes input to an immutable, non-empty set. Define enums exactly as:

```csharp
public enum ReminderKind { Countdown, Alarm, Plan }
public enum ReminderImportance { Normal, Important }
public enum OccurrenceState { Scheduled, Fired, Completed, Ignored, Missed, Snoozed }
public enum RecurrenceKind { Daily, Weekdays, Weekly }
public enum SeriesScope { OccurrenceOnly, ThisAndFuture }
```

- [ ] **Step 7: Create shared test support**

`TempDirectory` creates a unique directory under `Path.GetTempPath()` and removes that exact directory on dispose. `TestData` initially exposes:

```csharp
public static ScheduledReminder Scheduled(string title, string dueAt,
    ReminderImportance importance = ReminderImportance.Normal);
```

Extend `TestData` in the same task that introduces each new production return type. This is the only test-helper project; do not duplicate fake clocks or repositories in individual test assemblies.

- [ ] **Step 8: Run all tests**

```powershell
dotnet test Moment.slnx
```

Expected: PASS.

- [ ] **Step 9: Commit**

```powershell
git add Moment.slnx Directory.Build.props Directory.Packages.props src/Moment.Core tests/Moment.TestSupport tests/Moment.Core.Tests
git commit -m "feat: establish reminder domain model"
```

---

### Task 2: SQLite Schema, Migrations, and Repository

**Files:**
- Create: `src/Moment.Core/Abstractions/IReminderRepository.cs`
- Create: `src/Moment.Infrastructure/Moment.Infrastructure.csproj`
- Create: `src/Moment.Infrastructure/Data/DatabasePathResolver.cs`
- Create: `src/Moment.Infrastructure/Data/DatabaseMigrator.cs`
- Create: `src/Moment.Infrastructure/Data/SqliteReminderRepository.cs`
- Create: `tests/Moment.TestSupport/FakeReminderRepository.cs`
- Create: `tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj`
- Create: `tests/Moment.Infrastructure.Tests/Data/SqliteReminderRepositoryTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IReminderRepository
{
    Task SaveItemWithOccurrenceAsync(ReminderItem item, ReminderOccurrence occurrence, CancellationToken ct);
    Task<IReadOnlyList<ScheduledReminder>> GetScheduledAsync(CancellationToken ct);
    Task<IReadOnlyList<ScheduledReminder>> GetDueAsync(DateTimeOffset through, CancellationToken ct);
    Task<ScheduledReminder?> GetScheduledReminderAsync(Guid occurrenceId, CancellationToken ct);
    Task<ReminderItem?> GetItemAsync(Guid itemId, CancellationToken ct);
    Task SetOccurrenceStateAsync(Guid occurrenceId, OccurrenceState state, DateTimeOffset handledAt, CancellationToken ct);
    Task SaveOccurrenceAsync(ReminderOccurrence occurrence, CancellationToken ct);
    Task<bool> TryMarkFiredAsync(Guid occurrenceId, DateTimeOffset firedAt, CancellationToken ct);
    Task ApplyActionAsync(Guid occurrenceId, OccurrenceState state,
        DateTimeOffset handledAt, ReminderOccurrence? nextOccurrence, CancellationToken ct);
    Task EditAsync(Guid occurrenceId, ReminderItem item,
        ReminderOccurrence occurrence, SeriesScope scope, CancellationToken ct);
    Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct);
}
```

- Database schema version 1 contains `items`, `occurrences`, `recurrence_rules`, `action_log`, `settings`, and `schema_info`.

- [ ] **Step 1: Scaffold infrastructure projects**

```powershell
dotnet new classlib --name Moment.Infrastructure --output src/Moment.Infrastructure --framework net10.0
dotnet new xunit --name Moment.Infrastructure.Tests --output tests/Moment.Infrastructure.Tests --framework net10.0
dotnet add src/Moment.Infrastructure/Moment.Infrastructure.csproj package Microsoft.Data.Sqlite
dotnet add src/Moment.Infrastructure/Moment.Infrastructure.csproj reference src/Moment.Core/Moment.Core.csproj
dotnet add tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj reference src/Moment.Infrastructure/Moment.Infrastructure.csproj
dotnet add tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj reference src/Moment.Core/Moment.Core.csproj
dotnet add tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj reference tests/Moment.TestSupport/Moment.TestSupport.csproj
dotnet sln Moment.slnx add src/Moment.Infrastructure/Moment.Infrastructure.csproj
dotnet sln Moment.slnx add tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj
```

- [ ] **Step 2: Write the failing persistence test**

```csharp
[Fact]
public async Task SaveItemWithOccurrence_is_atomic_and_round_trips()
{
    using var temp = new TempDirectory();
    var repository = await SqliteReminderRepository.OpenAsync(
        Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
    var created = new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));
    var item = ReminderItem.Create("会议", ReminderKind.Plan,
        ReminderImportance.Important, created, created.AddHours(1));
    var occurrence = ReminderOccurrence.Schedule(item.Id, created.AddHours(1));

    await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);

    var scheduled = await repository.GetScheduledAsync(CancellationToken.None);
    Assert.Single(scheduled);
    Assert.Equal(item.Id, scheduled[0].Occurrence.ItemId);
}
```

- [ ] **Step 3: Verify failure**

```powershell
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj --filter SaveItemWithOccurrence_is_atomic_and_round_trips
```

Expected: FAIL because the repository is absent.

- [ ] **Step 4: Implement migration 1 and repository transactions**

`DatabaseMigrator` opens SQLite with `Mode=ReadWriteCreate;Cache=Shared`, enables `PRAGMA foreign_keys=ON`, and executes migration 1 inside one transaction. Use ISO-8601 round-trip strings for `DateTimeOffset`.

The atomic save must have this shape:

```csharp
await using var transaction = await connection.BeginTransactionAsync(ct);
await InsertItemAsync(connection, transaction, item, ct);
if (item.Recurrence is not null)
    await InsertRecurrenceAsync(connection, transaction, item.Id, item.Recurrence, ct);
await InsertOccurrenceAsync(connection, transaction, occurrence, ct);
await transaction.CommitAsync(ct);
```

Create indexes on `occurrences(state, due_at)` and `occurrences(item_id)`. Add a unique constraint on `(item_id, due_at)` to prevent duplicate recurrence instances.

Extend `tests/Moment.TestSupport/FakeReminderRepository.cs` to implement every repository method with locked dictionaries, including compare-and-set behavior in `TryMarkFiredAsync`. Keep the fake non-sealed and its interface methods virtual so ordering tests can derive recording variants.

- [ ] **Step 5: Implement deterministic path selection**

```csharp
public static string Resolve(string executableDirectory)
{
    if (File.Exists(Path.Combine(executableDirectory, "portable.flag")))
        return Path.Combine(executableDirectory, "Data", "moment.db");
    var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(root, "Moment", "data", "moment.db");
}
```

Add tests for installed and portable paths.

- [ ] **Step 6: Run repository tests twice**

```powershell
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.csproj
```

Expected: both runs PASS, proving migration idempotence.

- [ ] **Step 7: Commit**

```powershell
git add src/Moment.Core/Abstractions src/Moment.Infrastructure tests/Moment.Infrastructure.Tests Moment.slnx
git commit -m "feat: persist reminders in sqlite"
```

---

### Task 3: Recurrence Calculation

**Files:**
- Create: `src/Moment.Core/Recurrence/RecurrenceCalculator.cs`
- Create: `tests/Moment.Core.Tests/Recurrence/RecurrenceCalculatorTests.cs`

**Interfaces:**
- Consumes: `RecurrenceRule`.
- Produces:

```csharp
public interface IRecurrenceCalculator
{
    DateTimeOffset NextAfter(RecurrenceRule rule, DateTimeOffset after, TimeZoneInfo zone);
}
```

- [ ] **Step 1: Write failing boundary tests**

```csharp
[Theory]
[InlineData("2026-07-31T18:00:00+08:00", "2026-08-03T18:00:00+08:00")]
[InlineData("2026-08-03T18:00:00+08:00", "2026-08-04T18:00:00+08:00")]
public void Weekdays_skip_weekends(string afterText, string expectedText)
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var rule = RecurrenceRule.Weekdays(new TimeOnly(18, 0));
    var next = new RecurrenceCalculator().NextAfter(
        rule, DateTimeOffset.Parse(afterText), zone);
    Assert.Equal(DateTimeOffset.Parse(expectedText), next);
}

[Fact]
public void Weekly_supports_more_than_one_day()
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var rule = RecurrenceRule.Weekly(
        [DayOfWeek.Monday, DayOfWeek.Friday], new TimeOnly(16, 0));
    var next = new RecurrenceCalculator().NextAfter(
        rule, DateTimeOffset.Parse("2026-07-27T16:00:00+08:00"), zone);
    Assert.Equal(DateTimeOffset.Parse("2026-07-31T16:00:00+08:00"), next);
}
```

- [ ] **Step 2: Verify failure**

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "Weekdays_skip_weekends|Weekly_supports_more_than_one_day"
```

Expected: FAIL because `RecurrenceCalculator` is absent.

- [ ] **Step 3: Implement recurrence using local wall-clock values**

Convert `after` into `zone`, advance at least one minute/day as required, choose the next permitted local date, combine it with `rule.LocalTime`, then convert through `TimeZoneInfo.GetUtcOffset`. Reject invalid local DST times by advancing to the first valid minute; choose the earlier UTC instant for an ambiguous local time.

Core loop:

```csharp
for (var offset = 0; offset <= 370; offset++)
{
    var date = localAfter.Date.AddDays(offset);
    if (!rule.Allows(date.DayOfWeek))
        continue;
    var candidateLocal = date + rule.LocalTime.ToTimeSpan();
    if (candidateLocal <= localAfter.DateTime)
        continue;
    return ResolveLocal(candidateLocal, zone);
}
throw new InvalidOperationException("No occurrence found within 370 days.");
```

- [ ] **Step 4: Test month, year, and DST transitions**

Add explicit tests for December 31 to January 1 and a DST-observing test zone. Run:

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Recurrence
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Moment.Core/Recurrence tests/Moment.Core.Tests/Recurrence
git commit -m "feat: calculate recurring reminder times"
```

---

### Task 4: Deterministic Chinese Time Parser

**Files:**
- Create: `src/Moment.Core/Parsing/ParseResult.cs`
- Create: `src/Moment.Core/Parsing/ChineseTimeParser.cs`
- Modify: `tests/Moment.TestSupport/TestData.cs`
- Create: `tests/Moment.Core.Tests/Parsing/ChineseTimeParserTests.cs`

**Interfaces:**
- Produces:

```csharp
public abstract record ParseResult
{
    public sealed record Success(ReminderDraft Draft) : ParseResult;
    public sealed record Ambiguous(string OriginalText, IReadOnlyList<ParseChoice> Choices) : ParseResult;
    public sealed record Invalid(string OriginalText, string Message) : ParseResult;
}

public sealed record ReminderDraft(
    string Title,
    DateTimeOffset DueAt,
    ReminderKind Kind,
    ReminderImportance Importance,
    RecurrenceRule? Recurrence);

public sealed record ParseChoice(string Label, ReminderDraft Draft);

public interface IChineseTimeParser
{
    ParseResult Parse(string text, DateTimeOffset now, TimeZoneInfo zone);
}
```

- [ ] **Step 1: Write a parameterized failing test**

```csharp
[Theory]
[InlineData("20分钟后休息", "休息", "2026-07-29T09:20:00+08:00")]
[InlineData("下午3点半提醒我打电话", "打电话", "2026-07-29T15:30:00+08:00")]
[InlineData("明早9点开会", "开会", "2026-07-30T09:00:00+08:00")]
public void Parses_supported_phrases(string text, string title, string due)
{
    var now = DateTimeOffset.Parse("2026-07-29T09:00:00+08:00");
    var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var result = Assert.IsType<ParseResult.Success>(
        new ChineseTimeParser().Parse(text, now, zone));
    Assert.Equal(title, result.Draft.Title);
    Assert.Equal(DateTimeOffset.Parse(due), result.Draft.DueAt);
}

[Theory]
[InlineData("晚上提醒我看书")]
[InlineData("待会提醒我喝水")]
[InlineData("下周提醒我交报告")]
public void Returns_choices_for_ambiguous_phrases(string text)
{
    var result = new ChineseTimeParser().Parse(text,
        DateTimeOffset.Parse("2026-07-29T09:00:00+08:00"),
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
    Assert.IsType<ParseResult.Ambiguous>(result);
}
```

- [ ] **Step 2: Verify failure**

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "Parses_supported_phrases|Returns_choices_for_ambiguous_phrases"
```

Expected: FAIL because parser types are absent.

- [ ] **Step 3: Implement ordered parsing rules**

Apply rules in this order:

1. Trim whitespace and normalize Chinese punctuation.
2. Extract recurrence prefixes `每天`, `每个工作日`, `每周X`.
3. Extract relative duration `N分钟后` or `N小时后`.
4. Extract date tokens `今天`, `明天`, `明早`.
5. Extract `上午`, `中午`, `下午`, `晚上` plus hour/minute.
6. Remove `提醒我` and time tokens to form the title.
7. Return `Ambiguous` when a required date or clock time remains non-unique.

Use compiled regular expressions with named groups. Do not call external services or use culture-dependent `DateTime.Parse` for Chinese phrases.

Add `TestData.Draft(string title, string dueAt)` when `ReminderDraft` is introduced.

- [ ] **Step 4: Add recurrence and invalid-input tests**

Cover `每个工作日18点下班`, `每周五下午4点写周报`, blank input, a title longer than 200 characters, and a past explicit time. Run:

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Parsing
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Moment.Core/Parsing tests/Moment.Core.Tests/Parsing
git commit -m "feat: parse common Chinese reminder phrases"
```

---

### Task 5: Scheduler and Resume Recovery

**Files:**
- Create: `src/Moment.Core/Abstractions/IClock.cs`
- Create: `src/Moment.Core/Abstractions/IReminderSink.cs`
- Create: `src/Moment.Core/Scheduling/ReminderScheduler.cs`
- Create: `src/Moment.Core/Scheduling/RecoveryClassifier.cs`
- Create: `tests/Moment.TestSupport/FakeClock.cs`
- Create: `tests/Moment.TestSupport/RecordingReminderSink.cs`
- Modify: `tests/Moment.TestSupport/FakeReminderRepository.cs`
- Create: `tests/Moment.Core.Tests/Scheduling/ReminderSchedulerTests.cs`
- Create: `tests/Moment.Core.Tests/Scheduling/RecoveryClassifierTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IClock
{
    DateTimeOffset Now { get; }
    Task DelayUntilAsync(DateTimeOffset dueAt, CancellationToken ct);
}

public interface IReminderSink
{
    Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct);
    Task DeliverMissedSummaryAsync(IReadOnlyList<ScheduledReminder> reminders, CancellationToken ct);
}

public interface ISchedulerSignal
{
    void Refresh();
}

public sealed record RecoveryResult(
    IReadOnlyList<ScheduledReminder> Immediate,
    IReadOnlyList<ScheduledReminder> Summary);
```

- [ ] **Step 1: Write a failing scheduler test with fakes**

```csharp
[Fact]
public async Task Refresh_interrupts_wait_and_delivers_new_earlier_occurrence_once()
{
    var clock = new FakeClock("2026-07-29T09:00:00+08:00");
    var repository = new FakeReminderRepository();
    var sink = new RecordingReminderSink();
    await repository.AddAsync(TestData.Scheduled("later", clock.Now.AddHours(1).ToString("O")));
    using var scheduler = new ReminderScheduler(repository, sink, clock);
    await scheduler.StartAsync(CancellationToken.None);

    await repository.AddAsync(TestData.Scheduled("earlier", clock.Now.AddMinutes(1).ToString("O")));
    scheduler.Refresh();
    clock.AdvanceBy(TimeSpan.FromMinutes(1));

    await sink.WaitForCountAsync(1);
    Assert.Equal("earlier", sink.Deliveries.Single().Item.Title);
}
```

- [ ] **Step 2: Verify failure**

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Refresh_interrupts_wait
```

Expected: FAIL because scheduler types are absent.

- [ ] **Step 3: Implement a single interruptible scheduling loop**

Use `SemaphoreSlim _refreshSignal = new(0, 1)`. Each loop reads the earliest scheduled occurrence, races `clock.DelayUntilAsync` against `_refreshSignal.WaitAsync`, and re-queries after either completion. Before delivery, atomically transition `Scheduled` to `Fired`; if the conditional update affects zero rows, skip delivery.

Use the repository `TryMarkFiredAsync` compare-and-set method defined in Task 2.

Implement `FakeClock` with deterministic `Now`, queued `DelayUntilAsync`, and `AdvanceBy`. Implement `RecordingReminderSink` with a thread-safe delivery list and `WaitForCountAsync`.

- [ ] **Step 4: Implement recovery classification**

```csharp
public RecoveryResult Classify(
    IReadOnlyList<ScheduledReminder> due,
    DateTimeOffset now)
{
    var cutoff = now.AddMinutes(-5);
    var immediate = due.Where(x =>
        x.Item.Importance == ReminderImportance.Important ||
        x.Occurrence.DueAt >= cutoff).ToArray();
    var summary = due.Where(x =>
        x.Item.Importance == ReminderImportance.Normal &&
        x.Occurrence.DueAt < cutoff).ToArray();
    return new(immediate, summary);
}
```

Add tests at exactly 4:59, 5:00, and 5:01 late.

- [ ] **Step 5: Run scheduler tests**

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Scheduling
```

Expected: PASS, including simultaneous due items and duplicate refresh signals.

- [ ] **Step 6: Commit**

```powershell
git add src/Moment.Core/Abstractions src/Moment.Core/Scheduling tests/Moment.Core.Tests/Scheduling
git commit -m "feat: schedule and recover reminder occurrences"
```

---

### Task 6: Reminder Creation and Action Services

**Files:**
- Create: `src/Moment.Core/Services/ReminderService.cs`
- Create: `src/Moment.Core/Services/ReminderActionService.cs`
- Create: `tests/Moment.Core.Tests/Services/ReminderServiceTests.cs`
- Create: `tests/Moment.Core.Tests/Services/ReminderActionServiceTests.cs`

**Interfaces:**
- Consumes: `IReminderRepository`, `IRecurrenceCalculator`, `ISchedulerSignal`, and `IClock`.
- Produces:

```csharp
public interface IReminderService
{
    Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct);
    Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct);
    Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct);
}

public interface IReminderActionService
{
    Task CompleteAsync(Guid occurrenceId, CancellationToken ct);
    Task IgnoreAsync(Guid occurrenceId, CancellationToken ct);
    Task<ReminderOccurrence> SnoozeAsync(Guid occurrenceId, TimeSpan delay, CancellationToken ct);
}
```

- [ ] **Step 1: Write the persistence-before-signal test**

```csharp
[Fact]
public async Task Create_signals_scheduler_only_after_atomic_save_succeeds()
{
    var events = new List<string>();
    var repository = new RecordingRepository(events);
    var signal = new RecordingSignal(events);
    var service = new ReminderService(repository, signal,
        new FakeClock("2026-07-29T09:00:00+08:00"));

    await service.CreateAsync(TestData.Draft("休息", "2026-07-29T09:20:00+08:00"),
        CancellationToken.None);

    Assert.Equal(["save", "refresh"], events);
}
```

Add a second test where repository save throws and assert that `refresh` is absent.

- [ ] **Step 2: Verify failure**

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Create_signals_scheduler
```

Expected: FAIL because services are absent.

- [ ] **Step 3: Implement creation and actions**

`CreateAsync` validates the draft, creates item and occurrence, awaits `SaveItemWithOccurrenceAsync`, then calls `Refresh`.

`SnoozeAsync` accepts only 5, 10, 30, or 60 minutes for important alerts and 10 minutes for normal notification actions. It marks the original occurrence `Snoozed`, creates a new occurrence with `SnoozeParentId`, saves both changes in one repository transaction, then refreshes.

`CompleteAsync` and `IgnoreAsync` load through `GetScheduledReminderAsync`, calculate at most one next recurrence, then call `ApplyActionAsync`. The repository transaction updates the current occurrence, inserts one `action_log` row, and inserts the optional next occurrence before commit.

`EditAsync` and `DeleteAsync` require an explicit `SeriesScope`. `OccurrenceOnly` changes or cancels only the selected occurrence. `ThisAndFuture` updates or removes the recurrence rule and all scheduled occurrences at or after the selected due time inside one transaction; past action history remains unchanged.

In `ReminderServiceTests.cs`, define private `RecordingRepository : FakeReminderRepository` and `RecordingSignal : ISchedulerSignal` test classes; both append their operation name to the shared list used by the ordering assertion.

- [ ] **Step 4: Test action idempotency and recurrence**

Run the same completion command twice and assert one action transition and one future occurrence. Test snooze parent linkage, invalid delay rejection, occurrence-only edit/delete, and this-and-future edit/delete.

```powershell
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter Services
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Moment.Core/Services tests/Moment.Core.Tests/Services
git commit -m "feat: create and act on reminders"
```

---

### Task 7: Native Notifications and Important Alert Delivery

**Files:**
- Create: `src/Moment.Windows/Moment.Windows.csproj`
- Create: `src/Moment.Windows/Notifications/NotificationArguments.cs`
- Create: `src/Moment.Windows/Notifications/AppNotificationSink.cs`
- Create: `src/Moment.Core/Domain/ReminderAlert.cs`
- Create: `src/Moment.Windows/Alerts/IImportantAlertPresenter.cs`
- Create: `src/Moment.Windows/Alerts/ImportantAlertController.cs`
- Modify: `tests/Moment.TestSupport/TestData.cs`
- Create: `tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj`
- Create: `tests/Moment.Windows.Tests/Notifications/NotificationArgumentsTests.cs`
- Create: `tests/Moment.Windows.Tests/Alerts/ImportantAlertControllerTests.cs`

**Interfaces:**
- Produces `AppNotificationSink : IReminderSink`.
- Produces `ReminderAlert` and `ImportantAlertAction` in `Moment.Core`, allowing the presenter contract and shared test data to avoid a dependency cycle.
- Produces:

```csharp
public interface IImportantAlertPresenter
{
    Task<ImportantAlertAction> ShowAsync(ReminderAlert alert, CancellationToken ct);
}
```

- [ ] **Step 1: Scaffold Windows integration projects**

```powershell
dotnet new classlib --name Moment.Windows --output src/Moment.Windows --framework net10.0
dotnet new xunit --name Moment.Windows.Tests --output tests/Moment.Windows.Tests --framework net10.0
dotnet add src/Moment.Windows/Moment.Windows.csproj package Microsoft.WindowsAppSDK
dotnet add src/Moment.Windows/Moment.Windows.csproj reference src/Moment.Core/Moment.Core.csproj
dotnet add tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj reference src/Moment.Windows/Moment.Windows.csproj
dotnet add tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj reference tests/Moment.TestSupport/Moment.TestSupport.csproj
dotnet sln Moment.slnx add src/Moment.Windows/Moment.Windows.csproj tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj
```

Change both target frameworks to `net10.0-windows10.0.22621.0`.

- [ ] **Step 2: Write failing notification argument tests**

```csharp
[Fact]
public void Arguments_round_trip_occurrence_and_action()
{
    var id = Guid.Parse("4b3eb3c9-970d-47d7-89e2-bab9778a406d");
    var text = NotificationArguments.Format(id, NotificationAction.Snooze10);
    var parsed = NotificationArguments.Parse(text);
    Assert.Equal(id, parsed.OccurrenceId);
    Assert.Equal(NotificationAction.Snooze10, parsed.Action);
}
```

- [ ] **Step 3: Implement normal notification payloads**

Build a Windows App SDK app notification containing title, due time, and three buttons whose arguments are:

```text
action=complete&occurrenceId={guid}
action=snooze10&occurrenceId={guid}
action=ignore&occurrenceId={guid}
```

Set notification `Tag` to the occurrence ID and `Group` to `moment-reminders`. Parse activation arguments strictly; reject missing, unknown, or non-GUID values without changing repository state.

`AppNotificationSink.DeliverAsync` routes important reminders to `ImportantAlertController` and normal reminders to `AppNotificationManager`. `DeliverMissedSummaryAsync` creates one notification containing the count and up to three titles; its body activation opens the missed section of the timeline. Notification button activation calls only `IReminderActionService`.

Expose `NotificationHealth` as `Available`, `PermissionDisabled`, or `RegistrationFailed`. The App settings banner observes this state and offers “发送测试通知” plus a button that opens Windows notification settings.

- [ ] **Step 4: Write failing important-alert queue test**

```csharp
[Fact]
public async Task Important_alerts_are_presented_one_at_a_time_in_due_order()
{
    var presenter = new RecordingPresenter();
    var controller = new ImportantAlertController(presenter);
    await Task.WhenAll(
        controller.EnqueueAsync(TestData.Alert("B", dueMinute: 2), default),
        controller.EnqueueAsync(TestData.Alert("A", dueMinute: 1), default));
    Assert.Equal(["A", "B"], presenter.Titles);
    Assert.Equal(1, presenter.MaximumConcurrency);
}
```

- [ ] **Step 5: Implement important-alert queue and fallback**

Use a `Channel<ReminderAlert>` with a single reader. Sort batches by due time and ID. `ImportantAlertController` maps presenter results to `IReminderActionService`. If custom audio fails, play embedded `default-alert.wav`; if the presenter fails, emit an in-app fault and keep the occurrence in `Fired` state for recovery.

Add `TestData.Alert(string title, int dueMinute)` when `ReminderAlert` is introduced.

The single reader waits a 25 ms coalescing window after the first queued item, drains all currently queued alerts, sorts that batch, and then presents them sequentially. Define `RecordingPresenter` as a private test fake that records titles and active-call count.

- [ ] **Step 6: Run tests**

```powershell
dotnet test tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/Moment.Windows tests/Moment.Windows.Tests Moment.slnx
git commit -m "feat: deliver native and important reminders"
```

---

### Task 8: Single Instance, Global Hotkey, Tray, and Resume Events

**Files:**
- Create: `src/Moment.Windows/Hotkeys/GlobalHotkeyService.cs`
- Create: `src/Moment.Windows/Hotkeys/HotkeyGestureParser.cs`
- Create: `src/Moment.Windows/Lifecycle/SingleInstanceCoordinator.cs`
- Create: `src/Moment.Windows/Lifecycle/SystemResumeMonitor.cs`
- Create: `src/Moment.Windows/Startup/StartupRegistrationService.cs`
- Create: `tests/Moment.Windows.Tests/Hotkeys/GlobalHotkeyServiceTests.cs`
- Create: `tests/Moment.Windows.Tests/Lifecycle/SingleInstanceCoordinatorTests.cs`

**Interfaces:**
- Produces `IGlobalHotkeyService`, `ISingleInstanceCoordinator`, `ISystemResumeMonitor`, and `IStartupRegistrationService`.
- Resume monitor emits `ResumeReason.Unlock`, `ResumeReason.PowerResume`, `ResumeReason.TimeChanged`, or `ResumeReason.TimeZoneChanged`.

- [ ] **Step 1: Write failing pure mapping tests**

Extract `HotkeyGestureParser` and test:

```csharp
[Theory]
[InlineData("Ctrl+Alt+Space", 0x0003u, 0x20u)]
[InlineData("Ctrl+Shift+R", 0x0006u, 0x52u)]
public void Maps_supported_gestures(string text, uint modifiers, uint key)
{
    Assert.Equal((modifiers, key), HotkeyGestureParser.Parse(text));
}
```

Reject gestures without a modifier and unknown key names.

- [ ] **Step 2: Implement `RegisterHotKey` wrapper**

Create a message-only window, call Win32 `RegisterHotKey`, translate `WM_HOTKEY` into an event, and always call `UnregisterHotKey` during disposal. Registration failure returns `HotkeyRegistrationResult.Conflict`; it must not terminate the application.

- [ ] **Step 3: Implement single-instance activation**

Use a named mutex `Local\Moment.ReminderApp` and named pipe `Moment.ReminderApp.Activation`. The primary instance listens for `show-main`, `show-quick-add`, and serialized notification arguments. A secondary instance sends one message, waits up to two seconds for acknowledgement, then exits.

Test the pipe protocol with unique names so tests can run in parallel.

- [ ] **Step 4: Implement lifecycle and startup adapters**

Listen for session unlock, `WM_POWERBROADCAST`, system time change, and time-zone change. Debounce events within 500 ms and call one recovery callback.

Startup registration writes or removes current-user registry value:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
Name: Moment
Value: "{full executable path}" --background
```

Default state is disabled. If a portable executable path changes, compare the registered path and return `StartupPathStatus.Stale`.

- [ ] **Step 5: Run Windows integration tests**

```powershell
dotnet test tests/Moment.Windows.Tests/Moment.Windows.Tests.csproj --filter "Hotkeys|Lifecycle"
```

Expected: PASS. Manually reserve the default hotkey with another process and verify the service reports conflict.

- [ ] **Step 6: Commit**

```powershell
git add src/Moment.Windows tests/Moment.Windows.Tests
git commit -m "feat: integrate Windows app lifecycle"
```

---

### Task 9: WPF Shell, Timeline, and Quick Add

**Files:**
- Create: `src/Moment.App/Moment.App.csproj`
- Create: `src/Moment.App/App.xaml`
- Create: `src/Moment.App/App.xaml.cs`
- Create: `src/Moment.App/CompositionRoot.cs`
- Create: `src/Moment.App/MainWindow.xaml`
- Create: `src/Moment.App/Timeline/TimelineView.xaml`
- Create: `src/Moment.App/Timeline/TimelineViewModel.cs`
- Create: `src/Moment.App/Timeline/TimelineItemViewModel.cs`
- Create: `src/Moment.Core/Services/ITimelineQuery.cs`
- Create: `src/Moment.Infrastructure/Data/SqliteTimelineQuery.cs`
- Create: `src/Moment.App/QuickAdd/QuickAddWindow.xaml`
- Create: `src/Moment.App/QuickAdd/QuickAddViewModel.cs`
- Create: `src/Moment.App/Shell/TrayIconController.cs`
- Create: `src/Moment.App/Styles/Colors.xaml`
- Create: `src/Moment.App/Styles/Controls.xaml`
- Create: `tests/Moment.App.Tests/Moment.App.Tests.csproj`
- Create: `tests/Moment.App.Tests/Timeline/TimelineViewModelTests.cs`
- Create: `tests/Moment.App.Tests/QuickAdd/QuickAddViewModelTests.cs`

**Interfaces:**
- Timeline consumes:

```csharp
public interface ITimelineQuery
{
    Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(
        DateOnly localDate, TimeZoneInfo zone, CancellationToken ct);
}

public sealed record TimelineRow(
    Guid OccurrenceId, string Title, DateTimeOffset DueAt,
    ReminderKind Kind, ReminderImportance Importance,
    OccurrenceState State, string? RecurrenceText);
```

- Quick Add consumes `IChineseTimeParser` and `IReminderService`.
- View models expose `ICommand` properties and testable state; code-behind only handles window placement and focus.
- `TestData.Row(...)` produces `TimelineRow`; `FakeTimelineQuery` is a private fake in `TimelineViewModelTests.cs`.

- [ ] **Step 1: Scaffold WPF and test projects**

```powershell
dotnet new wpf --name Moment.App --output src/Moment.App --framework net10.0
dotnet new xunit --name Moment.App.Tests --output tests/Moment.App.Tests --framework net10.0
dotnet add src/Moment.App/Moment.App.csproj reference src/Moment.Core/Moment.Core.csproj src/Moment.Infrastructure/Moment.Infrastructure.csproj src/Moment.Windows/Moment.Windows.csproj
dotnet add tests/Moment.App.Tests/Moment.App.Tests.csproj reference src/Moment.App/Moment.App.csproj
dotnet add tests/Moment.App.Tests/Moment.App.Tests.csproj reference tests/Moment.TestSupport/Moment.TestSupport.csproj
dotnet sln Moment.slnx add src/Moment.App/Moment.App.csproj tests/Moment.App.Tests/Moment.App.Tests.csproj
```

Set `TargetFramework` to `net10.0-windows10.0.22621.0`, `UseWPF=true`, `UseWindowsForms=true`, `OutputType=WinExe`, and `WindowsPackageType=None`.

- [ ] **Step 2: Write failing timeline ordering test**

```csharp
[Fact]
public async Task Timeline_orders_by_due_time_and_exposes_text_status()
{
    var query = new FakeTimelineQuery(
        TestData.Row("午休", "2026-07-29T12:00:00+08:00", OccurrenceState.Scheduled),
        TestData.Row("会议", "2026-07-29T10:30:00+08:00", OccurrenceState.Fired));
    var vm = new TimelineViewModel(query, new FakeClock("2026-07-29T09:00:00+08:00"));

    await vm.LoadAsync();

    Assert.Equal(["会议", "午休"], vm.Items.Select(x => x.Title));
    Assert.Equal("等待处理", vm.Items[0].StatusText);
}
```

- [ ] **Step 3: Implement timeline presentation**

Use an `ItemsControl` grouped into “已错过 / 接下来 / 已完成”. Each row has time, icon, title, recurrence text, importance text, and status text. Bind keyboard commands:

- `Enter`: edit selected item.
- `Ctrl+N`: open creation form.
- `Delete`: open deletion confirmation.
- `Ctrl+Shift+Space`: complete selected item.

Use `VirtualizingStackPanel`, no animation when system client-area animation is disabled, and minimum 4.5:1 text contrast.

Editing or deleting a recurring row always opens a scope dialog. The edit dialog returns `OccurrenceOnly` or `ThisAndFuture`; the delete dialog returns `OccurrenceOnly`, `ThisAndFuture`, or `Cancel`. Add view-model tests proving no repository command runs before a scope is selected.

- [ ] **Step 4: Write failing Quick Add ambiguity test**

```csharp
[Fact]
public async Task Enter_does_not_create_when_parser_returns_ambiguity()
{
    var service = new RecordingReminderService();
    var vm = new QuickAddViewModel(
        new StubParser(new ParseResult.Ambiguous("晚上提醒我看书",
            [new("今天 20:00", TestData.Draft("看书", "2026-07-29T20:00:00+08:00"))])),
        service);
    vm.Text = "晚上提醒我看书";

    await vm.SubmitAsync();

    Assert.True(vm.IsChoicePanelVisible);
    Assert.Empty(service.Created);
}
```

- [ ] **Step 5: Implement Quick Add window**

The window is centered on the current monitor, activates without a taskbar button, focuses its text box, and displays an absolute preview:

```text
2026年7月30日 09:00 · 单次 · 普通提醒
```

`Enter` creates only `ParseResult.Success`; `Tab` expands detailed fields; `Escape` hides the window without clearing input; choosing an ambiguity option converts it to a success preview before creation.

Define `StubParser` and `RecordingReminderService` as private test fakes in `QuickAddViewModelTests.cs`; the former returns its constructor-supplied `ParseResult`, and the latter appends every draft passed to `CreateAsync`.

- [ ] **Step 6: Compose shell and tray behavior**

`CompositionRoot` creates one repository, scheduler, notification sink, action service, parser, and view model graph. `MainWindow.Closing` cancels close and hides the window unless `_allowExit` is true. `TrayIconController` exposes “打开今天”, “快速创建”, preset countdowns, “设置”, and “退出”. Exit with scheduled occurrences displays a confirmation before stopping scheduler.

- [ ] **Step 7: Run view-model and build checks**

```powershell
dotnet test tests/Moment.App.Tests/Moment.App.Tests.csproj
dotnet build Moment.slnx -c Release
```

Expected: PASS and zero warnings.

- [ ] **Step 8: Commit**

```powershell
git add src/Moment.App tests/Moment.App.Tests Moment.slnx
git commit -m "feat: add timeline and quick reminder UI"
```

---

### Task 10: Important Alert Window, Settings, and Accessibility

**Files:**
- Create: `src/Moment.App/Alerts/ImportantAlertWindow.xaml`
- Create: `src/Moment.App/Alerts/ImportantAlertWindow.xaml.cs`
- Create: `src/Moment.App/Settings/SettingsView.xaml`
- Create: `src/Moment.App/Settings/SettingsViewModel.cs`
- Create: `src/Moment.Core/Abstractions/ISettingsStore.cs`
- Create: `src/Moment.Infrastructure/Data/SqliteSettingsStore.cs`
- Create: `src/Moment.App/Shell/WindowPlacementService.cs`
- Create: `src/Moment.App/Assets/default-alert.wav`
- Create: `tests/Moment.App.Tests/Settings/SettingsViewModelTests.cs`

**Interfaces:**
- Important alert returns one of `Complete`, `Snooze5`, `Snooze10`, `Snooze30`, `Snooze60`, or `Ignore`.
- Settings persists through:

```csharp
public sealed record AppSettings(
    string Hotkey, bool StartWithWindows, int AlertVolume,
    string? CustomAlertSoundPath);

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}
```

- [ ] **Step 1: Write failing settings tests**

```csharp
[Fact]
public async Task Conflicting_hotkey_is_not_saved_and_exposes_help_text()
{
    var hotkeys = new StubHotkeys(HotkeyRegistrationResult.Conflict);
    var store = new RecordingSettingsStore();
    var vm = new SettingsViewModel(hotkeys, store);

    await vm.SaveHotkeyAsync("Ctrl+Alt+Space");

    Assert.Equal("该快捷键已被其他程序占用", vm.HotkeyError);
    Assert.Null(store.LastSavedHotkey);
}
```

- [ ] **Step 2: Implement the important alert interaction**

Make the window topmost, accessible by keyboard, and visible on the current working area. Start looping embedded audio after it is shown. Closing through the title bar returns `Snooze10`. Every button stops and disposes audio before completing the presenter task. Add visible text labels beside every icon.

- [ ] **Step 3: Implement settings**

Settings include:

- Hotkey editor with conflict test.
- Startup toggle, default off.
- Normal and important default levels.
- Alert sound picker, play/stop preview, and volume from 0–100.
- “发送测试通知”.
- Data folder and backup actions.
- Current version and optional release-page link.

Persist settings through the repository `settings` table; validate file paths before save. A missing custom WAV path resets to the embedded sound and shows one non-modal warning.

Define `StubHotkeys` and `RecordingSettingsStore` as private test fakes in `SettingsViewModelTests.cs`.

- [ ] **Step 4: Add accessibility verification**

Set `AutomationProperties.Name` for all icon-only controls. Confirm tab order for timeline, Quick Add, important alert, and settings. Test at 100%, 125%, 150%, and 200% scaling and in Windows high-contrast mode. Record results in `docs/accessibility-checklist.md`.

- [ ] **Step 5: Run tests and manual alert check**

```powershell
dotnet test tests/Moment.App.Tests/Moment.App.Tests.csproj --filter Settings
dotnet run --project src/Moment.App/Moment.App.csproj
```

Expected: automated tests PASS; test important alert remains visible, loops audio, and title-bar close snoozes ten minutes.

- [ ] **Step 6: Commit**

```powershell
git add src/Moment.App tests/Moment.App.Tests docs/accessibility-checklist.md
git commit -m "feat: add important alerts and settings"
```

---

### Task 11: Backup, Restore, and Update Link

**Files:**
- Create: `src/Moment.Infrastructure/Backup/BackupManifest.cs`
- Create: `src/Moment.Infrastructure/Backup/BackupService.cs`
- Create: `tests/Moment.Infrastructure.Tests/Backup/BackupServiceTests.cs`
- Create: `tests/Moment.Infrastructure.Tests/Backup/TestBackupFactory.cs`
- Create: `src/Moment.Infrastructure/Backup/DatabaseRecoveryService.cs`
- Create: `tests/Moment.Infrastructure.Tests/Backup/DatabaseRecoveryServiceTests.cs`
- Modify: `src/Moment.App/Settings/SettingsViewModel.cs`

**Interfaces:**
- Produces:

```csharp
public interface IBackupService
{
    Task<string> CreateDailyBackupAsync(CancellationToken ct);
    Task ExportAsync(string destinationPath, CancellationToken ct);
    Task RestoreAsync(string backupPath, CancellationToken ct);
}
```

- `.moment-backup` is a ZIP containing `moment.db` and UTF-8 `manifest.json` with `formatVersion`, `schemaVersion`, `createdAt`, and SHA-256.

- [ ] **Step 1: Write failing round-trip and tamper tests**

```csharp
[Fact]
public async Task Export_and_restore_round_trips_and_rejects_tampering()
{
    using var temp = new TempDirectory();
    var service = TestBackupFactory.Create(temp.Path);
    var path = Path.Combine(temp.Path, "data.moment-backup");
    await service.ExportAsync(path, default);
    await TestBackupFactory.ChangeDatabaseAsync(temp.Path);
    await service.RestoreAsync(path, default);
    Assert.Equal("original", await TestBackupFactory.ReadMarkerAsync(temp.Path));

    await TestBackupFactory.TamperWithDatabaseEntryAsync(path);
    await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(path, default));
}
```

- [ ] **Step 2: Implement consistent backup and seven-file retention**

Use SQLite `VACUUM INTO` or online backup API to create a consistent snapshot. Hash the snapshot, write manifest and database into ZIP, and atomically rename the completed package. Daily backup names use UTC timestamps. After success, sort automatic backups newest-first and delete entries after the seventh.

`TestBackupFactory` creates a real temporary SQLite database through `DatabaseMigrator`, writes a marker setting, mutates that setting, reads it back, and rewrites the `moment.db` ZIP entry without updating the manifest to simulate tampering.

- [ ] **Step 3: Implement corruption-safe database opening**

`DatabaseRecoveryService.OpenWithRecoveryAsync` first runs `PRAGMA quick_check`. On corruption it copies the original database to `moment.db.corrupt-{UTC timestamp}` without modifying that copy, then examines automatic backups newest-first. It restores the first package with a valid manifest, SHA-256, compatible schema, and `integrity_check=ok`. If none is valid, it returns `DatabaseRecoveryResult.RequiresUserDecision` and does not create a replacement database.

Test a corrupt primary database with one corrupt and one valid backup. Assert that the valid backup opens and the byte-for-byte corrupt copy remains.

- [ ] **Step 4: Implement guarded restore**

Verify ZIP entry names, size limit of 1 GiB, manifest version, schema compatibility, and SHA-256 before stopping scheduler. Create a safety backup of current data, replace database atomically, migrate, run `PRAGMA integrity_check`, then restart scheduler and recovery scan. On any failure, restore the safety copy.

- [ ] **Step 5: Integrate daily backup**

After the database passes integrity checks and migrations, call `CreateDailyBackupAsync`. Store the last successful local backup date in `settings`; skip a second automatic backup on the same local date. Backup failure must not prevent reminders from starting, but it creates a persistent in-app warning.

- [ ] **Step 6: Implement manual update link**

Read `ReleasePageUrl` from assembly metadata. Show “检查更新” only when it is an absolute HTTPS URL. Clicking it opens the system browser; no download or installation occurs inside the app.

Add this to `Moment.App.csproj`, with an empty default that hides the command:

```xml
<PropertyGroup>
  <ReleasePageUrl Condition="'$(ReleasePageUrl)' == ''"></ReleasePageUrl>
</PropertyGroup>
<ItemGroup>
  <AssemblyMetadata Include="ReleasePageUrl" Value="$(ReleasePageUrl)" />
</ItemGroup>
```

- [ ] **Step 7: Run tests**

```powershell
dotnet test tests/Moment.Infrastructure.Tests/Moment.Infrastructure.Tests.cs --filter Backup
```

Expected: PASS, including corrupt ZIP, wrong checksum, unsupported schema, and retention.

- [ ] **Step 8: Commit**

```powershell
git add src/Moment.Infrastructure/Backup tests/Moment.Infrastructure.Tests/Backup src/Moment.App/Settings
git commit -m "feat: back up and restore reminder data"
```

---

### Task 12: Installer, Portable Build, End-to-End Verification, and Documentation

**Files:**
- Create: `installer/Moment.iss`
- Create: `scripts/build-release.ps1`
- Create: `scripts/smoke-test.ps1`
- Create: `src/Moment.App/Diagnostics/SmokeTestRunner.cs`
- Create: `tests/Moment.App.Tests/Diagnostics/SmokeTestRunnerTests.cs`
- Create: `docs/user-guide.md`
- Create: `docs/release-checklist.md`
- Modify: `src/Moment.App/Moment.App.csproj`

**Interfaces:**
- Produces `artifacts/Moment-Setup-x64.exe`, `artifacts/Moment-Portable-x64.zip`, and SHA-256 files.
- Installer is per-user and requires no elevation.

- [ ] **Step 1: Install and verify Inno Setup 6**

Install Inno Setup 6, then run:

```powershell
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' /?
```

Expected: Inno Setup compiler help. If installed under `C:\Program Files`, set the script’s explicit compiler path to that location.

- [ ] **Step 2: Create deterministic release publishing**

`build-release.ps1` must:

```powershell
$ErrorActionPreference = 'Stop'
dotnet test Moment.slnx -c Release
dotnet publish src/Moment.App/Moment.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
Copy-Item artifacts/publish artifacts/portable -Recurse
New-Item artifacts/portable/portable.flag -ItemType File
Compress-Archive artifacts/portable/* artifacts/Moment-Portable-x64.zip
& $InnoCompiler 'installer/Moment.iss'
Get-FileHash artifacts/Moment-Portable-x64.zip -Algorithm SHA256
Get-FileHash artifacts/Moment-Setup-x64.exe -Algorithm SHA256
```

The actual script resolves repository-root absolute paths, clears only `artifacts/publish` and `artifacts/portable` after validating both are descendants of `artifacts`, and writes hashes to adjacent `.sha256` files.

- [ ] **Step 3: Define per-user installer behavior**

`Moment.iss` uses:

```ini
[Setup]
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Moment
UninstallDisplayName=时刻
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
```

Install application files only. Do not delete `%LOCALAPPDATA%\Moment\data` during uninstall or upgrade. Create Start Menu and optional desktop shortcuts. The application itself owns the opt-in startup registry value.

- [ ] **Step 4: Add an isolated self-test entry point**

When invoked as `Moment.App.exe --self-test <absolute-output-directory>`, `SmokeTestRunner` creates an isolated database under that output directory, uses a fast fake clock, and drives the real repository, parser, recurrence service, action service, and scheduler. It writes one JSON Lines result file and exits 0 only when these events occur exactly once:

```text
normal-delivery
important-delivery
completed
snoozed
restart-recovered
single-instance-protocol
```

The self-test never reads or writes the normal installed or portable data directory and never registers startup or a global hotkey. Reject relative output paths.

- [ ] **Step 5: Write smoke-test script**

The script extracts an isolated portable copy, launches it with `--self-test <absolute temp directory>`, and verifies structured local test log entries for:

- normal notification delivery,
- important delivery request,
- completion,
- snooze,
- application restart recovery,
- duplicate-instance redirection.

Exit nonzero when any expected event is absent, duplicated, or the process exceeds 30 seconds.

- [ ] **Step 6: Perform the full automated gate**

```powershell
dotnet test Moment.slnx -c Release
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

Expected: all tests PASS; both artifacts and their SHA-256 files exist; smoke test exits 0.

- [ ] **Step 7: Perform the manual Windows 11 matrix**

Record each result in `docs/release-checklist.md`:

- Main window closed while tray scheduler fires.
- Lock screen delivery.
- Sleep longer than five minutes then resume.
- Manual date, time, and time-zone changes.
- Three reminders due simultaneously.
- Focus mode behavior for normal notification.
- Important alert queue, looped audio, and all snooze values.
- Notification permission disabled.
- Default hotkey occupied.
- Missing custom audio.
- Installer install/upgrade/uninstall with data retained.
- Portable folder moved and startup path reported stale.
- Scaling at 100%, 125%, 150%, and 200%.
- Windows high-contrast mode and keyboard-only operation.
- 24-hour residency without increasing scheduler count, duplicate delivery, or sustained memory growth.

- [ ] **Step 8: Write the user guide**

Document installation, portable mode, first reminder, command examples, recurring items, important reminders, notification permission, startup, backups, updates, exit semantics, sleep limitations, and SmartScreen warning for unsigned builds. Include screenshots captured from the release build.

- [ ] **Step 9: Commit**

```powershell
git add installer scripts docs src/Moment.App/Moment.App.csproj
git commit -m "build: package and verify Windows release"
```

- [ ] **Step 10: Final verification**

```powershell
git status --short
git log --oneline -12
```

Expected: clean worktree and one focused commit for each completed task.
