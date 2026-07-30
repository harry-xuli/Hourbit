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
        try
        {
            if (string.IsNullOrWhiteSpace(_alert.CustomAudioPath))
                await _audio.StartDefaultLoopAsync(CancellationToken.None);
            else
                await _audio.StartCustomLoopAsync(
                    _alert.CustomAudioPath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            AudioWarningText.Text = "提醒声音无法播放：" + exception.Message;
            AudioWarningPanel.Visibility = Visibility.Visible;
        }
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
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        _ = FinishAsync(ImportantAlertAction.Snooze10);
    }

    private async Task FinishAsync(ImportantAlertAction action)
    {
        if (Interlocked.Exchange(ref _finishing, 1) != 0)
            return;

        try
        {
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
