using Hourbit.App.Localization;

namespace Hourbit.App.Shell;

public sealed class TrayMenuItem(
    string text,
    Func<Task>? action = null,
    IReadOnlyList<TrayMenuItem>? children = null)
{
    public string Text { get; } = text;
    public IReadOnlyList<TrayMenuItem> Children { get; } = children ?? [];
    public Task InvokeAsync() => action?.Invoke() ?? Task.CompletedTask;
}

public interface ITrayMenuHost : IDisposable
{
    event Action? Activated;
    void SetItems(IReadOnlyList<TrayMenuItem> items);
}

public interface IExitConfirmationService
{
    Task<bool> ConfirmExitAsync(CancellationToken ct);
}

public sealed class TrayIconController : IDisposable
{
    private readonly ITrayMenuHost _host;
    private readonly Func<Task<bool>> _hasScheduled;
    private readonly IExitConfirmationService _confirmation;
    private readonly ILocalizationService _localization;
    private readonly Action _openTimeline;
    private readonly Action _openQuickAdd;
    private readonly Action<TimeSpan> _createCountdown;
    private readonly Action _openAnalytics;
    private readonly Action _openHelp;
    private readonly Action _openSettings;
    private readonly Func<Task> _exit;
    private int _disposed;

    public TrayIconController(
        ITrayMenuHost host,
        Func<Task<bool>> hasScheduled,
        IExitConfirmationService confirmation,
        ILocalizationService localization,
        Action openTimeline,
        Action openQuickAdd,
        Action<TimeSpan> createCountdown,
        Action openAnalytics,
        Action openHelp,
        Action openSettings,
        Func<Task> exit)
    {
        _host = host;
        _hasScheduled = hasScheduled;
        _confirmation = confirmation;
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _openTimeline = openTimeline;
        _openQuickAdd = openQuickAdd;
        _createCountdown = createCountdown;
        _openAnalytics = openAnalytics;
        _openHelp = openHelp;
        _openSettings = openSettings;
        _exit = exit;
        _host.Activated += openTimeline;
        _localization.LanguageChanged += OnLanguageChanged;
        RebuildMenu();
    }

    public event Action<Exception>? ErrorOccurred;

    public static TrayIconController CreateWindows(
        Func<Task<bool>> hasScheduled,
        ILocalizationService localization,
        Action openTimeline,
        Action openQuickAdd,
        Action<TimeSpan> createCountdown,
        Action openAnalytics,
        Action openHelp,
        Action openSettings,
        Func<Task> exit) =>
        new(new WindowsFormsTrayMenuHost(), hasScheduled,
            new MessageBoxExitConfirmationService(localization), localization,
            openTimeline, openQuickAdd, createCountdown, openAnalytics, openHelp,
            openSettings, exit);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
            _host.Dispose();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        RebuildMenu();
    }

    private void RebuildMenu() => _host.SetItems(
    [
        new(_localization.Translate("Tray.OpenTimeline"), () => InvokeAsync(_openTimeline)),
        new(_localization.Translate("Tray.QuickCreate"), () => InvokeAsync(_openQuickAdd)),
        new(_localization.Translate("Tray.Countdowns"), children:
        [
            new(_localization.Translate("Tray.FiveMinutes"), () => InvokeAsync(() => _createCountdown(TimeSpan.FromMinutes(5)))),
            new(_localization.Translate("Tray.TenMinutes"), () => InvokeAsync(() => _createCountdown(TimeSpan.FromMinutes(10)))),
            new(_localization.Translate("Tray.TwentyMinutes"), () => InvokeAsync(() => _createCountdown(TimeSpan.FromMinutes(20))))
        ]),
        new(_localization.Translate("Tray.Analytics"), () => InvokeAsync(_openAnalytics)),
        new(_localization.Translate("Tray.Help"), () => InvokeAsync(_openHelp)),
        new(_localization.Translate("Tray.Settings"), () => InvokeAsync(_openSettings)),
        new(_localization.Translate("Tray.Exit"), ExitAsync)
    ]);

    private async Task ExitAsync()
    {
        try
        {
            if (await _hasScheduled() &&
                !await _confirmation.ConfirmExitAsync(CancellationToken.None))
                return;
            await _exit();
        }
        catch (Exception exception)
        {
            ErrorOccurred?.Invoke(exception);
        }
    }

    private async Task InvokeAsync(Action action)
    {
        try
        {
            action();
            await Task.CompletedTask;
        }
        catch (Exception exception)
        {
            ErrorOccurred?.Invoke(exception);
        }
    }
}

public sealed class MessageBoxExitConfirmationService(ILocalizationService localization) : IExitConfirmationService
{
    public Task<bool> ConfirmExitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            localization.Translate("Exit.Warning"),
            localization.Translate("Exit.Title"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        return Task.FromResult(result == System.Windows.MessageBoxResult.Yes);
    }
}

public sealed class WindowsFormsTrayMenuHost : ITrayMenuHost
{
    private const string ApplicationIconResourceName =
        "Hourbit.App.Assets.hourbit.ico";

    private readonly System.Drawing.Icon _applicationIcon;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private int _disposed;

    public WindowsFormsTrayMenuHost()
    {
        _applicationIcon = LoadApplicationIcon();
        var icon = new System.Windows.Forms.NotifyIcon();
        try
        {
            icon.Icon = _applicationIcon;
            icon.Text = "Hourbit 日程";
            icon.DoubleClick += OnDoubleClick;
            icon.Visible = true;
            _icon = icon;
        }
        catch
        {
            icon.Dispose();
            _applicationIcon.Dispose();
            throw;
        }
    }

    public event Action? Activated;

    public void SetItems(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        foreach (var item in items)
            menu.Items.Add(Create(item));
        _icon.ContextMenuStrip?.Dispose();
        _icon.ContextMenuStrip = menu;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            _icon.Visible = false;
            _icon.DoubleClick -= OnDoubleClick;
            _icon.ContextMenuStrip?.Dispose();
        }
        finally
        {
            try
            {
                _icon.Dispose();
            }
            finally
            {
                _applicationIcon.Dispose();
            }
        }
    }

    private void OnDoubleClick(object? sender, EventArgs e) => Activated?.Invoke();

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        using var stream = typeof(WindowsFormsTrayMenuHost).Assembly
            .GetManifestResourceStream(ApplicationIconResourceName)
            ?? throw new InvalidOperationException(
                $"Missing tray icon resource: {ApplicationIconResourceName}");
        using var embeddedIcon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)embeddedIcon.Clone();
    }

    private static System.Windows.Forms.ToolStripMenuItem Create(TrayMenuItem item)
    {
        var native = new System.Windows.Forms.ToolStripMenuItem(item.Text);
        foreach (var child in item.Children)
            native.DropDownItems.Add(Create(child));
        if (item.Children.Count == 0)
            native.Click += async (_, _) => await item.InvokeAsync();
        return native;
    }
}
