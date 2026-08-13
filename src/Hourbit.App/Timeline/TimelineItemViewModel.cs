using Hourbit.App.Commands;
using Hourbit.Core.Domain;
using Hourbit.Core.Services;

namespace Hourbit.App.Timeline;

public sealed class TimelineItemViewModel : ObservableObject
{
    private DateTimeOffset _now;

    public TimelineItemViewModel(TimelineRow row, DateTimeOffset now)
    {
        OccurrenceId = row.OccurrenceId;
        Title = row.Title;
        DueAt = row.DueAt;
        Kind = row.Kind;
        Importance = row.Importance;
        State = row.State;
        RecurrenceText = row.RecurrenceText;
        _now = now;
        GroupName = GroupFor(row, now);
        StatusText = StatusFor(row, now);
    }

    public Guid OccurrenceId { get; }
    public string Title { get; }
    public DateTimeOffset DueAt { get; }
    public string TimeText => DueAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public ReminderKind Kind { get; }
    public bool IsCountdown => Kind == ReminderKind.Countdown;
    public string RemainingText
    {
        get
        {
            if (!IsCountdown)
                return string.Empty;
            var remaining = DueAt - _now;
            if (remaining <= TimeSpan.Zero)
                return "已到时";
            var totalSeconds = (long)Math.Floor(remaining.TotalSeconds);
            if (totalSeconds <= 0)
                return "已到时";
            var hours = totalSeconds / 3600;
            var minutes = totalSeconds % 3600 / 60;
            var seconds = totalSeconds % 60;
            return hours > 0
                ? $"剩余 {hours}:{minutes:00}:{seconds:00}"
                : $"剩余 {minutes:00}:{seconds:00}";
        }
    }
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
        OccurrenceState.DeliveryFailed => "!",
        OccurrenceState.Ignored => "×",
        _ => "◷"
    };
    public string StatusText { get; }
    public string GroupName { get; }
    public int GroupOrder => GroupName switch { "已错过" => 0, "接下来" => 1, _ => 2 };

    public void UpdateNow(DateTimeOffset now)
    {
        if (!IsCountdown || _now == now)
            return;
        _now = now;
        OnPropertyChanged(nameof(RemainingText));
    }

    private static string GroupFor(TimelineRow row, DateTimeOffset now) => row.State switch
    {
        OccurrenceState.Completed or OccurrenceState.Ignored => "已完成",
        OccurrenceState.Missed => "已错过",
        _ => "接下来"
    };

    private static string StatusFor(TimelineRow row, DateTimeOffset now) => row.State switch
    {
        OccurrenceState.Fired => "等待处理",
        OccurrenceState.Completed => "已完成",
        OccurrenceState.Ignored => "已忽略",
        OccurrenceState.Missed => "已错过",
        OccurrenceState.Snoozed => "已推迟",
        OccurrenceState.Delivering => "正在提醒",
        OccurrenceState.DeliveryFailed => "提醒失败",
        _ => "等待中"
    };
}

public sealed class TimelineGroupViewModel(string name)
{
    public string Name { get; } = name;
    public System.Collections.ObjectModel.ObservableCollection<TimelineItemViewModel> Items { get; } = [];
}
