using DualClip.Core.Models;

namespace DualClip.Capture;

public sealed class AudioReplaySessionOptions
{
    public required string BufferDirectory { get; init; }

    public required int ReplayLengthSeconds { get; init; }

    public required AudioCaptureMode AudioMode { get; init; }

    public string? MicrophoneDeviceId { get; init; }
}
