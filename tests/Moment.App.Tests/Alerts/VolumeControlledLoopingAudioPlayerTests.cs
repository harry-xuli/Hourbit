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

    [Fact]
    public async Task Zero_volume_silences_sixteen_bit_pcm_samples()
    {
        var inner = new RecordingPlayer();
        var player = new VolumeControlledLoopingAudioPlayer(inner, () => 0);
        await using var wave = CreateWave(1, 16,
            [0xff, 0x7f, 0x00, 0x80, 0x34, 0x12]);

        await player.StartLoopAsync(wave, CancellationToken.None);

        Assert.Equal([0, 0, 0, 0, 0, 0], inner.Data);
    }

    [Theory]
    [InlineData(1, 24)]
    [InlineData(1, 32)]
    [InlineData(3, 32)]
    [InlineData(6, 8)]
    public async Task Unsupported_wave_formats_are_rejected_before_playback(
        short format,
        short bitsPerSample)
    {
        var inner = new RecordingPlayer();
        var player = new VolumeControlledLoopingAudioPlayer(inner, () => 0);
        await using var wave = CreateWave(format, bitsPerSample, [1, 2, 3, 4]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => player.StartLoopAsync(wave, CancellationToken.None));

        Assert.Contains("PCM 8 位或 16 位", error.Message);
        Assert.Null(inner.Data);
    }

    [Fact]
    public async Task Malformed_chunk_length_is_rejected_before_playback()
    {
        var inner = new RecordingPlayer();
        var player = new VolumeControlledLoopingAudioPlayer(inner, () => 0);
        await using var wave = CreateEightBitPcm([1, 2]);
        var bytes = wave.ToArray();
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 16);
        await using var malformed = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => player.StartLoopAsync(malformed, CancellationToken.None));
        Assert.Null(inner.Data);
    }

    private static MemoryStream CreateEightBitPcm(byte[] samples)
        => CreateWave(1, 8, samples);

    private static MemoryStream CreateWave(
        short format,
        short bitsPerSample,
        byte[] samples)
    {
        var bytesPerSample = Math.Max(1, bitsPerSample / 8);
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples.Length + (samples.Length & 1));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write(format);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(8000 * bytesPerSample);
            writer.Write((short)bytesPerSample);
            writer.Write(bitsPerSample);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Length);
            writer.Write(samples);
            if ((samples.Length & 1) != 0)
                writer.Write((byte)0);
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
            Data = bytes[44..(44 + BitConverter.ToInt32(bytes, 40))];
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
