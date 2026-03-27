namespace DualClip.Core.Models;

public sealed class MonitorDescriptor
{
    public required string DeviceName { get; init; }

    public required string DisplayName { get; init; }

    public required ScreenBounds Bounds { get; init; }

    public required bool IsPrimary { get; init; }

    public required nint Handle { get; init; }

    public override string ToString() => DisplayName;
}
