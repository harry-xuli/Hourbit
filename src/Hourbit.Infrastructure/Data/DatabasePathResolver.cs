namespace Hourbit.Infrastructure.Data;

public static class DatabasePathResolver
{
    public static string Resolve(string executableDirectory)
    {
        if (File.Exists(Path.Combine(executableDirectory, "portable.flag")))
        {
            return Path.Combine(executableDirectory, "Data", "moment.db");
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Moment", "data", "moment.db");
    }
}
