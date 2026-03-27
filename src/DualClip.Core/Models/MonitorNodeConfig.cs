namespace DualClip.Core.Models;

public sealed class MonitorNodeConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string? MonitorDeviceName { get; set; }

    public string OutputFolder { get; set; } = string.Empty;

    public HotkeyGesture Hotkey { get; set; } = HotkeyGesture.Disabled();
}
