namespace DualClip.Core.Models;

public sealed class HotkeyGesture
{
    public uint VirtualKey { get; set; }

    public HotkeyModifiers Modifiers { get; set; }

    public bool IsEnabled => VirtualKey != 0;

    public static HotkeyGesture Disabled() => new();

    public string ToDisplayString(Func<uint, string>? keyNameResolver = null)
    {
        if (!IsEnabled)
        {
            return "Disabled";
        }

        var parts = new List<string>();

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(keyNameResolver?.Invoke(VirtualKey) ?? $"VK {VirtualKey}");
        return string.Join(" + ", parts);
    }

    public string ToStableId() => $"{(uint)Modifiers}:{VirtualKey}";
}
