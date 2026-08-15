using System.Globalization;
using System.Collections.Immutable;
using System.Windows.Markup;
using Hourbit.App.Commands;
using Hourbit.App.Localization;
using Hourbit.Core.Analytics;
using Hourbit.Core.Reporting;

namespace Hourbit.App.Analytics;

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
public sealed record AnalyticsLegendItem(
    string Label,
    int Value,
    string Percentage,
    string ColorKey,
    string AccessibleName,
    string MarkerAccessibleName);

public sealed class AnalyticsViewModel : ObservableObject, IDisposable
{
    private readonly Func<LocalDateRange, CancellationToken, Task<AnalyticsSnapshot>> _loadSnapshot;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly ILocalizationService _localization;
    private readonly ReportExportService? _exportService;
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
    private ImmutableArray<AnalyticsLegendItem> _legendItems = [];
    private int _disposed;

    public AnalyticsViewModel(
        Func<LocalDateRange, CancellationToken, Task<AnalyticsSnapshot>> loadSnapshot,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? zone = null,
        CultureInfo? culture = null,
        ILocalizationService? localization = null,
        ReportExportService? exportService = null)
    {
        _loadSnapshot = loadSnapshot ?? throw new ArgumentNullException(nameof(loadSnapshot));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _localization = localization ?? new LocalizationService(_culture, null);
        _exportService = exportService;
        _localization.LanguageChanged += OnLanguageChanged;
        _donutSummary = Translate("Analytics.NoDistribution");
        _trendSummary = Translate("Analytics.NoTrend");
        var today = LocalToday;
        _selectedYear = today.Year;
        _customStart = today.AddDays(-6);
        _customEnd = today;
        RefreshCommand = new AsyncCommand((_, _) => SelectRangeAsync(SelectedRangeKind));
        ApplyCustomRangeCommand = new AsyncCommand((_, _) => ApplyCustomRangeAsync());
    }

    public IReadOnlyList<AnalyticsRangeOption> RangeOptions =>
    [
        new(AnalyticsRangeKind.Recent7Days, Translate("Analytics.Range.Recent7Days")),
        new(AnalyticsRangeKind.Recent30Days, Translate("Analytics.Range.Recent30Days")),
        new(AnalyticsRangeKind.CurrentMonth, Translate("Analytics.Range.CurrentMonth")),
        new(AnalyticsRangeKind.CalendarYear, Translate("Analytics.Range.CalendarYear")),
        new(AnalyticsRangeKind.Custom, Translate("Analytics.Range.Custom"))
    ];

    public IReadOnlyList<DonutDimensionOption> DimensionOptions =>
    [
        new(DonutDimension.Status, Translate("Analytics.Dimension.Status")),
        new(DonutDimension.ItemType, Translate("Analytics.Dimension.ItemType")),
        new(DonutDimension.Importance, Translate("Analytics.Dimension.Importance"))
    ];

    public string WindowTitle => Translate("Analytics.WindowTitle");
    public string TitleText => Translate("Analytics.Title");
    public string RangeText => Translate("Analytics.Range");
    public string YearText => Translate("Analytics.Year");
    public string FromText => Translate("Analytics.From");
    public string ToText => Translate("Analytics.To");
    public string ApplyText => Translate("Analytics.Apply");
    public string ExportText => Translate("Analytics.Export");
    public XmlLanguage UiLanguageTag => XmlLanguage.GetLanguage(_localization.LanguageTag);
    public string LoadingText => Translate("Analytics.Loading");
    public string CompletedText => Translate("Analytics.Completed");
    public string FuturePlannedText => Translate("Analytics.Future");
    public string OverdueText => Translate("Analytics.Overdue");
    public string DistributionText => Translate("Analytics.Distribution");
    public string TrendText => Translate("Analytics.Trend");
    public string DimensionText => Translate("Analytics.Dimension");
    public string LegendText => Translate("Analytics.Legend");
    public string CompletedAccessibleName => $"{CompletedText} {Completed}";
    public string FutureAccessibleName => $"{FuturePlannedText} {FuturePlanned}";
    public string OverdueAccessibleName => $"{OverdueText} {Overdue}";

    public IAsyncCommand RefreshCommand { get; }
    public IAsyncCommand ApplyCustomRangeCommand { get; }

