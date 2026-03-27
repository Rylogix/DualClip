namespace DualClip.Core.Models;

public readonly record struct ScreenBounds(int Left, int Top, int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height} @ {Left},{Top}";
}
