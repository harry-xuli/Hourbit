using System.Windows.Data;
using System.Windows.Markup;

namespace Hourbit.App.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationHub.Text,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
