using System.Text.Json;
using System.IO;
using DualClip.Core.Models;

namespace DualClip.Infrastructure;

public sealed class JsonAppConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.AppDataRoot);

        if (!File.Exists(AppPaths.ConfigPath))
        {
            return AppConfig.CreateDefault(AppPaths.ResolveDefaultFfmpegPath());
        }

        try
        {
            await using var stream = File.OpenRead(AppPaths.ConfigPath);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, SerializerOptions, cancellationToken);

            if (config is null)
            {
                throw new InvalidOperationException("The config file was empty.");
            }

            return Normalize(config);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidOperationException(
                $"Failed to read config file at '{AppPaths.ConfigPath}'. {ex.Message}",
                ex);
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.AppDataRoot);

        await using var stream = File.Create(AppPaths.ConfigPath);
        await JsonSerializer.SerializeAsync(stream, Normalize(config), SerializerOptions, cancellationToken);
    }

    private static AppConfig Normalize(AppConfig config)
    {
        var defaults = AppConfig.CreateDefault(AppPaths.ResolveDefaultFfmpegPath());

        config.ReplayLengthSeconds = config.ReplayLengthSeconds <= 0 ? defaults.ReplayLengthSeconds : config.ReplayLengthSeconds;
        config.FpsTarget = config.FpsTarget <= 0 ? defaults.FpsTarget : config.FpsTarget;
        config.VideoQuality = Enum.IsDefined(config.VideoQuality) ? config.VideoQuality : defaults.VideoQuality;
        config.AudioMode = Enum.IsDefined(config.AudioMode) ? config.AudioMode : defaults.AudioMode;
        config.MicrophoneDeviceId = string.IsNullOrWhiteSpace(config.MicrophoneDeviceId) ? null : config.MicrophoneDeviceId;
        config.OutputFolderA = string.IsNullOrWhiteSpace(config.OutputFolderA) ? defaults.OutputFolderA : config.OutputFolderA;
        config.OutputFolderB = string.IsNullOrWhiteSpace(config.OutputFolderB) ? defaults.OutputFolderB : config.OutputFolderB;
        config.FfmpegPath = string.IsNullOrWhiteSpace(config.FfmpegPath) || !File.Exists(config.FfmpegPath)
            ? defaults.FfmpegPath
            : config.FfmpegPath;
        config.HotkeyA ??= defaults.HotkeyA;
        config.HotkeyB ??= defaults.HotkeyB;
        config.HotkeyBoth ??= defaults.HotkeyBoth;
        config.MonitorNodes = NormalizeMonitorNodes(config, defaults);

        return config;
    }

    private static List<MonitorNodeConfig> NormalizeMonitorNodes(AppConfig config, AppConfig defaults)
    {
        var sourceNodes = config.MonitorNodes is { Count: > 0 }
            ? config.MonitorNodes
            : BuildLegacyMonitorNodes(config, defaults);

        var normalizedNodes = sourceNodes
            .Select((node, index) => NormalizeMonitorNode(node, defaults, index))
            .ToList();

        if (normalizedNodes.Count == 0)
        {
            normalizedNodes = defaults.MonitorNodes
                .Select((node, index) => NormalizeMonitorNode(node, defaults, index))
                .ToList();
        }

        config.MonitorADeviceName = normalizedNodes.ElementAtOrDefault(0)?.MonitorDeviceName;
        config.MonitorBDeviceName = normalizedNodes.ElementAtOrDefault(1)?.MonitorDeviceName;
        config.OutputFolderA = normalizedNodes.ElementAtOrDefault(0)?.OutputFolder ?? defaults.OutputFolderA;
        config.OutputFolderB = normalizedNodes.ElementAtOrDefault(1)?.OutputFolder ?? normalizedNodes.ElementAtOrDefault(0)?.OutputFolder ?? defaults.OutputFolderB;
        config.HotkeyA = CloneHotkey(normalizedNodes.ElementAtOrDefault(0)?.Hotkey ?? defaults.HotkeyA);
        config.HotkeyB = CloneHotkey(normalizedNodes.ElementAtOrDefault(1)?.Hotkey ?? defaults.HotkeyB);

        return normalizedNodes;
    }

    private static List<MonitorNodeConfig> BuildLegacyMonitorNodes(AppConfig config, AppConfig defaults)
    {
        var legacyNodes = new List<MonitorNodeConfig>();

        if (!string.IsNullOrWhiteSpace(config.OutputFolderA)
            || !string.IsNullOrWhiteSpace(config.MonitorADeviceName)
            || config.HotkeyA?.IsEnabled == true)
        {
            legacyNodes.Add(new MonitorNodeConfig
            {
                Name = "Monitor 1",
                MonitorDeviceName = config.MonitorADeviceName,
                OutputFolder = config.OutputFolderA,
                Hotkey = CloneHotkey(config.HotkeyA ?? defaults.HotkeyA),
            });
        }

        if (!string.IsNullOrWhiteSpace(config.OutputFolderB)
            || !string.IsNullOrWhiteSpace(config.MonitorBDeviceName)
            || config.HotkeyB?.IsEnabled == true)
        {
            legacyNodes.Add(new MonitorNodeConfig
            {
                Name = "Monitor 2",
                MonitorDeviceName = config.MonitorBDeviceName,
                OutputFolder = config.OutputFolderB,
                Hotkey = CloneHotkey(config.HotkeyB ?? defaults.HotkeyB),
            });
        }

        return legacyNodes;
    }

    private static MonitorNodeConfig NormalizeMonitorNode(MonitorNodeConfig source, AppConfig defaults, int index)
    {
        var defaultNode = defaults.MonitorNodes.ElementAtOrDefault(index);
        var defaultOutputFolder = defaultNode?.OutputFolder
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DualClip", $"Monitor{index + 1}");

        return new MonitorNodeConfig
        {
            Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name) ? $"Monitor {index + 1}" : source.Name.Trim(),
            MonitorDeviceName = string.IsNullOrWhiteSpace(source.MonitorDeviceName) ? null : source.MonitorDeviceName,
            OutputFolder = string.IsNullOrWhiteSpace(source.OutputFolder) ? defaultOutputFolder : source.OutputFolder,
            Hotkey = CloneHotkey(source.Hotkey ?? defaultNode?.Hotkey ?? HotkeyGesture.Disabled()),
        };
    }

    private static HotkeyGesture CloneHotkey(HotkeyGesture hotkey)
    {
        return new HotkeyGesture
        {
            VirtualKey = hotkey.VirtualKey,
            Modifiers = hotkey.Modifiers,
        };
    }
}
