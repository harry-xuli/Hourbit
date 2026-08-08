using System.Globalization;
using Moment.App.Commands;
using Moment.Core.Analytics;

namespace Moment.App.Analytics;

public enum AnalyticsRangeKind
{
    Recent7Days,
    Recent30Days,
    CurrentMonth,
    CalendarYear,
    Custom
}

public enum DonutDimension
{
    Status,
    ItemType,
    Importance
}

public sealed record AnalyticsRangeOption(AnalyticsRangeKind Kind, string Label);
public sealed record DonutDimensionOption(DonutDimension Dimension, string Label);

public sealed class AnalyticsViewModel : ObservableObject
{
    private readonly Func<LocalDateRange, CancellationToken, Task<AnalyticsSnapshot>> _loadSnapshot;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private CancellationTokenSource? _loadCancellation;
    private long _generation;
    private AnalyticsSnapshot? _snapshot;
    private AnalyticsRangeKind _selectedRangeKind = AnalyticsRangeKind.Recent7Days;
    private DonutDimension _selectedDimension;
    private int _selectedYear;
    private DateOnly _customStart;
    private DateOnly _customEnd;
    private int _completed;
    private int _futurePlanned;
    private int _overdue;
    private string _rangeLabel = string.Empty;
    private string _donutSummary = "暂无分布数据。";
    private string _trendSummary = "暂无完成趋势数据。";
    private string? _errorMessage;
    private bool _isLoading;
    private DonutGeometry _donutGeometry = DonutGeometry.Empty;
    private TrendGeometry _trendGeometry = TrendGeometry.Empty;

    public AnalyticsViewModel(
        Func<LocalDateRange, CancellationToken, Task<AnalyticsSnapshot>> loadSnapshot,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? zone = null,
        CultureInfo? culture = null)
    {
        _loadSnapshot = loadSnapshot ?? throw new ArgumentNullException(nameof(loadSnapshot));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _culture = culture ?? CultureInfo.CurrentCulture;
        var today = LocalToday;
        _selectedYear = today.Year;
        _customStart = today.AddDays(-6);
        _customEnd = today;
        RefreshCommand = new AsyncCommand((_, _) => SelectRangeAsync(SelectedRangeKind));
        ApplyCustomRangeCommand = new AsyncCommand((_, _) => ApplyCustomRangeAsync());
    }

    public IReadOnlyList<AnalyticsRangeOption> RangeOptions { get; } =
    [
        new(AnalyticsRangeKind.Recent7Days, "最近 7 天"),
        new(AnalyticsRangeKind.Recent30Days, "最近 30 天"),
        new(AnalyticsRangeKind.CurrentMonth, "本月"),
        new(AnalyticsRangeKind.CalendarYear, "年份"),
        new(AnalyticsRangeKind.Custom, "自定义")
    ];

    public IReadOnlyList<DonutDimensionOption> DimensionOptions { get; } =
    [
        new(DonutDimension.Status, "状态"),
        new(DonutDimension.ItemType, "待办 / 提醒"),
        new(DonutDimension.Importance, "重要性")
    ];

    public IAsyncCommand RefreshCommand { get; }
    public IAsyncCommand ApplyCustomRangeCommand { get; }

    public AnalyticsSnapshot? Snapshot => _snapshot;
    public bool HasSnapshot => _snapshot is not null;
    public bool HasNoData => _snapshot is not null && _snapshot.Totals.Active == 0;
    public string EmptyStateMessage => HasNoData
        ? "这个日期范围内还没有可分析的记录。"
        : string.Empty;

    public AnalyticsRangeKind SelectedRangeKind
    {
        get => _selectedRangeKind;
        set
        {
            if (SetProperty(ref _selectedRangeKind, value))
                OnPropertyChanged(nameof(IsCustomRange));
        }
    }

    public bool IsCustomRange => SelectedRangeKind == AnalyticsRangeKind.Custom;

    public DonutDimension SelectedDimension
    {
        get => _selectedDimension;
        set
        {
            if (SetProperty(ref _selectedDimension, value))
                DeriveDonut();
        }
    }

    public int SelectedYear
    {
        get => _selectedYear;
        set => SetProperty(ref _selectedYear, value);
    }

    public DateOnly CustomStart
    {
        get => _customStart;
        set
        {
            if (SetProperty(ref _customStart, value))
                OnPropertyChanged(nameof(CustomStartDate));
        }
    }

    public DateOnly CustomEnd
    {
        get => _customEnd;
        set
        {
            if (SetProperty(ref _customEnd, value))
                OnPropertyChanged(nameof(CustomEndDate));
        }
    }

    public DateTime? CustomStartDate
    {
        get => CustomStart.ToDateTime(TimeOnly.MinValue);
        set
        {
            if (value is not null)
                CustomStart = DateOnly.FromDateTime(value.Value);
        }
    }

    public DateTime? CustomEndDate
    {
        get => CustomEnd.ToDateTime(TimeOnly.MinValue);
        set
        {
            if (value is not null)
                CustomEnd = DateOnly.FromDateTime(value.Value);
        }
    }

