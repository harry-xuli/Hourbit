namespace Moment.App.Timeline;

public partial class EditTodoWindow : System.Windows.Window
{
    private EditTodoViewModel? _viewModel;

    public EditTodoWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;
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

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
