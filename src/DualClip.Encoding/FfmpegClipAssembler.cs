using System.Globalization;
using System.Text.RegularExpressions;

namespace DualClip.Encoding;

public sealed class FfmpegClipAssembler
{
    private const double SegmentDurationSeconds = 1d;
    private static readonly Regex SegmentTimestampRegex = new(
        @"(?<timestamp>\d{8}_\d{6})(?=\.[^.]+$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FfmpegProcessRunner _runner = new();

    public async Task<string> BuildClipAsync(
        string ffmpegPath,
        IReadOnlyList<string> segmentPaths,
        IReadOnlyList<string>? audioSegmentPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (segmentPaths.Count == 0)
        {
            throw new InvalidOperationException("No completed segment files were available yet.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"dualclip_video_{Guid.NewGuid():N}.mp4");
        var tempAudioPath = Path.Combine(Path.GetTempPath(), $"dualclip_audio_{Guid.NewGuid():N}.m4a");
        var videoConcatPath = Path.Combine(Path.GetTempPath(), $"dualclip_video_concat_{Guid.NewGuid():N}.txt");
        var audioConcatPath = Path.Combine(Path.GetTempPath(), $"dualclip_audio_concat_{Guid.NewGuid():N}.txt");
        var videoStageDirectory = Path.Combine(Path.GetTempPath(), $"dualclip_video_stage_{Guid.NewGuid():N}");
        var audioStageDirectory = Path.Combine(Path.GetTempPath(), $"dualclip_audio_stage_{Guid.NewGuid():N}");

        try
        {
            var videoSegments = ParseSegments(segmentPaths);
            var stagedVideoSegments = await StageSegmentsAsync(videoSegments, videoStageDirectory, cancellationToken).ConfigureAwait(false);

            if (stagedVideoSegments.Count == 0)
            {
                throw new InvalidOperationException("The replay buffer advanced before DualClip could snapshot the selected video segments. Try saving again.");
            }

            await File.WriteAllLinesAsync(videoConcatPath, stagedVideoSegments.Select(segment => BuildConcatLine(segment.Path)), cancellationToken).ConfigureAwait(false);
            await BuildVideoAsync(ffmpegPath, videoConcatPath, tempVideoPath, cancellationToken).ConfigureAwait(false);

            if (audioSegmentPaths is null || audioSegmentPaths.Count == 0)
            {
                TryDelete(outputPath);
                File.Move(tempVideoPath, outputPath, overwrite: true);
                return outputPath;
            }

            var audioSegments = ParseSegments(audioSegmentPaths);
            var alignedAudioSegments = SelectAudioSegmentsForVideoWindow(audioSegments, stagedVideoSegments);

            if (alignedAudioSegments.Count == 0)
            {
                TryDelete(outputPath);
                File.Move(tempVideoPath, outputPath, overwrite: true);
                return outputPath;
            }

            var stagedAudioSegments = await StageSegmentsAsync(alignedAudioSegments, audioStageDirectory, cancellationToken).ConfigureAwait(false);

            if (stagedAudioSegments.Count == 0)
            {
                TryDelete(outputPath);
                File.Move(tempVideoPath, outputPath, overwrite: true);
                return outputPath;
            }

            try
            {
                var audioAlignment = BuildAudioAlignment(stagedVideoSegments, stagedAudioSegments);
                await File.WriteAllLinesAsync(audioConcatPath, stagedAudioSegments.Select(segment => BuildConcatLine(segment.Path)), cancellationToken).ConfigureAwait(false);
                await BuildAudioAsync(ffmpegPath, audioConcatPath, tempAudioPath, cancellationToken).ConfigureAwait(false);
                await MuxAudioAsync(ffmpegPath, tempVideoPath, tempAudioPath, outputPath, audioAlignment, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Keep the clip save working even if the audio buffer advanced during assembly.
                TryDelete(outputPath);
                File.Move(tempVideoPath, outputPath, overwrite: true);
            }

            return outputPath;
        }
        finally
        {
            TryDelete(videoConcatPath);
            TryDelete(audioConcatPath);
            TryDelete(tempVideoPath);
            TryDelete(tempAudioPath);
            TryDeleteDirectory(videoStageDirectory);
            TryDeleteDirectory(audioStageDirectory);
        }
    }

    private async Task BuildVideoAsync(string ffmpegPath, string concatPath, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            await TryCopyAsync(ffmpegPath, concatPath, outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(outputPath);
            await ReencodeVideoAsync(ffmpegPath, concatPath, outputPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryCopyAsync(string ffmpegPath, string concatPath, string outputPath, CancellationToken cancellationToken)
    {
        await _runner.RunAsync(
            ffmpegPath,
            [
                "-hide_banner",
                "-loglevel", "warning",
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", concatPath,
                "-c", "copy",
                "-movflags", "+faststart",
                outputPath,
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReencodeVideoAsync(string ffmpegPath, string concatPath, string outputPath, CancellationToken cancellationToken)
    {
        await _runner.RunAsync(
            ffmpegPath,
            [
                "-hide_banner",
                "-loglevel", "warning",
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", concatPath,
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-movflags", "+faststart",
                outputPath,
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task BuildAudioAsync(string ffmpegPath, string concatPath, string outputPath, CancellationToken cancellationToken)
    {
        await _runner.RunAsync(
            ffmpegPath,
            [
                "-hide_banner",
                "-loglevel", "warning",
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", concatPath,
                "-c:a", "aac",
                "-b:a", "192k",
                outputPath,
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MuxAudioAsync(
        string ffmpegPath,
        string videoPath,
        string audioPath,
        string outputPath,
        AudioAlignment audioAlignment,
        CancellationToken cancellationToken)
    {
        TryDelete(outputPath);

        var audioFilter = BuildAudioAlignmentFilter(audioAlignment);

        await _runner.RunAsync(
            ffmpegPath,
            [
                "-hide_banner",
                "-loglevel", "warning",
                "-y",
                "-i", videoPath,
                "-i", audioPath,
                "-filter_complex", audioFilter,
                "-map", "0:v:0",
                "-map", "[aout]",
                "-c:v", "copy",
                "-c:a", "aac",
                "-b:a", "192k",
                "-movflags", "+faststart",
                outputPath,
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildConcatLine(string path)
    {
        var normalized = Path.GetFullPath(path).Replace("\\", "/").Replace("'", "'\\''");
        return $"file '{normalized}'";
    }

    private static async Task<IReadOnlyList<BufferedSegment>> StageSegmentsAsync(
        IReadOnlyList<BufferedSegment> segments,
        string stageDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stageDirectory);

        var stagedPaths = new List<BufferedSegment>(segments.Count);

        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceSegment = segments[index];
            var sourcePath = sourceSegment.Path;

            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var extension = Path.GetExtension(sourcePath);
            var destinationPath = Path.Combine(stageDirectory, $"{index:D4}{extension}");

            try
            {
                await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
                stagedPaths.Add(sourceSegment with { Path = destinationPath });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return stagedPaths;
    }

    private static IReadOnlyList<BufferedSegment> ParseSegments(IReadOnlyList<string> segmentPaths)
    {
        return segmentPaths
            .Select(path => new BufferedSegment(path, TryParseTimestamp(path)))
            .ToList();
    }

    private static IReadOnlyList<BufferedSegment> SelectAudioSegmentsForVideoWindow(
        IReadOnlyList<BufferedSegment> audioSegments,
        IReadOnlyList<BufferedSegment> videoSegments)
    {
        if (audioSegments.Count == 0)
        {
            return [];
        }

        if (videoSegments.Count == 0)
        {
            return audioSegments;
        }

        var targetAudioCount = Math.Max(1, videoSegments.Count + 2);

        if (videoSegments[^1].Timestamp is not DateTime videoEndTimestamp)
        {
            return audioSegments.TakeLast(Math.Min(audioSegments.Count, targetAudioCount)).ToList();
        }

        var eligibleAudioSegments = audioSegments
            .Where(segment => segment.Timestamp is not DateTime timestamp
                || timestamp <= videoEndTimestamp.AddSeconds(SegmentDurationSeconds))
            .ToList();

        if (eligibleAudioSegments.Count == 0)
        {
            eligibleAudioSegments = audioSegments.ToList();
        }

        return eligibleAudioSegments
            .TakeLast(Math.Min(eligibleAudioSegments.Count, targetAudioCount))
            .ToList();
    }

    private static AudioAlignment BuildAudioAlignment(
        IReadOnlyList<BufferedSegment> videoSegments,
        IReadOnlyList<BufferedSegment> audioSegments)
    {
        var targetDurationSeconds = Math.Max(SegmentDurationSeconds, videoSegments.Count * SegmentDurationSeconds);

        if (!TryGetSyntheticSegmentStart(videoSegments, out var videoStart)
            || !TryGetSyntheticSegmentStart(audioSegments, out var audioStart))
        {
            return new AudioAlignment(0d, targetDurationSeconds);
        }

        var offsetSeconds = (audioStart - videoStart).TotalSeconds;

        if (Math.Abs(offsetSeconds) >= targetDurationSeconds)
        {
            offsetSeconds = 0d;
        }

        return new AudioAlignment(offsetSeconds, targetDurationSeconds);
    }

    private static string BuildAudioAlignmentFilter(AudioAlignment audioAlignment)
    {
        var duration = audioAlignment.TargetDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        if (audioAlignment.OffsetSeconds > 0.0005d)
        {
            var delayMilliseconds = Math.Max(0, (int)Math.Round(audioAlignment.OffsetSeconds * 1000d, MidpointRounding.AwayFromZero));
            return $"[1:a]adelay={delayMilliseconds}:all=1,apad,atrim=duration={duration}[aout]";
        }

        if (audioAlignment.OffsetSeconds < -0.0005d)
        {
            var trimStart = Math.Abs(audioAlignment.OffsetSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            return $"[1:a]atrim=start={trimStart},asetpts=PTS-STARTPTS,apad,atrim=duration={duration}[aout]";
        }

        return $"[1:a]apad,atrim=duration={duration}[aout]";
    }

    private static bool TryGetSyntheticSegmentStart(
        IReadOnlyList<BufferedSegment> segments,
        out DateTime syntheticStart)
    {
        syntheticStart = default;

        if (segments.Count == 0 || segments[^1].Timestamp is not DateTime lastTimestamp)
        {
            return false;
        }

        syntheticStart = lastTimestamp.AddSeconds(-(segments.Count - 1) * SegmentDurationSeconds);
        return true;
    }

    private static DateTime? TryParseTimestamp(string path)
    {
        var fileName = Path.GetFileName(path);
        var match = SegmentTimestampRegex.Match(fileName);

        if (!match.Success)
        {
            return null;
        }

        return DateTime.TryParseExact(
            match.Groups["timestamp"].Value,
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var timestamp)
            ? timestamp
            : null;
    }

    private readonly record struct AudioAlignment(double OffsetSeconds, double TargetDurationSeconds);

    private readonly record struct BufferedSegment(string Path, DateTime? Timestamp);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