    public int Completed
    {
        get => _completed;
        private set => SetProperty(ref _completed, value);
    }

    public int FuturePlanned
    {
        get => _futurePlanned;
        private set => SetProperty(ref _futurePlanned, value);
    }

    public int Overdue
    {
        get => _overdue;
        private set => SetProperty(ref _overdue, value);
    }

    public string RangeLabel
    {
        get => _rangeLabel;
        private set => SetProperty(ref _rangeLabel, value);
    }

    public DonutGeometry DonutGeometry
    {
        get => _donutGeometry;
        private set => SetProperty(ref _donutGeometry, value);
    }

    public TrendGeometry TrendGeometry
    {
        get => _trendGeometry;
        private set => SetProperty(ref _trendGeometry, value);
    }

    public string DonutSummary
    {
        get => _donutSummary;
        private set => SetProperty(ref _donutSummary, value);
    }

    public string TrendSummary
    {
        get => _trendSummary;
        private set => SetProperty(ref _trendSummary, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public Task SelectRangeAsync(AnalyticsRangeKind kind)
    {
        SelectedRangeKind = kind;
        if (kind == AnalyticsRangeKind.Custom)
            return ApplyCustomRangeAsync();

        LocalDateRange range;
        try
        {
            range = CreateRange(kind);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            RejectRange(exception.Message);
            return Task.CompletedTask;
        }
        return LoadCoreAsync(range);
    }

    public Task ApplyCustomRangeAsync()
    {
        SelectedRangeKind = AnalyticsRangeKind.Custom;
        if (CustomStart > CustomEnd)
        {
            RejectRange("开始日期不能晚于结束日期。");
            return Task.CompletedTask;
        }
        return LoadCoreAsync(new LocalDateRange(CustomStart, CustomEnd));
    }

    public Task LoadRangeAsync(LocalDateRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        SelectedRangeKind = AnalyticsRangeKind.Custom;
        CustomStart = range.Start;
        CustomEnd = range.End;
        return ApplyCustomRangeAsync();
    }

    public void SelectDimension(DonutDimension dimension) =>
        SelectedDimension = dimension;

    private LocalDateRange CreateRange(AnalyticsRangeKind kind)
    {
        var today = LocalToday;
        return kind switch
        {
            AnalyticsRangeKind.Recent7Days => new(today.AddDays(-6), today),
            AnalyticsRangeKind.Recent30Days => new(today.AddDays(-29), today),
            AnalyticsRangeKind.CurrentMonth => new(
                new DateOnly(today.Year, today.Month, 1),
                new DateOnly(today.Year, today.Month,
                    DateTime.DaysInMonth(today.Year, today.Month))),
            AnalyticsRangeKind.CalendarYear => new(
                new DateOnly(SelectedYear, 1, 1),
                new DateOnly(SelectedYear, 12, 31)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private DateOnly LocalToday => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _zone).DateTime);

    private async Task LoadCoreAsync(LocalDateRange range)
    {
        if (range.Start > range.End)
        {
            RejectRange("开始日期不能晚于结束日期。");
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var snapshot = await _loadSnapshot(range, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _generation))
                return;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _generation))
                ErrorMessage = exception.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
                IsLoading = false;
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _loadCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void ApplySnapshot(AnalyticsSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(HasNoData));
        OnPropertyChanged(nameof(EmptyStateMessage));
        Completed = snapshot.Totals.Completed;
        FuturePlanned = snapshot.Totals.FuturePlanned;
        Overdue = snapshot.Totals.Overdue;
        RangeLabel = $"{FormatDate(snapshot.Range.Start)} — {FormatDate(snapshot.Range.End)}";
        TrendGeometry = ChartGeometryBuilder.CreateTrend(snapshot.Trend);
        TrendSummary = snapshot.Trend.IsEmpty
            ? "暂无完成趋势数据。"
            : $"完成趋势：共完成 {snapshot.Trend.Sum(static bucket => bucket.Completed)}，" +
              $"最高 {snapshot.Trend.Max(static bucket => bucket.Completed)}。";
        DeriveDonut();
    }

    private void DeriveDonut()
    {
        if (_snapshot is null)
            return;
        var (label, slices) = SelectedDimension switch
        {
            DonutDimension.Status => ("状态", _snapshot.Status),
            DonutDimension.ItemType => ("类型", _snapshot.ItemTypes),
            DonutDimension.Importance => ("重要性", _snapshot.Importance),
            _ => throw new ArgumentOutOfRangeException()
        };
        DonutGeometry = ChartGeometryBuilder.CreateDonut(slices);
        DonutSummary = slices.IsEmpty
            ? $"{label}分布：暂无数据。"
            : $"{label}分布：{string.Join("，", slices.Select(static slice => $"{slice.Label} {slice.Count}"))}。";
    }

    private void RejectRange(string message)
    {
        Interlocked.Increment(ref _generation);
        var previous = Interlocked.Exchange(ref _loadCancellation, null);
        previous?.Cancel();
        previous?.Dispose();
        IsLoading = false;
        ErrorMessage = message;
    }

    private string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", _culture);
}
