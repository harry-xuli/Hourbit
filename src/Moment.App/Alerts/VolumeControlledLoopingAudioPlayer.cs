using System.IO;
using System.Text;
using Moment.Windows.Alerts;

namespace Moment.App.Alerts;

public sealed class VolumeControlledLoopingAudioPlayer(
    ILoopingAudioPlayer inner,
    Func<int> volume) : ILoopingAudioPlayer
{
    public async Task StartLoopAsync(Stream wave, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        await wave.CopyToAsync(copy, ct);
        var bytes = copy.ToArray();
        var format = SupportedPcmWave.Validate(bytes);
        ApplyPcmVolume(bytes, format, Math.Clamp(volume(), 0, 100));
        using var adjusted = new MemoryStream(bytes, writable: false);
        await inner.StartLoopAsync(adjusted, ct);
    }

    public Task StopAsync(CancellationToken ct) => inner.StopAsync(ct);

    private static void ApplyPcmVolume(
        byte[] wave,
        SupportedPcmWave.Format format,
        int percentage)
    {
        if (percentage == 100)
            return;

        var factor = percentage / 100d;
        if (format.BitsPerSample == 8)
        {
            for (var index = format.DataOffset;
                 index < format.DataOffset + format.DataLength;
                 index++)
            {
                wave[index] = (byte)Math.Clamp(
                    128 + (int)Math.Round(
                        (wave[index] - 128) * factor,
                        MidpointRounding.AwayFromZero),
                    byte.MinValue,
                    byte.MaxValue);
            }
        }
        else
        {
            for (var index = format.DataOffset;
                 index + 1 < format.DataOffset + format.DataLength;
                 index += 2)
            {
                var sample = BitConverter.ToInt16(wave, index);
                var adjusted = (short)Math.Clamp(
                    (int)Math.Round(
                        sample * factor,
                        MidpointRounding.AwayFromZero),
                    short.MinValue,
                    short.MaxValue);
                wave[index] = (byte)(adjusted & 0xff);
                wave[index + 1] = (byte)((adjusted >> 8) & 0xff);
            }
        }
    }
}

public static class SupportedPcmWave
{
    internal sealed record Format(
        short BitsPerSample,
        int DataOffset,
        int DataLength);

    public static void ValidateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(File.ReadAllBytes(path));
    }

    internal static Format Validate(byte[] wave)
    {
        if (wave.Length < 44 ||
            !Matches(wave, 0, "RIFF") ||
            !Matches(wave, 8, "WAVE"))
        {
            throw Unsupported();
        }

        var riffLength = BitConverter.ToUInt32(wave, 4);
        if (riffLength + 8L > wave.Length)
            throw new InvalidDataException("WAV 文件块长度无效。");

        short audioFormat = 0;
        short channels = 0;
        short bitsPerSample = 0;
        short blockAlign = 0;
        var dataOffset = -1;
        var dataLength = 0;
        for (var offset = 12; offset + 8 <= wave.Length;)
        {
            var length = BitConverter.ToUInt32(wave, offset + 4);
            var chunkEnd = offset + 8L + length;
            if (chunkEnd > wave.Length)
                throw new InvalidDataException("WAV 文件块长度无效。");

            if (Matches(wave, offset, "fmt "))
            {
                if (length < 16)
                    throw new InvalidDataException("WAV fmt 块无效。");
                audioFormat = BitConverter.ToInt16(wave, offset + 8);
                channels = BitConverter.ToInt16(wave, offset + 10);
                blockAlign = BitConverter.ToInt16(wave, offset + 20);
                bitsPerSample = BitConverter.ToInt16(wave, offset + 22);
            }
            else if (Matches(wave, offset, "data"))
            {
                dataOffset = offset + 8;
                dataLength = checked((int)length);
            }

            var next = chunkEnd + (length & 1);
            if (next > wave.Length)
                throw new InvalidDataException("WAV 文件块填充无效。");
            offset = checked((int)next);
        }

        var bytesPerSample = bitsPerSample / 8;
        if (audioFormat != 1 ||
            bitsPerSample is not (8 or 16) ||
            channels <= 0 ||
            blockAlign != channels * bytesPerSample ||
            dataOffset < 0 ||
            dataLength % blockAlign != 0)
        {
            throw Unsupported();
        }

        return new Format(bitsPerSample, dataOffset, dataLength);
    }

    private static InvalidDataException Unsupported() =>
        new("仅支持未压缩 PCM 8 位或 16 位 WAV 声音文件。");

    private static bool Matches(byte[] value, int offset, string expected) =>
        offset >= 0 &&
        offset + expected.Length <= value.Length &&
        Encoding.ASCII.GetString(value, offset, expected.Length) == expected;
}
