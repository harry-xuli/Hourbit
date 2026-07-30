using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Moment.App.Shell;
using Moment.Core.Domain;
using Moment.Windows.Alerts;

namespace Moment.App.Alerts;

public partial class ImportantAlertWindow : Window
{
    private readonly ReminderAlert _alert;
    private readonly IImportantAlertAudio _audio;
    private readonly WindowPlacementService _placement;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly TaskCompletionSource<ImportantAlertAction> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _audioStartCancellation = new();
    private Task _audioStart = Task.CompletedTask;
    private int _finishing;
    private bool _allowClose;

    public ImportantAlertWindow(
        ReminderAlert alert,
        IImportantAlertAudio audio,
        WindowPlacementService placement,
        CancellationToken ct = default)
    {
        _alert = alert ?? throw new ArgumentNullException(nameof(alert));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        InitializeComponent();
        ReminderTitle.Text = alert.Title;
        DueTime.Text = $"计划时间：{alert.DueAt:yyyy-MM-dd HH:mm}";
        ContentRendered += OnShown;
        _cancellationRegistration = ct.Register(() =>
            Dispatcher.BeginInvoke(
                new Action(() => _ = CancelAsync(ct)),
                DispatcherPriority.Send));
    }

    public Task<ImportantAlertAction> Completion => _completion.Task;

    private async void OnShown(object? sender, EventArgs e)
    {
        ContentRendered -= OnShown;
        _placement.Place(this);
        _audioStart = StartAudioAsync(_audioStartCancellation.Token);
        try
        {
            await _audioStart;
        }
        catch (OperationCanceledException)
            when (_audioStartCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AudioWarningText.Text = "提醒声音无法播放：" + exception.Message;
            AudioWarningPanel.Visibility = Visibility.Visible;
        }
        if (Volatile.Read(ref _finishing) == 0)
            CompleteButton.Focus();
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ImportantAlertAction action })
            _ = FinishAsync(action);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        _ = FinishAsync(ImportantAlertAction.Snooze10);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = TryHandleKey(e.Key);
    }

    internal bool TryHandleKey(Key key)
    {
        ImportantAlertAction? action = key switch
        {
            Key.Escape => ImportantAlertAction.Snooze10,
            Key.Enter when Keyboard.FocusedElement is
                System.Windows.Controls.Button
                { Tag: ImportantAlertAction focusedAction } => focusedAction,
            _ => null
        };
        if (action is null)
            return false;
        _ = FinishAsync(action.Value);
        return true;
    }

    private async Task FinishAsync(ImportantAlertAction action)
    {
        if (Interlocked.Exchange(ref _finishing, 1) != 0)
            return;

        try
        {
            await FinishAudioStartAsync();
            await CleanupAudioAsync();
            _allowClose = true;
            _completion.TrySetResult(action);
            QueueClose();
        }
        catch (Exception exception)
        {
            _allowClose = true;
            _completion.TrySetException(exception);
            QueueClose();
        }
        finally
        {
            _cancellationRegistration.Dispose();
        }
    }

    private async Task CancelAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _finishing, 1) != 0)
            return;
        try
        {
            await FinishAudioStartAsync();
            await CleanupAudioAsync();
        }
        finally
        {
            _allowClose = true;
            _completion.TrySetCanceled(ct);
            QueueClose();
            _cancellationRegistration.Dispose();
        }
    }

    private Task StartAudioAsync(CancellationToken ct) =>
        string.IsNullOrWhiteSpace(_alert.CustomAudioPath)
            ? _audio.StartDefaultLoopAsync(ct)
            : _audio.StartCustomLoopAsync(_alert.CustomAudioPath, ct);

    private async Task FinishAudioStartAsync()
    {
        _audioStartCancellation.Cancel();
        try
        {
            await _audioStart;
        }
        catch (OperationCanceledException)
            when (_audioStartCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // OnShown already exposes startup failures non-modally.
        }
    }

    private async Task CleanupAudioAsync()
    {
        Exception? stopFailure = null;
        try
        {
            await _audio.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        if (_audio is IAsyncDisposable disposable)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch (Exception exception)
            {
                stopFailure = stopFailure is null
                    ? exception
                    : new AggregateException(stopFailure, exception);
            }
        }

        if (stopFailure is not null)
            throw stopFailure;
    }

    private void QueueClose() =>
        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
}

public sealed class ImportantAlertWindowPresenter(
    Dispatcher dispatcher,
    WindowPlacementService placement,
    Func<IImportantAlertAudio> audioFactory,
    Func<string?> customSoundPath) : IImportantAlertPresenter
{
    public async Task<ImportantAlertAction> ShowAsync(
        ReminderAlert alert,
        CancellationToken ct)
    {
        var completion = await dispatcher.InvokeAsync(() =>
        {
            var configuredAlert = string.IsNullOrWhiteSpace(alert.CustomAudioPath)
                ? alert with { CustomAudioPath = customSoundPath() }
                : alert;
            var window = new ImportantAlertWindow(
                configuredAlert, audioFactory(), placement, ct);
            window.Show();
            window.Activate();
            return window.Completion;
        });
        return await completion;
    }
}
