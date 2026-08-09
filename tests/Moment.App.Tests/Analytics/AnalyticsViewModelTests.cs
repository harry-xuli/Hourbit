using System.Globalization;
using Moment.App.Analytics;
using Moment.Core.Analytics;

namespace Moment.App.Tests.Analytics;

public sealed class AnalyticsViewModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08-analytics-vm", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(AnalyticsRangeKind.Recent7Days, "2026-08-03", "2026-08-09")]
    [InlineData(AnalyticsRangeKind.Recent30Days, "2026-07-11", "2026-08-09")]
    [InlineData(AnalyticsRangeKind.CurrentMonth, "2026-08-01", "2026-08-31")]
    public async Task Preset_ranges_query_the_exact_inclusive_local_dates(
        AnalyticsRangeKind kind,
        string expectedStart,
        string expectedEnd)
    {
        var loader = new RecordingLoader();
        var vm = Create(loader.LoadAsync);

        await vm.SelectRangeAsync(kind);

        Assert.Equal(
            new LocalDateRange(DateOnly.Parse(expectedStart), DateOnly.Parse(expectedEnd)),
            Assert.Single(loader.Ranges));
        Assert.Same(loader.Snapshots.Single(), vm.Snapshot);
    }

    [Fact]
    public async Task Year_and_custom_ranges_are_supported_only_inside_analytics()
    {
        var loader = new RecordingLoader();
        var vm = Create(loader.LoadAsync);

        vm.SelectedYear = 2024;
        await vm.SelectRangeAsync(AnalyticsRangeKind.CalendarYear);
        vm.CustomStart = new DateOnly(2026, 2, 3);
        vm.CustomEnd = new DateOnly(2026, 2, 8);
        await vm.ApplyCustomRangeAsync();

        Assert.Equal(
        [
            new LocalDateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31)),
            new LocalDateRange(new DateOnly(2026, 2, 3), new DateOnly(2026, 2, 8))
        ], loader.Ranges);
        Assert.Equal(AnalyticsRangeKind.Custom, vm.SelectedRangeKind);
    }

    [Fact]
    public async Task Invalid_custom_range_is_rejected_before_querying_and_keeps_the_last_snapshot()
    {
        var loader = new RecordingLoader();
        var vm = Create(loader.LoadAsync);
        await vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);
        var last = vm.Snapshot;
        vm.CustomStart = new DateOnly(2026, 8, 10);
        vm.CustomEnd = new DateOnly(2026, 8, 1);

        await vm.ApplyCustomRangeAsync();

        Assert.Single(loader.Ranges);
        Assert.Same(last, vm.Snapshot);
        Assert.Equal("开始日期不能晚于结束日期。", vm.ErrorMessage);
    }

    [Fact]
    public async Task Switching_donut_dimension_reuses_the_same_snapshot()
    {
        var loader = new RecordingLoader(CreateSnapshot(
            new LocalDateRange(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 9))));
        var vm = Create(loader.LoadAsync);
        await vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);

        Assert.Equal(["ChartCompletedBrush", "ChartIncompleteBrush", "ChartOverdueBrush"],
            vm.DonutGeometry.Sectors.Select(static sector => sector.ColorKey));
        vm.SelectDimension(DonutDimension.ItemType);
        Assert.Equal(["ChartTodoBrush", "ChartReminderBrush"],
            vm.DonutGeometry.Sectors.Select(static sector => sector.ColorKey));
        vm.SelectDimension(DonutDimension.Importance);
        Assert.Equal(["ChartNormalBrush", "ChartImportantBrush"],
            vm.DonutGeometry.Sectors.Select(static sector => sector.ColorKey));
        Assert.Single(loader.Ranges);
        Assert.Contains("重要 2", vm.DonutSummary);
    }

    [Fact]
    public async Task A_new_generation_cancels_and_discards_a_stale_result()
    {
        var first = new TaskCompletionSource<AnalyticsSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AnalyticsSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken firstToken = default;
        Task<AnalyticsSnapshot> Load(LocalDateRange range, CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstToken = ct;
                return first.Task;
            }
            return second.Task;
        }
        var vm = Create(Load);

        var staleLoad = vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);
        var currentLoad = vm.SelectRangeAsync(AnalyticsRangeKind.Recent30Days);
        Assert.True(firstToken.IsCancellationRequested);
        var currentSnapshot = CreateSnapshot(
            new LocalDateRange(new DateOnly(2026, 7, 11), new DateOnly(2026, 8, 9)),
            completed: 8);
        second.SetResult(currentSnapshot);
        await currentLoad;
        first.SetResult(CreateSnapshot(
            new LocalDateRange(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 9)),
            completed: 1));
        await staleLoad;

        Assert.Same(currentSnapshot, vm.Snapshot);
        Assert.Equal(8, vm.Completed);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Query_error_is_inline_and_keeps_the_last_successful_geometry_visible()
    {
        var success = CreateSnapshot(
            new LocalDateRange(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 9)));
        var calls = 0;
        Task<AnalyticsSnapshot> Load(LocalDateRange range, CancellationToken ct) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromResult(success)
                : Task.FromException<AnalyticsSnapshot>(
                    new InvalidOperationException("数据库暂时不可用"));
        var vm = Create(Load);
        await vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);
        var geometry = vm.DonutGeometry;

        await vm.SelectRangeAsync(AnalyticsRangeKind.CurrentMonth);

        Assert.Same(success, vm.Snapshot);
        Assert.Same(geometry, vm.DonutGeometry);
        Assert.Equal("数据库暂时不可用", vm.ErrorMessage);
    }

    [Fact]
    public async Task Summary_card_deep_link_loads_the_exact_supplied_range()
    {
        var loader = new RecordingLoader();
        var vm = Create(loader.LoadAsync);
        var exact = new LocalDateRange(
            new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 23));

        await vm.LoadRangeAsync(exact);

        Assert.Equal(exact, Assert.Single(loader.Ranges));
        Assert.Equal(AnalyticsRangeKind.Custom, vm.SelectedRangeKind);
        Assert.Equal(exact.Start, vm.CustomStart);
        Assert.Equal(exact.End, vm.CustomEnd);
    }

    [Fact]
    public async Task Disposing_cancels_an_active_load_and_ignores_a_loader_that_completes_late()
    {
        var completion = new TaskCompletionSource<AnalyticsSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var vm = Create((range, ct) =>
        {
            observed = ct;
            return completion.Task;
        });

        var load = vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);
        vm.Dispose();
        completion.SetResult(CreateSnapshot(
            new LocalDateRange(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 9))));
        await load;

        Assert.True(observed.IsCancellationRequested);
        Assert.Null(vm.Snapshot);
        Assert.False(vm.IsLoading);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => vm.SelectRangeAsync(AnalyticsRangeKind.Recent30Days));
        vm.Dispose();
    }

    [Fact]
    public async Task Cancelling_a_window_load_keeps_the_view_model_reusable_for_reopen()
    {
        var first = new TaskCompletionSource<AnalyticsSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var current = CreateSnapshot(
            new LocalDateRange(new DateOnly(2026, 7, 11), new DateOnly(2026, 8, 9)),
            completed: 9);
        var vm = Create((range, ct) => Interlocked.Increment(ref calls) == 1
            ? first.Task
            : Task.FromResult(current));

        var stale = vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);
        vm.CancelActiveLoad();
        await vm.SelectRangeAsync(AnalyticsRangeKind.Recent30Days);
        first.SetResult(CreateSnapshot(
            new LocalDateRange(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 9))));
        await stale;

        Assert.Same(current, vm.Snapshot);
        Assert.Equal(9, vm.Completed);
    }

    private static AnalyticsViewModel Create(
        Func<LocalDateRange, CancellationToken, Task<AnalyticsSnapshot>> loader) =>
        new(loader, new FixedTimeProvider(Now.ToUniversalTime()), Zone,
            CultureInfo.GetCultureInfo("zh-CN"));

    private static AnalyticsSnapshot CreateSnapshot(
        LocalDateRange range,
        int completed = 4) =>
        new(
            Guid.NewGuid(),
            Now,
            range,
            Zone.Id,
            new AnalyticsTotals(7, completed, 3, 1, 0, 5, 2, 1),
            [
                new DistributionSlice("completed", "已完成", completed),
                new DistributionSlice("incomplete", "未完成", 2),
                new DistributionSlice("overdue", "已逾期", 1)
            ],
            [
                new DistributionSlice("todo", "待办", 5),
                new DistributionSlice("reminder", "提醒", 2)
            ],
            [
                new DistributionSlice("normal", "普通", 5),
                new DistributionSlice("important", "重要", 2)
            ],
            [
                new TrendBucket(range.Start, range.Start, "首日", 1),
                new TrendBucket(range.End, range.End, "末日", completed)
            ],
            []);

    private sealed class RecordingLoader
    {
        private readonly AnalyticsSnapshot? _fixedSnapshot;

        public RecordingLoader(AnalyticsSnapshot? fixedSnapshot = null) =>
            _fixedSnapshot = fixedSnapshot;

        public List<LocalDateRange> Ranges { get; } = [];
        public List<AnalyticsSnapshot> Snapshots { get; } = [];

        public Task<AnalyticsSnapshot> LoadAsync(
            LocalDateRange range,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Ranges.Add(range);
            var snapshot = _fixedSnapshot ?? CreateSnapshot(range);
            Snapshots.Add(snapshot);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
