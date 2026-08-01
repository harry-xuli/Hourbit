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

        Assert.Equal("Hourbit 日程", metadata.ProductName);
        Assert.Equal("Hourbit", metadata.ExecutableName);
        Assert.Equal("0.2.0", metadata.Version);
        Assert.Equal("2026-08-01", metadata.ReleaseDate);
        Assert.Equal("Hourbit", assembly.GetName().Name);
        Assert.Equal(
            "Hourbit 日程",
            assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }

    [Fact]
    public Task Public_windows_use_hourbit_without_the_legacy_product_label() =>
        WpfTestHost.RunAsync(() =>
        {
            var main = new MainWindow();
            var quickAdd = new QuickAddWindow();
            var settings = new SettingsView();
            try
            {
                Assert.Equal("Hourbit 日程", main.Title);
                Assert.Equal("Hourbit 日程", quickAdd.Title);
                Assert.Equal(
                    "Hourbit 日程设置",
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
