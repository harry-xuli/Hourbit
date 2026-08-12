using Moment.Core.Services;

namespace Moment.Windows.Alerts;

/// <summary>Production construction path: never selects the silent test fallback.</summary>
public static class ImportantAlertControllerFactory
{
    public static ImportantAlertController Create(
        IImportantAlertPresenter presenter,
        IReminderActionService actions,
        ILoopingAudioPlayer? player = null,
        Notifications.IReminderActionCompletedObserver? actionCompletedObserver = null) =>
        new(presenter, actions, CreateAudio(player),
            actionCompletedObserver: actionCompletedObserver);

    public static ImportantAlertController CreatePresenterManaged(
        IImportantAlertPresenter presenter,
        IReminderActionService actions,
        Notifications.IReminderActionCompletedObserver? actionCompletedObserver = null) =>
        new(presenter, actions, actionCompletedObserver: actionCompletedObserver);

    public static IImportantAlertAudio CreateAudio(
        ILoopingAudioPlayer? player = null,
        Func<Stream>? defaultWave = null) =>
        defaultWave is null
            ? new ImportantAlertAudio(player ?? new WindowsLoopingAudioPlayer())
            : new ImportantAlertAudio(
                player ?? new WindowsLoopingAudioPlayer(), defaultWave);
}
