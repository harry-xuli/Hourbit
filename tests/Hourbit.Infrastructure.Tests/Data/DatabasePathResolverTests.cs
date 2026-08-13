using Hourbit.Infrastructure.Data;
using Hourbit.TestSupport;

namespace Hourbit.Infrastructure.Tests.Data;

public sealed class DatabasePathResolverTests
{
    [Fact]
    public void Resolve_returns_local_application_data_path_when_not_portable()
    {
        using var temp = new TempDirectory();
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var path = DatabasePathResolver.Resolve(temp.Path);

        Assert.Equal(Path.Combine(root, "Moment", "data", "moment.db"), path);
    }

    [Fact]
    public void Resolve_returns_executable_data_path_when_portable_flag_exists()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "portable.flag"), string.Empty);

        var path = DatabasePathResolver.Resolve(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "Data", "moment.db"), path);
    }
}
