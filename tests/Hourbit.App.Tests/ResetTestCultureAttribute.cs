using System.Globalization;
using System.Reflection;
using Xunit.Sdk;

namespace Hourbit.App.Tests;

[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = false)]
public sealed class ResetTestCultureAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => Reset();

    public override void After(MethodInfo methodUnderTest) => Reset();

    private static void Reset()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
    }
}
