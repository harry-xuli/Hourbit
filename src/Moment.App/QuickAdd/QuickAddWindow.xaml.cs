using System.Windows.Interop;

namespace Moment.App.QuickAdd;

public partial class QuickAddWindow : System.Windows.Window, IQuickAddWindow
{
    private QuickAddViewModel? _viewModel;
    public bool IsClosed { get; private set; }

    public QuickAddWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PlaceOnCurrentMonitor();
            InputBox.Focus();
            InputBox.SelectAll();
        };
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        IsClosed = true;
        if (_viewModel is not null)
            _viewModel.HideRequested -= OnHideRequested;
        base.OnClosed(eventArgs);
    }

    public void ShowAndFocus()
    {
        Show();
        PlaceOnCurrentMonitor();
        Activate();
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs eventArgs)
    {
        if (_viewModel is not null)
            _viewModel.HideRequested -= OnHideRequested;
        _viewModel = eventArgs.NewValue as QuickAddViewModel;
        if (_viewModel is not null)
            _viewModel.HideRequested += OnHideRequested;
    }

    private void OnHideRequested(object? sender, EventArgs eventArgs) => Hide();

    private void PlaceOnCurrentMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        Left = area.Left + (area.Width - ActualWidth) / 2d;
        Top = area.Top + (area.Height - ActualHeight) / 2d;
    }
}
