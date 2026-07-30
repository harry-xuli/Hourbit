using Moment.Core.Domain;
using Moment.Core.Parsing;

namespace Moment.App.Timeline;

public sealed class TimelineDialogService(Action openQuickAdd, TimeZoneInfo zone) : ITimelineDialogService
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

    public Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            $"确定删除“{item.Title}”吗？",
            "删除提醒",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return Task.FromResult(result == System.Windows.MessageBoxResult.Yes);
    }

    public Task<ReminderDraft?> EditAsync(TimelineItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var viewModel = new EditReminderViewModel(item, zone);
        var window = new EditReminderWindow { DataContext = viewModel };
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
            window.Owner = owner;
        window.ShowDialog();
        return Task.FromResult(window.Draft);
    }

    public void OpenQuickAdd() => openQuickAdd();

}
