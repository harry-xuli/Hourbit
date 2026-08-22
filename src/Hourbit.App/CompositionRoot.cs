using Hourbit.App.Alerts;
using Hourbit.App.Analytics;
using Hourbit.App.Help;
using Hourbit.App.Localization;
using Hourbit.App.Search;
using Hourbit.App.QuickAdd;
using Hourbit.App.Settings;
using Hourbit.App.Shell;
using Hourbit.App.Timeline;
using Hourbit.App.Startup;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Analytics;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Recurrence;
using Hourbit.Core.Scheduling;
using Hourbit.Core.Services;
using Hourbit.Infrastructure.Data;
using Hourbit.Infrastructure.Backup;
using Hourbit.Windows.Alerts;
using Hourbit.Windows.Hotkeys;
using Hourbit.Windows.Lifecycle;
using Hourbit.Windows.Notifications;
using Hourbit.Windows.Startup;
using System.IO;
using System.Globalization;
using System.Reflection;

namespace Hourbit.App;

public sealed class CompositionRoot : IAsyncDisposable
{
    private readonly SqliteReminderRepository _repository;
    private readonly ReminderScheduler _scheduler;
    private readonly TimelineRefreshCoordinator _timelineRefresh;
    private readonly ReminderRecoveryCoordinator _reminderRecovery;
    private readonly ImportantAlertController _importantAlerts;
    private readonly WindowsNotificationRuntime _notificationRuntime;
    private readonly SystemResumeMonitor _resumeMonitor;
    private readonly GlobalHotkeyService _hotkey;
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly TrayIconController _tray;
    private readonly HelpWindowController _helpWindow;
    private readonly IReminderService _reminderService;
    private readonly IClock _clock;
    private readonly AppNotificationSink _notificationSink;
    private readonly ImportantAlertWindowPresenter _importantAlertPresenter;
    private readonly WindowPlacementService _windowPlacement;
    private readonly string _dataFolder;
    private readonly CancellationTokenSource _lifetime;
    private readonly EventHandler _schedulerStateChanged;
    private readonly IDisposable _runtimeFailureReporting;
    private SettingsView? _settingsWindow;
    private AnalyticsWindow? _analyticsWindow;
    private bool _started;
    private int _disposed;

    private CompositionRoot(
        SqliteReminderRepository repository,
        ReminderScheduler scheduler,
        ReminderRecoveryService reminderRecoveryService,
        TimelineRefreshCoordinator timelineRefresh,
        ReminderRecoveryCoordinator reminderRecovery,
        ImportantAlertController importantAlerts,
        WindowsNotificationRuntime notificationRuntime,
        SystemResumeMonitor resumeMonitor,
        GlobalHotkeyService hotkey,
        SingleInstanceCoordinator singleInstance,
        TrayIconController tray,
        HelpWindowController helpWindow,
        IReminderService reminderService,
        IClock clock,
        AppNotificationSink notificationSink,
        ImportantAlertWindowPresenter importantAlertPresenter,
        WindowPlacementService windowPlacement,
        CancellationTokenSource lifetime,
        string dataFolder,
        SettingsViewModel settings,
        TimelineViewModel timeline,
        AnalyticsViewModel analytics,
        QuickAddViewModel quickAdd,
        MainWindow mainWindow,
        QuickAddWindowController quickAddWindow)
    {
        _repository = repository;
        _scheduler = scheduler;
        _timelineRefresh = timelineRefresh;
        _reminderRecovery = reminderRecovery;
        _importantAlerts = importantAlerts;
        _notificationRuntime = notificationRuntime;
        _resumeMonitor = resumeMonitor;
        _hotkey = hotkey;
        _singleInstance = singleInstance;
        _tray = tray;
        _helpWindow = helpWindow;
        _reminderService = reminderService;
        _clock = clock;
        _notificationSink = notificationSink;
        _importantAlertPresenter = importantAlertPresenter;
        _windowPlacement = windowPlacement;
        _lifetime = lifetime;
        _schedulerStateChanged = ComposeTimelineRefreshHandler(
            timelineRefresh, OnRuntimeError, lifetime.Token);
        _runtimeFailureReporting = ConnectRuntimeFailureReporting(
            reminderRecoveryService, importantAlerts, OnRuntimeError);
        _dataFolder = dataFolder;
        Settings = settings;
        Timeline = timeline;
        Analytics = analytics;
        QuickAdd = quickAdd;
        MainWindow = mainWindow;
        QuickAddWindow = quickAddWindow;
    }

