using System.ComponentModel;

namespace Hourbit.App.Timeline;

public partial class EditTodoWindow : System.Windows.Window
{
    private EditTodoViewModel? _viewModel;
    private bool _allowClose;

    public EditTodoWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        if (System.Windows.Application.Current is { } application)
            application.SessionEnding += OnSessionEnding;
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!_allowClose && _viewModel?.IsRefreshOnly == true &&
            !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            eventArgs.Cancel = true;
        }
        base.OnClosing(eventArgs);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;
        if (System.Windows.Application.Current is { } application)
            application.SessionEnding -= OnSessionEnding;
        base.OnClosed(e);
    }

    private void OnDataContextChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel = e.NewValue as EditTodoViewModel;
        if (_viewModel is not null)
            _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void OnSessionEnding(
        object? sender,
        System.Windows.SessionEndingCancelEventArgs eventArgs) => _allowClose = true;
}
