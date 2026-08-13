using Hourbit.Core.Domain;

namespace Hourbit.Windows.Alerts;

public interface IImportantAlertPresenter
{
    Task<ImportantAlertAction> ShowAsync(ReminderAlert alert, CancellationToken ct);
}

public interface IImportantAlertAudio
{
    Task StartCustomLoopAsync(string audioPath, CancellationToken ct);
    Task StartDefaultLoopAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
