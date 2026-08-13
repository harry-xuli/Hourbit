using System.Text;
using Hourbit.TestSupport;
using Hourbit.Windows.Alerts;

namespace Hourbit.Windows.Tests.Alerts;

public sealed class ImportantAlertAudioTests
{
    [Fact]
    public async Task Failed_custom_loop_uses_embedded_default_wave_and_stops_the_player()
    {
        using var directory = new TempDirectory();
        var customPath = Path.Combine(directory.Path, "custom.wav");
        await File.WriteAllBytesAsync(customPath, [1, 2, 3]);
        var player = new FailingFirstPlayer();
        await using var audio = new ImportantAlertAudio(player);

        await audio.StartCustomLoopAsync(customPath, CancellationToken.None);
        await audio.StopAsync(CancellationToken.None);

        Assert.Equal(2, player.StartAttempts);
        Assert.Equal("RIFF", player.SuccessfulWaveHeader);
        Assert.Equal(1, player.StopCount);
    }

    [Fact]
    public async Task Application_supplied_default_wave_is_used_for_playback()
    {
        var player = new HeaderRecordingPlayer();
        await using var audio = new ImportantAlertAudio(
            player,
            () => new MemoryStream(
                Encoding.ASCII.GetBytes("RIFF-app-wave"), writable: false));

        await audio.StartDefaultLoopAsync(CancellationToken.None);

        Assert.Equal("RIFF", player.Header);
    }

    [Fact]
    public async Task Stop_failure_still_disposes_the_active_wave_stream()
    {
        var stream = new TrackingStream(
            Encoding.ASCII.GetBytes("RIFF-app-wave"));
        var audio = new ImportantAlertAudio(
            new StopFailingPlayer(), () => stream);
        await audio.StartDefaultLoopAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => audio.StopAsync(CancellationToken.None));

        Assert.Equal("stop failed", error.Message);
        Assert.True(stream.WasDisposed);
    }

    private sealed class FailingFirstPlayer : ILoopingAudioPlayer
    {
        public int StartAttempts { get; private set; }
        public string? SuccessfulWaveHeader { get; private set; }
        public int StopCount { get; private set; }

        public async Task StartLoopAsync(Stream wave, CancellationToken ct)
        {
            StartAttempts++;
            if (StartAttempts == 1)
            {
                throw new InvalidOperationException("custom decoder failed");
            }

            var header = new byte[4];
            _ = await wave.ReadAsync(header, ct);
            SuccessfulWaveHeader = Encoding.ASCII.GetString(header);
        }

        public Task StopAsync(CancellationToken ct)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class HeaderRecordingPlayer : ILoopingAudioPlayer
    {
        public string? Header { get; private set; }
        public async Task StartLoopAsync(Stream wave, CancellationToken ct)
        {
            var header = new byte[4];
            _ = await wave.ReadAsync(header, ct);
            Header = Encoding.ASCII.GetString(header);
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StopFailingPlayer : ILoopingAudioPlayer
    {
        public Task StartLoopAsync(Stream wave, CancellationToken ct) =>
            Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("stop failed"));
    }

    private sealed class TrackingStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
