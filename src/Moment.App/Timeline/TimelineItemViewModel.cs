using Moment.App.Commands;
using Moment.Core.Domain;
using Moment.Core.Services;

namespace Moment.App.Timeline;

public sealed class TimelineItemViewModel : ObservableObject
{
    public TimelineItemViewModel(TimelineRow row, DateTimeOffset now)
    {
        OccurrenceId = row.OccurrenceId;
        Title = row.Title;
        DueAt = row.DueAt;
        Kind = row.Kind;
        Importance = row.Importance;
        State = row.State;
        RecurrenceText = row.RecurrenceText;
        GroupName = GroupFor(row, now);
        StatusText = StatusFor(row, now);
    }

    public Guid OccurrenceId { get; }
    public string Title { get; }
    public DateTimeOffset DueAt { get; }
    public string TimeText => DueAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public ReminderKind Kind { get; }
    public ReminderImportance Importance { get; }
    public OccurrenceState State { get; }
    public string? RecurrenceText { get; }
    public string RecurrenceDisplay => RecurrenceText ?? "不重复";
    public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurrenceText);
    public string ImportanceText => Importance == ReminderImportance.Important ? "重要" : "普通";
    public string ImportanceSymbol => Importance == ReminderImportance.Important ? "↑" : "●";
    public string KindSymbol => Kind switch
    {
        ReminderKind.Countdown => "◷",
        ReminderKind.Alarm => "⌚",
        _ => "□"
    };
    public string StatusSymbol => State switch
    {
        OccurrenceState.Completed => "✓",
        OccurrenceState.Missed => "!",
        OccurrenceState.Ignored => "×",
        _ => "◷"
    };
    public string StatusText { get; }
    public string GroupName { get; }
    public int GroupOrder => GroupName switch { "已错过" => 0, "接下来" => 1, _ => 2 };

    private static string GroupFor(TimelineRow row, DateTimeOffset now) => row.State switch
    {
        OccurrenceState.Completed or OccurrenceState.Ignored => "已完成",
        OccurrenceState.Missed => "已错过",
        OccurrenceState.Scheduled when row.DueAt < now => "已错过",
        _ => "接下来"
    };

    private static string StatusFor(TimelineRow row, DateTimeOffset now) => row.State switch
    {
        OccurrenceState.Fired => "等待处理",
        OccurrenceState.Completed => "已完成",
        OccurrenceState.Ignored => "已忽略",
        OccurrenceState.Missed => "已错过",
        OccurrenceState.Snoozed => "已推迟",
        OccurrenceState.Scheduled when row.DueAt < now => "已错过",
        _ => "等待中"
    };
}

public sealed class TimelineGroupViewModel(string name)
{
    public string Name { get; } = name;
    public System.Collections.ObjectModel.ObservableCollection<TimelineItemViewModel> Items { get; } = [];
}
