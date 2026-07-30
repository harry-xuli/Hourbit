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
        ApplyPcmVolume(bytes, Math.Clamp(volume(), 0, 100));
        using var adjusted = new MemoryStream(bytes, writable: false);
        await inner.StartLoopAsync(adjusted, ct);
    }

    public Task StopAsync(CancellationToken ct) => inner.StopAsync(ct);

    private static void ApplyPcmVolume(byte[] wave, int percentage)
    {
        if (percentage == 100 || wave.Length < 44 ||
            !Matches(wave, 0, "RIFF") || !Matches(wave, 8, "WAVE"))
            return;

        short format = 0;
        short bitsPerSample = 0;
        var dataOffset = -1;
        var dataLength = 0;
        for (var offset = 12; offset + 8 <= wave.Length;)
        {
            var length = BitConverter.ToInt32(wave, offset + 4);
            if (length < 0 || offset + 8L + length > wave.Length)
                return;

            if (Matches(wave, offset, "fmt ") && length >= 16)
            {
                format = BitConverter.ToInt16(wave, offset + 8);
                bitsPerSample = BitConverter.ToInt16(wave, offset + 22);
            }
            else if (Matches(wave, offset, "data"))
            {
                dataOffset = offset + 8;
                dataLength = length;
            }

            offset += 8 + length + (length & 1);
        }

        if (format != 1 || dataOffset < 0)
            return;

        var factor = percentage / 100d;
        if (bitsPerSample == 8)
        {
            for (var index = dataOffset;
                 index < dataOffset + dataLength;
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
        else if (bitsPerSample == 16)
        {
            for (var index = dataOffset;
                 index + 1 < dataOffset + dataLength;
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

    private static bool Matches(byte[] value, int offset, string expected) =>
        offset >= 0 &&
        offset + expected.Length <= value.Length &&
        Encoding.ASCII.GetString(value, offset, expected.Length) == expected;
}
