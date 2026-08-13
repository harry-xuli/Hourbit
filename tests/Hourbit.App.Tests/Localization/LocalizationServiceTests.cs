using System.Globalization;
using Hourbit.App.Localization;

namespace Hourbit.App.Tests.Localization;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void Chinese_and_English_catalogs_have_the_same_keys()
    {
        Assert.Equal(
            LocalizationCatalog.Keys(UiLanguage.ZhCn),
            LocalizationCatalog.Keys(UiLanguage.EnUs));
    }

    [Theory]
    [InlineData("zh-CN", null, UiLanguage.ZhCn)]
    [InlineData("en-GB", null, UiLanguage.EnUs)]
    [InlineData("zh-CN", "en-US", UiLanguage.EnUs)]
    public void Persisted_language_wins_then_Windows_language_supplies_default(
        string windowsCulture,
        string? persisted,
        UiLanguage expected)
    {
        var service = new LocalizationService(
            new CultureInfo(windowsCulture), persisted);

        Assert.Equal(expected, service.CurrentLanguage);
    }

    [Fact]
    public void Switching_language_changes_UI_text_and_exposes_persisted_code()
    {
        var service = new LocalizationService(
            CultureInfo.GetCultureInfo("zh-CN"), null);

        service.SetLanguage(UiLanguage.EnUs);

        Assert.Equal("New", service.Translate("Action.New"));
        Assert.Equal("en-US", service.PersistedCode);
    }
}
