namespace Hourbit.App;

public partial class App : System.Windows.Application
{
    private CompositionRoot? _root;

    protected override async void OnStartup(System.Windows.StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ApplySystemTheme();
        System.Windows.SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        try
        {
            var root = await CompositionRoot.OpenAsync(CancellationToken.None);
            if (root is null)
            {
                Shutdown();
                return;
            }
            _root = root;
            root.RuntimeError += OnRuntimeError;
            var activation = eventArgs.Args.Contains("--quick-add", StringComparer.OrdinalIgnoreCase)
                ? Hourbit.Windows.Lifecycle.InstanceActivation.ShowQuickAdd
                : Hourbit.Windows.Lifecycle.InstanceActivation.ShowMain;
            if (!await root.StartAsync(activation, CancellationToken.None))
                Shutdown();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message, "Hourbit 日程", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs eventArgs)
    {
        System.Windows.SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        if (_root is not null)
        {
            _root.RuntimeError -= OnRuntimeError;
            _root.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.OnExit(eventArgs);
    }

    private static void OnRuntimeError(Exception exception) =>
        System.Diagnostics.Debug.WriteLine(exception);

    private void OnSystemParametersChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(System.Windows.SystemParameters.HighContrast))
            ApplySystemTheme();
    }

    private void ApplySystemTheme() =>
        Styles.HighContrastPalette.Apply(
            Resources,
            System.Windows.SystemParameters.HighContrast,
            TryFindResource);
}