    public AnalyticsSnapshot? Snapshot => _snapshot;
    public bool HasSnapshot => _snapshot is not null;
    public bool HasNoData => _snapshot is not null && _snapshot.Totals.Active == 0;
    public string EmptyStateMessage => HasNoData
        ? Translate("Analytics.Empty")
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
        private set
        {
            if (SetProperty(ref _completed, value))
                OnPropertyChanged(nameof(CompletedAccessibleName));
        }
    }

    public int FuturePlanned
    {
        get => _futurePlanned;
        private set
        {
            if (SetProperty(ref _futurePlanned, value))
                OnPropertyChanged(nameof(FutureAccessibleName));
        }
    }

    public int Overdue
    {
        get => _overdue;
        private set
        {
            if (SetProperty(ref _overdue, value))
                OnPropertyChanged(nameof(OverdueAccessibleName));
        }
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

    public ImmutableArray<AnalyticsLegendItem> LegendItems
    {
        get => _legendItems;
        private set => SetProperty(ref _legendItems, value);
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
        ThrowIfDisposed();
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
        CustomStart = range.Start;
        CustomEnd = range.End;
        return LoadCoreAsync(range);
    }

    public Task ApplyCustomRangeAsync()
    {
        ThrowIfDisposed();
        SelectedRangeKind = AnalyticsRangeKind.Custom;
        if (CustomStart > CustomEnd)
        {
            RejectRange(Translate("Analytics.InvalidRange"));
            return Task.CompletedTask;
        }
        return LoadCoreAsync(new LocalDateRange(CustomStart, CustomEnd));
    }

    public Task LoadRangeAsync(LocalDateRange range)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(range);
        SelectedRangeKind = AnalyticsRangeKind.Custom;
        CustomStart = range.Start;
        CustomEnd = range.End;
        return ApplyCustomRangeAsync();
    }

    public async Task<IReadOnlyList<string>> ExportReportAsync(
        ReportPrivacyMode privacy,
        string basePath,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        var exportService = _exportService ??
            throw new InvalidOperationException("报告导出未配置。");
        if (_snapshot is null)
            throw new InvalidOperationException("没有可导出的报告。");

        return await exportService.ExportAsync(_snapshot, privacy, basePath, ct);
    }

    public void SelectDimension(DonutDimension dimension) =>
        SelectedDimension = dimension;

    public void CancelActiveLoad()
    {
        Interlocked.Increment(ref _generation);
        var previous = Interlocked.Exchange(ref _loadCancellation, null);
        previous?.Cancel();
        previous?.Dispose();
        IsLoading = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _localization.LanguageChanged -= OnLanguageChanged;
        CancelActiveLoad();
    }

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
        ThrowIfDisposed();
        if (range.Start > range.End)
        {
            RejectRange(Translate("Analytics.InvalidRange"));
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
            if (Volatile.Read(ref _disposed) != 0 ||
                generation != Volatile.Read(ref _generation))
                return;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                generation == Volatile.Read(ref _generation))
                ErrorMessage = exception.Message;
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                generation == Volatile.Read(ref _generation))
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
        UpdateTrendSummary();
        DeriveDonut();
    }

    private void DeriveDonut()
    {
        if (_snapshot is null)
            return;
        var (label, slices) = SelectedDimension switch
        {
            DonutDimension.Status => (Translate("Analytics.Dimension.Status"), _snapshot.Status),
            DonutDimension.ItemType => (Translate("Analytics.Dimension.ItemType"), _snapshot.ItemTypes),
            DonutDimension.Importance => (Translate("Analytics.Dimension.Importance"), _snapshot.Importance),
            _ => throw new ArgumentOutOfRangeException()
        };
        var localizedSlices = slices.Select(slice => new DistributionSlice(
            slice.Key,
            Translate($"Analytics.Slice.{slice.Key}"),
            slice.Count)).ToImmutableArray();
        DonutGeometry = ChartGeometryBuilder.CreateDonut(localizedSlices);
        LegendItems = DonutGeometry.Sectors.Select(sector =>
        {
            var percentage = DonutGeometry.Total == 0
                ? 0d
                : (double)sector.Value / DonutGeometry.Total;
            var percentageText = percentage.ToString("P0", CurrentUiCulture);
            return new AnalyticsLegendItem(
                sector.Label,
                sector.Value,
                percentageText,
                sector.ColorKey,
                IsEnglish
                    ? $"{sector.Label} {sector.Value}, {percentageText}"
                    : $"{sector.Label} {sector.Value}，占 {percentageText}",
                $"{Translate("Analytics.LegendMarker")} {sector.Label}");
        }).ToImmutableArray();
        DonutSummary = localizedSlices.IsEmpty
            ? IsEnglish
                ? $"{label} distribution: {Translate("Analytics.NoDistribution")}"
                : $"{label}分布：{Translate("Analytics.NoDistribution")}"
            : IsEnglish
                ? $"{label} distribution: {string.Join(", ", localizedSlices.Select(static slice => $"{slice.Label} {slice.Count}"))}."
                : $"{label}分布：{string.Join("，", localizedSlices.Select(static slice => $"{slice.Label} {slice.Count}"))}。";
    }

    private void UpdateTrendSummary()
    {
        if (_snapshot is null || _snapshot.Trend.IsEmpty)
        {
            TrendSummary = Translate("Analytics.NoTrend");
            return;
        }

        var completed = _snapshot.Trend.Sum(static bucket => bucket.Completed);
        var peak = _snapshot.Trend.Max(static bucket => bucket.Completed);
        TrendSummary = IsEnglish
            ? $"Completion trend: {completed} completed, peak {peak}."
            : $"完成趋势：共完成 {completed}，最高 {peak}。";
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        foreach (var propertyName in new[]
                 {
                     nameof(WindowTitle), nameof(TitleText), nameof(RangeText),
                     nameof(YearText), nameof(FromText), nameof(ToText),
                     nameof(ApplyText), nameof(ExportText), nameof(UiLanguageTag),
                     nameof(LoadingText),
                     nameof(CompletedText),
                     nameof(FuturePlannedText), nameof(OverdueText),
                     nameof(DistributionText), nameof(TrendText), nameof(DimensionText),
                     nameof(LegendText),
                     nameof(CompletedAccessibleName), nameof(FutureAccessibleName),
                     nameof(OverdueAccessibleName),
                     nameof(RangeOptions), nameof(DimensionOptions), nameof(EmptyStateMessage)
                 })
        {
            OnPropertyChanged(propertyName);
        }

        if (_snapshot is null)
        {
            DonutSummary = Translate("Analytics.NoDistribution");
            TrendSummary = Translate("Analytics.NoTrend");
            return;
        }

        RangeLabel = $"{FormatDate(_snapshot.Range.Start)} — {FormatDate(_snapshot.Range.End)}";
        UpdateTrendSummary();
        DeriveDonut();
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
        value.ToString("yyyy-MM-dd", CurrentUiCulture);

    private bool IsEnglish => _localization.CurrentLanguage == UiLanguage.EnUs;

    private CultureInfo CurrentUiCulture => _localization.CurrentCulture;

    private string Translate(string key) => _localization.Translate(key);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
