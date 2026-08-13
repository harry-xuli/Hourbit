using System.IO.Pipes;
using System.Text;
using Hourbit.Windows.Notifications;

namespace Hourbit.Windows.Lifecycle;

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

public interface IInstancePipeClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task WriteMessageAsync(string message, CancellationToken cancellationToken);
    Task<string?> ReadAcknowledgementAsync(CancellationToken cancellationToken);
}

public interface IInstancePipeClientFactory
{
    IInstancePipeClient Create(string pipeName);
}

public interface IInstanceDeadline : IDisposable
{
    CancellationToken Token { get; }
    bool IsExpired { get; }
}

public interface IInstanceDeadlineFactory
{
    IInstanceDeadline Create(
        TimeSpan timeout,
        CancellationToken caller,
        CancellationToken lifetime);
}

public sealed class SingleInstanceCoordinator : ISingleInstanceCoordinator
{
    public const string ProductionMutexName = @"Local\Moment.ReminderApp";
    public const string ProductionPipeName = "Moment.ReminderApp.Activation";
    public const int MaximumMessageBytes = 4096;
    public static readonly TimeSpan ProductionSecondaryTimeout = TimeSpan.FromSeconds(2);

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly AsyncLocal<CallbackScope?> CurrentCallback = new();
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly TimeSpan _secondaryTimeout;
    private readonly IInstancePipeClientFactory _pipeClientFactory;
    private readonly IInstanceDeadlineFactory _deadlineFactory;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _callbacks = [];
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
        TimeSpan? secondaryTimeout = null,
        IInstancePipeClientFactory? pipeClientFactory = null,
        IInstanceDeadlineFactory? deadlineFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (secondaryTimeout is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(secondaryTimeout));
        _mutexName = mutexName;
        _pipeName = pipeName;
        _secondaryTimeout = secondaryTimeout ?? ProductionSecondaryTimeout;
        _pipeClientFactory = pipeClientFactory ?? new NamedPipeClientFactory();
        _deadlineFactory = deadlineFactory ?? new InstanceDeadlineFactory();
    }

    public bool IsPrimary
    {
        get { lock (_gate) return _isPrimary; }
    }

    public event Func<InstanceActivation, Task>? ActivationReceived;
    public event Action<Exception>? ActivationFailed;

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
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _lifetime.Cancel();
                _activeServer?.Dispose();
                _disposeTask = CompleteDisposalAsync();
            }
            disposeTask = _disposeTask;
        }
        return CurrentCallback.Value is { Active: true } scope &&
            ReferenceEquals(scope.Owner, this)
            ? ValueTask.CompletedTask
            : new ValueTask(disposeTask);
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
                TaskCompletionSource? releaseCallback = null;
                if (accepted && !TryPrepareCallback(activation!, out releaseCallback))
                    accepted = false;
                try
                {
                    await WriteLineAsync(server, accepted ? "ack" : "reject", cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    releaseCallback?.TrySetResult();
                }
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

    private bool TryPrepareCallback(
        InstanceActivation activation,
        out TaskCompletionSource? release)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                release = null;
                return false;
            }
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _callbacks.Add(DispatchTrackedAsync(activation, release.Task));
            return true;
        }
    }

    private async Task DispatchTrackedAsync(InstanceActivation activation, Task release)
    {
        await release.ConfigureAwait(false);
        var previous = CurrentCallback.Value;
        var scope = new CallbackScope(this);
        CurrentCallback.Value = scope;
        try
        {
            await DispatchAsync(activation).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                ActivationFailed?.Invoke(exception);
            }
            catch
            {
                // The activation exception is observed even if a diagnostic observer fails.
            }
        }
        finally
        {
            scope.Active = false;
            CurrentCallback.Value = previous;
        }
    }

    private async Task<SingleInstanceResult> SendToPrimaryAsync(
        InstanceActivation activation,
        CancellationToken cancellationToken)
    {
        var message = FormatMessage(activation);
        using var deadline = _deadlineFactory.Create(
            _secondaryTimeout,
            cancellationToken,
            _lifetime.Token);
        await using var client = _pipeClientFactory.Create(_pipeName);
        var connected = false;
        try
        {
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            connected = true;
            await client.WriteMessageAsync(message, deadline.Token).ConfigureAwait(false);
            var response = await client.ReadAcknowledgementAsync(deadline.Token).ConfigureAwait(false);
            return response switch
            {
                "ack" => SingleInstanceResult.SecondaryAcknowledged,
                "reject" => SingleInstanceResult.SecondaryRejected,
                _ => SingleInstanceResult.SecondaryRejected
            };
        }
        catch (OperationCanceledException) when (
            deadline.IsExpired &&
            !cancellationToken.IsCancellationRequested &&
            !_lifetime.IsCancellationRequested)
        {
            return connected
                ? SingleInstanceResult.SecondaryTimedOut
                : SingleInstanceResult.SecondaryNoPrimary;
        }
        catch (TimeoutException)
        {
            return connected
                ? SingleInstanceResult.SecondaryTimedOut
                : SingleInstanceResult.SecondaryNoPrimary;
        }
        catch (IOException)
        {
            return connected
                ? SingleInstanceResult.SecondaryTimedOut
                : SingleInstanceResult.SecondaryNoPrimary;
        }
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

        Task[] callbacks;
        lock (_gate)
            callbacks = _callbacks.ToArray();
        await Task.WhenAll(callbacks).ConfigureAwait(false);

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

    private sealed class NamedPipeClientFactory : IInstancePipeClientFactory
    {
        public IInstancePipeClient Create(string pipeName) => new InstancePipeClient(pipeName);
    }

    private sealed class InstancePipeClient : IInstancePipeClient
    {
        private readonly NamedPipeClientStream _pipe;

        public InstancePipeClient(string pipeName) =>
            _pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

        public Task ConnectAsync(CancellationToken cancellationToken) =>
            _pipe.ConnectAsync(cancellationToken);

        public Task WriteMessageAsync(string message, CancellationToken cancellationToken) =>
            WriteLineAsync(_pipe, message, cancellationToken);

        public Task<string?> ReadAcknowledgementAsync(CancellationToken cancellationToken) =>
            ReadBoundedLineAsync(_pipe, cancellationToken);

        public ValueTask DisposeAsync() => _pipe.DisposeAsync();
    }

    private sealed class InstanceDeadlineFactory(TimeProvider? timeProvider = null)
        : IInstanceDeadlineFactory
    {
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        public IInstanceDeadline Create(
            TimeSpan timeout,
            CancellationToken caller,
            CancellationToken lifetime) =>
            new InstanceDeadline(timeout, caller, lifetime, _timeProvider);
    }

    private sealed class InstanceDeadline : IInstanceDeadline
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _linked;

        public InstanceDeadline(
            TimeSpan timeout,
            CancellationToken caller,
            CancellationToken lifetime,
            TimeProvider timeProvider)
        {
            _timeout = new CancellationTokenSource(timeout, timeProvider);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                caller,
                lifetime,
                _timeout.Token);
        }

        public CancellationToken Token => _linked.Token;
        public bool IsExpired => _timeout.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _timeout.Dispose();
        }
    }

    private sealed class CallbackScope(SingleInstanceCoordinator owner)
    {
        public SingleInstanceCoordinator Owner { get; } = owner;
        public bool Active { get; set; } = true;
    }
}
