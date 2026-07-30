using System.Globalization;
using Moment.App.Commands;
using Moment.Core.Domain;
using Moment.Core.Parsing;

namespace Moment.App.Timeline;

public enum EditRecurrenceMode
{
    None,
    Daily,
    Weekdays,
    Weekly
}

public sealed record EditOption<T>(T Value, string Label);

public sealed class EditReminderViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, DayOfWeek> DayLabels =
        new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
        {
            ["周一"] = DayOfWeek.Monday,
            ["周二"] = DayOfWeek.Tuesday,
            ["周三"] = DayOfWeek.Wednesday,
            ["周四"] = DayOfWeek.Thursday,
            ["周五"] = DayOfWeek.Friday,
            ["周六"] = DayOfWeek.Saturday,
            ["周日"] = DayOfWeek.Sunday
        };

    private readonly TimeZoneInfo _zone;
    private string _title;
    private string _dateText;
    private string _timeText;
    private ReminderKind _selectedKind;
    private ReminderImportance _selectedImportance;
    private EditRecurrenceMode _selectedRecurrence;
    private string _weeklyDaysText;
    private string? _errorMessage;

    public EditReminderViewModel(TimelineItemViewModel item, TimeZoneInfo zone)
        : this(CreateDraft(item), zone)
    {
        ArgumentNullException.ThrowIfNull(item);
    }

    public EditReminderViewModel(ReminderDraft draft, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        var localDue = TimeZoneInfo.ConvertTime(draft.DueAt, zone);
        _title = draft.Title;
        _dateText = localDue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _timeText = localDue.ToString("HH:mm", CultureInfo.InvariantCulture);
        _selectedKind = draft.Kind;
        _selectedImportance = draft.Importance;
        _selectedRecurrence = draft.Recurrence?.Kind switch
        {
            RecurrenceKind.Daily => EditRecurrenceMode.Daily,
            RecurrenceKind.Weekdays => EditRecurrenceMode.Weekdays,
            RecurrenceKind.Weekly => EditRecurrenceMode.Weekly,
            _ => EditRecurrenceMode.None
        };
        _weeklyDaysText = string.Join("、", DayLabels
            .Where(pair => draft.Recurrence?.DaysOfWeek.Contains(pair.Value) == true)
            .Select(pair => pair.Key));
    }

    public IReadOnlyList<EditOption<ReminderKind>> Kinds { get; } =
    [
        new(ReminderKind.Countdown, "倒计时"),
        new(ReminderKind.Alarm, "闹钟"),
        new(ReminderKind.Plan, "计划")
    ];

    public IReadOnlyList<EditOption<ReminderImportance>> Importances { get; } =
    [
        new(ReminderImportance.Normal, "普通"),
        new(ReminderImportance.Important, "重要")
    ];

    public IReadOnlyList<EditOption<EditRecurrenceMode>> Recurrences { get; } =
    [
        new(EditRecurrenceMode.None, "不重复"),
        new(EditRecurrenceMode.Daily, "每天"),
        new(EditRecurrenceMode.Weekdays, "工作日（周一至周五）"),
        new(EditRecurrenceMode.Weekly, "每周")
    ];

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }

    public string DateText
    {
        get => _dateText;
        set => SetProperty(ref _dateText, value ?? string.Empty);
    }

    public string TimeText
    {
        get => _timeText;
        set => SetProperty(ref _timeText, value ?? string.Empty);
    }

    public ReminderKind SelectedKind
    {
        get => _selectedKind;
        set => SetProperty(ref _selectedKind, value);
    }

    public ReminderImportance SelectedImportance
    {
        get => _selectedImportance;
        set => SetProperty(ref _selectedImportance, value);
    }

    public EditRecurrenceMode SelectedRecurrence
    {
        get => _selectedRecurrence;
        set
        {
            if (SetProperty(ref _selectedRecurrence, value))
                OnPropertyChanged(nameof(IsWeekly));
        }
    }

    public string WeeklyDaysText
    {
        get => _weeklyDaysText;
        set => SetProperty(ref _weeklyDaysText, value ?? string.Empty);
    }

    public bool IsWeekly => SelectedRecurrence == EditRecurrenceMode.Weekly;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool TryBuildDraft(out ReminderDraft? draft)
    {
        draft = null;
        var title = Title.Trim();
        if (title.Length == 0)
        {
            ErrorMessage = "请输入提醒内容。";
            return false;
        }

        if (!DateOnly.TryParseExact(DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            || !TimeOnly.TryParseExact(TimeText, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            ErrorMessage = "请输入有效的日期和时间。";
            return false;
        }

        var localDue = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (_zone.IsInvalidTime(localDue))
        {
            ErrorMessage = "该本地时间不存在，请选择其他时间。";
            return false;
        }

        RecurrenceRule? recurrence;
        switch (SelectedRecurrence)
        {
            case EditRecurrenceMode.None:
                recurrence = null;
                break;
            case EditRecurrenceMode.Daily:
                recurrence = RecurrenceRule.Daily(time);
                break;
            case EditRecurrenceMode.Weekdays:
                recurrence = RecurrenceRule.Weekdays(time);
                break;
            case EditRecurrenceMode.Weekly:
                var days = DayLabels
                    .Where(pair => WeeklyDaysText.Contains(pair.Key, StringComparison.Ordinal))
                    .Select(pair => pair.Value)
                    .Distinct()
                    .ToArray();
                if (days.Length == 0)
                {
                    ErrorMessage = "每周重复请至少选择一天。";
                    return false;
                }
                recurrence = RecurrenceRule.Weekly(days, time);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var dueAt = ResolveLocal(localDue);
        draft = new ReminderDraft(title, dueAt, SelectedKind, SelectedImportance, recurrence);
        ErrorMessage = null;
        return true;
    }

    private DateTimeOffset ResolveLocal(DateTime local)
    {
        if (_zone.IsAmbiguousTime(local))
        {
            return _zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => new DateTimeOffset(local, offset))
                .MinBy(candidate => candidate.UtcDateTime);
        }
        return new DateTimeOffset(local, _zone.GetUtcOffset(local));
    }

    private static ReminderDraft CreateDraft(TimelineItemViewModel item)
    {
        var time = TimeOnly.FromDateTime(item.DueAt.DateTime);
        RecurrenceRule? recurrence = null;
        if (item.RecurrenceText == "每天")
        {
            recurrence = RecurrenceRule.Daily(time);
        }
        else if (item.RecurrenceText?.StartsWith("工作日", StringComparison.Ordinal) == true)
        {
            recurrence = RecurrenceRule.Weekdays(time);
        }
        else if (item.IsRecurring)
        {
            var days = DayLabels
                .Where(pair => item.RecurrenceText?.Contains(pair.Key, StringComparison.Ordinal) == true)
                .Select(pair => pair.Value)
                .ToArray();
            if (days.Length > 0)
                recurrence = RecurrenceRule.Weekly(days, time);
        }

        return new ReminderDraft(item.Title, item.DueAt, item.Kind, item.Importance, recurrence);
    }
}
