using System.IO;
using System.Media;

namespace DualClip.App;

internal static class ClipSoundPlayer
{
    private static readonly byte[] ClipSoundBytes = CreateWaveBytes(
        (1040, 45, 0.22),
        (0, 18, 0),
        (1320, 55, 0.20));

    public static void PlayHotkeyMonitorA()
    {
        PlayClipBeep();
    }

    public static void PlayHotkeyMonitorB()
    {
        PlayClipBeep();
    }

    public static void PlayHotkeyBoth()
    {
        PlayClipBeep();
    }

    public static void PlayQueued()
    {
        PlayClipBeep();
    }

    public static void PlaySuccess()
    {
        Play(SystemSounds.Asterisk);
    }

    public static void PlayFailure()
    {
        Play(SystemSounds.Hand);
    }

    private static void Play(SystemSound sound)
    {
        _ = Task.Run(sound.Play);
    }

    private static void PlayClipBeep()
    {
        _ = Task.Run(() =>
        {
            using var stream = new MemoryStream(ClipSoundBytes, writable: false);
            using var player = new SoundPlayer(stream);
            player.PlaySync();
        });
    }

    private static byte[] CreateWaveBytes(params (int Frequency, int DurationMs, double Volume)[] segments)
    {
        const int sampleRate = 22050;
        const short channels = 1;
        const short bitsPerSample = 16;
        const short bytesPerSample = bitsPerSample / 8;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));

        var dataSizePosition = stream.Position;
        writer.Write(0);

        foreach (var segment in segments)
        {
            var sampleCount = (int)(sampleRate * (segment.DurationMs / 1000d));

            for (var index = 0; index < sampleCount; index++)
            {
                short sample = 0;

                if (segment.Frequency > 0)
                {
                    var time = index / (double)sampleRate;
                    var amplitude = Math.Sin(2 * Math.PI * segment.Frequency * time) * short.MaxValue * segment.Volume;
                    sample = (short)amplitude;
                }

                writer.Write(sample);
            }
        }

        var fileLength = stream.Length;
        var dataLength = (int)(fileLength - dataSizePosition - sizeof(int));

        stream.Position = dataSizePosition;
        writer.Write(dataLength);

        stream.Position = 4;
        writer.Write((int)(fileLength - 8));

        writer.Flush();
        return stream.ToArray();
    }
}
