using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        "single-instance-protocol"
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
                await ExerciseReminderPipelineAsync(databasePath, events, ct);
                await ExerciseSingleInstanceProtocolAsync(events, ct);
            }

            await ValidateResultAsync(resultPath, ct);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Self-test failed: {exception}");
            return 1;
        }
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
        parser.Parse(text, now, zone) is ParseResult.Success success
            ? success.Draft
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
            if (!ExpectedEvents.Contains(eventName, StringComparer.Ordinal))
                throw new InvalidOperationException($"Unknown self-test event: {eventName}");

            await _gate.WaitAsync(ct);
            try
            {
                if (!_recorded.Add(eventName))
                    throw new InvalidOperationException(
                        $"Duplicate self-test event: {eventName}");

                var line = JsonSerializer.Serialize(
                    new SmokeEvent(eventName, DateTimeOffset.UtcNow));
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
}
