using System.Globalization;
using Hourbit.App.Localization;

namespace Hourbit.App.Tests.Localization;

public sealed class LocalizedTextSourceTests
{
    [Fact]
    public void Existing_binding_source_notifies_when_language_changes()
    {
        var service = new LocalizationService(
            CultureInfo.GetCultureInfo("zh-CN"), null);
        using var source = new LocalizedTextSource(service);
        var changed = new List<string?>();
        source.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        service.SetLanguage(UiLanguage.EnUs);

        Assert.Equal("Settings", source["Settings.Title"]);
        Assert.Contains("Item[]", changed);
    }
}
