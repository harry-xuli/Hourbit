using Moment.App.QuickAdd;
using Moment.App.Shell;
using Moment.App.Timeline;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Recurrence;
using Moment.Core.Scheduling;
using Moment.Core.Services;
using Moment.Infrastructure.Data;
using Moment.Windows.Alerts;
using Moment.Windows.Hotkeys;
using Moment.Windows.Lifecycle;
using Moment.Windows.Notifications;

namespace Moment.App;

public sealed class CompositionRoot : IAsyncDisposable
{
    private readonly SqliteReminderRepository _repository;
    private readonly ReminderScheduler _scheduler;
    private readonly ImportantAlertController _importantAlerts;
    private readonly WindowsNotificationRuntime _notificationRuntime;
    private readonly SystemResumeMonitor _resumeMonitor;
    private readonly GlobalHotkeyService _hotkey;
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly TrayIconController _tray;
    private readonly IReminderService _reminderService;
    private readonly IClock _clock;
    private bool _started;
    private int _disposed;

    private CompositionRoot(
        SqliteReminderRepository repository,
        ReminderScheduler scheduler,
        ImportantAlertController importantAlerts,
        WindowsNotificationRuntime notificationRuntime,
        SystemResumeMonitor resumeMonitor,
        GlobalHotkeyService hotkey,
        SingleInstanceCoordinator singleInstance,
        TrayIconController tray,
        IReminderService reminderService,
        IClock clock,
        TimelineViewModel timeline,
        QuickAddViewModel quickAdd,
        MainWindow mainWindow,
        QuickAddWindowController quickAddWindow)
    {
        _repository = repository;
        _scheduler = scheduler;
        _importantAlerts = importantAlerts;
        _notificationRuntime = notificationRuntime;
        _resumeMonitor = resumeMonitor;
        _hotkey = hotkey;
        _singleInstance = singleInstance;
        _tray = tray;
        _reminderService = reminderService;
        _clock = clock;
        Timeline = timeline;
        QuickAdd = quickAdd;
        MainWindow = mainWindow;
        QuickAddWindow = quickAddWindow;
    }

    public TimelineViewModel Timeline { get; }
    public QuickAddViewModel QuickAdd { get; }
    public MainWindow MainWindow { get; }
    public QuickAddWindowController QuickAddWindow { get; }
    public event Action<Exception>? RuntimeError;

    public static async Task<CompositionRoot> OpenAsync(CancellationToken ct)
    {
        var clock = new SystemClock();
        var zone = TimeZoneInfo.Local;
        var databasePath = DatabasePathResolver.Resolve(AppContext.BaseDirectory);
        var repository = await SqliteReminderRepository.OpenAsync(databasePath, ct);

        var schedulerSignal = new SchedulerSignalProxy();
        var recurrence = new RecurrenceCalculator();
        var actions = new ReminderActionService(repository, recurrence, schedulerSignal, clock, zone);
        var importantAlerts = ImportantAlertControllerFactory.Create(
            new MessageBoxImportantAlertPresenter(), actions);
        var notificationPlatform = new WindowsAppNotificationPlatform();
        var notificationSink = new AppNotificationSink(notificationPlatform, importantAlerts, actions);
        var scheduler = new ReminderScheduler(repository, notificationSink, clock);
        schedulerSignal.Target = scheduler;
        var reminders = new ReminderService(repository, scheduler, clock);

        var parser = new ChineseTimeParser();
        QuickAddViewModel? quickAdd = null;
        var quickWindow = new QuickAddWindowController(
            () => new QuickAddWindow { DataContext = quickAdd! },
            System.Windows.Application.Current.Dispatcher);
        var dialogs = new TimelineDialogService(quickWindow.ShowAndFocus, zone);
        var timelineQuery = new SqliteTimelineQuery(databasePath);
        var timeline = new TimelineViewModel(
            timelineQuery, clock, reminders, actions, dialogs, zone);
        quickAdd = ComposeQuickAdd(parser, reminders, clock, zone, timeline);
        var mainWindow = new MainWindow { DataContext = timeline };
        var navigator = new WindowNotificationNavigator(mainWindow);
        var notificationRuntime = new WindowsNotificationRuntime(actions, navigator);
        var resumeMonitor = new SystemResumeMonitor((_, _) =>
        {
            scheduler.Refresh();
            return timeline.LoadAsync();
        });
        var hotkey = new GlobalHotkeyService();
        var singleInstance = new SingleInstanceCoordinator();

        CompositionRoot? root = null;
        var tray = TrayIconController.CreateWindows(
            async () => (await repository.GetScheduledAsync(CancellationToken.None)).Count > 0,
            mainWindow.ShowAndActivate,
            quickWindow.ShowAndFocus,
            delay =>
            {
                if (root is not null)
                    _ = root.CreateCountdownObservedAsync(delay);
            },
            () => System.Windows.MessageBox.Show("设置", "时刻"),
            () =>
            {
                if (root is not null)
                    root.RequestExit();
                return Task.CompletedTask;
            });

        root = new CompositionRoot(
            repository, scheduler, importantAlerts, notificationRuntime,
            resumeMonitor, hotkey, singleInstance, tray, reminders, clock,
            timeline, quickAdd, mainWindow, quickWindow);
        return root;
    }

    internal static QuickAddViewModel ComposeQuickAdd(
        IChineseTimeParser parser,
        IReminderService reminders,
        IClock clock,
        TimeZoneInfo zone,
        TimelineViewModel timeline) =>
        new(parser, reminders, clock, zone, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            await timeline.LoadAsync();
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(timeline.ErrorMessage))
                throw new InvalidOperationException(timeline.ErrorMessage);
        });

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
        _tray.ErrorOccurred += OnRuntimeError;

        await _scheduler.StartAsync(ct);
        TryStart(_notificationRuntime.Start);
        TryStart(_resumeMonitor.Start);
        TryStart(() => _hotkey.Register("Ctrl+Alt+Space"));
        await Timeline.LoadAsync();
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
        _tray.ErrorOccurred -= OnRuntimeError;
        _tray.Dispose();
        _hotkey.Dispose();
        await _resumeMonitor.DisposeAsync();
        await _notificationRuntime.DisposeAsync();
        await _singleInstance.DisposeAsync();
        _scheduler.Dispose();
        await _importantAlerts.DisposeAsync();
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

    private sealed class SchedulerSignalProxy : ISchedulerSignal
    {
        public ISchedulerSignal? Target { get; set; }
        public void Refresh() => Target?.Refresh();
    }

    private sealed class WindowNotificationNavigator(MainWindow window) : INotificationNavigator
    {
        public Task NavigateAsync(NotificationNavigation navigation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return window.Dispatcher.InvokeAsync(window.ShowAndActivate).Task;
        }
    }

    private sealed class MessageBoxImportantAlertPresenter : IImportantAlertPresenter
    {
        public async Task<ImportantAlertAction> ShowAsync(ReminderAlert alert, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var result = System.Windows.MessageBox.Show(
                    $"{alert.Title}\n{alert.DueAt:HH:mm}\n\n选择“是”完成，选择“否”10分钟后提醒。",
                    "重要提醒",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Exclamation);
                return result == System.Windows.MessageBoxResult.Yes
                    ? ImportantAlertAction.Complete
                    : ImportantAlertAction.Snooze10;
            });
        }
    }
}
