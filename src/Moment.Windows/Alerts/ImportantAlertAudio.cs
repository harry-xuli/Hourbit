using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Moment.Windows.Alerts;

/// <summary>Minimal OS boundary for looped WAV playback.</summary>
public interface ILoopingAudioPlayer
{
    Task StartLoopAsync(Stream wave, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public sealed class WindowsLoopingAudioPlayer : ILoopingAudioPlayer
{
    private const uint Async = 0x0001;
    private const uint NoDefault = 0x0002;
    private const uint Memory = 0x0004;
    private const uint Loop = 0x0008;
    private readonly object _gate = new();
    private GCHandle? _wave;

    public Task StartLoopAsync(Stream wave, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var copy = new MemoryStream();
        wave.CopyTo(copy);
        var bytes = copy.ToArray();
        lock (_gate)
        {
            StopCore();
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            _wave = pinned;
            if (!NativeMethods.PlaySound(pinned.AddrOfPinnedObject(), IntPtr.Zero, Async | NoDefault | Memory | Loop))
            {
                StopCore();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not play the reminder sound.");
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            StopCore();
        return Task.CompletedTask;
    }

    private void StopCore()
    {
        NativeMethods.PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_wave is { } pinned)
        {
            pinned.Free();
            _wave = null;
        }
    }

    private static class NativeMethods
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
    }
}

/// <summary>Production audio service that owns the active stream for the duration of a loop.</summary>
public sealed class ImportantAlertAudio(ILoopingAudioPlayer player) : IImportantAlertAudio, IAsyncDisposable
{
    private const string DefaultResourceName = "Moment.Windows.Assets.default-alert.wav";
    private Stream? _activeWave;

    public async Task StartCustomLoopAsync(string audioPath, CancellationToken ct)
    {
        try
        {
            await StartAsync(() => File.OpenRead(audioPath), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StartDefaultLoopAsync(ct).ConfigureAwait(false);
        }
    }

    public Task StartDefaultLoopAsync(CancellationToken ct) =>
        StartAsync(OpenDefaultWave, ct);

    public async Task StopAsync(CancellationToken ct)
    {
        await player.StopAsync(ct).ConfigureAwait(false);
        _activeWave?.Dispose();
        _activeWave = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task StartAsync(Func<Stream> openWave, CancellationToken ct)
    {
        if (_activeWave is not null)
        {
            await StopAsync(ct).ConfigureAwait(false);
        }
        var wave = openWave();
        try
        {
            await player.StartLoopAsync(wave, ct).ConfigureAwait(false);
            _activeWave = wave;
        }
        catch
        {
            wave.Dispose();
            throw;
        }
    }

    private static Stream OpenDefaultWave()
    {
        using var encoded = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException("The embedded default-alert.wav resource is missing.");
        using var reader = new StreamReader(encoded);
        return new MemoryStream(Convert.FromBase64String(reader.ReadToEnd()), writable: false);
    }
}
