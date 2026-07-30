namespace Moment.App.Shell;

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
    private readonly Func<Task> _exit;
    private int _disposed;

    public TrayIconController(
        ITrayMenuHost host,
        Func<Task<bool>> hasScheduled,
        IExitConfirmationService confirmation,
        Action openTimeline,
        Action openQuickAdd,
        Action<TimeSpan> createCountdown,
        Action openSettings,
        Func<Task> exit)
    {
        _host = host;
        _hasScheduled = hasScheduled;
        _confirmation = confirmation;
        _exit = exit;
        _host.SetItems(
        [
            new("打开今天时间轴", () => InvokeAsync(openTimeline)),
            new("快速创建", () => InvokeAsync(openQuickAdd)),
            new("常用倒计时", children:
            [
                new("5 分钟", () => InvokeAsync(() => createCountdown(TimeSpan.FromMinutes(5)))),
                new("10 分钟", () => InvokeAsync(() => createCountdown(TimeSpan.FromMinutes(10)))),
                new("20 分钟", () => InvokeAsync(() => createCountdown(TimeSpan.FromMinutes(20))))
            ]),
            new("设置", () => InvokeAsync(openSettings)),
            new("退出", ExitAsync)
        ]);
    }

    public event Action<Exception>? ErrorOccurred;

    public static TrayIconController CreateWindows(
        Func<Task<bool>> hasScheduled,
        Action openTimeline,
        Action openQuickAdd,
        Action<TimeSpan> createCountdown,
        Action openSettings,
        Func<Task> exit) =>
        new(new WindowsFormsTrayMenuHost(), hasScheduled,
            new MessageBoxExitConfirmationService(), openTimeline, openQuickAdd,
            createCountdown, openSettings, exit);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _host.Dispose();
    }

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

public sealed class MessageBoxExitConfirmationService : IExitConfirmationService
{
    public Task<bool> ConfirmExitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            "退出后，尚未到期的提醒将不会触发。确定退出吗？",
            "退出时刻",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        return Task.FromResult(result == System.Windows.MessageBoxResult.Yes);
    }
}

public sealed class WindowsFormsTrayMenuHost : ITrayMenuHost
{
    private readonly System.Windows.Forms.NotifyIcon _icon = new()
    {
        Icon = System.Drawing.SystemIcons.Application,
        Text = "时刻",
        Visible = true
    };

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
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
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
