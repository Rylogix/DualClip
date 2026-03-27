namespace DualClip.App;

public sealed class HotkeyKeyOption
{
    public required uint VirtualKey { get; init; }

    public required string DisplayName { get; init; }

    public override string ToString() => DisplayName;
}
