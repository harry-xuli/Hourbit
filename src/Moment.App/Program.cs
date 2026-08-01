using Moment.App.Diagnostics;

namespace Moment.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
