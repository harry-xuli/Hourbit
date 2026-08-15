using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hourbit.App.Analytics;
using Hourbit.App.Localization;
using Hourbit.App.Styles;
using Hourbit.Core.Analytics;

namespace Hourbit.App.Tests.Analytics;

public sealed class AnalyticsWindowTests
{
    private static readonly LocalDateRange Range = new(
        new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 9));

    [Fact]
    public Task Report_window_follows_shared_language_and_gives_dates_room_below_the_title() =>
        WpfTestHost.RunAsync(async () =>
        {
            var localization = new LocalizationService(
                CultureInfo.GetCultureInfo("zh-CN"), null);
            var vm = new AnalyticsViewModel(
                (range, ct) => Task.FromResult(Snapshot()),
                new FixedTimeProvider(
                    new DateTimeOffset(2026, 8, 9, 2, 0, 0, TimeSpan.Zero)),
                TimeZoneInfo.CreateCustomTimeZone(
                    "UTC+08-window-language", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
                CultureInfo.GetCultureInfo("zh-CN"),
                localization);
            var window = await ShowAsync(vm);

            localization.SetLanguage(UiLanguage.EnUs);
            window.UpdateLayout();

            Assert.Equal("Analytics report - Hourbit", window.Title);
            Assert.Equal("Analytics report", Assert.IsType<TextBlock>(
                window.FindName("AnalyticsTitle")).Text);
            Assert.Equal("Completed", Assert.IsType<TextBlock>(
                window.FindName("CompletedLabel")).Text);
            Assert.Equal("Apply", Assert.IsType<Button>(
                window.FindName("ApplyRangeButton")).Content);
            var header = Assert.IsType<TextBlock>(window.FindName("AnalyticsTitle"));
            var start = Assert.IsType<DatePicker>(window.FindName("StartDatePicker"));
            var end = Assert.IsType<DatePicker>(window.FindName("EndDatePicker"));
            Assert.Equal("en-US", start.Language.IetfLanguageTag);
            Assert.Equal("en-US", end.Language.IetfLanguageTag);
            Assert.True(start.TranslatePoint(new Point(), window).Y >
                        header.TranslatePoint(new Point(), window).Y);
            Assert.True(start.ActualWidth >= 190d);
            Assert.True(end.ActualWidth >= 190d);
        });

    [Fact]
    public Task Dashboard_exposes_kpis_range_picker_charts_and_text_summaries_to_uia() =>
        WpfTestHost.RunAsync(async () =>
        {
            var (window, vm) = await ShowAsync(Snapshot());

            Assert.Equal("分析报告 - Hourbit 日程", window.Title);
            Assert.Equal("日期范围", PeerName(Assert.IsType<ComboBox>(
                window.FindName("RangePicker"))));
            Assert.Equal("图表维度", PeerName(Assert.IsType<ComboBox>(
                window.FindName("DimensionPicker"))));
            Assert.Equal("已完成 4", AutomationProperties.GetName(
                Assert.IsType<Border>(window.FindName("CompletedCard"))));
            Assert.Equal("未来计划 3", AutomationProperties.GetName(
                Assert.IsType<Border>(window.FindName("FutureCard"))));
            Assert.Equal("已逾期 1", AutomationProperties.GetName(
                Assert.IsType<Border>(window.FindName("OverdueCard"))));

            var donut = Assert.IsType<DonutChartControl>(window.FindName("DonutChart"));
            var trend = Assert.IsType<TrendChartControl>(window.FindName("TrendChart"));
            Assert.Equal(vm.DonutSummary, PeerName(donut));
            Assert.Equal(vm.TrendSummary, PeerName(trend));
            Assert.True(donut.Focusable);
            Assert.True(trend.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(donut));
            Assert.True(KeyboardNavigation.GetIsTabStop(trend));
            Assert.Equal(vm.DonutSummary, Assert.IsType<TextBlock>(
                window.FindName("DonutSummaryText")).Text);
            Assert.Equal(vm.TrendSummary, Assert.IsType<TextBlock>(
                window.FindName("TrendSummaryText")).Text);
            var legend = Assert.IsType<ItemsControl>(window.FindName("DonutLegend"));
            Assert.Equal("分布图例", PeerName(legend));
            Assert.Equal(3, legend.Items.Count);
            Assert.Equal(
                ["已完成 4，占 57%", "未完成 2，占 29%", "已逾期 1，占 14%"],
                vm.LegendItems.Select(static item => item.AccessibleName));
            Assert.Equal(
                ["图例标记 已完成", "图例标记 未完成", "图例标记 已逾期"],
                Descendants<LegendSwatchControl>(legend).Select(PeerName));
        });

    [Fact]
    public Task Dimension_selector_updates_the_live_donut_without_another_query() =>
        WpfTestHost.RunAsync(async () =>
        {
            var calls = 0;
            var vm = Create((range, ct) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Snapshot());
            });
            var window = await ShowAsync(vm);
            var picker = Assert.IsType<ComboBox>(window.FindName("DimensionPicker"));

            picker.SelectedValue = DonutDimension.Importance;
            window.UpdateLayout();

            Assert.Equal(1, calls);
            Assert.Contains("重要性分布", vm.DonutSummary);
            Assert.Equal(["ChartNormalBrush", "ChartImportantBrush"],
                Assert.IsType<DonutChartControl>(window.FindName("DonutChart"))
                    .Geometry.Sectors.Select(static sector => sector.ColorKey));
        });

    [Fact]
    public Task Empty_snapshot_shows_a_useful_zero_data_state_instead_of_an_empty_plot() =>
        WpfTestHost.RunAsync(async () =>
        {
            var empty = Snapshot(active: 0, completed: 0, future: 0, overdue: 0);
            var (window, vm) = await ShowAsync(empty);

            var message = Assert.IsType<TextBlock>(window.FindName("EmptyState"));
            Assert.True(message.IsVisible);
            Assert.Equal("这个日期范围内还没有可分析的记录。", message.Text);
            Assert.True(vm.DonutGeometry.IsEmpty);
            Assert.True(vm.TrendGeometry.IsEmpty);
        });

    [Fact]
    public Task Range_and_chart_controls_participate_in_keyboard_focus_order() =>
        WpfTestHost.RunAsync(async () =>
        {
            var (window, _) = await ShowAsync(Snapshot());
            var range = Assert.IsType<ComboBox>(window.FindName("RangePicker"));
            var donut = Assert.IsType<DonutChartControl>(window.FindName("DonutChart"));
            var trend = Assert.IsType<TrendChartControl>(window.FindName("TrendChart"));

            Assert.True(range.Focus());
            Assert.True(range.IsKeyboardFocusWithin);
            Assert.True(KeyboardNavigation.GetIsTabStop(donut));
            Assert.True(KeyboardNavigation.GetIsTabStop(trend));
            Assert.Equal(KeyboardNavigationMode.Continue,
                KeyboardNavigation.GetTabNavigation(window));
        });

    [Fact]
    public Task Chart_resources_are_dynamic_and_receive_system_high_contrast_brushes() =>
        WpfTestHost.RunAsync(async () =>
        {
            var (window, _) = await ShowAsync(Snapshot());
            string[] chartKeys =
            [
                "ChartCompletedBrush", "ChartIncompleteBrush", "ChartOverdueBrush",
                "ChartTodoBrush", "ChartReminderBrush", "ChartNormalBrush",
                "ChartImportantBrush", "ChartOtherBrush"
            ];
            Assert.All(chartKeys, key => Assert.IsAssignableFrom<Brush>(window.FindResource(key)));

            window.Resources[SystemColors.HighlightBrushKey] = Brushes.Yellow;
            window.Resources[SystemColors.WindowTextBrushKey] = Brushes.White;
            window.Resources[SystemColors.GrayTextBrushKey] = Brushes.Gray;
            var donut = Assert.IsType<DonutChartControl>(window.FindName("DonutChart"));
            var trend = Assert.IsType<TrendChartControl>(window.FindName("TrendChart"));
            _ = RenderedPixelCount(donut);
            _ = RenderedPixelCount(trend);
            var donutRevision = donut.PaletteRevision;
            var trendRevision = trend.PaletteRevision;
            HighContrastPalette.Apply(window.Resources, true, window.FindResource);
            _ = RenderedPixelCount(donut);
            _ = RenderedPixelCount(trend);

            Assert.Same(Brushes.Yellow, window.Resources["ChartCompletedBrush"]);
            Assert.Same(Brushes.White, window.Resources["ChartIncompleteBrush"]);
            Assert.Same(Brushes.Gray, window.Resources["ChartOverdueBrush"]);
            Assert.Same(Brushes.Yellow, window.Resources["ChartImportantBrush"]);
            Assert.True(donut.PaletteRevision > donutRevision);
            Assert.True(trend.PaletteRevision > trendRevision);
            Assert.Same(Brushes.Yellow, donut.LastPrimaryBrush);
            Assert.Same(Brushes.Yellow, trend.LastPrimaryBrush);

            var enabledDonutRevision = donut.PaletteRevision;
            var enabledTrendRevision = trend.PaletteRevision;
            HighContrastPalette.Apply(window.Resources, false, window.FindResource);
            Assert.True(donut.PaletteRevision > enabledDonutRevision);
            Assert.True(trend.PaletteRevision > enabledTrendRevision);
        });

    [Fact]
    public Task Custom_controls_render_geometry_directly_without_shape_children() =>
        WpfTestHost.RunAsync(async () =>
        {
            var (window, vm) = await ShowAsync(Snapshot());
            var donut = Assert.IsType<DonutChartControl>(window.FindName("DonutChart"));
            var trend = Assert.IsType<TrendChartControl>(window.FindName("TrendChart"));

            Assert.Same(vm.DonutGeometry, donut.Geometry);
            Assert.Same(vm.TrendGeometry, trend.Geometry);
            Assert.Empty(Descendants<System.Windows.Shapes.Shape>(donut));
            Assert.Empty(Descendants<System.Windows.Shapes.Shape>(trend));
            Assert.NotEqual(0, RenderedPixelCount(donut));
            Assert.NotEqual(0, RenderedPixelCount(trend));
            Assert.NotEmpty(trend.LastBarBounds);
            Assert.All(trend.LastBarBounds, bounds =>
            {
                Assert.True(bounds.Left >= 10d - 0.001d);
                Assert.True(bounds.Right <= trend.ActualWidth - 10d + 0.001d);
                Assert.True(bounds.Width > 0);
            });
            Assert.Equal(trend.LastBarBounds[0].Width,
                trend.LastBarBounds[^1].Width, 6);
        });

    [Fact]
    public async Task Composition_builds_the_view_model_on_the_real_snapshot_service_boundary()
    {
        var query = new EmptyAnalyticsQuery();
        var time = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 9, 2, 0, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08-composed", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        var vm = CompositionRoot.ComposeAnalytics(
            query, time, zone, CultureInfo.GetCultureInfo("zh-CN"));
        var range = new LocalDateRange(
            new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 23));

        await vm.LoadRangeAsync(range);

        Assert.Equal(range, vm.Snapshot?.Range);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 9, 16, 0, 0, TimeSpan.Zero),
            query.Start);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 23, 16, 0, 0, TimeSpan.Zero),
            query.EndExclusive);
        Assert.True(query.IncludeDeleted);
    }

    [Fact]
    public Task Closing_the_window_cancels_the_active_load_and_the_view_model_can_reopen() =>
        WpfTestHost.RunAsync(async () =>
        {
            var first = new TaskCompletionSource<AnalyticsSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken firstToken = default;
            var calls = 0;
            var vm = Create((range, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstToken = ct;
                    return first.Task;
                }
                return Task.FromResult(Snapshot(completed: 8));
            });
            var firstWindow = new AnalyticsWindow { DataContext = vm };
            firstWindow.Show();
            var stale = vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);

            firstWindow.Close();
            Assert.True(firstToken.IsCancellationRequested);
            first.SetResult(Snapshot(completed: 1));
            await stale;
            Assert.Null(vm.Snapshot);

            var secondWindow = new AnalyticsWindow { DataContext = vm };
            secondWindow.Show();
            await vm.SelectRangeAsync(AnalyticsRangeKind.Recent30Days);

            Assert.Equal(8, vm.Completed);
            secondWindow.Close();
        });

    [Fact]
    public Task Composition_lifetime_guard_rejects_disposed_and_cancelled_dispatches() =>
        WpfTestHost.RunAsync(() =>
        {
            using var lifetime = new CancellationTokenSource();
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            Assert.True(CompositionRoot.CanShowAnalytics(0, lifetime.Token, dispatcher));
            Assert.False(CompositionRoot.CanShowAnalytics(1, lifetime.Token, dispatcher));
            lifetime.Cancel();
            Assert.False(CompositionRoot.CanShowAnalytics(0, lifetime.Token, dispatcher));
            return Task.CompletedTask;
        });

    private static int RenderedPixelCount(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)element.ActualWidth),
            Math.Max(1, (int)element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Count(static value => value != 0);
    }

    private static async Task<(AnalyticsWindow Window, AnalyticsViewModel ViewModel)> ShowAsync(
        AnalyticsSnapshot snapshot)
    {
        var vm = Create((range, ct) => Task.FromResult(snapshot));
        var window = await ShowAsync(vm);
        return (window, vm);
    }

    private static async Task<AnalyticsWindow> ShowAsync(AnalyticsViewModel vm)
    {
        var window = new AnalyticsWindow { DataContext = vm };
        window.Show();
        await vm.SelectRangeAsync(AnalyticsRangeKind.Recent7Days);
        window.UpdateLayout();
        return window;
    }

    private static AnalyticsViewModel Create(
        Func<LocalDateRange, CancellationToken, Task<AnalyticsSnapshot>> loader) =>
        new(loader, new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 9, 2, 0, 0, TimeSpan.Zero)),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-window", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
            CultureInfo.GetCultureInfo("zh-CN"));

    private static AnalyticsSnapshot Snapshot(
        int active = 7,
        int completed = 4,
        int future = 3,
        int overdue = 1)
    {
        var status = active == 0
            ? new[]
            {
                new DistributionSlice("completed", "已完成", 0),
                new DistributionSlice("incomplete", "未完成", 0),
                new DistributionSlice("overdue", "已逾期", 0)
            }
            : new[]
            {
                new DistributionSlice("completed", "已完成", completed),
                new DistributionSlice("incomplete", "未完成", 2),
                new DistributionSlice("overdue", "已逾期", overdue)
            };
        return new AnalyticsSnapshot(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8)),
            Range,
            "UTC+08-window",
            new AnalyticsTotals(active, completed, future, overdue, 0, 5, 2, 1),
            status,
            [
                new DistributionSlice("todo", "待办", active == 0 ? 0 : 5),
                new DistributionSlice("reminder", "提醒", active == 0 ? 0 : 2)
            ],
            [
                new DistributionSlice("normal", "普通", active == 0 ? 0 : 5),
                new DistributionSlice("important", "重要", active == 0 ? 0 : 2)
            ],
            [
                new TrendBucket(Range.Start, Range.Start, "08-03", active == 0 ? 0 : 1),
                new TrendBucket(Range.End, Range.End, "08-09", active == 0 ? 0 : completed)
            ],
            []);
    }

    private static string PeerName(FrameworkElement element) =>
        (FrameworkElementAutomationPeer.CreatePeerForElement(element)
         ?? throw new InvalidOperationException("UIA peer missing.")).GetName();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class EmptyAnalyticsQuery : IAnalyticsQuery
    {
        public DateTimeOffset Start { get; private set; }
        public DateTimeOffset EndExclusive { get; private set; }
        public bool IncludeDeleted { get; private set; }

        public Task<AnalyticsHistory> ReadAsync(
            DateTimeOffset utcStartInclusive,
            DateTimeOffset utcEndExclusive,
            bool includeDeleted,
            CancellationToken ct)
        {
            Start = utcStartInclusive;
            EndExclusive = utcEndExclusive;
            IncludeDeleted = includeDeleted;
            return Task.FromResult(AnalyticsHistory.Empty);
        }
    }
}