    public TimelineViewModel Timeline { get; }
    public AnalyticsViewModel Analytics { get; }
    public QuickAddViewModel QuickAdd { get; }
    public SettingsViewModel Settings { get; }
    public MainWindow MainWindow { get; }
    public QuickAddWindowController QuickAddWindow { get; }
    public event Action<Exception>? RuntimeError;

    public static async Task<CompositionRoot?> OpenAsync(CancellationToken ct)
    {
        var clock = new SystemClock();
        var zone = TimeZoneInfo.Local;
        var databasePath = DatabasePathResolver.Resolve(AppContext.BaseDirectory);
        try
        {
            var dataResetApplier = new DataResetApplier(
                new DataResetRequestStore(databasePath));
            await dataResetApplier.ApplyPendingAsync(databasePath, ct);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "本地数据重置失败，原有数据已保留：" + exception.Message,
                exception);
        }

        var dataFolder =
            Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
        var backupDirectory = Path.Combine(dataFolder, "backups");
        var recoveryService = new DatabaseRecoveryService(
            databasePath,
            backupDirectory,
            () => clock.Now.ToUniversalTime());
        var recovery = await recoveryService.OpenWithRecoveryAsync(ct);
        if (!await DatabaseRecoveryWorkflow.RunAsync(
                recoveryService,
                recovery,
                new WpfPreCompositionRecoveryDialog(),
                ct))
            return null;

        var repository = await SqliteReminderRepository.OpenAsync(databasePath, ct);
        var todoRepository = await SqliteTodoRepository.OpenAsync(databasePath, ct);
        var todoOrderStore = new SqliteTodoOrderStore(databasePath);
        var conversionStore = await SqliteItemConversionStore.OpenAsync(databasePath, ct);
        var settingsStore = new SqliteSettingsStore(databasePath);
        var hotkey = new GlobalHotkeyService();
        var executablePath = Environment.ProcessPath
            ?? Assembly.GetExecutingAssembly().Location;
        var restoreLifecycle = new ConfigurableBackupRestoreLifecycle();
        var backupService = new BackupService(
            databasePath,
            backupDirectory,
            restoreLifecycle,
            () => clock.Now.ToUniversalTime(),
            zone);
        var dataResetCoordinator = new DataResetCoordinator(
            backupService,
            new DataResetRequestStore(databasePath),
            databasePath,
            clock,
            () => System.Windows.Application.Current.Dispatcher.BeginInvoke(
                new Action(System.Windows.Application.Current.Shutdown)));
        var settings = new SettingsViewModel(
            hotkey,
            settingsStore,
            new StartupRegistrationService(),
            executablePath,
            backupService,
            ReleasePageService.FromAssembly(Assembly.GetExecutingAssembly()),
            dataResetCoordinator);
        await settings.LoadAsync(ct);
        var localization = new LocalizationService(
            CultureInfo.CurrentUICulture, settings.UiLanguage);
        LocalizationHub.Use(localization);
        await TryCreateDailyBackupAsync(backupService, settings, ct);

        var schedulerSignal = new SchedulerSignalProxy();
        var actionCompletedObserver = new ReminderActionCompletedObserverProxy();
        var recurrence = new RecurrenceCalculator();
        var actions = new ReminderActionService(repository, recurrence, schedulerSignal, clock, zone);
        var windowPlacement = new WindowPlacementService();
        var importantAlertPresenter = new ImportantAlertWindowPresenter(
            System.Windows.Application.Current.Dispatcher,
            windowPlacement,
            () => CreateAppAlertAudio(() => settings.AlertVolume),
            () => settings.CustomAlertSoundPath);
        var importantAlerts = ImportantAlertControllerFactory.CreatePresenterManaged(
            importantAlertPresenter, actions, actionCompletedObserver);
        var notificationPlatform = new WindowsAppNotificationPlatform();
        var notificationSink = new AppNotificationSink(notificationPlatform, importantAlerts, actions);
        var scheduler = new ReminderScheduler(
            repository, notificationSink, clock, recurrence, zone);
        schedulerSignal.Target = scheduler;
        var reminders = new ReminderService(repository, scheduler, clock);
        var todos = new TodoService(
            todoRepository,
            repository,
            conversionStore,
            recurrence,
            schedulerSignal,
            clock,
            zone);

