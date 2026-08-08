using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Moment.App.Tests.Diagnostics;

internal sealed record BuildReleaseMetadata(
    string ProductName,
    string ExecutableName,
    string SemanticVersion,
    string ReleaseDate)
{
    private static readonly Regex SemanticVersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)" +
        "(?:-(?:(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)" +
        "(?:\\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);

    public static BuildReleaseMetadata Current { get; } = FromBuildAssembly();

    public string SettingsFooterText =>
        $"版本 {SemanticVersion} · 发布于 {ReleaseDate}";

    public static void ValidateVersionAndDate(
        string semanticVersion,
        string releaseDate)
    {
        if (!SemanticVersionPattern.IsMatch(semanticVersion))
        {
            throw new InvalidDataException(
                $"Build semantic version is invalid: {semanticVersion}");
        }

        if (!DateOnly.TryParseExact(
                releaseDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate) ||
            parsedDate.ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture) != releaseDate)
        {
            throw new InvalidDataException(
                $"Build release date is invalid: {releaseDate}");
        }
    }

    private static BuildReleaseMetadata FromBuildAssembly()
    {
        var attributes = typeof(BuildReleaseMetadata).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(
                attribute => attribute.Key,
                attribute => attribute.Value,
                StringComparer.Ordinal);
        var metadata = new BuildReleaseMetadata(
            ReadRequired(attributes, "ExpectedProductName"),
            ReadRequired(attributes, "ExpectedExecutableName"),
            ReadRequired(attributes, "ExpectedSemanticVersion"),
            ReadRequired(attributes, "ExpectedReleaseDate"));
        ValidateVersionAndDate(
            metadata.SemanticVersion, metadata.ReleaseDate);
        return metadata;
    }

    private static string ReadRequired(
        IReadOnlyDictionary<string, string?> attributes,
        string key) =>
        attributes.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                $"Build metadata '{key}' is missing.");
}
