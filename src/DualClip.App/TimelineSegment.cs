using System.Windows;

namespace DualClip.App;

public sealed class TimelineSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string SourceClipPath { get; set; } = string.Empty;

    public double SourceStartSeconds { get; set; }

    public double SourceEndSeconds { get; set; }

    public double TimelineStartSeconds { get; set; }

    public Rect CropRectSource { get; set; }

    public double RotationDegrees { get; set; }

    public double ScalePercent { get; set; } = 100d;

    public double TranslateX { get; set; }

    public double TranslateY { get; set; }

    public bool FlipHorizontal { get; set; }

    public bool FlipVertical { get; set; }

    public double OpacityPercent { get; set; } = 100d;

    public double PlaybackSpeed { get; set; } = 1d;

    public double ZoomKeyframe1OffsetSeconds { get; set; }

    public double ZoomKeyframe2OffsetSeconds { get; set; }

    public double ZoomKeyframe1Percent { get; set; } = 100d;

    public double ZoomKeyframe2Percent { get; set; } = 100d;

    public double DurationSeconds => Math.Max(0, SourceEndSeconds - SourceStartSeconds);

    public TimelineSegment CloneForSnapshot()
    {
        return CloneCore(Id);
    }

    public TimelineSegment DuplicateForTimeline()
    {
        return CloneCore(Guid.NewGuid());
    }

    public void CopyVisualSettingsFrom(TimelineSegment other)
    {
        CropRectSource = other.CropRectSource;
        RotationDegrees = other.RotationDegrees;
        ScalePercent = other.ScalePercent;
        TranslateX = other.TranslateX;
        TranslateY = other.TranslateY;
        FlipHorizontal = other.FlipHorizontal;
        FlipVertical = other.FlipVertical;
        OpacityPercent = other.OpacityPercent;
        PlaybackSpeed = other.PlaybackSpeed;
        ZoomKeyframe1OffsetSeconds = other.ZoomKeyframe1OffsetSeconds;
        ZoomKeyframe2OffsetSeconds = other.ZoomKeyframe2OffsetSeconds;
        ZoomKeyframe1Percent = other.ZoomKeyframe1Percent;
        ZoomKeyframe2Percent = other.ZoomKeyframe2Percent;
    }

    private TimelineSegment CloneCore(Guid id)
    {
        return new TimelineSegment
        {
            Id = id,
            SourceClipPath = SourceClipPath,
            SourceStartSeconds = SourceStartSeconds,
            SourceEndSeconds = SourceEndSeconds,
            TimelineStartSeconds = TimelineStartSeconds,
            CropRectSource = CropRectSource,
            RotationDegrees = RotationDegrees,
            ScalePercent = ScalePercent,
            TranslateX = TranslateX,
            TranslateY = TranslateY,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
            OpacityPercent = OpacityPercent,
            PlaybackSpeed = PlaybackSpeed,
            ZoomKeyframe1OffsetSeconds = ZoomKeyframe1OffsetSeconds,
            ZoomKeyframe2OffsetSeconds = ZoomKeyframe2OffsetSeconds,
            ZoomKeyframe1Percent = ZoomKeyframe1Percent,
            ZoomKeyframe2Percent = ZoomKeyframe2Percent,
        };
    }
}
