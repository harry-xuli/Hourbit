using System.Diagnostics;
using System.Reflection;

namespace Moment.App.Settings;

public interface IReleasePageService
{
    Uri? Url { get; }
    void Open();
}

public sealed class ReleasePageService : IReleasePageService
{
    private readonly Action<Uri> _open;

    public ReleasePageService(
        string? metadataValue,
        Action<Uri>? open = null)
    {
        if (Uri.TryCreate(metadataValue, UriKind.Absolute, out var uri) &&
            string.Equals(
                uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            Url = uri;
        }
        _open = open ?? OpenWithSystemBrowser;
    }

    public Uri? Url { get; }

    public void Open()
    {
        if (Url is null)
            throw new InvalidOperationException("Release page is not configured.");
        _open(Url);
    }

    public static ReleasePageService FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var value = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(
                    attribute.Key,
                    "ReleasePageUrl",
                    StringComparison.Ordinal))
            ?.Value;
        return new ReleasePageService(value);
    }

    private static void OpenWithSystemBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
}
