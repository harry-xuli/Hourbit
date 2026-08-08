using System.IO;
using System.Reflection;
using System.Windows.Automation;
using Moment.App.QuickAdd;
using Moment.App.Settings;

namespace Moment.App.Tests.Diagnostics;

public sealed class ProductMetadataTests
{
    [Fact]
    public void Published_assembly_exposes_the_hourbit_release_identity()
    {
        var assembly = typeof(MainWindow).Assembly;
        var metadata = ProductMetadata.FromAssembly(assembly);
        var expected = BuildReleaseMetadata.Current;

        Assert.Equal(expected.ProductName, metadata.ProductName);
        Assert.Equal(expected.ExecutableName, metadata.ExecutableName);
        Assert.Equal(expected.SemanticVersion, metadata.Version);
        Assert.Equal(expected.ReleaseDate, metadata.ReleaseDate);
        Assert.Equal(expected.ExecutableName, assembly.GetName().Name);
        Assert.Equal(
            expected.ProductName,
            assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }

    [Theory]
    [InlineData("9.8.7", "2031-12-24", "版本 9.8.7 · 发布于 2031-12-24")]
    [InlineData("10.20.30-rc.1+build.5", "2040-02-29",
        "版本 10.20.30-rc.1+build.5 · 发布于 2040-02-29")]
    public void Settings_footer_composes_any_valid_release_metadata(
        string semanticVersion,
        string releaseDate,
        string expectedFooter)
    {
        BuildReleaseMetadata.ValidateVersionAndDate(
            semanticVersion, releaseDate);
        var metadata = new ProductMetadata(
            "Example", "Example", semanticVersion, releaseDate);

        Assert.Equal(expectedFooter, metadata.SettingsFooterText);
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-alpha.01")]
    [InlineData("1.2.3+")]
    public void Build_metadata_rejects_invalid_semantic_versions(string value)
    {
        Assert.Throws<InvalidDataException>(() =>
            BuildReleaseMetadata.ValidateVersionAndDate(value, "2031-12-24"));
    }

    [Theory]
    [InlineData("2031-02-29")]
    [InlineData("2031/12/24")]
    [InlineData("24-12-2031")]
    public void Build_metadata_rejects_invalid_iso_release_dates(string value)
    {
        Assert.Throws<InvalidDataException>(() =>
            BuildReleaseMetadata.ValidateVersionAndDate("9.8.7", value));
    }

    [Fact]
    public Task Public_windows_use_hourbit_without_the_legacy_product_label() =>
        WpfTestHost.RunAsync(() =>
        {
            var expected = BuildReleaseMetadata.Current;
            var main = new MainWindow();
            var quickAdd = new QuickAddWindow();
            var settings = new SettingsView();
            try
            {
                Assert.Equal(expected.ProductName, main.Title);
                Assert.Equal(expected.ProductName, quickAdd.Title);
                Assert.Equal(
                    expected.ProductName + "设置",
                    AutomationProperties.GetName(settings));

                Assert.DoesNotContain("时刻", main.Title);
                Assert.DoesNotContain("时刻", quickAdd.Title);
                Assert.DoesNotContain(
                    "时刻",
                    AutomationProperties.GetName(settings));
            }
            finally
            {
                main.AllowExit();
                main.Close();
                quickAdd.Close();
                settings.Close();
            }
        });
}
