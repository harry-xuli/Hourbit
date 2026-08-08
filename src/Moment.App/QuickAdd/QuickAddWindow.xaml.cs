using System.Windows.Interop;
using System.ComponentModel;

namespace Moment.App.QuickAdd;

public partial class QuickAddWindow : System.Windows.Window, IQuickAddWindow
{
    private QuickAddViewModel? _viewModel;
    private bool _allowClose;
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

    protected override void OnClosed(EventArgs eventArgs)
    {
        IsClosed = true;
        if (_viewModel is not null)
            _viewModel.HideRequested -= OnHideRequested;
        if (System.Windows.Application.Current is { } application)
            application.SessionEnding -= OnSessionEnding;
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

    internal bool TryExpandDetailsFromTab()
    {
        if (!InputBox.IsKeyboardFocusWithin
            || DataContext is not QuickAddViewModel viewModel
            || !viewModel.ShowDetails())
        {
            return false;
        }

        UpdateLayout();
        if (viewModel.IsTodoDetailsVisible)
            TodoDetailTitleBox.Focus();
        else
            DetailTitleBox.Focus();
        return true;
    }

    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == System.Windows.Input.Key.Tab && TryExpandDetailsFromTab())
        {
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == System.Windows.Input.Key.Enter && CanSubmitFromEnter())
        {
            eventArgs.Handled = true;
            await TrySubmitFromEnterAsync();
        }
    }

    internal async Task<bool> TrySubmitFromEnterAsync()
    {
        if (!CanSubmitFromEnter() || DataContext is not QuickAddViewModel viewModel)
            return false;

        await viewModel.SubmitCommand.ExecuteAsync(null);
        return true;
    }

    private bool CanSubmitFromEnter() =>
        DataContext is QuickAddViewModel viewModel &&
        (viewModel.IsRefreshOnly || InputBox.IsKeyboardFocusWithin);

    private void OnSessionEnding(
        object? sender,
        System.Windows.SessionEndingCancelEventArgs eventArgs) => _allowClose = true;

    private void PlaceOnCurrentMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        Left = area.Left + (area.Width - ActualWidth) / 2d;
        Top = area.Top + (area.Height - ActualHeight) / 2d;
    }
}
