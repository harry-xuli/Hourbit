using Moment.Core.Services;

namespace Moment.Windows.Alerts;

/// <summary>Production construction path: never selects the silent test fallback.</summary>
public static class ImportantAlertControllerFactory
{
    public static ImportantAlertController Create(IImportantAlertPresenter presenter, IReminderActionService actions, ILoopingAudioPlayer? player = null) =>
        new(presenter, actions, new ImportantAlertAudio(player ?? new WindowsLoopingAudioPlayer()));
}
