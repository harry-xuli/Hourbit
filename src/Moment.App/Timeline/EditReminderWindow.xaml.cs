using Moment.Core.Parsing;

namespace Moment.App.Timeline;

public partial class EditReminderWindow : System.Windows.Window
{
    public EditReminderWindow()
    {
        InitializeComponent();
    }

    public ReminderDraft? Draft { get; private set; }

    private void OnSave(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not EditReminderViewModel viewModel
            || !viewModel.TryBuildDraft(out var draft))
        {
            return;
        }

        Draft = draft;
        DialogResult = true;
    }
}
