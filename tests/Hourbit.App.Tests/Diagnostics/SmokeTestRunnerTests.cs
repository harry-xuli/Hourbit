using System.IO;
using System.Text.Json;
using Hourbit.App.Diagnostics;

namespace Hourbit.App.Tests.Diagnostics;

public sealed class SmokeTestRunnerTests
{
    private static readonly string[] ExpectedEvents =
    [
        "normal-delivery",
        "important-delivery",
        "completed",
        "snoozed",
        "restart-recovered",
        "missed-recovery",
        "single-instance-protocol",
        "schema-v1-upgrade",
        "schema-v2-upgrade",
        "todos-created",
        "todo-scheduler-exclusion",
        "release-metadata"
    ];

    [Fact]
    public async Task Relative_output_directory_is_rejected_without_creating_results()
    {
        var relativePath = Path.Combine("relative", Guid.NewGuid().ToString("N"));

        var result = await SmokeTestRunner.RunAsync(relativePath, CancellationToken.None);

        Assert.Equal(2, result);
        Assert.False(Directory.Exists(relativePath));
    }

    [Fact]
    public async Task Isolated_real_pipeline_writes_each_required_event_once()
    {
        using var output = new TemporaryDirectory();

        var result = await SmokeTestRunner.RunAsync(output.Path, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(output.Path, "data", "moment-self-test.db")));
        var events = File.ReadLines(Path.Combine(output.Path, "self-test.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement
                .GetProperty("event").GetString())
            .ToArray();
        Assert.Equal(ExpectedEvents.Order(), events.Order());
        Assert.All(ExpectedEvents, expected =>
            Assert.Equal(1, events.Count(actual => actual == expected)));

        using var metadata = File.ReadLines(Path.Combine(output.Path, "self-test.jsonl"))
            .Select(line => JsonDocument.Parse(line))
            .Single(document => document.RootElement
                .GetProperty("event").GetString() == "release-metadata");
        var expected = BuildReleaseMetadata.Current;
        Assert.Equal(expected.ProductName,
            metadata.RootElement.GetProperty("productName").GetString());
        Assert.Equal(expected.ExecutableName,
            metadata.RootElement.GetProperty("executableName").GetString());
        Assert.Equal(expected.SemanticVersion,
            metadata.RootElement.GetProperty("semanticVersion").GetString());
        Assert.Equal(expected.ReleaseDate,
            metadata.RootElement.GetProperty("releaseDate").GetString());
        Assert.Equal(expected.SettingsFooterText,
            metadata.RootElement.GetProperty("settingsFooter").GetString());
    }

    [Fact]
    public async Task Program_dispatches_self_test_arguments_without_starting_normal_app()
    {
        using var output = new TemporaryDirectory();

        var result = await Program.TryRunDiagnosticsAsync(
            ["--self-test", output.Path],
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(output.Path, "self-test.jsonl")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Hourbit.App.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
