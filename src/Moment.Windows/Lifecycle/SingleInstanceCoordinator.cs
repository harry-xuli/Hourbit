using System.IO.Pipes;
using System.Text;
using Moment.Windows.Notifications;

namespace Moment.Windows.Lifecycle;

public enum InstanceActivationKind
{
    ShowMain,
    ShowQuickAdd,
    Notification
}

public sealed record InstanceActivation(InstanceActivationKind Kind, string? NotificationArguments = null)
{
    public static InstanceActivation ShowMain { get; } = new(InstanceActivationKind.ShowMain);
    public static InstanceActivation ShowQuickAdd { get; } = new(InstanceActivationKind.ShowQuickAdd);

    public static InstanceActivation ForNotification(string arguments)
    {
        if (!Notifications.NotificationArguments.TryParse(arguments, out _))
            throw new FormatException("Notification activation arguments are invalid.");
        return new(InstanceActivationKind.Notification, arguments);
    }
}

public enum SingleInstanceResult
{
    Primary,
    SecondaryAcknowledged,
    SecondaryNoPrimary,
    SecondaryTimedOut,
    SecondaryRejected
}

public interface ISingleInstanceCoordinator : IAsyncDisposable
{
    bool IsPrimary { get; }
    event Func<InstanceActivation, Task>? ActivationReceived;
    Task<SingleInstanceResult> StartAsync(
        InstanceActivation activation,
        CancellationToken cancellationToken = default);
}

public sealed class SingleInstanceCoordinator : ISingleInstanceCoordinator
{
    public const string ProductionMutexName = @"Local\Moment.ReminderApp";
    public const string ProductionPipeName = "Moment.ReminderApp.Activation";
    public const int MaximumMessageBytes = 4096;
    public static readonly TimeSpan ProductionSecondaryTimeout = TimeSpan.FromSeconds(2);

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly TimeSpan _secondaryTimeout;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Mutex? _mutex;
    private NamedPipeServerStream? _activeServer;
    private Task? _listenerTask;
    private Task<SingleInstanceResult>? _startTask;
    private Task? _disposeTask;
    private bool _isPrimary;
    private bool _disposed;

    public SingleInstanceCoordinator() :
        this(ProductionMutexName, ProductionPipeName, ProductionSecondaryTimeout)
    {
    }

    public SingleInstanceCoordinator(
        string mutexName,
        string pipeName,
        TimeSpan? secondaryTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (secondaryTimeout is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(secondaryTimeout));
        _mutexName = mutexName;
        _pipeName = pipeName;
        _secondaryTimeout = secondaryTimeout ?? ProductionSecondaryTimeout;
    }

    public bool IsPrimary
    {
        get { lock (_gate) return _isPrimary; }
    }

    public event Func<InstanceActivation, Task>? ActivationReceived;

    public Task<SingleInstanceResult> StartAsync(
        InstanceActivation activation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startTask is not null)
                return _startTask;

            _mutex = new Mutex(false, _mutexName, out var createdNew);
            _isPrimary = createdNew;
            if (createdNew)
            {
                _listenerTask = ListenAsync(_lifetime.Token);
                return _startTask = Task.FromResult(SingleInstanceResult.Primary);
            }

