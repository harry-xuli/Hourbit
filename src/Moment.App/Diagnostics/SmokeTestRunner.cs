using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Moment.App.Settings;
using Moment.App.Timeline;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Recurrence;
using Moment.Core.Scheduling;
using Moment.Core.Services;
using Moment.Infrastructure.Data;
using Moment.Windows.Lifecycle;

namespace Moment.App.Diagnostics;

internal static class SmokeTestRunner
{
    private static readonly string[] ExpectedEvents =
    [
        "normal-delivery",
        "important-delivery",
        "completed",
        "snoozed",
        "restart-recovered",
        "missed-recovery",
        "single-instance-protocol",
        "schema-v1-upgrade",
        "schema-v2-upgrade",
        "todos-created",
        "todo-scheduler-exclusion",
        "release-metadata"
    ];

    public static async Task<int> RunAsync(
        string outputDirectory,
        CancellationToken ct)
    {
        string outputPath;
        try
        {
            outputPath = ValidateOutputDirectory(outputDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
            UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Invalid self-test output directory: {exception.Message}");
            return 2;
        }

        var databasePath = Path.Combine(outputPath, "data", "moment-self-test.db");
        var resultPath = Path.Combine(outputPath, "self-test.jsonl");
        try
        {
            if (File.Exists(databasePath) || File.Exists(resultPath))
                throw new IOException("Self-test output files already exist.");

            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            await using (var events = await EventLog.CreateAsync(resultPath, ct))
            {
                await ExerciseLegacyUpgradesAndTodosAsync(databasePath, events, ct);
                await ExerciseReminderPipelineAsync(databasePath, events, ct);
                await ExerciseMissedRecoveryAsync(databasePath, events, ct);
                await ExerciseSingleInstanceProtocolAsync(events, ct);
                await ExerciseReleaseMetadataAsync(events, ct);
            }

            await ValidateResultAsync(resultPath, ct);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Self-test failed: {exception}");
            return 1;
        }
        finally
        {
            // In-process self-tests must release provider-owned native handles
            // before their isolated output directory can be removed on Windows.
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task ExerciseLegacyUpgradesAndTodosAsync(
        string versionTwoDatabasePath,
        EventLog events,
        CancellationToken ct)
    {
        var versionOneDatabasePath = Path.Combine(
            Path.GetDirectoryName(versionTwoDatabasePath)!,
            "moment-self-test-v1.db");
        var versionOneReminder = await CreateLegacyDatabaseAsync(
            versionOneDatabasePath, version: 1, ct);
        await AssertLegacyReminderSurvivesUpgradeAsync(
            versionOneDatabasePath,
            versionOneReminder,
            "schema-v1-upgrade",
            events,
            ct);

        var versionTwoReminder = await CreateLegacyDatabaseAsync(
            versionTwoDatabasePath, version: 2, ct);
        await AssertLegacyReminderSurvivesUpgradeAsync(
            versionTwoDatabasePath,
            versionTwoReminder,
            "schema-v2-upgrade",
            events,
            ct);

        var todoRepository = await SqliteTodoRepository.OpenAsync(
            versionTwoDatabasePath, ct);
        var createdAt = new DateTimeOffset(
            2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        var dated = new TodoItem(
            Guid.NewGuid(),
            "升级后有日期待办",
            createdAt,
            new DateOnly(2026, 1, 7),
            ReminderImportance.Important,
            IsCompleted: false,
            CompletedAt: null);
        var undated = new TodoItem(
            Guid.NewGuid(),
            "升级后无日期待办",
            createdAt,
            DueDate: null,
            ReminderImportance.Normal,
            IsCompleted: false,
            CompletedAt: null);
        await todoRepository.SaveAsync(dated, ct);
        await todoRepository.SaveAsync(undated, ct);

        var persistedTodos = await todoRepository.GetAllAsync(ct);
        if (persistedTodos.Count != 2 ||
            persistedTodos.SingleOrDefault(todo => todo.Id == dated.Id) != dated ||
            persistedTodos.SingleOrDefault(todo => todo.Id == undated.Id) != undated)
        {
            throw new InvalidOperationException(
                "The upgraded database did not persist dated and undated todos.");
        }
        await events.RecordAsync("todos-created", ct);

        var reminderRepository = await SqliteReminderRepository.OpenAsync(
            versionTwoDatabasePath, ct);
        var scheduled = await reminderRepository.GetScheduledAsync(ct);
        var due = await reminderRepository.GetDueAsync(
            versionTwoReminder.DueAt, ct);
        if (scheduled.Count != 1 ||
            scheduled[0].Occurrence.Id != versionTwoReminder.OccurrenceId ||
            due.Count != 1 ||
            due[0].Occurrence.Id != versionTwoReminder.OccurrenceId)
        {
            throw new InvalidOperationException(
                "Todo records entered reminder scheduler queries.");
        }
        await events.RecordAsync("todo-scheduler-exclusion", ct);
    }

    private static async Task AssertLegacyReminderSurvivesUpgradeAsync(
        string databasePath,
        LegacyReminderFixture fixture,
        string eventName,
        EventLog events,
        CancellationToken ct)
    {
        _ = await SqliteTodoRepository.OpenAsync(databasePath, ct);
        var reminders = await SqliteReminderRepository.OpenAsync(databasePath, ct);
        var retained = await reminders.GetScheduledReminderAsync(
            fixture.OccurrenceId, ct);
        if (retained?.Item.Id != fixture.ItemId ||
            retained.Item.Title != "升级保留提醒" ||
            retained.Occurrence.DueAt != fixture.DueAt)
        {
            throw new InvalidOperationException(
                "A legacy reminder was not retained during the schema v3 upgrade.");
        }

        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            databasePath, ct);
        await using var marker = connection.CreateCommand();
        marker.CommandText =
            "SELECT COUNT(*) FROM schema_info WHERE version = 3;";
        if (Convert.ToInt32(
                await marker.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException(
                "The legacy database does not have exactly one schema v3 marker.");
        }

        await events.RecordAsync(eventName, ct);
    }

    private static async Task<LegacyReminderFixture> CreateLegacyDatabaseAsync(
        string databasePath,
        int version,
        CancellationToken ct)
    {
        if (version is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(version));

        var itemId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(
            2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        var dueAt = new DateTimeOffset(
            2026, 1, 6, 9, 0, 0, TimeSpan.Zero);
        var utcColumn = version == 1
            ? string.Empty
            : ", due_at_utc TEXT NOT NULL";
        var utcInsertColumn = version == 1 ? string.Empty : ", due_at_utc";
        var utcInsertValue = version == 1 ? string.Empty : ", $dueAtUtc";
        var uniqueColumn = version == 1 ? "due_at" : "due_at_utc";

        await using var connection = await DatabaseMigrator.OpenConnectionAsync(
            databasePath, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE schema_info (version INTEGER NOT NULL);
            CREATE TABLE items (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                kind INTEGER NOT NULL,
                importance INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE occurrences (
                id TEXT PRIMARY KEY,
                item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                due_at TEXT NOT NULL{utcColumn},
                state INTEGER NOT NULL,
                handled_at TEXT NULL,
                snooze_parent_id TEXT NULL,
                UNIQUE(item_id, {uniqueColumn})
            );
            CREATE TABLE recurrence_rules (
                item_id TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL,
                days_of_week TEXT NOT NULL,
                time TEXT NOT NULL
            );
            CREATE TABLE action_log (
                id TEXT PRIMARY KEY,
                occurrence_id TEXT NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
                state INTEGER NOT NULL,
                handled_at TEXT NOT NULL
            );
            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE INDEX ix_occurrences_item_id ON occurrences(item_id);
            INSERT INTO schema_info(version) VALUES (1);
            {(version == 2 ? "INSERT INTO schema_info(version) VALUES (2);" : string.Empty)}
            INSERT INTO items(id, title, kind, importance, created_at)
                VALUES ($itemId, '升级保留提醒', $kind, $importance, $createdAt);
            INSERT INTO occurrences(
                id, item_id, due_at{utcInsertColumn}, state,
                handled_at, snooze_parent_id)
                VALUES (
                    $occurrenceId, $itemId, $dueAt{utcInsertValue},
                    $state, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
        command.Parameters.AddWithValue(
            "$occurrenceId", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)ReminderKind.Plan);
        command.Parameters.AddWithValue(
            "$importance", (int)ReminderImportance.Normal);
        command.Parameters.AddWithValue(
            "$state", (int)OccurrenceState.Scheduled);
        command.Parameters.AddWithValue(
            "$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$dueAt", dueAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$dueAtUtc",
            dueAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
        return new LegacyReminderFixture(itemId, occurrenceId, dueAt);
    }

    private static Task ExerciseReleaseMetadataAsync(
        EventLog events,
        CancellationToken ct)
    {
        var metadata = ProductMetadata.FromAssembly(Assembly.GetExecutingAssembly());
        return events.RecordReleaseMetadataAsync(metadata, ct);
    }

    private static async Task ExerciseMissedRecoveryAsync(
        string databasePath,
        EventLog events,
        CancellationToken ct)
    {
        var clock = new ControllableClock(
            new DateTimeOffset(2026, 1, 5, 18, 59, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.Utc;
        var repository = await SqliteReminderRepository.OpenAsync(databasePath, ct);
        var reminders = new ReminderService(repository, new SchedulerSignalProxy(), clock);
        var occurrence = await reminders.CreateAsync(
            ParseSuccess(
                new ChineseTimeParser(),
                "19点 提醒我 恢复验证",
                clock.Now,
                zone),
            ct);

        clock.AdvanceBy(TimeSpan.FromMinutes(65));
        var sink = new MissedRecoverySink(occurrence.Id);
        var recovery = new ReminderRecoveryService(repository, sink, sink);

        var first = await recovery.RecoverAsync(clock.Now, ct);
        var second = await recovery.RecoverAsync(clock.Now, ct);
        var persisted = await repository.GetScheduledReminderAsync(occurrence.Id, ct);
        var snapshot = await new SqliteTimelineQuery(databasePath).GetTimelineAsync(
            DateOnly.FromDateTime(clock.Now.UtcDateTime), zone, ct);
        var visible = new TimelineItemViewModel(
            snapshot.Reminders.Single(row => row.OccurrenceId == occurrence.Id),
            clock.Now);

        if (occurrence.DueAt !=
                new DateTimeOffset(2026, 1, 5, 19, 0, 0, TimeSpan.Zero) ||
            first != new ReminderRecoveryResult(Fired: 0, Missed: 1, Failed: 0) ||
            second != new ReminderRecoveryResult(Fired: 0, Missed: 0, Failed: 0) ||
            persisted?.Occurrence.State != OccurrenceState.Missed ||
            visible.StatusText != "已错过" ||
            visible.GroupName != "已错过" ||
            sink.SummaryCount != 1)
        {
            throw new InvalidOperationException(
                "The 19:00 reminder was not recovered once as a visible missed reminder at 20:04.");
        }

        await events.RecordAsync("missed-recovery", ct);
    }

    private static string ValidateOutputDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("The path must be absolute.", nameof(outputDirectory));

        var fullPath = Path.GetFullPath(outputDirectory);
        if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A filesystem root cannot be used.", nameof(outputDirectory));

        RejectProtectedDataPath(
            fullPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Moment"));
        RejectProtectedDataPath(
            fullPath,
            Path.Combine(AppContext.BaseDirectory, "Data"));

        var existing = new DirectoryInfo(fullPath);
        while (!existing.Exists)
        {
            existing = existing.Parent
                ?? throw new IOException("No existing parent directory was found.");
        }

        for (DirectoryInfo? current = existing; current is not null; current = current.Parent)
        {
            current.Refresh();
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point paths are not allowed: {current.FullName}");
        }

        return fullPath;
    }

    private static void RejectProtectedDataPath(string candidate, string protectedRoot)
    {
        var root = Path.GetFullPath(protectedRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalized = candidate.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The self-test output cannot be inside an application data directory.",
                nameof(candidate));
    }

    private static async Task ExerciseReminderPipelineAsync(
        string databasePath,
        EventLog events,
        CancellationToken ct)
    {
        var clock = new ControllableClock(
            new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.Utc;
        var parser = new ChineseTimeParser();
        var repository = await SqliteReminderRepository.OpenAsync(databasePath, ct);
        var signal = new SchedulerSignalProxy();
        var recurrence = new RecurrenceCalculator();
        var actions = new ReminderActionService(
            repository, recurrence, signal, clock, zone);
        var reminders = new ReminderService(repository, signal, clock);
        var firstSink = new DeliverySink(events, expectedDeliveries: 2);

        ReminderOccurrence normal;
        ReminderOccurrence important;
        ReminderOccurrence snoozed;
        using (var scheduler = new ReminderScheduler(repository, firstSink, clock))
        {
            signal.Target = scheduler;
            await scheduler.StartAsync(ct);

            var recurringDraft = ParseSuccess(
                parser,
                "每天 8点1分 提醒我 普通交付",
                clock.Now,
                zone);
            normal = await reminders.CreateAsync(recurringDraft, ct);

            var importantDraft = ParseSuccess(
                parser,
                "1分钟后 提醒我 重要交付",
                clock.Now,
                zone) with
            {
                Importance = ReminderImportance.Important
            };
            important = await reminders.CreateAsync(importantDraft, ct);

            clock.AdvanceBy(TimeSpan.FromMinutes(1));
            await firstSink.WaitAsync(ct);

            await actions.CompleteAsync(normal.Id, ct);
            var completed = await repository.GetScheduledReminderAsync(normal.Id, ct);
            var scheduledAfterCompletion = await repository.GetScheduledAsync(ct);
            if (completed?.Occurrence.State != OccurrenceState.Completed ||
                !scheduledAfterCompletion.Any(reminder =>
                    reminder.Item.Id == normal.ItemId &&
                    reminder.Occurrence.Id != normal.Id))
            {
                throw new InvalidOperationException(
                    "The recurring completion was not persisted with its next occurrence.");
            }
            await events.RecordAsync("completed", ct);

            snoozed = await actions.SnoozeAsync(
                important.Id,
                TimeSpan.FromMinutes(5),
                ct);
            var snoozedParent = await repository.GetScheduledReminderAsync(important.Id, ct);
            if (snoozedParent?.Occurrence.State != OccurrenceState.Snoozed ||
                snoozed.SnoozeParentId != important.Id)
            {
                throw new InvalidOperationException(
                    "The snooze action was not persisted.");
            }
            await events.RecordAsync("snoozed", ct);

            await scheduler.StopAsync(ct);
            signal.Target = null;
        }

        var reopenedRepository =
            await SqliteReminderRepository.OpenAsync(databasePath, ct);
        var restartSink = new DeliverySink(
            events,
            expectedDeliveries: 1,
            restartOccurrenceId: snoozed.Id);
        using (var restartedScheduler =
               new ReminderScheduler(reopenedRepository, restartSink, clock))
        {
            await restartedScheduler.StartAsync(ct);
            clock.AdvanceBy(TimeSpan.FromMinutes(5));
            await restartSink.WaitAsync(ct);
            await restartedScheduler.StopAsync(ct);
        }
    }

    private static ReminderDraft ParseSuccess(
        IChineseTimeParser parser,
        string text,
        DateTimeOffset now,
        TimeZoneInfo zone) =>
        parser.Parse(text, now, zone, CultureInfo.GetCultureInfo("zh-CN")) is
            ParseResult.Success { Draft: ReminderDraft draft }
            ? draft
            : throw new InvalidOperationException(
                $"The production parser rejected the self-test input: {text}");

    private static async Task ExerciseSingleInstanceProtocolAsync(
        EventLog events,
        CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var primary = new SingleInstanceCoordinator(
            $@"Local\Moment.SelfTest.{suffix}",
            $"Moment.SelfTest.{suffix}",
            TimeSpan.FromSeconds(2));
        var received = new TaskCompletionSource<InstanceActivation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationReceived += activation =>
        {
            received.TrySetResult(activation);
            return Task.CompletedTask;
        };
        if (await primary.StartAsync(InstanceActivation.ShowMain, ct) !=
            SingleInstanceResult.Primary)
            throw new InvalidOperationException("The self-test primary instance was not created.");

        await using var secondary = new SingleInstanceCoordinator(
            $@"Local\Moment.SelfTest.{suffix}",
            $"Moment.SelfTest.{suffix}",
            TimeSpan.FromSeconds(2));
        if (await secondary.StartAsync(InstanceActivation.ShowQuickAdd, ct) !=
            SingleInstanceResult.SecondaryAcknowledged)
            throw new InvalidOperationException("The self-test secondary was not acknowledged.");

        var activation = await received.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        if (activation != InstanceActivation.ShowQuickAdd)
            throw new InvalidOperationException("The redirected activation was not received.");
        await events.RecordAsync("single-instance-protocol", ct);
    }

    private static async Task ValidateResultAsync(
        string resultPath,
        CancellationToken ct)
    {
        var counts = ExpectedEvents.ToDictionary(
            eventName => eventName,
            _ => 0,
            StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(resultPath, ct))
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("event", out var eventProperty) ||
                eventProperty.ValueKind != JsonValueKind.String ||
                eventProperty.GetString() is not { } eventName ||
                !counts.TryGetValue(eventName, out var count))
            {
                throw new InvalidDataException("The self-test log contains an unknown event.");
            }
            counts[eventName] = count + 1;
        }

        if (counts.Any(pair => pair.Value != 1))
            throw new InvalidDataException(
                "The self-test log does not contain every event exactly once.");
    }

    private sealed class SchedulerSignalProxy : ISchedulerSignal
    {
        public ISchedulerSignal? Target { get; set; }
        public void Refresh() => Target?.Refresh();
    }

    private sealed class DeliverySink(
        EventLog events,
        int expectedDeliveries,
        Guid? restartOccurrenceId = null) : IReminderSink
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deliveries;

        public async Task DeliverAsync(
            ScheduledReminder reminder,
            CancellationToken ct)
        {
            string eventName;
            if (restartOccurrenceId is not null)
            {
                if (reminder.Occurrence.Id != restartOccurrenceId)
                    throw new InvalidOperationException(
                        "The restarted scheduler delivered an unexpected occurrence.");
                eventName = "restart-recovered";
            }
            else
            {
                eventName = reminder.Item.Importance switch
                {
                    ReminderImportance.Normal => "normal-delivery",
                    ReminderImportance.Important => "important-delivery",
                    _ => throw new InvalidOperationException(
                        "The scheduler delivered an unknown importance.")
                };
            }

            await events.RecordAsync(eventName, ct);
            if (Interlocked.Increment(ref _deliveries) == expectedDeliveries)
                _completed.TrySetResult();
        }

        public Task DeliverMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders,
            CancellationToken ct) =>
            throw new InvalidOperationException(
                "The self-test scheduler unexpectedly requested a missed summary.");

        public Task WaitAsync(CancellationToken ct) =>
            _completed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private sealed class MissedRecoverySink(Guid expectedOccurrenceId) :
        IReminderSink,
        IReminderRecoverySummarySink
    {
        public int SummaryCount { get; private set; }

        public Task DeliverAsync(ScheduledReminder reminder, CancellationToken ct) =>
            throw new InvalidOperationException(
                "The expired normal reminder was delivered instead of marked missed.");

        public Task DeliverMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders,
            CancellationToken ct) => SendMissedSummaryAsync(reminders, ct);

        public Task SendMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reminders.Count != 1 ||
                reminders[0].Occurrence.Id != expectedOccurrenceId)
            {
                throw new InvalidOperationException(
                    "The missed reminder summary contained an unexpected occurrence.");
            }

            SummaryCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ControllableClock(DateTimeOffset now) : IClock
    {
        private readonly object _gate = new();
        private readonly List<PendingDelay> _pending = [];
        private DateTimeOffset _now = now;

        public DateTimeOffset Now
        {
            get
            {
                lock (_gate)
                    return _now;
            }
        }

        public Task DelayUntilAsync(DateTimeOffset dueAt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (dueAt <= _now)
                    return Task.CompletedTask;

                var pending = new PendingDelay(dueAt, ct);
                _pending.Add(pending);
                pending.Registration = ct.Register(() => Cancel(pending));
                return pending.Source.Task;
            }
        }

        public void AdvanceBy(TimeSpan duration)
        {
            PendingDelay[] completed;
            lock (_gate)
            {
                _now = _now.Add(duration);
                completed = _pending
                    .Where(pending => pending.DueAt <= _now)
                    .ToArray();
                _pending.RemoveAll(pending => pending.DueAt <= _now);
            }

            foreach (var pending in completed)
            {
                pending.Registration.Dispose();
                pending.Source.TrySetResult();
            }
        }

        private void Cancel(PendingDelay pending)
        {
            lock (_gate)
                _pending.Remove(pending);
            pending.Source.TrySetCanceled(pending.CancellationToken);
        }

        private sealed class PendingDelay(
            DateTimeOffset dueAt,
            CancellationToken cancellationToken)
        {
            public DateTimeOffset DueAt { get; } = dueAt;
            public CancellationToken CancellationToken { get; } = cancellationToken;
            public TaskCompletionSource Source { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration Registration { get; set; }
        }
    }

    private sealed class EventLog : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);

        private EventLog(StreamWriter writer) => _writer = writer;

        public static async Task<EventLog> CreateAsync(
            string path,
            CancellationToken ct)
        {
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var writer = new StreamWriter(stream);
            await writer.FlushAsync(ct);
            return new EventLog(writer);
        }

        public async Task RecordAsync(string eventName, CancellationToken ct)
        {
            var line = JsonSerializer.Serialize(
                new SmokeEvent(eventName, DateTimeOffset.UtcNow));
            await WriteAsync(eventName, line, ct);
        }

        public async Task RecordReleaseMetadataAsync(
            ProductMetadata metadata,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            const string eventName = "release-metadata";
            var line = JsonSerializer.Serialize(
                new ReleaseMetadataSmokeEvent(
                    eventName,
                    DateTimeOffset.UtcNow,
                    metadata.ProductName,
                    metadata.ExecutableName,
                    metadata.Version,
                    metadata.ReleaseDate,
                    metadata.SettingsFooterText));
            await WriteAsync(eventName, line, ct);
        }

        private async Task WriteAsync(
            string eventName,
            string line,
            CancellationToken ct)
        {
            if (!ExpectedEvents.Contains(eventName, StringComparer.Ordinal))
                throw new InvalidOperationException($"Unknown self-test event: {eventName}");

            await _gate.WaitAsync(ct);
            try
            {
                if (!_recorded.Add(eventName))
                    throw new InvalidOperationException(
                        $"Duplicate self-test event: {eventName}");

                await _writer.WriteLineAsync(line.AsMemory(), ct);
                await _writer.FlushAsync(ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            _gate.Dispose();
        }
    }

    private sealed record SmokeEvent(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);

    private sealed record ReleaseMetadataSmokeEvent(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
        [property: JsonPropertyName("productName")] string ProductName,
        [property: JsonPropertyName("executableName")] string ExecutableName,
        [property: JsonPropertyName("semanticVersion")] string SemanticVersion,
        [property: JsonPropertyName("releaseDate")] string ReleaseDate,
        [property: JsonPropertyName("settingsFooter")] string SettingsFooter);

    private sealed record LegacyReminderFixture(
        Guid ItemId,
        Guid OccurrenceId,
        DateTimeOffset DueAt);
}
