namespace DualClip.Core.Models;

public sealed class AudioDeviceDescriptor
{
    public required string Id { get; init; }

    public required string FriendlyName { get; init; }

    public override string ToString() => FriendlyName;
}
