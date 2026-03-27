using DualClip.Core.Models;

namespace DualClip.Capture;

public sealed class MonitorCaptureSessionOptions
{
    public required string SlotName { get; init; }

    public required MonitorDescriptor Monitor { get; init; }

    public required string FfmpegPath { get; init; }

    public required string BufferDirectory { get; init; }

    public required int ReplayLengthSeconds { get; init; }

    public required int FpsTarget { get; init; }

    public required VideoQualityPreset VideoQuality { get; init; }

    public bool PreferBorderlessCapture { get; init; }
}
