using System.Diagnostics;
using System.Reflection;

namespace Moment.App.Settings;

public sealed record ProductMetadata(
    string ProductName,
    string ExecutableName,
    string Version,
    string ReleaseDate)
{
    public string SettingsFooterText =>
        $"版本 {Version} · 发布于 {ReleaseDate}";

    public static ProductMetadata FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var attributes = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(
                attribute => attribute.Key,
                attribute => attribute.Value,
                StringComparer.Ordinal);

        return new ProductMetadata(
            ReadRequired(attributes, "ProductName"),
            ReadRequired(attributes, "ExecutableName"),
            ReadRequired(attributes, "SemanticVersion"),
            ReadRequired(attributes, "ReleaseDate"));
    }

    private static string ReadRequired(
        IReadOnlyDictionary<string, string?> attributes,
        string key) =>
        attributes.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Assembly metadata '{key}' is missing.");
}

public interface IReleasePageService
{
    Uri? Url { get; }
    ProductMetadata Metadata { get; }
    void Open();
}

public sealed class ReleasePageService : IReleasePageService
{
    private readonly Action<Uri> _open;

    public ReleasePageService(
        string? metadataValue,
        Action<Uri>? open = null,
        ProductMetadata? productMetadata = null)
    {
        if (Uri.TryCreate(metadataValue, UriKind.Absolute, out var uri) &&
            string.Equals(
                uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            Url = uri;
        }
        _open = open ?? OpenWithSystemBrowser;
        Metadata = productMetadata ??
            ProductMetadata.FromAssembly(typeof(ReleasePageService).Assembly);
    }

    public Uri? Url { get; }
    public ProductMetadata Metadata { get; }

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
        return new ReleasePageService(
            value,
            productMetadata: ProductMetadata.FromAssembly(assembly));
    }

    private static void OpenWithSystemBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
}
