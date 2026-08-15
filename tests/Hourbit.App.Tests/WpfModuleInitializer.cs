using System.Runtime.CompilerServices;

namespace Hourbit.App.Tests;

/// <summary>
/// Forces WPF to initialize on the single thread that loads this assembly,
/// before xUnit discovery reflects over the test types.
///
/// WPF is backed by C++/CLI assemblies whose module constructors can deadlock
/// when they run concurrently on different threads (dotnet/runtime#108506).
/// xUnit's discovery reflects over this assembly on the testhost thread while
/// other runtime threads are alive, which can trigger that concurrent load.
/// Touching the WPF types here, once, avoids the loader-lock deadlock.
/// </summary>
internal static class WpfModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        _ = typeof(System.Windows.Application);
        _ = typeof(System.Windows.Window);
        _ = typeof(System.Windows.DependencyObject);
        _ = typeof(System.Windows.Media.Brush);
        _ = typeof(System.Windows.Media.Color);
        _ = typeof(System.Windows.Threading.Dispatcher);
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    }
}