            return _startTask = SendToPrimaryAsync(activation, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposed = true;
            _lifetime.Cancel();
            _activeServer?.Dispose();
            return new ValueTask(_disposeTask = CompleteDisposalAsync());
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                lock (_gate)
                {
                    if (_disposed)
                    {
                        server.Dispose();
                        return;
                    }
                    _activeServer = server;
                }

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var message = await ReadBoundedLineAsync(server, cancellationToken).ConfigureAwait(false);
                InstanceActivation? activation = null;
                var accepted = message is not null && TryParseMessage(message, out activation);
                if (accepted)
                {
                    try
                    {
                        await DispatchAsync(activation!).ConfigureAwait(false);
                    }
                    catch
                    {
                        accepted = false;
                    }
                }

                await WriteLineAsync(server, accepted ? "ack" : "reject", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client may disconnect mid-message; keep the primary listener alive.
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeServer, server))
                        _activeServer = null;
                }
                server?.Dispose();
            }
        }
    }

    private async Task DispatchAsync(InstanceActivation activation)
    {
        if (ActivationReceived is not { } handlers)
            return;

        foreach (Func<InstanceActivation, Task> handler in handlers.GetInvocationList())
            await handler(activation).ConfigureAwait(false);
    }

    private async Task<SingleInstanceResult> SendToPrimaryAsync(
        InstanceActivation activation,
        CancellationToken cancellationToken)
    {
        var message = FormatMessage(activation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        connectTimeout.CancelAfter(_secondaryTimeout);
        await using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
        {
            return SingleInstanceResult.SecondaryNoPrimary;
        }
        catch (TimeoutException)
        {
            return SingleInstanceResult.SecondaryNoPrimary;
        }
        catch (IOException)
        {
            return SingleInstanceResult.SecondaryNoPrimary;
        }

        await WriteLineAsync(client, message, linked.Token).ConfigureAwait(false);
        using var acknowledgementTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        acknowledgementTimeout.CancelAfter(_secondaryTimeout);
        string? response;
        try
        {
            response = await ReadBoundedLineAsync(client, acknowledgementTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
        {
            return SingleInstanceResult.SecondaryTimedOut;
        }
        catch (IOException)
        {
            return SingleInstanceResult.SecondaryTimedOut;
        }

        return response switch
        {
            "ack" => SingleInstanceResult.SecondaryAcknowledged,
            "reject" => SingleInstanceResult.SecondaryRejected,
            _ => SingleInstanceResult.SecondaryRejected
        };
    }

    private async Task CompleteDisposalAsync()
    {
        Task? work;
        lock (_gate)
            work = _listenerTask ?? _startTask;

        if (work is not null)
        {
            try
            {
                await work.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        lock (_gate)
        {
            _activeServer?.Dispose();
            _activeServer = null;
            _mutex?.Dispose();
            _mutex = null;
            _isPrimary = false;
        }
        _lifetime.Dispose();
    }

    private static string FormatMessage(InstanceActivation activation)
    {
        var message = activation.Kind switch
        {
            InstanceActivationKind.ShowMain when activation.NotificationArguments is null => "show-main",
            InstanceActivationKind.ShowQuickAdd when activation.NotificationArguments is null => "show-quick-add",
            InstanceActivationKind.Notification
                when activation.NotificationArguments is { } arguments &&
                     Notifications.NotificationArguments.TryParse(arguments, out _) =>
                $"notification:{arguments}",
            _ => throw new FormatException("Instance activation is invalid.")
        };
        if (StrictUtf8.GetByteCount(message) > MaximumMessageBytes)
            throw new FormatException("Instance activation exceeds the maximum message size.");
        return message;
    }

    private static bool TryParseMessage(string message, out InstanceActivation? activation)
    {
        activation = message switch
        {
            "show-main" => InstanceActivation.ShowMain,
            "show-quick-add" => InstanceActivation.ShowQuickAdd,
            _ => null
        };
        if (activation is not null)
            return true;

        const string prefix = "notification:";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var arguments = message[prefix.Length..];
        if (!Notifications.NotificationArguments.TryParse(arguments, out _))
            return false;
        activation = InstanceActivation.ForNotification(arguments);
        return true;
    }

    private static async Task<string?> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumMessageBytes];
        var count = 0;
        var oversized = false;
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return null;
            if (one[0] == (byte)'\n')
                break;
            if (count == bytes.Length)
                oversized = true;
            else
                bytes[count++] = one[0];
        }

        if (oversized || count == 0 || bytes[count - 1] == (byte)'\r')
            return null;
        try
        {
            return StrictUtf8.GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static async Task WriteLineAsync(Stream stream, string message, CancellationToken cancellationToken)
    {
        var bytes = StrictUtf8.GetBytes(message + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
