using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DualClip.Core.Models;

namespace DualClip.App;

public sealed class MainWindowViewModel : BindableObject
{
    private SelectionOption<VideoQualityPreset>? _selectedVideoQuality;
    private SelectionOption<AudioCaptureMode>? _selectedAudioMode;
    private AudioDeviceDescriptor? _selectedMicrophone;
    private string _replayLengthSecondsText = "30";
    private string _fpsTargetText = "30";
    private string _ffmpegPath = string.Empty;
    private string _appStatus = "Ready.";
    private string _editorStatus = "No clip selected.";
    private string _errorMessage = string.Empty;
    private string _currentVersionText = "Current version: unknown";
    private string _updateStatusText = "Checks GitHub releases for new portable builds.";
    private string _installUpdateButtonText = "Install Update";
    private bool _isCapturing;
    private bool _startCaptureOnStartup;
    private bool _startInBackgroundOnStartup;
    private bool _isSettingsTabSelected;
    private bool _isCheckingForUpdates;
    private bool _isUpdateAvailable;

    public MainWindowViewModel()
    {
        Editor = new EditorSurfaceState();
        MonitorNodes.CollectionChanged += MonitorNodes_CollectionChanged;

        VideoQualities.Add(new SelectionOption<VideoQualityPreset> { Label = "Original", Value = VideoQualityPreset.Original });
        VideoQualities.Add(new SelectionOption<VideoQualityPreset> { Label = "1440p", Value = VideoQualityPreset.P1440 });
        VideoQualities.Add(new SelectionOption<VideoQualityPreset> { Label = "1080p", Value = VideoQualityPreset.P1080 });
        VideoQualities.Add(new SelectionOption<VideoQualityPreset> { Label = "720p", Value = VideoQualityPreset.P720 });

        AudioModes.Add(new SelectionOption<AudioCaptureMode> { Label = "Disabled", Value = AudioCaptureMode.None });
        AudioModes.Add(new SelectionOption<AudioCaptureMode> { Label = "System Audio", Value = AudioCaptureMode.System });
        AudioModes.Add(new SelectionOption<AudioCaptureMode> { Label = "Microphone", Value = AudioCaptureMode.Microphone });
    }

    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];

    public ObservableCollection<MonitorNodeViewModel> MonitorNodes { get; } = [];

    public ObservableCollection<SelectionOption<VideoQualityPreset>> VideoQualities { get; } = [];

    public ObservableCollection<SelectionOption<AudioCaptureMode>> AudioModes { get; } = [];

    public ObservableCollection<AudioDeviceDescriptor> Microphones { get; } = [];

    public ObservableCollection<ClipLibraryItem> ClipLibrary { get; } = [];

    public EditorSurfaceState Editor { get; }

    public SelectionOption<VideoQualityPreset>? SelectedVideoQuality
    {
        get => _selectedVideoQuality;
        set => SetProperty(ref _selectedVideoQuality, value);
    }

    public SelectionOption<AudioCaptureMode>? SelectedAudioMode
    {
        get => _selectedAudioMode;
        set
        {
            if (SetProperty(ref _selectedAudioMode, value))
            {
                RaisePropertyChanged(nameof(IsMicrophoneSelectionEnabled));
            }
        }
    }

    public AudioDeviceDescriptor? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set => SetProperty(ref _selectedMicrophone, value);
    }

    public string ReplayLengthSecondsText
    {
        get => _replayLengthSecondsText;
        set => SetProperty(ref _replayLengthSecondsText, value);
    }

    public string FpsTargetText
    {
        get => _fpsTargetText;
        set => SetProperty(ref _fpsTargetText, value);
    }

    public string FfmpegPath
    {
        get => _ffmpegPath;
        set => SetProperty(ref _ffmpegPath, value);
    }

    public bool StartCaptureOnStartup
    {
        get => _startCaptureOnStartup;
        set => SetProperty(ref _startCaptureOnStartup, value);
    }

    public bool StartInBackgroundOnStartup
    {
        get => _startInBackgroundOnStartup;
        set => SetProperty(ref _startInBackgroundOnStartup, value);
    }

    public string AppStatus
    {
        get => _appStatus;
        set => SetProperty(ref _appStatus, value);
    }

    public string EditorStatus
    {
        get => _editorStatus;
        set => SetProperty(ref _editorStatus, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string CurrentVersionText
    {
        get => _currentVersionText;
        set => SetProperty(ref _currentVersionText, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => SetProperty(ref _updateStatusText, value);
    }

    public string InstallUpdateButtonText
    {
        get => _installUpdateButtonText;
        set => SetProperty(ref _installUpdateButtonText, value);
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                RaisePropertyChanged(nameof(CanStart));
                RaisePropertyChanged(nameof(CanStop));
                RaisePropertyChanged(nameof(CanSave));
                RaisePropertyChanged(nameof(CanSaveAll));
            }
        }
    }

    public bool CanStart => !IsCapturing;

    public bool CanStop => IsCapturing;

    public bool CanSave => IsCapturing && MonitorNodes.Count > 0;

    public bool CanSaveAll => IsCapturing && MonitorNodes.Count > 1;

    public bool CanRemoveMonitorNodes => MonitorNodes.Count > 1;

    public bool IsMicrophoneSelectionEnabled => SelectedAudioMode?.Value == AudioCaptureMode.Microphone;

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                RaisePropertyChanged(nameof(CanCheckForUpdates));
                RaisePropertyChanged(nameof(CanInstallUpdate));
            }
        }
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (SetProperty(ref _isUpdateAvailable, value))
            {
                RaisePropertyChanged(nameof(CanInstallUpdate));
            }
        }
    }

    public bool CanCheckForUpdates => !IsCheckingForUpdates;

    public bool CanInstallUpdate => IsUpdateAvailable && !IsCheckingForUpdates;

    public bool IsSettingsTabSelected
    {
        get => _isSettingsTabSelected;
        set
        {
            if (SetProperty(ref _isSettingsTabSelected, value))
            {
                RaisePropertyChanged(nameof(IsCaptureTabSelected));
            }
        }
    }

    public bool IsCaptureTabSelected
    {
        get => !_isSettingsTabSelected;
        set => IsSettingsTabSelected = !value;
    }

    public void ApplyConfig(
        AppConfig config,
        IReadOnlyList<MonitorDescriptor> monitors,
        IReadOnlyList<AudioDeviceDescriptor> microphones)
    {
        Monitors.Clear();

        foreach (var monitor in monitors.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.DeviceName))
        {
            Monitors.Add(monitor);
        }

        Microphones.Clear();

        foreach (var microphone in microphones.OrderBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            Microphones.Add(microphone);
        }

        SelectedVideoQuality = VideoQualities.FirstOrDefault(item => item.Value == config.VideoQuality) ?? VideoQualities.FirstOrDefault();
        SelectedAudioMode = AudioModes.FirstOrDefault(item => item.Value == config.AudioMode) ?? AudioModes.FirstOrDefault();
        SelectedMicrophone = Microphones.FirstOrDefault(item => item.Id == config.MicrophoneDeviceId) ?? Microphones.FirstOrDefault();

        ReplayLengthSecondsText = config.ReplayLengthSeconds.ToString();
        FpsTargetText = config.FpsTarget.ToString();
        StartCaptureOnStartup = config.StartCaptureOnStartup;
        StartInBackgroundOnStartup = config.StartInBackgroundOnStartup;
        FfmpegPath = config.FfmpegPath;

        var usedMonitorDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MonitorNodes.Clear();

        foreach (var nodeConfig in config.MonitorNodes)
        {
            var node = new MonitorNodeViewModel(
                string.IsNullOrWhiteSpace(nodeConfig.Id) ? Guid.NewGuid().ToString("N") : nodeConfig.Id,
                string.IsNullOrWhiteSpace(nodeConfig.Name) ? $"Monitor {MonitorNodes.Count + 1}" : nodeConfig.Name,
                nodeConfig.OutputFolder);

            node.LoadHotkey(nodeConfig.Hotkey ?? HotkeyGesture.Disabled());

            var selectedMonitor = Monitors.FirstOrDefault(item => item.DeviceName == nodeConfig.MonitorDeviceName)
                ?? Monitors.FirstOrDefault(item => !usedMonitorDeviceNames.Contains(item.DeviceName))
                ?? Monitors.FirstOrDefault();

            node.SelectedMonitor = selectedMonitor;

            if (selectedMonitor is not null)
            {
                usedMonitorDeviceNames.Add(selectedMonitor.DeviceName);
            }

            MonitorNodes.Add(node);
        }
    }

    private void MonitorNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaisePropertyChanged(nameof(CanSave));
        RaisePropertyChanged(nameof(CanSaveAll));
        RaisePropertyChanged(nameof(CanRemoveMonitorNodes));
    }
}
