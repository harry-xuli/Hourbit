using System.IO;

namespace Hourbit.App.Tests.Diagnostics;

public sealed class PublicBrandingTests
{
    [Fact]
    public void Current_solution_projects_installer_and_icon_use_Hourbit_names()
    {
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "Hourbit.slnx")));
        Assert.False(File.Exists(Path.Combine(root, "Moment.slnx")));

        foreach (var area in new[] { "App", "Core", "Infrastructure", "Windows" })
        {
            Assert.True(Directory.Exists(Path.Combine(root, "src", $"Hourbit.{area}")));
            Assert.False(Directory.Exists(Path.Combine(root, "src", $"Moment.{area}")));
        }

        Assert.True(File.Exists(Path.Combine(root, "installer", "Hourbit.iss")));
        Assert.False(File.Exists(Path.Combine(root, "installer", "Moment.iss")));
        Assert.True(File.Exists(Path.Combine(
            root, "src", "Hourbit.App", "Assets", "hourbit.ico")));
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
