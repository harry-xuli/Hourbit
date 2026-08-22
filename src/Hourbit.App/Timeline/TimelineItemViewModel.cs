using Hourbit.App.Commands;
using Hourbit.App.Localization;
using Hourbit.Core.Domain;
using Hourbit.Core.Services;

namespace Hourbit.App.Timeline;

public sealed class TimelineItemViewModel : ObservableObject
{
    private DateTimeOffset _now;
    private UiLanguage _language;

    public TimelineItemViewModel(
        TimelineRow row,
        DateTimeOffset now,
        UiLanguage language = UiLanguage.ZhCn)
    {
        OccurrenceId = row.OccurrenceId;
        Title = row.Title;
        DueAt = row.DueAt;
        Kind = row.Kind;
        Importance = row.Importance;
        State = row.State;
        RecurrenceText = row.RecurrenceText;
        _now = now;
        _language = language;
        GroupKind = GroupFor(row);
    }

    public Guid OccurrenceId { get; }
    public string Title { get; }
    public DateTimeOffset DueAt { get; }
    public string TimeText => DueAt.ToString(
        "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public ReminderKind Kind { get; }
    public bool IsCountdown => Kind == ReminderKind.Countdown;
    public string RemainingText
    {
        get
        {
            if (!IsCountdown || State is OccurrenceState.Completed or OccurrenceState.Ignored)
                return string.Empty;
            var remaining = DueAt - _now;
            if (remaining <= TimeSpan.Zero)
                return Translate("Timeline.CountdownDue");
            var totalSeconds = (long)Math.Floor(remaining.TotalSeconds);
            if (totalSeconds <= 0)
                return Translate("Timeline.CountdownDue");
            var hours = totalSeconds / 3600;
            var minutes = totalSeconds % 3600 / 60;
            var seconds = totalSeconds % 60;
            var value = hours > 0
                ? $"{hours}:{minutes:00}:{seconds:00}"
                : $"{minutes:00}:{seconds:00}";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Translate("Timeline.CountdownRemaining"),
                value);
        }
    }
    public ReminderImportance Importance { get; }
    public OccurrenceState State { get; }
    public string? RecurrenceText { get; }
    public string RecurrenceDisplay => RecurrenceText ?? Translate("Recurrence.None");
    public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurrenceText);
    public string ImportanceText => Translate(
        Importance == ReminderImportance.Important
            ? "Importance.Important"
            : "Importance.Normal");
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
    public string StatusText => StatusFor(State);
    public TimelineGroupKind GroupKind { get; }
    public string GroupName => Translate(GroupKind switch
    {
        TimelineGroupKind.Missed => "Timeline.Group.Missed",
        TimelineGroupKind.Upcoming => "Timeline.Group.Upcoming",
        _ => "Timeline.Group.Completed"
    });
    public int GroupOrder => (int)GroupKind;

    public void UpdateNow(DateTimeOffset now)
    {
        if (!IsCountdown || _now == now)
            return;
        _now = now;
        OnPropertyChanged(nameof(RemainingText));
    }

    public void SetLanguage(UiLanguage language)
    {
        if (_language == language)
            return;
        _language = language;
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(RecurrenceDisplay));
        OnPropertyChanged(nameof(ImportanceText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(GroupName));
    }

    private static TimelineGroupKind GroupFor(TimelineRow row) => row.State switch
    {
        OccurrenceState.Completed or OccurrenceState.Ignored => TimelineGroupKind.Completed,
        OccurrenceState.Missed => TimelineGroupKind.Missed,
        _ => TimelineGroupKind.Upcoming
    };

    private string StatusFor(OccurrenceState state) => Translate(state switch
    {
        OccurrenceState.Fired => "Timeline.Status.WaitingAction",
        OccurrenceState.Completed => "Timeline.Status.Completed",
        OccurrenceState.Ignored => "Timeline.Status.Ignored",
        OccurrenceState.Missed => "Timeline.Status.Missed",
        OccurrenceState.Snoozed => "Timeline.Status.Snoozed",
        OccurrenceState.Delivering => "Timeline.Status.Delivering",
        OccurrenceState.DeliveryFailed => "Timeline.Status.DeliveryFailed",
        _ => "Timeline.Status.Waiting"
    });

    private string Translate(string key) =>
        LocalizationCatalog.Translate(_language, key);
}

public enum TimelineGroupKind
{
    Missed,
    Upcoming,
    Completed
}

public sealed class TimelineGroupViewModel(
    TimelineGroupKind kind,
    UiLanguage language) : ObservableObject
{
    private UiLanguage _language = language;

    public TimelineGroupKind Kind { get; } = kind;
    public string Name => LocalizationCatalog.Translate(_language, Kind switch
    {
        TimelineGroupKind.Missed => "Timeline.Group.Missed",
        TimelineGroupKind.Upcoming => "Timeline.Group.Upcoming",
        _ => "Timeline.Group.Completed"
    });
    public System.Collections.ObjectModel.ObservableCollection<TimelineItemViewModel> Items { get; } = [];

    public void SetLanguage(UiLanguage language)
    {
        if (_language == language)
            return;
        _language = language;
        OnPropertyChanged(nameof(Name));
    }
}
