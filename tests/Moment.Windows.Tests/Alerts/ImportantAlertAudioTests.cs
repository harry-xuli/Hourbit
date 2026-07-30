using System.Text;
using Moment.TestSupport;
using Moment.Windows.Alerts;

namespace Moment.Windows.Tests.Alerts;

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
}
