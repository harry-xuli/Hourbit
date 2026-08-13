using Hourbit.App.Shell;
using Hourbit.App.Localization;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

namespace Hourbit.App.Tests.Shell;

public sealed class TrayIconControllerTests
{
    [Fact]
    public void Windows_tray_uses_the_Hourbit_application_icon()
    {
        using var host = new WindowsFormsTrayMenuHost();
        var field = typeof(WindowsFormsTrayMenuHost).GetField(
            "_icon", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var notifyIcon = Assert.IsType<System.Windows.Forms.NotifyIcon>(
            field.GetValue(host));
        Assert.NotNull(notifyIcon.Icon);

        Assert.Equal(
            "481254C68E1906032F0B5EEAFC5C65198F14CEE6E6C67C59FFBA7C31210FAD36",
            HashPixelsAt32Pixels(notifyIcon.Icon));
    }

    [Fact]
    public async Task Menu_exposes_the_required_shell_actions()
    {
        var host = new TrayHost();
        var analyticsOpens = 0;
        var helpOpens = 0;
        using var controller = new TrayIconController(
            host, () => Task.FromResult(false), new Confirmation(true),
            () => { }, () => { }, _ => { }, () => analyticsOpens++,
            () => helpOpens++, () => { }, () => Task.CompletedTask);

        Assert.Equal(
            ["打开时间轴", "快速创建", "常用倒计时", "分析报告", "使用说明", "设置", "退出"],
            host.Items.Select(item => item.Text));
        await host.Items.Single(item => item.Text == "分析报告").InvokeAsync();
        await host.Items.Single(item => item.Text == "使用说明").InvokeAsync();
        Assert.Equal(1, analyticsOpens);
        Assert.Equal(1, helpOpens);
    }

    [Fact]
    public void Menu_uses_the_selected_UI_language()
    {
        try
        {
            LocalizationHub.Use(new LocalizationService(
                CultureInfo.GetCultureInfo("en-US"), "en-US"));
            var host = new TrayHost();
            using var controller = new TrayIconController(
                host, () => Task.FromResult(false), new Confirmation(true),
                () => { }, () => { }, _ => { }, () => { },
                () => { }, () => { }, () => Task.CompletedTask);

            Assert.Equal(
                ["Open timeline", "Quick create", "Common countdowns", "Reports", "Help", "Settings", "Exit"],
                host.Items.Select(item => item.Text));
        }
        finally
        {
            LocalizationHub.Use(new LocalizationService(
                CultureInfo.GetCultureInfo("zh-CN"), "zh-CN"));
        }
    }

    [Fact]
    public void Tray_double_click_opens_the_existing_timeline_but_single_click_does_nothing()
    {
        var host = new TrayHost();
        var opens = 0;
        using var controller = new TrayIconController(
            host, () => Task.FromResult(false), new Confirmation(true),
            () => opens++, () => { }, _ => { }, () => { },
            () => { }, () => { }, () => Task.CompletedTask);

        host.RaiseSingleClick();
        Assert.Equal(0, opens);

        host.RaiseActivated();
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Exit_with_scheduled_occurrences_requires_confirmation_before_shutdown()
    {
        var host = new TrayHost();
        var confirmation = new Confirmation(false);
        var exits = 0;
        using var controller = new TrayIconController(
            host, () => Task.FromResult(true), confirmation,
            () => { }, () => { }, _ => { }, () => { }, () => { }, () => { },
            () => { exits++; return Task.CompletedTask; });

        await host.Items.Single(item => item.Text == "退出").InvokeAsync();

        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(0, exits);
    }

    private sealed class TrayHost : ITrayMenuHost
    {
        public event Action? Activated;
        public IReadOnlyList<TrayMenuItem> Items { get; private set; } = [];
        public void SetItems(IReadOnlyList<TrayMenuItem> items) => Items = items;
        public void RaiseActivated() => Activated?.Invoke();
        public void RaiseSingleClick() { }
        public void Dispose() { }
    }

    private sealed class Confirmation(bool result) : IExitConfirmationService
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmExitAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static string HashPixelsAt32Pixels(System.Drawing.Icon source)
    {
        using var icon = new System.Drawing.Icon(
            source, new System.Drawing.Size(32, 32));
        using var bitmap = icon.ToBitmap();
        var bytes = new byte[32 * 32 * 4];
        var offset = 0;
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                bytes[offset++] = pixel.R;
                bytes[offset++] = pixel.G;
                bytes[offset++] = pixel.B;
                bytes[offset++] = pixel.A;
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
