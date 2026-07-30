using Moment.App.Shell;

namespace Moment.App.Tests.Shell;

public sealed class TrayIconControllerTests
{
    [Fact]
    public void Menu_exposes_the_required_shell_actions()
    {
        var host = new TrayHost();
        using var controller = new TrayIconController(
            host, () => Task.FromResult(false), new Confirmation(true),
            () => { }, () => { }, _ => { }, () => { }, () => Task.CompletedTask);

        Assert.Equal(
            ["打开今天时间轴", "快速创建", "常用倒计时", "设置", "退出"],
            host.Items.Select(item => item.Text));
    }

    [Fact]
    public async Task Exit_with_scheduled_occurrences_requires_confirmation_before_shutdown()
    {
        var host = new TrayHost();
        var confirmation = new Confirmation(false);
        var exits = 0;
        using var controller = new TrayIconController(
            host, () => Task.FromResult(true), confirmation,
            () => { }, () => { }, _ => { }, () => { },
            () => { exits++; return Task.CompletedTask; });

        await host.Items.Single(item => item.Text == "退出").InvokeAsync();

        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(0, exits);
    }

    private sealed class TrayHost : ITrayMenuHost
    {
        public IReadOnlyList<TrayMenuItem> Items { get; private set; } = [];
        public void SetItems(IReadOnlyList<TrayMenuItem> items) => Items = items;
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
}
