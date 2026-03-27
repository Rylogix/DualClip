namespace DualClip.Encoding;

public sealed class FfmpegTimelineEditor
{
    private readonly FfmpegClipAssembler _clipAssembler = new();
    private readonly FfmpegClipEditor _clipEditor = new();

    public async Task ExportAsync(TimelineEditRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException($"The source clip '{request.InputPath}' was not found.");
        }

        if (request.Segments.Count == 0)
        {
            throw new InvalidOperationException("The timeline is empty. Add or keep at least one segment before exporting.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);

        var tempRoot = Path.Combine(Path.GetTempPath(), $"dualclip_timeline_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var tempSegmentPaths = new List<string>(request.Segments.Count);

            for (var index = 0; index < request.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = request.Segments[index];
                var tempSegmentPath = Path.Combine(tempRoot, $"segment_{index:D4}.mp4");

                await _clipEditor.EditAsync(
                    new VideoEditRequest
                    {
                        InputPath = request.InputPath,
                        OutputPath = tempSegmentPath,
                        FfmpegPath = request.FfmpegPath,
                        SourceWidth = request.SourceWidth,
                        SourceHeight = request.SourceHeight,
                        FpsTarget = request.FpsTarget,
                        TrimStartSeconds = segment.SourceStartSeconds,
                        TrimEndSeconds = segment.SourceEndSeconds,
                        CropX = segment.CropX,
                        CropY = segment.CropY,
                        CropWidth = segment.CropWidth,
                        CropHeight = segment.CropHeight,
                        RotationDegrees = segment.RotationDegrees,
                        ScalePercent = segment.ScalePercent,
                        TranslateX = segment.TranslateX,
                        TranslateY = segment.TranslateY,
                        FlipHorizontal = segment.FlipHorizontal,
                        FlipVertical = segment.FlipVertical,
                        OpacityPercent = segment.OpacityPercent,
                        ZoomKeyframe1TimeSeconds = segment.ZoomKeyframe1TimeSeconds,
                        ZoomKeyframe2TimeSeconds = segment.ZoomKeyframe2TimeSeconds,
                        ZoomKeyframe1Percent = segment.ZoomKeyframe1Percent,
                        ZoomKeyframe2Percent = segment.ZoomKeyframe2Percent,
                    },
                    cancellationToken).ConfigureAwait(false);

                tempSegmentPaths.Add(tempSegmentPath);
            }

            if (tempSegmentPaths.Count == 1)
            {
                TryDelete(request.OutputPath);
                File.Move(tempSegmentPaths[0], request.OutputPath, overwrite: true);
                return;
            }

            await _clipAssembler.BuildClipAsync(
                request.FfmpegPath,
                tempSegmentPaths,
                audioSegmentPaths: null,
                clipAudioVolumePercent: 100d,
                request.OutputPath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
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
        catch
        {
        }
    }
}
