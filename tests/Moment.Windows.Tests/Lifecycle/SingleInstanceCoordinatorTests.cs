using System.IO.Pipes;
using System.Text;
using Moment.Windows.Lifecycle;
using Moment.Windows.Notifications;

namespace Moment.Windows.Tests.Lifecycle;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void Production_identifiers_and_timeout_are_exact()
    {
        Assert.Equal(@"Local\Moment.ReminderApp", SingleInstanceCoordinator.ProductionMutexName);
        Assert.Equal("Moment.ReminderApp.Activation", SingleInstanceCoordinator.ProductionPipeName);
        Assert.Equal(TimeSpan.FromSeconds(2), SingleInstanceCoordinator.ProductionSecondaryTimeout);
    }

    [Fact]
    public async Task Primary_receives_known_command_and_secondary_waits_for_acknowledgement()
    {
        var names = InstanceNames.Create();
        await using var primary = new SingleInstanceCoordinator(names.Mutex, names.Pipe, TimeSpan.FromSeconds(2));
        var received = new TaskCompletionSource<InstanceActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationReceived += activation => { received.TrySetResult(activation); return Task.CompletedTask; };
        Assert.Equal(SingleInstanceResult.Primary, await primary.StartAsync(InstanceActivation.ShowMain));

        await using var secondary = new SingleInstanceCoordinator(names.Mutex, names.Pipe, TimeSpan.FromSeconds(2));
        Assert.Equal(SingleInstanceResult.SecondaryAcknowledged,
            await secondary.StartAsync(InstanceActivation.ShowQuickAdd));

        Assert.Equal(InstanceActivation.ShowQuickAdd, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Notification_arguments_are_strictly_parsed_and_delivered()
    {
        var names = InstanceNames.Create();
        var arguments = NotificationArguments.Format(Guid.NewGuid(), NotificationAction.Snooze10);
        await using var primary = new SingleInstanceCoordinator(names.Mutex, names.Pipe);
        var received = new TaskCompletionSource<InstanceActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationReceived += activation => { received.TrySetResult(activation); return Task.CompletedTask; };
        await primary.StartAsync(InstanceActivation.ShowMain);

        await using var secondary = new SingleInstanceCoordinator(names.Mutex, names.Pipe);
        Assert.Equal(SingleInstanceResult.SecondaryAcknowledged,
            await secondary.StartAsync(InstanceActivation.ForNotification(arguments)));
        Assert.Equal(arguments, (await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).NotificationArguments);
    }

    [Fact]
    public async Task Malformed_and_oversized_messages_are_rejected_without_stopping_listener()
    {
        var names = InstanceNames.Create();
        await using var primary = new SingleInstanceCoordinator(names.Mutex, names.Pipe);
        var calls = 0;
        primary.ActivationReceived += _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; };
        await primary.StartAsync(InstanceActivation.ShowMain);

        Assert.Equal("reject", await SendRawAsync(names.Pipe, "unknown"));
        Assert.Equal("reject", await SendRawAsync(names.Pipe, "notification:action=complete&occurrenceId=bad"));
        Assert.Equal("reject", await SendRawAsync(names.Pipe, new string('x', SingleInstanceCoordinator.MaximumMessageBytes + 1)));
        Assert.Equal("ack", await SendRawAsync(names.Pipe, "show-main"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Secondary_distinguishes_missing_listener_from_acknowledgement_timeout()
    {
        var missing = InstanceNames.Create();
        using var occupiedMutex = new Mutex(true, missing.Mutex);
        await using var noPrimary = new SingleInstanceCoordinator(missing.Mutex, missing.Pipe, TimeSpan.FromMilliseconds(100));
        Assert.Equal(SingleInstanceResult.SecondaryNoPrimary,
            await noPrimary.StartAsync(InstanceActivation.ShowMain));

        var timeout = InstanceNames.Create();
        using var timeoutMutex = new Mutex(true, timeout.Mutex);
        await using var silentServer = new NamedPipeServerStream(timeout.Pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await silentServer.WaitForConnectionAsync();
            var buffer = new byte["show-main\n"u8.Length];
            await silentServer.ReadExactlyAsync(buffer);
            await Task.Delay(500);
        });
        await using var secondary = new SingleInstanceCoordinator(timeout.Mutex, timeout.Pipe, TimeSpan.FromMilliseconds(100));
        Assert.Equal(SingleInstanceResult.SecondaryTimedOut,
            await secondary.StartAsync(InstanceActivation.ShowMain));
        await serverTask;
    }

    [Fact]
    public async Task Disposal_interrupts_an_incomplete_pipe_read_and_is_idempotent()
    {
        var names = InstanceNames.Create();
        var primary = new SingleInstanceCoordinator(names.Mutex, names.Pipe);
        Assert.Equal(SingleInstanceResult.Primary, await primary.StartAsync(InstanceActivation.ShowMain));
        await using var client = new NamedPipeClientStream(".", names.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await client.WriteAsync(Encoding.UTF8.GetBytes("show"));
        await client.FlushAsync();

        await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await primary.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => primary.StartAsync(InstanceActivation.ShowMain));
    }

    [Fact]
    public async Task Repeated_start_does_not_create_duplicate_listener_or_dispatch_initial_activation()
    {
        var names = InstanceNames.Create();
        await using var primary = new SingleInstanceCoordinator(names.Mutex, names.Pipe);
        var calls = 0;
        primary.ActivationReceived += _ => { calls++; return Task.CompletedTask; };

        Assert.Equal(SingleInstanceResult.Primary, await primary.StartAsync(InstanceActivation.ShowMain));
        Assert.Equal(SingleInstanceResult.Primary, await primary.StartAsync(InstanceActivation.ShowQuickAdd));
        Assert.Equal(0, calls);
    }

    private static async Task<string> SendRawAsync(string pipeName, string message)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        using var reader = new StreamReader(client, Encoding.UTF8, false, 128, leaveOpen: true);
        return (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!;
    }

    private sealed record InstanceNames(string Mutex, string Pipe)
    {
        public static InstanceNames Create()
        {
            var suffix = Guid.NewGuid().ToString("N");
            return new($"Local\\Moment.Tests.{suffix}", $"Moment.Tests.{suffix}");
        }
    }
}
