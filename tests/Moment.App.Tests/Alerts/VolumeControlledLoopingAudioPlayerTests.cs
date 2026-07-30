using Moment.App.Alerts;
using Moment.Windows.Alerts;
using System.IO;

namespace Moment.App.Tests.Alerts;

public sealed class VolumeControlledLoopingAudioPlayerTests
{
    [Fact]
    public async Task Zero_volume_centers_every_eight_bit_pcm_sample()
    {
        var inner = new RecordingPlayer();
        var player = new VolumeControlledLoopingAudioPlayer(inner, () => 0);
        await using var wave = CreateEightBitPcm([0, 64, 128, 192, 255]);

        await player.StartLoopAsync(wave, CancellationToken.None);

        Assert.Equal([128, 128, 128, 128, 128], inner.Data);
    }

    [Fact]
    public async Task Full_volume_preserves_pcm_samples()
    {
        var inner = new RecordingPlayer();
        var player = new VolumeControlledLoopingAudioPlayer(inner, () => 100);
        await using var wave = CreateEightBitPcm([0, 64, 128, 192, 255]);

        await player.StartLoopAsync(wave, CancellationToken.None);

        Assert.Equal([0, 64, 128, 192, 255], inner.Data);
    }

    private static MemoryStream CreateEightBitPcm(byte[] samples)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples.Length);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(8000);
            writer.Write((short)1);
            writer.Write((short)8);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Length);
            writer.Write(samples);
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class RecordingPlayer : ILoopingAudioPlayer
    {
        public byte[]? Data { get; private set; }
        public async Task StartLoopAsync(Stream wave, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            await wave.CopyToAsync(copy, ct);
            var bytes = copy.ToArray();
            Data = bytes[44..];
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
