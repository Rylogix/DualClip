using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DualClip.Core.Models;

namespace DualClip.Encoding;

public sealed class FfmpegSegmentWriter : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private Process? _process;
    private Stream? _stdin;
    private Task? _stderrReaderTask;

    public async Task StartAsync(
        string ffmpegPath,
        string segmentDirectory,
        string segmentPrefix,
        int width,
        int height,
        VideoQualityPreset videoQuality,
        int fps,
        CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("The FFmpeg segment writer is already running.");
        }

        Directory.CreateDirectory(segmentDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(segmentDirectory, segmentPrefix, width, height, videoQuality, fps))
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to start ffmpeg at '{ffmpegPath}'.");
        }

        _stdin = _process.StandardInput.BaseStream;
        _stderrReaderTask = Task.Run(() => ReadStderrAsync(_process, cancellationToken), cancellationToken);

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited immediately while starting segment capture.{Environment.NewLine}{GetRecentErrorOutput()}");
        }
    }

    public async Task WriteFrameAsync(ReadOnlyMemory<byte> frameBuffer, CancellationToken cancellationToken)
    {
        if (_stdin is null)
        {
            throw new InvalidOperationException("The FFmpeg segment writer was not started.");
        }

        await _stdin.WriteAsync(frameBuffer, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (_stdin is not null)
            {
                await _stdin.FlushAsync().ConfigureAwait(false);
                await _stdin.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }

        _stdin = null;

        if (!_process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(_process);
            }
        }

        if (_stderrReaderTask is not null)
        {
            try
            {
                await _stderrReaderTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stderrReaderTask = null;
        _process.Dispose();
        _process = null;
    }

    public string GetRecentErrorOutput()
    {
        if (_stderrLines.IsEmpty)
        {
            return "ffmpeg did not emit any stderr output.";
        }

        var builder = new StringBuilder();

        foreach (var line in _stderrLines.TakeLast(25))
        {
            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static IEnumerable<string> BuildArguments(
        string segmentDirectory,
        string segmentPrefix,
        int width,
        int height,
        VideoQualityPreset videoQuality,
        int fps)
    {
        var outputSize = ResolveOutputSize(width, height, videoQuality);
        var segmentPattern = Path.Combine(segmentDirectory, $"{segmentPrefix}_%Y%m%d_%H%M%S.ts");

        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-video_size", $"{width}x{height}",
            "-framerate", fps.ToString(),
            "-i", "-",
            "-vf", $"scale={outputSize.Width}:{outputSize.Height}",
            "-an",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-pix_fmt", "yuv420p",
            "-crf", "23",
            "-g", fps.ToString(),
            "-keyint_min", fps.ToString(),
            "-sc_threshold", "0",
            "-force_key_frames", "expr:gte(t,n_forced*1)",
            "-f", "segment",
            "-segment_time", "1",
            "-segment_format", "mpegts",
            "-reset_timestamps", "1",
            "-strftime", "1",
            segmentPattern,
        ];
    }

    private static (int Width, int Height) ResolveOutputSize(int sourceWidth, int sourceHeight, VideoQualityPreset videoQuality)
    {
        var targetHeight = videoQuality switch
        {
            VideoQualityPreset.P1440 => 1440,
            VideoQualityPreset.P1080 => 1080,
            VideoQualityPreset.P720 => 720,
            _ => sourceHeight,
        };

        if (sourceHeight <= targetHeight)
        {
            return (MakeEven(sourceWidth), MakeEven(sourceHeight));
        }

        var scaledWidth = (int)Math.Round(sourceWidth * (targetHeight / (double)sourceHeight));
        return (MakeEven(scaledWidth), MakeEven(targetHeight));
    }

    private static int MakeEven(int value)
    {
        return value % 2 == 0 ? value : value - 1;
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        while (!process.StandardError.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _stderrLines.Enqueue(line);

            while (_stderrLines.Count > 100 && _stderrLines.TryDequeue(out _))
            {
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
