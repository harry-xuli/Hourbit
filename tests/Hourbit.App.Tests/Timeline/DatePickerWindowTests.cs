using System.Globalization;
using System.Windows.Controls;
using Hourbit.App.Localization;
using Hourbit.App.Timeline;

namespace Hourbit.App.Tests.Timeline;

public sealed class DatePickerWindowTests
{
    [Fact]
    public Task English_localization_sets_dialog_copy_and_calendar_language() =>
        WpfTestHost.RunAsync(() =>
        {
            var localization = new LocalizationService(
                CultureInfo.GetCultureInfo("zh-CN"), "en-US");
            var window = new DatePickerWindow(new DateOnly(2026, 8, 15), localization);
            window.Show();
            window.UpdateLayout();

            Assert.Equal("Choose date", window.Title);
            Assert.Equal("Which day do you want to view?",
                Assert.IsType<TextBlock>(window.FindName("DatePickerHeading")).Text);
            Assert.Equal("en-US",
                Assert.IsType<DatePicker>(window.FindName("DateInput")).Language.IetfLanguageTag);
        });

    [Fact]
    public Task Chinese_localization_sets_dialog_copy_and_calendar_language() =>
        WpfTestHost.RunAsync(() =>
        {
            var localization = new LocalizationService(
                CultureInfo.GetCultureInfo("zh-CN"), null);
            var window = new DatePickerWindow(new DateOnly(2026, 8, 15), localization);
            window.Show();
            window.UpdateLayout();

            Assert.Equal("选择日期", window.Title);
            Assert.Equal("查看哪一天？",
                Assert.IsType<TextBlock>(window.FindName("DatePickerHeading")).Text);
            Assert.Equal("zh-CN",
                Assert.IsType<DatePicker>(window.FindName("DateInput")).Language.IetfLanguageTag);
        });
}
