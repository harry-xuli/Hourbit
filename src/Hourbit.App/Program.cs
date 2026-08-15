using Hourbit.App.Diagnostics;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace Hourbit.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var bootstrapFailure = TryBootstrapWindowsAppRuntime();
        if (bootstrapFailure is not null)
            return bootstrapFailure.Value;

        var diagnosticResult = TryRunDiagnosticsAsync(
            args,
            CancellationToken.None).GetAwaiter().GetResult();
        if (diagnosticResult is not null)
            return diagnosticResult.Value;

        ApplicationBootstrap.EnsureWindowsDirectoryEnvironment();
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }

    private static int? TryBootstrapWindowsAppRuntime()
    {
#if MICROSOFT_WINDOWSAPPSDK_AUTOINITIALIZE_UNDOCKEDREGFREEWINRT
        // Self-contained single-file builds activate the Windows App Runtime
        // through the undocked reg-free WinRT auto-initializer, not the
        // bootstrap API, so there is nothing to do here.
        return null;
#else
        try
        {
            var minVersion = new PackageVersion(
                Microsoft.WindowsAppSDK.Runtime.Version.UInt64);
            if (!Bootstrap.TryInitialize(
                    Microsoft.WindowsAppSDK.Release.MajorMinor,
                    Microsoft.WindowsAppSDK.Release.VersionTag,
                    minVersion,
                    Bootstrap.InitializeOptions.OnNoMatch_ShowUI,
                    out var hr))
            {
                return hr;
            }

            return null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Windows App SDK bootstrap failed: {exception}");
            return -2147467259; // E_FAIL
        }
#endif
    }

    internal static Task<int?> TryRunDiagnosticsAsync(
        string[] args,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<int?>(null);

        if (args.Length != 2)
            return Task.FromResult<int?>(2);

        return RunSelfTestAsync(args[1], ct);
    }

    private static async Task<int?> RunSelfTestAsync(
        string outputDirectory,
        CancellationToken ct) =>
        await SmokeTestRunner.RunAsync(outputDirectory, ct);
}