        var parser = new ChineseTimeParser();
        QuickAddViewModel? quickAdd = null;
        var quickWindow = new QuickAddWindowController(
            () => new QuickAddWindow { DataContext = quickAdd! },
            System.Windows.Application.Current.Dispatcher);
        TimelineViewModel? timelineForDialogs = null;
        var dialogs = new TimelineDialogService(
            quickWindow.ShowAndFocus,
            zone,
            clock,
            reminders,
            todos,
            async refreshCancellation =>
            {
                refreshCancellation.ThrowIfCancellationRequested();
                var currentTimeline = timelineForDialogs ??
                    throw new InvalidOperationException("时间轴尚未初始化。");
                await currentTimeline.LoadAsync();
                refreshCancellation.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(currentTimeline.ErrorMessage))
                    throw new InvalidOperationException(currentTimeline.ErrorMessage);
            });
        var timelineQuery = new SqliteTimelineQuery(databasePath);
        var analytics = ComposeAnalytics(
            new SqliteAnalyticsQuery(databasePath),
            TimeProvider.System,
            zone,
            CultureInfo.CurrentCulture,
            localization);
        CompositionRoot? root = null;
        var helpWindow = new HelpWindowController(() => new HelpWindow
        {
            DataContext = new HelpContentViewModel(localization)
        });
        var datePicker = new WpfDatePicker(() => root?.MainWindow, localization);
        TimelineViewModel? timelineForSearch = null;
        var search = new SearchViewModel(
            new SqliteItemSearchQuery(databasePath),
            date => timelineForSearch?.NavigateToDateAsync(date)
                ?? Task.CompletedTask);
        var timeline = new TimelineViewModel(
            timelineQuery, clock, reminders, actions, todos,
            dialogs, dialogs, zone,
            CultureInfo.CurrentCulture,
            range => root?.ShowAnalytics(range),
            helpWindow.ShowAndFocus,
            () => root?.ShowAnalytics(),
            localization,
            async language =>
            {
                var result = await settings.SaveUiLanguageAsync(
                    language == UiLanguage.EnUs ? "en-US" : "zh-CN");
                if (!result.Succeeded)
                    throw new InvalidOperationException(result.ErrorMessage);
            },
            datePicker,
            search,
            todoOrderStore);
        timelineForSearch = timeline;
        timelineForDialogs = timeline;
        var timelineRefresh = new TimelineRefreshCoordinator(
            System.Windows.Application.Current.Dispatcher, timeline);
        actionCompletedObserver.Target = timelineRefresh;
        var lifetime = new CancellationTokenSource();
        var reminderRecoveryService = new ReminderRecoveryService(
            repository,
            notificationSink,
            new ReminderRecoverySummarySink(notificationSink),
            recurrence,
            zone);
        var reminderRecovery = new ReminderRecoveryCoordinator(
            scheduler,
            reminderRecoveryService,
            clock,
            timelineRefresh,
            lifetime.Token);
        restoreLifecycle.Configure(scheduler, timelineRefresh);
        quickAdd = ComposeQuickAdd(
            parser,
            reminders,
            todos,
            clock,
            zone,
            CultureInfo.CurrentCulture,
            timeline);
        var mainWindow = new MainWindow { DataContext = timeline };
        var navigator = new WindowNotificationNavigator(mainWindow);
        var notificationRuntime = new WindowsNotificationRuntime(
            actions, navigator, actionCompletedObserver);
        var resumeMonitor = new SystemResumeMonitor(
            (_, resumeCancellation) =>
                reminderRecovery.RecoverAndRefreshAsync(resumeCancellation));
        var singleInstance = new SingleInstanceCoordinator();

        var tray = TrayIconController.CreateWindows(
            async () => (await repository.GetScheduledAsync(CancellationToken.None)).Count > 0,
            localization,
            mainWindow.ShowAndActivate,
            quickWindow.ShowAndFocus,
            delay =>
            {
                if (root is not null)
                    _ = root.CreateCountdownObservedAsync(delay);
            },
            () => root?.ShowAnalytics(),
            helpWindow.ShowAndFocus,
            () => root?.ShowSettings(),
            () =>
            {
                if (root is not null)
                    root.RequestExit();
                return Task.CompletedTask;
            });

        root = new CompositionRoot(
            repository, scheduler, reminderRecoveryService,
            timelineRefresh, reminderRecovery,
            importantAlerts, notificationRuntime,
            resumeMonitor, hotkey, singleInstance, tray, helpWindow, reminders, clock,
            notificationSink, importantAlertPresenter,
            windowPlacement, lifetime,
            dataFolder,
            settings, timeline, analytics, quickAdd, mainWindow, quickWindow);
        return root;
    }

    internal static QuickAddViewModel ComposeQuickAdd(
        IChineseTimeParser parser,
        IReminderService reminders,
        ITodoService todos,
        IClock clock,
        TimeZoneInfo zone,
        CultureInfo culture,
        TimelineViewModel timeline) =>
        new(parser, reminders, todos, clock, zone, culture, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            await timeline.LoadAsync();
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(timeline.ErrorMessage))
                throw new InvalidOperationException(timeline.ErrorMessage);
        });

    internal static AnalyticsViewModel ComposeAnalytics(
        IAnalyticsQuery query,
        TimeProvider timeProvider,
        TimeZoneInfo zone,
        CultureInfo culture,
        ILocalizationService? localization = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        var service = new AnalyticsQueryService(query, timeProvider, culture);
        var metadata = ProductMetadata.FromAssembly(
            typeof(AnalyticsViewModel).Assembly);
        var exportService = new ReportExportService(
            metadata.ProductName, metadata.Version);
        return new AnalyticsViewModel(
            (range, ct) => service.CreateSnapshotAsync(range, zone, ct),
            timeProvider,
            zone,
            culture,
            localization,
            exportService);
    }

    internal static async Task TryCreateDailyBackupAsync(
        IBackupService backupService,
        SettingsViewModel settings,
        CancellationToken ct)
    {
        try
        {
            _ = await backupService.CreateDailyBackupAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            settings.ReportAutomaticBackupFailure(exception);
        }
    }

    public async Task<bool> StartAsync(InstanceActivation activation, CancellationToken ct)
    {
        if (_started)
            return true;
        var result = await _singleInstance.StartAsync(activation, ct);
        if (result != SingleInstanceResult.Primary)
            return false;

        _singleInstance.ActivationReceived += OnActivationReceivedAsync;
        _hotkey.Pressed += OnHotkeyPressed;
        _scheduler.DeliveryFailed += OnDeliveryFailed;
        _scheduler.StateChanged += _schedulerStateChanged;
        _resumeMonitor.RecoveryFailed += OnRuntimeError;
        _tray.ErrorOccurred += OnRuntimeError;

        await RecoverBeforeStartingRuntimeAsync(
            _reminderRecovery,
            () =>
            {
                TryStart(_notificationRuntime.Start);
                TryStart(_resumeMonitor.Start);
            },
            ct);
        try
        {
            await Settings.SaveHotkeyAsync(Settings.Hotkey, ct);
        }
        catch (Exception exception)
        {
            OnRuntimeError(exception);
        }
        if (activation.Kind == InstanceActivationKind.ShowQuickAdd)
            QuickAddWindow.ShowAndFocus();
        else
            MainWindow.ShowAndActivate();
        _started = true;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _singleInstance.ActivationReceived -= OnActivationReceivedAsync;
        _hotkey.Pressed -= OnHotkeyPressed;
        _scheduler.DeliveryFailed -= OnDeliveryFailed;
        _scheduler.StateChanged -= _schedulerStateChanged;
        _resumeMonitor.RecoveryFailed -= OnRuntimeError;
        _tray.ErrorOccurred -= OnRuntimeError;
        _lifetime.Cancel();
        Analytics.Dispose();
        _tray.Dispose();
        _helpWindow.Dispose();
        _analyticsWindow?.Close();
        _settingsWindow?.Close();
        _hotkey.Dispose();
        await _resumeMonitor.DisposeAsync();
        await _reminderRecovery.DisposeAsync();
        await _notificationRuntime.DisposeAsync();
        await _importantAlerts.DisposeAsync();
        try
        {
            await _timelineRefresh.DisposeAsync();
        }
        catch (Exception exception)
        {
            try
            {
                OnRuntimeError(exception);
            }
            catch
            {
                // Refresh disposal must not prevent the remaining runtime cleanup.
            }
        }
        await _singleInstance.DisposeAsync();
        _scheduler.Dispose();
        _runtimeFailureReporting.Dispose();
        _lifetime.Dispose();
    }

    private Task OnActivationReceivedAsync(InstanceActivation activation)
    {
        return MainWindow.Dispatcher.InvokeAsync(() =>
        {
            if (activation.Kind == InstanceActivationKind.ShowQuickAdd)
                QuickAddWindow.ShowAndFocus();
            else
                MainWindow.ShowAndActivate();
        }).Task;
    }

    private void OnHotkeyPressed(object? sender, EventArgs eventArgs) =>
        QuickAddWindow.ShowAndFocus();

    private void OnDeliveryFailed(SchedulerDeliveryFailure failure) => OnRuntimeError(failure.Exception);

    internal static async Task RecoverBeforeStartingRuntimeAsync(
        ReminderRecoveryCoordinator reminderRecovery,
        Action startRuntimeSignals,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reminderRecovery);
        ArgumentNullException.ThrowIfNull(startRuntimeSignals);
        await reminderRecovery.RecoverAndRefreshAsync(ct);
        startRuntimeSignals();
    }

    internal static EventHandler ComposeTimelineRefreshHandler(
        TimelineRefreshCoordinator timelineRefresh,
        Action<Exception> reportError,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(timelineRefresh);
        ArgumentNullException.ThrowIfNull(reportError);
        return (_, _) =>
            _ = ObserveTimelineRefreshAsync(timelineRefresh, reportError, lifetime);
    }

    internal static IDisposable ConnectRuntimeFailureReporting(
        ReminderRecoveryService reminderRecovery,
        ImportantAlertController importantAlerts,
        Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(reminderRecovery);
        ArgumentNullException.ThrowIfNull(importantAlerts);
        ArgumentNullException.ThrowIfNull(reportError);
        return new RuntimeFailureSubscription(
            reminderRecovery, importantAlerts, reportError);
    }

    private static async Task ObserveTimelineRefreshAsync(
        TimelineRefreshCoordinator timelineRefresh,
        Action<Exception> reportError,
        CancellationToken lifetime)
    {
        try
        {
            lifetime.ThrowIfCancellationRequested();
            await timelineRefresh.RequestAndDrainAsync(lifetime);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                reportError(exception);
            }
            catch
            {
                // Runtime error observers must not fault the scheduler callback.
            }
        }
    }

    private void OnRuntimeError(Exception exception) => RuntimeError?.Invoke(exception);

    private void TryStart(Action start)
    {
        try
        {
            start();
        }
        catch (Exception exception)
        {
            OnRuntimeError(exception);
        }
    }

    private async Task CreateCountdownObservedAsync(TimeSpan delay)
    {
        try
        {
            await _reminderService.CreateAsync(
                new ReminderDraft("倒计时", _clock.Now.Add(delay),
                    ReminderKind.Countdown, ReminderImportance.Normal, null),
                CancellationToken.None);
            await Timeline.LoadAsync();
        }
        catch (Exception exception)
        {
            OnRuntimeError(exception);
        }
    }

    private void RequestExit()
    {
        MainWindow.AllowExit();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new Action(System.Windows.Application.Current.Shutdown));
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }

        var testAlert = new ReminderAlert(
            Guid.NewGuid(),
            "这是一条重要提醒测试。",
            DateTimeOffset.Now);
        var view = new SettingsView(
            Settings,
            new SettingsViewActions(
                CreateAppAlertAudio(() => Settings.AlertVolume),
                _notificationSink.SendTestNotificationAsync,
                async ct =>
                {
                    _ = await _importantAlertPresenter.ShowAsync(testAlert, ct);
                },
                _dataFolder));
        view.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, view))
                _settingsWindow = null;
        };
        _settingsWindow = view;
        view.Show();
        _windowPlacement.Place(view);
        view.Activate();
    }

    public void ShowAnalytics() => ShowAnalyticsCore(null);

    private void ShowAnalytics(LocalDateRange range) => ShowAnalyticsCore(range);

    private void ShowAnalyticsCore(LocalDateRange? range)
    {
        if (!CanShowAnalytics(
                Volatile.Read(ref _disposed), _lifetime.Token, MainWindow.Dispatcher))
            return;

        if (!MainWindow.Dispatcher.CheckAccess())
        {
            _ = MainWindow.Dispatcher.BeginInvoke(
                new Action(() => ShowAnalyticsCore(range)));
            return;
        }

        if (_analyticsWindow is not { IsLoaded: true } window)
        {
            window = new AnalyticsWindow
            {
                DataContext = Analytics,
                Owner = MainWindow
            };
            window.Closed += (_, _) =>
            {
                Analytics.CancelActiveLoad();
                if (ReferenceEquals(_analyticsWindow, window))
                    _analyticsWindow = null;
            };
            _analyticsWindow = window;
        }

        window.ShowAndActivate();
        _ = range is null
            ? Analytics.SelectRangeAsync(Analytics.SelectedRangeKind)
            : Analytics.LoadRangeAsync(range);
    }

    internal static bool CanShowAnalytics(
        int disposed,
        CancellationToken lifetime,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return disposed == 0 &&
               !lifetime.IsCancellationRequested &&
               !dispatcher.HasShutdownStarted &&
               !dispatcher.HasShutdownFinished;
    }

    private static IImportantAlertAudio CreateAppAlertAudio(Func<int> volume) =>
        ImportantAlertControllerFactory.CreateAudio(
            new VolumeControlledLoopingAudioPlayer(
                new WindowsLoopingAudioPlayer(), volume),
            defaultWave: OpenAppDefaultAlertWave);

    private static Stream OpenAppDefaultAlertWave() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Hourbit.App.Assets.default-alert.wav")
        ?? throw new InvalidOperationException(
            "The embedded default-alert.wav resource is missing.");

    private sealed class SchedulerSignalProxy : ISchedulerSignal
    {
        public ISchedulerSignal? Target { get; set; }
        public void Refresh() => Target?.Refresh();
    }

    private sealed class ReminderActionCompletedObserverProxy :
        IReminderActionCompletedObserver
    {
        public TimelineRefreshCoordinator? Target { get; set; }

        public Task OnCompletedAsync(CancellationToken ct) =>
            Target?.RequestAsync(ct) ?? Task.CompletedTask;
    }

    internal sealed class ConfigurableBackupRestoreLifecycle :
        IBackupRestoreLifecycle
    {
        private ReminderScheduler? _scheduler;
        private TimelineRefreshCoordinator? _timelineRefresh;

        internal void Configure(
            ReminderScheduler scheduler,
            TimelineRefreshCoordinator timelineRefresh)
        {
            _scheduler = scheduler;
            _timelineRefresh = timelineRefresh;
        }

        public Task StopAsync(CancellationToken ct) =>
            Scheduler.StopAsync(ct);

        public Task StartAsync(CancellationToken ct) =>
            Scheduler.StartAsync(ct);

        public async Task RefreshAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                await TimelineRefresh.DrainAsync(ct);
                return;
            }

            Scheduler.Refresh();
            await TimelineRefresh.RequestAndDrainAsync(ct);
        }

        private ReminderScheduler Scheduler =>
            _scheduler ?? throw new InvalidOperationException(
                "Backup restore lifecycle is not initialized.");

        private TimelineRefreshCoordinator TimelineRefresh =>
            _timelineRefresh ?? throw new InvalidOperationException(
                "Backup restore lifecycle is not initialized.");
    }

    private sealed class ReminderRecoverySummarySink(IReminderSink sink) :
        IReminderRecoverySummarySink
    {
        public Task SendMissedSummaryAsync(
            IReadOnlyList<ScheduledReminder> reminders,
            CancellationToken ct) =>
            sink.DeliverMissedSummaryAsync(reminders, ct);
    }

    private sealed class RuntimeFailureSubscription : IDisposable
    {
        private readonly ReminderRecoveryService _reminderRecovery;
        private readonly ImportantAlertController _importantAlerts;
        private readonly Action<Exception> _reportError;
        private readonly Action<ImportantAlertFailure> _reportImportantAlertFailure;
        private int _disposed;

        public RuntimeFailureSubscription(
            ReminderRecoveryService reminderRecovery,
            ImportantAlertController importantAlerts,
            Action<Exception> reportError)
        {
            _reminderRecovery = reminderRecovery;
            _importantAlerts = importantAlerts;
            _reportError = reportError;
            _reportImportantAlertFailure = failure => reportError(failure.Exception);
            _reminderRecovery.RecoveryFailed += _reportError;
            _importantAlerts.PresentationFailed += _reportImportantAlertFailure;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _importantAlerts.PresentationFailed -= _reportImportantAlertFailure;
            _reminderRecovery.RecoveryFailed -= _reportError;
        }
    }

    private sealed class WindowNotificationNavigator(MainWindow window) : INotificationNavigator
    {
        public Task NavigateAsync(NotificationNavigation navigation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return window.Dispatcher.InvokeAsync(window.ShowAndActivate).Task;
        }
    }

}
