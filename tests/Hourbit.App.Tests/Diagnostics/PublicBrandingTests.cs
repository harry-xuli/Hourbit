using System.IO;
using System.Security.Cryptography;

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

    [Fact]
    public void Selected_B_logo_is_the_release_master_artwork()
    {
        var logo = Path.Combine(
            RepositoryRoot(), "src", "Hourbit.App", "Assets",
            "hourbit-logo-master.png");

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(logo)));

        Assert.Equal(
            "AB42F148BCA83859399F8EDCED982B743DD990CC2B567F403657FA7A30A80364",
            hash);
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
