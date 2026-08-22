using System.Globalization;

namespace Hourbit.App.Tests.Localization;

public sealed class ResetTestCultureAttributeTests
{
    [Fact]
    public void Test_boundary_restores_the_default_Chinese_culture()
    {
        var boundary = new ResetTestCultureAttribute();
        var method = typeof(ResetTestCultureAttributeTests).GetMethod(
            nameof(Test_boundary_restores_the_default_Chinese_culture))!;

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

        boundary.After(method);

        Assert.Equal("zh-CN", CultureInfo.CurrentCulture.Name);
        Assert.Equal("zh-CN", CultureInfo.CurrentUICulture.Name);
    }
}
