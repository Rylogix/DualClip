namespace DualClip.Core.Models;

public sealed class AppConfig
{
    public List<MonitorNodeConfig> MonitorNodes { get; set; } = [];

    public string? MonitorADeviceName { get; set; }

    public string? MonitorBDeviceName { get; set; }

    public int ReplayLengthSeconds { get; set; } = 30;

    public int FpsTarget { get; set; } = 30;

    public VideoQualityPreset VideoQuality { get; set; } = VideoQualityPreset.Original;

    public AudioCaptureMode AudioMode { get; set; } = AudioCaptureMode.None;

    public string? MicrophoneDeviceId { get; set; }

    public int ClipAudioVolumePercent { get; set; } = 100;

    public string OutputFolderA { get; set; } = string.Empty;

    public string OutputFolderB { get; set; } = string.Empty;

    public bool UseUnifiedOutputFolder { get; set; }

    public bool StartCaptureOnStartup { get; set; }

    public string FfmpegPath { get; set; } = string.Empty;

    public HotkeyGesture HotkeyA { get; set; } = new()
    {
        VirtualKey = 0x78,
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
    };

    public HotkeyGesture HotkeyB { get; set; } = new()
    {
        VirtualKey = 0x79,
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
    };

    public HotkeyGesture HotkeyBoth { get; set; } = new()
    {
        VirtualKey = 0x7A,
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
    };

    public static AppConfig CreateDefault(string ffmpegPath)
    {
        var videosRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var dualClipRoot = Path.Combine(videosRoot, "DualClip");
        var defaultNodes = CreateDefaultMonitorNodes(dualClipRoot);

        return new AppConfig
        {
            MonitorNodes = defaultNodes,
            MonitorADeviceName = defaultNodes.ElementAtOrDefault(0)?.MonitorDeviceName,
            MonitorBDeviceName = defaultNodes.ElementAtOrDefault(1)?.MonitorDeviceName,
            OutputFolderA = Path.Combine(dualClipRoot, "MonitorA"),
            OutputFolderB = Path.Combine(dualClipRoot, "MonitorB"),
            HotkeyA = defaultNodes.ElementAtOrDefault(0)?.Hotkey ?? HotkeyGesture.Disabled(),
            HotkeyB = defaultNodes.ElementAtOrDefault(1)?.Hotkey ?? HotkeyGesture.Disabled(),
            FfmpegPath = ffmpegPath,
        };
    }

    private static List<MonitorNodeConfig> CreateDefaultMonitorNodes(string dualClipRoot)
    {
        return
        [
            new MonitorNodeConfig
            {
                Name = "Monitor 1",
                OutputFolder = Path.Combine(dualClipRoot, "MonitorA"),
                Hotkey = new HotkeyGesture
                {
                    VirtualKey = 0x78,
                    Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
                },
            },
            new MonitorNodeConfig
            {
                Name = "Monitor 2",
                OutputFolder = Path.Combine(dualClipRoot, "MonitorB"),
                Hotkey = new HotkeyGesture
                {
                    VirtualKey = 0x79,
                    Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
                },
            },
        ];
    }
}
