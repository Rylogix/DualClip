namespace DualClip.Encoding;

public sealed class TimelineEditRequest
{
    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    public required string FfmpegPath { get; init; }

    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    public required int FpsTarget { get; init; }

    public required IReadOnlyList<TimelineEditSegment> Segments { get; init; }
}

public sealed class TimelineEditSegment
{
    public string SourceClipPath { get; init; } = string.Empty;

    public required double SourceStartSeconds { get; init; }

    public required double SourceEndSeconds { get; init; }

    public required double TimelineStartSeconds { get; init; }

    public int? CropX { get; init; }

    public int? CropY { get; init; }

    public int? CropWidth { get; init; }

    public int? CropHeight { get; init; }

    public double RotationDegrees { get; init; }

    public double ScalePercent { get; init; } = 100d;

    public double TranslateX { get; init; }

    public double TranslateY { get; init; }

    public bool FlipHorizontal { get; init; }

    public bool FlipVertical { get; init; }

    public double OpacityPercent { get; init; } = 100d;

    public double? ZoomKeyframe1TimeSeconds { get; init; }

    public double? ZoomKeyframe2TimeSeconds { get; init; }

    public double? ZoomKeyframe1Percent { get; init; }

    public double? ZoomKeyframe2Percent { get; init; }
}
