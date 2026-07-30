using Moment.Core.Domain;
using Moment.Core.Parsing;

namespace Moment.App.Timeline;

public sealed class TimelineDialogService(Action openQuickAdd) : ITimelineDialogService
{
    public Task<SeriesScope?> SelectEditScopeAsync(TimelineItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            "选择“是”只修改本次，选择“否”修改本次及以后。",
            "编辑重复提醒",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);
        return Task.FromResult(result switch
        {
            System.Windows.MessageBoxResult.Yes => (SeriesScope?)SeriesScope.OccurrenceOnly,
            System.Windows.MessageBoxResult.No => SeriesScope.ThisAndFuture,
            _ => null
        });
    }

    public Task<SeriesScope?> SelectDeleteScopeAsync(TimelineItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            "选择“是”只删除本次，选择“否”删除本次及以后。",
            "删除重复提醒",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);
        return Task.FromResult(result switch
        {
            System.Windows.MessageBoxResult.Yes => (SeriesScope?)SeriesScope.OccurrenceOnly,
            System.Windows.MessageBoxResult.No => SeriesScope.ThisAndFuture,
            _ => null
        });
    }

    public Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            $"{item.Title}\n{item.DueAt:yyyy年M月d日 HH:mm}",
            "编辑提醒",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.None);
        var draft = result == System.Windows.MessageBoxResult.OK
            ? new ReminderDraft(item.Title, item.DueAt, item.Kind, item.Importance,
                ParseRecurrence(item))
            : null;
        return Task.FromResult<ReminderDraft?>(draft);
    }

    public void OpenQuickAdd() => openQuickAdd();

    private static RecurrenceRule? ParseRecurrence(TimelineItemViewModel item)
    {
        if (!item.IsRecurring)
            return null;
        var time = TimeOnly.FromDateTime(item.DueAt.DateTime);
        if (item.RecurrenceText == "每天")
            return RecurrenceRule.Daily(time);
        if (item.RecurrenceText?.StartsWith("工作日", StringComparison.Ordinal) == true)
            return RecurrenceRule.Weekdays(time);

        var days = new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
        {
            ["周一"] = DayOfWeek.Monday,
            ["周二"] = DayOfWeek.Tuesday,
            ["周三"] = DayOfWeek.Wednesday,
            ["周四"] = DayOfWeek.Thursday,
            ["周五"] = DayOfWeek.Friday,
            ["周六"] = DayOfWeek.Saturday,
            ["周日"] = DayOfWeek.Sunday
        }.Where(pair => item.RecurrenceText?.Contains(pair.Key, StringComparison.Ordinal) == true)
            .Select(pair => pair.Value)
            .ToArray();
        return days.Length == 0 ? null : RecurrenceRule.Weekly(days, time);
    }
}
