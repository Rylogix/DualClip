namespace DualClip.App;

public sealed class SelectionOption<T>
{
    public required string Label { get; init; }

    public required T Value { get; init; }

    public override string ToString() => Label;
}
