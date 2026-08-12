using System.Windows;

namespace Moment.App.Help;

public sealed class HelpWindowController(Func<HelpWindow>? factory = null) : IDisposable
{
    private readonly Func<HelpWindow> _factory = factory ?? (() => new HelpWindow());
    private HelpWindow? _window;
    private int _disposed;

    public void ShowAndFocus()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_window is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        var window = _factory();
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
            window.Owner = owner;
        window.Closed += OnClosed;
        _window = window;
        window.Show();
        window.Activate();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_window is { } window)
        {
            window.Closed -= OnClosed;
            _window = null;
            window.Close();
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is HelpWindow window)
            window.Closed -= OnClosed;
        _window = null;
    }
}
