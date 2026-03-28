using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Drawing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using DualClip.Capture;
using DualClip.Core.Models;
using DualClip.Encoding;
using DualClip.Infrastructure;
using Forms = System.Windows.Forms;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace DualClip.App;

public partial class MainWindow : Window
{
    private const string MonitorHotkeyPrefix = "monitor-node-";
    private const double MinimumTrimDurationSeconds = 0.1d;
    private const double MinimumCropSizePixels = 32d;
    private const double PreviewPlaybackSegmentEndToleranceSeconds = 0.03d;
    private const int AudioAlignmentContextSeconds = 12;
    private static readonly TimeSpan ClipSaveCooldown = TimeSpan.FromSeconds(3);

    private readonly JsonAppConfigStore _configStore = new();
    private readonly GitHubReleaseUpdateService _updateService = new();
    private readonly MonitorEnumerationService _monitorService = new();
    private readonly GlobalHotkeyManager _hotkeyManager = new();
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly MainWindowViewModel _viewModel = new();
    private readonly FfmpegTimelineEditor _timelineEditor = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _previewTimer;
    private readonly Dictionary<string, MonitorCaptureSession> _monitorSessionsByNodeId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextClipAllowedAtByNodeId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MonitorCaptureSession, MonitorNodeViewModel> _monitorNodesBySession = [];
    private readonly object _clipCooldownLock = new();
    private readonly MediaElement _previewMediaElement;
    private long _previewPlaybackRequestId;
    private bool _isExitRequested;
    private bool _hasShownTrayTip;
    private bool _hasShownUpdateTip;
    private bool _isEditorBusy;
    private bool _isUpdatingTransformControls;
    private double _selectedClipDurationSeconds;
    private int _selectedClipWidth;
    private int _selectedClipHeight;
    private double _trimStartSeconds;
    private double _trimEndSeconds;
    private double _playheadSeconds;
    private double _zoomKeyframe1Seconds;
    private double _zoomKeyframe2Seconds;
    private double _zoomKeyframe1Percent = 100d;
    private double _zoomKeyframe2Percent = 100d;
    private Rect _cropRectSource;
    private Rect _displayedVideoRect;
    private double _rotationDegrees;
    private double _scalePercent = 100d;
    private double _translateX;
    private double _translateY;
    private double _opacityPercent = 100d;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private AudioReplaySession? _audioSession;
    private GitHubUpdateRelease? _pendingUpdate;

    public MainWindow()
    {
        StartupDiagnostics.Write("MainWindow ctor entered.");
        InitializeComponent();
        StartupDiagnostics.Write("MainWindow InitializeComponent completed.");
        _previewMediaElement = CreatePreviewMediaElement();
        PreviewMediaHost.Children.Add(_previewMediaElement);
        StartupDiagnostics.Write("MainWindow preview media element created.");
        DataContext = _viewModel;
        _viewModel.CurrentVersionText = $"Current version: v{_updateService.CurrentVersionText}";
        PreviewMediaElement.SpeedRatio = 1.0d;
        StartupDiagnostics.Write("MainWindow creating notify icon.");
        _notifyIcon = CreateNotifyIcon();
        StartupDiagnostics.Write("MainWindow notify icon created.");
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.MonitorNodes.CollectionChanged += MonitorNodes_CollectionChanged;
        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _previewTimer.Tick += PreviewTimer_Tick;
        UpdateWindowFrameState();
        UpdateEditorControlState();
        StartupDiagnostics.Write("MainWindow ctor completed.");
    }

    private MediaElement PreviewMediaElement => _previewMediaElement;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartupDiagnostics.Write("Window_Loaded entered.");
        await LoadStateAsync();
        StartupDiagnostics.Write("Window_Loaded completed.");
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        _hotkeyManager.Attach(windowHandle);
        _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowFrameState();
    }

    private void CustomTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ToggleMaximizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximized();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.MonitorNodes.CollectionChanged -= MonitorNodes_CollectionChanged;
        foreach (var node in _viewModel.MonitorNodes)
        {
            node.PropertyChanged -= MonitorNode_PropertyChanged;
        }
        _hotkeyManager.HotkeyPressed -= HotkeyManager_HotkeyPressed;
        _hotkeyManager.Dispose();

        try
        {
            StopCaptureAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        base.OnClosed(e);
    }

    private MediaElement CreatePreviewMediaElement()
    {
        var mediaElement = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = System.Windows.Media.Stretch.Uniform,
        };

        mediaElement.MediaOpened += PreviewMediaElement_MediaOpened;
        mediaElement.MediaFailed += PreviewMediaElement_MediaFailed;
        mediaElement.MediaEnded += PreviewMediaElement_MediaEnded;
        return mediaElement;
    }

    private void ToggleWindowMaximized()
    {
        if (ResizeMode == ResizeMode.NoResize)
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateWindowFrameState()
    {
        if (WindowFrameBorder is null || WindowTitleBarBorder is null || ToggleMaximizeWindowButton is null || ToggleMaximizeWindowButtonGlyph is null)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        WindowFrameBorder.Margin = isMaximized ? new Thickness(8) : new Thickness(0);
        WindowFrameBorder.CornerRadius = isMaximized ? new CornerRadius(12) : new CornerRadius(18);
        WindowTitleBarBorder.CornerRadius = isMaximized ? new CornerRadius(12, 12, 0, 0) : new CornerRadius(18, 18, 0, 0);
        ToggleMaximizeWindowButton.ToolTip = isMaximized ? "Restore" : "Maximize";
        ToggleMaximizeWindowButtonGlyph.Text = isMaximized ? "\uE923" : "\uE922";
    }

    private async void StartCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await StartCaptureAsync();
    }

    private async void StopCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await StopCaptureAsync();
    }

    private async void SaveAllMonitorNodesButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAllMonitorClipsAsync();
    }

    private async void SaveMonitorNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MonitorNodeViewModel node)
        {
            await SaveMonitorNodeClipAsync(node);
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync();
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(isManual: true, installWhenAvailable: false);
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallPendingUpdateAsync(isAutomatic: false);
    }

    private void AddMonitorNodeButton_Click(object sender, RoutedEventArgs e)
    {
        var nodeNumber = _viewModel.MonitorNodes.Count + 1;
        var videosRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var defaultFolder = Path.Combine(videosRoot, "DualClip", $"Monitor{nodeNumber}");
        var node = new MonitorNodeViewModel(Guid.NewGuid().ToString("N"), $"Monitor {nodeNumber}", defaultFolder);
        var preferredMonitor = _viewModel.Monitors.FirstOrDefault(monitor =>
                _viewModel.MonitorNodes.All(nodeVm => nodeVm.SelectedMonitor?.DeviceName != monitor.DeviceName))
            ?? _viewModel.Monitors.FirstOrDefault();

        node.SelectedMonitor = preferredMonitor;
        node.LoadHotkey(HotkeyGesture.Disabled());
        _viewModel.MonitorNodes.Add(node);
    }

    private void RemoveMonitorNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is MonitorNodeViewModel node
            && _viewModel.MonitorNodes.Contains(node)
            && _viewModel.MonitorNodes.Count > 1)
        {
            _viewModel.MonitorNodes.Remove(node);
        }
    }

    private void BrowseMonitorNodeOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MonitorNodeViewModel node)
        {
            return;
        }

        var selectedFolder = BrowseForFolder(node.OutputFolder);

        if (!string.IsNullOrWhiteSpace(selectedFolder))
        {
            node.OutputFolder = selectedFolder;
        }
    }

    private void BrowseFfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|Executables|*.exe|All Files|*.*",
            FileName = string.IsNullOrWhiteSpace(_viewModel.FfmpegPath) ? "ffmpeg.exe" : _viewModel.FfmpegPath,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.FfmpegPath = dialog.FileName;
        }
    }

    private async Task LoadStateAsync()
    {
        StartupDiagnostics.Write("LoadStateAsync entered.");
        try
        {
            var config = await _configStore.LoadAsync();
            StartupDiagnostics.Write($"LoadStateAsync loaded config. StartCaptureOnStartup={config.StartCaptureOnStartup}, StartInBackgroundOnStartup={config.StartInBackgroundOnStartup}.");
            var monitors = _monitorService.GetMonitors();
            StartupDiagnostics.Write($"LoadStateAsync enumerated {monitors.Count} monitor(s).");
            var microphones = _audioDeviceService.GetMicrophones();
            StartupDiagnostics.Write($"LoadStateAsync enumerated {microphones.Count} microphone(s).");

            _viewModel.ApplyConfig(config, monitors, microphones);
            _viewModel.AppStatus = "Ready to capture.";
            _viewModel.ErrorMessage = string.Empty;
            UpdateNotifyIconText();
            RefreshClipLibrary();
            UpdateEditorControlState();

            StartupDiagnostics.Write("LoadStateAsync checking for updates.");
            await CheckForUpdatesAsync(isManual: false, installWhenAvailable: true);
            StartupDiagnostics.Write("LoadStateAsync finished update check.");

            if (_isExitRequested)
            {
                StartupDiagnostics.Write("LoadStateAsync detected exit requested after update check.");
                return;
            }

            if (config.StartCaptureOnStartup)
            {
                StartupDiagnostics.Write("LoadStateAsync auto-start capture beginning.");
                if (monitors.Count == 0)
                {
                    _viewModel.AppStatus = "Auto-start skipped.";
                    _viewModel.ErrorMessage = "Start clipping on startup is enabled, but DualClip needs at least one connected monitor.";
                }
                else
                {
                    await StartCaptureAsync();
                    StartupDiagnostics.Write($"LoadStateAsync auto-start capture completed. IsCapturing={_viewModel.IsCapturing}.");

                    if (_viewModel.IsCapturing && config.StartInBackgroundOnStartup)
                    {
                        HideToTray();
                    }
                }
            }
            else if (monitors.Count == 0)
            {
                _viewModel.ErrorMessage = "DualClip needs at least one connected monitor.";
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"LoadStateAsync failed: {ex}");
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.AppStatus = "Failed to load initial state.";
            UpdateNotifyIconText();
        }
    }

    private async Task StartCaptureAsync()
    {
        StartupDiagnostics.Write("StartCaptureAsync entered.");
        _viewModel.ErrorMessage = string.Empty;

        try
        {
            var borderlessCaptureAllowed = await BorderlessCaptureAccessService.RequestAsync();
            StartupDiagnostics.Write($"StartCaptureAsync borderless access result: {borderlessCaptureAllowed}.");
            var config = BuildValidatedConfig();
            StartupDiagnostics.Write("StartCaptureAsync validated config.");
            await _configStore.SaveAsync(config);
            StartupDiagnostics.Write("StartCaptureAsync saved config.");
            foreach (var node in _viewModel.MonitorNodes)
            {
                Directory.CreateDirectory(node.OutputFolder);
                node.Status = $"{node.DisplayTitle} idle.";
            }

            var audioSession = CreateAudioSession(config);
            var monitorSessions = _viewModel.MonitorNodes
                .Select(node => (Node: node, Session: CreateSession(node, config, borderlessCaptureAllowed)))
                .ToList();

            SubscribeAudioStatus(audioSession);

            try
            {
                await audioSession.StartAsync();
                StartupDiagnostics.Write("StartCaptureAsync audio session started.");

                foreach (var monitorSession in monitorSessions)
                {
                    SubscribeMonitorStatus(monitorSession.Session, monitorSession.Node);
                    await monitorSession.Session.StartAsync();
                    StartupDiagnostics.Write($"StartCaptureAsync monitor session started for {monitorSession.Node.DisplayTitle}.");
                }

                RegisterHotkeys(config);
                StartupDiagnostics.Write("StartCaptureAsync hotkeys registered.");
            }
            catch
            {
                UnsubscribeAudioStatus(audioSession);

                foreach (var monitorSession in monitorSessions)
                {
                    UnsubscribeMonitorStatus(monitorSession.Session);
                    await monitorSession.Session.DisposeAsync();
                }

                await audioSession.DisposeAsync();
                throw;
            }

            _audioSession = audioSession;
            _monitorSessionsByNodeId.Clear();

            foreach (var monitorSession in monitorSessions)
            {
                _monitorSessionsByNodeId[monitorSession.Node.Id] = monitorSession.Session;
            }

            _viewModel.IsCapturing = true;
            _viewModel.AppStatus = string.Empty;
            UpdateNotifyIconText();
            StartupDiagnostics.Write("StartCaptureAsync completed successfully.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"StartCaptureAsync failed: {ex}");
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.AppStatus = "Capture did not start.";
            UpdateNotifyIconText();
        }
    }

    private async Task StopCaptureAsync()
    {
        _hotkeyManager.UnregisterAll();

        var monitorSessions = _monitorSessionsByNodeId.Values.Distinct().ToList();
        _monitorSessionsByNodeId.Clear();
        var audioSession = _audioSession;
        _audioSession = null;

        foreach (var monitorSession in monitorSessions)
        {
            UnsubscribeMonitorStatus(monitorSession);
            await monitorSession.DisposeAsync();
        }

        if (audioSession is not null)
        {
            UnsubscribeAudioStatus(audioSession);
            await audioSession.DisposeAsync();
        }

        _viewModel.IsCapturing = false;
        _viewModel.AppStatus = string.Empty;
        lock (_clipCooldownLock)
        {
            _nextClipAllowedAtByNodeId.Clear();
        }
        UpdateNotifyIconText();
    }

    private async Task<string?> SaveMonitorNodeClipAsync(
        MonitorNodeViewModel node,
        IReadOnlyList<string>? audioSegments = null,
        bool refreshClipLibrary = true,
        bool playQueuedSound = false)
    {
        if (!_monitorSessionsByNodeId.TryGetValue(node.Id, out var monitorSession))
        {
            return null;
        }

        if (!TryReserveClipCooldown(node, out var remainingCooldown))
        {
            node.Status = BuildClipCooldownStatus(node, remainingCooldown);
            return null;
        }

        if (playQueuedSound)
        {
            ClipSoundPlayer.PlayQueued();
        }

        node.Status = $"Saving {node.DisplayTitle} clip...";

        try
        {
            var selectedAudioSegments = audioSegments ?? GetAudioSegments(monitorSession.Options.ReplayLengthSeconds);
            var outputPath = await monitorSession.SaveClipAsync(
                node.OutputFolder,
                selectedAudioSegments,
                Math.Clamp(_viewModel.ClipAudioVolumePercent, 0d, 200d));
            node.Status = $"Saved clip: {outputPath}";

            if (refreshClipLibrary)
            {
                RefreshClipLibrary(outputPath);
            }

            return outputPath;
        }
        catch (Exception ex)
        {
            node.Status = $"{node.DisplayTitle} save failed: {ex.Message}";
            _viewModel.ErrorMessage = ex.Message;
            return null;
        }
    }

    private async Task SaveAllMonitorClipsAsync()
    {
        var activeNodes = _viewModel.MonitorNodes
            .Where(node => _monitorSessionsByNodeId.ContainsKey(node.Id))
            .ToList();

        if (activeNodes.Count == 0)
        {
            return;
        }

        var replayLengthSeconds = _monitorSessionsByNodeId[activeNodes[0].Id].Options.ReplayLengthSeconds;
        var audioSegments = GetAudioSegments(replayLengthSeconds);
        string? preferredPath = null;

        foreach (var node in activeNodes)
        {
            var outputPath = await SaveMonitorNodeClipAsync(node, audioSegments, refreshClipLibrary: false);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                preferredPath ??= outputPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            RefreshClipLibrary(preferredPath);
        }
    }

    private async Task SaveSettingsAsync()
    {
        _viewModel.ErrorMessage = string.Empty;

        try
        {
            var config = BuildValidatedConfig();
            foreach (var node in config.MonitorNodes)
            {
                Directory.CreateDirectory(node.OutputFolder);
            }

            await _configStore.SaveAsync(config);

            if (_viewModel.IsCapturing)
            {
                _viewModel.AppStatus = "Settings saved. Restart capture to apply monitor node changes.";
            }
            else
            {
                _viewModel.AppStatus = "Settings saved.";
            }

            RefreshClipLibrary(GetSelectedClip()?.FilePath);
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.AppStatus = "Settings were not saved.";
        }
    }

    private async Task CheckForUpdatesAsync(bool isManual, bool installWhenAvailable)
    {
        if (_viewModel.IsCheckingForUpdates)
        {
            return;
        }

        _viewModel.IsCheckingForUpdates = true;
        _viewModel.UpdateStatusText = isManual
            ? "Checking GitHub releases..."
            : "Checking GitHub for updates...";

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            SetPendingUpdate(result.Release);
            _viewModel.UpdateStatusText = result.StatusMessage;

            if (result.IsUpdateAvailable && result.Release is not null && !_hasShownUpdateTip)
            {
                ShowUpdateAvailableNotification(result.Release);
            }

            if (result.IsUpdateAvailable && result.Release is not null && installWhenAvailable)
            {
                _viewModel.IsCheckingForUpdates = false;
                await InstallReleaseUpdateAsync(result.Release, isAutomatic: true);
                return;
            }
        }
        catch (Exception ex)
        {
            SetPendingUpdate(null);
            _viewModel.UpdateStatusText = isManual
                ? $"GitHub update check failed: {ex.Message}"
                : "Automatic update check could not reach GitHub.";
        }
        finally
        {
            _viewModel.IsCheckingForUpdates = false;
        }
    }

    private async Task InstallPendingUpdateAsync(bool isAutomatic)
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        await InstallReleaseUpdateAsync(_pendingUpdate, isAutomatic);
    }

    private async Task InstallReleaseUpdateAsync(GitHubUpdateRelease release, bool isAutomatic)
    {
        if (_viewModel.IsCheckingForUpdates)
        {
            return;
        }

        _viewModel.IsCheckingForUpdates = true;
        _viewModel.UpdateStatusText = isAutomatic
            ? $"Update v{release.VersionText} found on GitHub. Installing automatically..."
            : $"Downloading v{release.VersionText} from GitHub...";

        try
        {
            var progress = new Progress<double>(value =>
            {
                _viewModel.UpdateStatusText = $"Downloading v{release.VersionText} from GitHub... {value:P0}";
            });

            var preparedUpdate = await _updateService.DownloadUpdateAsync(release, progress);
            _viewModel.UpdateStatusText = $"Installing v{release.VersionText} and restarting DualClip...";

            if (_viewModel.IsCapturing)
            {
                _viewModel.AppStatus = "Stopping capture for update...";
                UpdateNotifyIconText();
                await StopCaptureAsync();
            }

            _updateService.LaunchUpdaterAndRestart(preparedUpdate);
            _isExitRequested = true;
            Close();
        }
        catch (Exception ex)
        {
            _viewModel.UpdateStatusText = isAutomatic
                ? $"Automatic update failed: {ex.Message}"
                : $"Update install failed: {ex.Message}";
        }
        finally
        {
            if (!_isExitRequested)
            {
                _viewModel.IsCheckingForUpdates = false;
            }
        }
    }

    private void SetPendingUpdate(GitHubUpdateRelease? release)
    {
        _pendingUpdate = release;
        _viewModel.IsUpdateAvailable = release is not null;
        _viewModel.InstallUpdateButtonText = release is null
            ? "Install Update"
            : $"Install v{release.VersionText}";
    }

    private void ShowUpdateAvailableNotification(GitHubUpdateRelease release)
    {
        _notifyIcon.BalloonTipTitle = "DualClip update available";
        _notifyIcon.BalloonTipText = $"Version v{release.VersionText} is available. Open Settings to install it.";
        _notifyIcon.ShowBalloonTip(4000);
        _hasShownUpdateTip = true;
    }

    private AppConfig BuildValidatedConfig()
    {
        if (_viewModel.MonitorNodes.Count == 0)
        {
            throw new InvalidOperationException("Add at least one monitor node before starting capture.");
        }

        if (!int.TryParse(_viewModel.ReplayLengthSecondsText, out var replayLengthSeconds) || replayLengthSeconds < 1 || replayLengthSeconds > 600)
        {
            throw new InvalidOperationException("Replay length must be a whole number between 1 and 600 seconds.");
        }

        if (!int.TryParse(_viewModel.FpsTargetText, out var fpsTarget) || fpsTarget < 1 || fpsTarget > 60)
        {
            throw new InvalidOperationException("FPS target must be a whole number between 1 and 60.");
        }

        if (string.IsNullOrWhiteSpace(_viewModel.FfmpegPath) || !File.Exists(_viewModel.FfmpegPath))
        {
            throw new InvalidOperationException($"ffmpeg.exe was not found at '{_viewModel.FfmpegPath}'.");
        }

        var selectedVideoQuality = _viewModel.SelectedVideoQuality?.Value
            ?? throw new InvalidOperationException("Choose a clip quality before starting capture.");
        var selectedAudioMode = _viewModel.SelectedAudioMode?.Value
            ?? throw new InvalidOperationException("Choose an audio source before starting capture.");
        var clipAudioVolumePercent = (int)Math.Round(Math.Clamp(_viewModel.ClipAudioVolumePercent, 0d, 200d), MidpointRounding.AwayFromZero);

        if (selectedAudioMode == AudioCaptureMode.Microphone && _viewModel.SelectedMicrophone is null)
        {
            throw new InvalidOperationException("Choose a microphone device or disable audio.");
        }

        var seenMonitorDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monitorNodeConfigs = new List<MonitorNodeConfig>(_viewModel.MonitorNodes.Count);

        foreach (var node in _viewModel.MonitorNodes)
        {
            if (node.SelectedMonitor is null)
            {
                throw new InvalidOperationException($"Select a display for {node.DisplayTitle}.");
            }

            if (!seenMonitorDeviceNames.Add(node.SelectedMonitor.DeviceName))
            {
                throw new InvalidOperationException("Each monitor node must target a different display.");
            }

            if (string.IsNullOrWhiteSpace(node.OutputFolder))
            {
                throw new InvalidOperationException($"Choose an output folder for {node.DisplayTitle}.");
            }

            monitorNodeConfigs.Add(new MonitorNodeConfig
            {
                Id = node.Id,
                Name = node.DisplayTitle,
                MonitorDeviceName = node.SelectedMonitor.DeviceName,
                OutputFolder = node.OutputFolder.Trim(),
                Hotkey = node.Hotkey.ToModel(),
            });
        }

        EnsureUniqueHotkeys(monitorNodeConfigs.Select(node => node.Hotkey).ToArray());

        return new AppConfig
        {
            MonitorNodes = monitorNodeConfigs,
            MonitorADeviceName = monitorNodeConfigs.ElementAtOrDefault(0)?.MonitorDeviceName,
            MonitorBDeviceName = monitorNodeConfigs.ElementAtOrDefault(1)?.MonitorDeviceName,
            ReplayLengthSeconds = replayLengthSeconds,
            FpsTarget = fpsTarget,
            VideoQuality = selectedVideoQuality,
            AudioMode = selectedAudioMode,
            ClipAudioVolumePercent = clipAudioVolumePercent,
            MicrophoneDeviceId = _viewModel.SelectedMicrophone?.Id,
            OutputFolderA = monitorNodeConfigs.ElementAtOrDefault(0)?.OutputFolder ?? string.Empty,
            OutputFolderB = monitorNodeConfigs.ElementAtOrDefault(1)?.OutputFolder ?? monitorNodeConfigs.ElementAtOrDefault(0)?.OutputFolder ?? string.Empty,
            UseUnifiedOutputFolder = false,
            StartCaptureOnStartup = _viewModel.StartCaptureOnStartup,
            StartInBackgroundOnStartup = _viewModel.StartInBackgroundOnStartup,
            FfmpegPath = _viewModel.FfmpegPath,
            HotkeyA = monitorNodeConfigs.ElementAtOrDefault(0)?.Hotkey ?? HotkeyGesture.Disabled(),
            HotkeyB = monitorNodeConfigs.ElementAtOrDefault(1)?.Hotkey ?? HotkeyGesture.Disabled(),
            HotkeyBoth = HotkeyGesture.Disabled(),
        };
    }

    private void RegisterHotkeys(AppConfig config)
    {
        _hotkeyManager.UnregisterAll();

        foreach (var node in config.MonitorNodes.Where(node => node.Hotkey.IsEnabled))
        {
            _hotkeyManager.Register(BuildMonitorHotkeyRegistrationName(node.Id), node.Hotkey);
        }
    }

    private MonitorCaptureSession CreateSession(
        MonitorNodeViewModel node,
        AppConfig config,
        bool preferBorderlessCapture)
    {
        return new MonitorCaptureSession(new MonitorCaptureSessionOptions
        {
            SlotName = BuildSessionSlotName(node),
            Monitor = node.SelectedMonitor ?? throw new InvalidOperationException($"No monitor is selected for {node.DisplayTitle}."),
            FfmpegPath = config.FfmpegPath,
            ReplayLengthSeconds = config.ReplayLengthSeconds,
            VideoQuality = config.VideoQuality,
            FpsTarget = config.FpsTarget,
            BufferDirectory = AppPaths.GetBufferDirectory($"monitor_{node.Id}"),
            PreferBorderlessCapture = preferBorderlessCapture,
        });
    }

    private AudioReplaySession CreateAudioSession(AppConfig config)
    {
        return new AudioReplaySession(new AudioReplaySessionOptions
        {
            BufferDirectory = AppPaths.GetBufferDirectory("Audio"),
            ReplayLengthSeconds = config.ReplayLengthSeconds,
            AudioMode = config.AudioMode,
            MicrophoneDeviceId = config.MicrophoneDeviceId,
        });
    }

    private void SubscribeMonitorStatus(MonitorCaptureSession session, MonitorNodeViewModel node)
    {
        _monitorNodesBySession[session] = node;
        session.StatusChanged += MonitorSession_StatusChanged;
    }

    private void UnsubscribeMonitorStatus(MonitorCaptureSession session)
    {
        session.StatusChanged -= MonitorSession_StatusChanged;
        _monitorNodesBySession.Remove(session);
    }

    private void MonitorSession_StatusChanged(object? sender, string message)
    {
        if (sender is not MonitorCaptureSession session || !_monitorNodesBySession.TryGetValue(session, out var node))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            node.Status = message;
            UpdateNotifyIconText();
        });
    }

    private void SubscribeAudioStatus(AudioReplaySession session)
    {
        session.StatusChanged += AudioSession_StatusChanged;
    }

    private void UnsubscribeAudioStatus(AudioReplaySession session)
    {
        session.StatusChanged -= AudioSession_StatusChanged;
    }

    private void AudioSession_StatusChanged(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _viewModel.AppStatus = message;
            UpdateNotifyIconText();
        });
    }

    private async void HotkeyManager_HotkeyPressed(object? sender, string registrationName)
    {
        if (IsAnyHotkeyEditorRecording())
        {
            return;
        }

        if (!registrationName.StartsWith(MonitorHotkeyPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var nodeId = registrationName[MonitorHotkeyPrefix.Length..];
        var node = _viewModel.MonitorNodes.FirstOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return;
        }

        await SaveMonitorNodeClipAsync(node, playQueuedSound: true);
    }

    private void HotkeyCaptureTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfTextBox textBox && !textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            textBox.Focus();
        }
    }

    private void HotkeyCaptureTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        GetHotkeyEditorState(sender)?.BeginRecording();
    }

    private void HotkeyCaptureTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        GetHotkeyEditorState(sender)?.EndRecording();
    }

    private void HotkeyCaptureTextBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var state = GetHotkeyEditorState(sender);

        if (state is null)
        {
            return;
        }

        var key = GetActualKey(e);

        if (key == Key.Tab)
        {
            state.EndRecording();
            return;
        }

        var modifiers = GetCurrentHotkeyModifiers(key);

        if (IsModifierKey(key))
        {
            state.SetPendingModifiers(modifiers);
            e.Handled = true;
            return;
        }

        if (modifiers == HotkeyModifiers.None && key == Key.Escape)
        {
            state.EndRecording();
            e.Handled = true;
            Keyboard.ClearFocus();
            return;
        }

        if (modifiers == HotkeyModifiers.None && (key == Key.Back || key == Key.Delete))
        {
            state.Clear();
            e.Handled = true;
            Keyboard.ClearFocus();
            return;
        }

        var virtualKey = TryGetVirtualKey(key);

        if (virtualKey == 0)
        {
            e.Handled = true;
            return;
        }

        state.Capture(virtualKey, modifiers);
        e.Handled = true;
        Keyboard.ClearFocus();
    }

    private void HotkeyCaptureTextBox_PreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        var state = GetHotkeyEditorState(sender);

        if (state is null || !state.IsRecording)
        {
            return;
        }

        var key = GetActualKey(e);

        if (IsModifierKey(key))
        {
            state.SetPendingModifiers(GetCurrentHotkeyModifiers());
            e.Handled = true;
        }
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is HotkeyEditorState state)
        {
            state.Clear();
        }
    }

    private void MonitorNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MonitorNodeViewModel node in e.OldItems)
            {
                node.PropertyChanged -= MonitorNode_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MonitorNodeViewModel node in e.NewItems)
            {
                node.PropertyChanged += MonitorNode_PropertyChanged;
            }
        }

        RefreshClipLibrary(GetSelectedClip()?.FilePath);
    }

    private void MonitorNode_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorNodeViewModel.OutputFolder))
        {
            RefreshClipLibrary(GetSelectedClip()?.FilePath);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsCapturing))
        {
            UpdateNotifyIconText();
        }
    }

    private void RefreshClipLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshClipLibrary(GetSelectedClip()?.FilePath);
    }

    private void RefreshClipLibrary(string? preferredPath = null)
    {
        var selectedPath = preferredPath ?? GetSelectedClip()?.FilePath;
        var items = GetClipLibraryItems();

        _viewModel.ClipLibrary.Clear();

        foreach (var item in items)
        {
            _viewModel.ClipLibrary.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            ClipListBox.SelectedItem = _viewModel.ClipLibrary.FirstOrDefault(item =>
                string.Equals(item.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        if (ClipListBox.SelectedItem is null && _viewModel.ClipLibrary.Count == 0)
        {
            StopPreview(clearSource: true);
            ClearLoadedClipEditorState();
            _viewModel.EditorStatus = "No clips found in the export folders yet.";
        }

        UpdateEditorControlState();
    }

    private IReadOnlyList<ClipLibraryItem> GetClipLibraryItems()
    {
        var folders = _viewModel.MonitorNodes
            .Select(node => node.OutputFolder)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var clips = new List<ClipLibraryItem>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(folder, "*.mp4", SearchOption.TopDirectoryOnly))
            {
                var fileInfo = new FileInfo(filePath);
                clips.Add(new ClipLibraryItem
                {
                    FilePath = filePath,
                    DisplayName = $"{fileInfo.Name}   [{fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}]",
                    ModifiedAt = fileInfo.LastWriteTime,
                    FileSizeBytes = fileInfo.Length,
                });
            }
        }

        return clips
            .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.ModifiedAt)
            .ToList();
    }

    private void ClipListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadSelectedClip();
    }

    private async void DeleteClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element
            || element.Tag is not string filePath
            || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var selectedClip = GetSelectedClip();
        var deletedSelectedClip = string.Equals(selectedClip?.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
        var preferredPath = deletedSelectedClip ? null : selectedClip?.FilePath;
        var clipName = Path.GetFileName(filePath);

        try
        {
            if (!File.Exists(filePath))
            {
                RefreshClipLibrary(preferredPath);
                _viewModel.ErrorMessage = string.Empty;
                _viewModel.EditorStatus = $"{clipName} was already removed.";
                return;
            }

            if (deletedSelectedClip)
            {
                StopPreview(clearSource: true);
                ClearTimelineUndoHistory();
                ClearLoadedClipEditorState();
                ClipListBox.SelectedItem = null;
                _viewModel.Editor.SelectedClipTitle = "No clip selected";
                _viewModel.EditorStatus = $"Deleting {clipName}...";
                UpdateEditorControlState();
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            File.Delete(filePath);
            RefreshClipLibrary(preferredPath);

            if (deletedSelectedClip && ClipListBox.SelectedItem is null && _viewModel.ClipLibrary.Count > 0)
            {
                ClipListBox.SelectedIndex = 0;
            }

            _viewModel.ErrorMessage = string.Empty;
            _viewModel.EditorStatus = $"Deleted {clipName}.";
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.EditorStatus = $"Delete failed: {ex.Message}";
        }
    }

    private void LoadSelectedClip()
    {
        StopPreview(clearSource: true);

        var selectedClip = GetSelectedClip();

        if (selectedClip is null || !File.Exists(selectedClip.FilePath))
        {
            ClearTimelineUndoHistory();
            ClearLoadedClipEditorState();
            _viewModel.Editor.SelectedClipTitle = "No clip selected";
            _viewModel.EditorStatus = "No clip selected.";
            UpdateEditorControlState();
            return;
        }

        ClearTimelineUndoHistory();
        _viewModel.Editor.SelectedClipTitle = Path.GetFileName(selectedClip.FilePath);
        _selectedClipDurationSeconds = 0;
        _selectedClipWidth = 0;
        _selectedClipHeight = 0;
        ClearLoadedClipEditorState();
        PreviewMediaElement.Source = new Uri(selectedClip.FilePath);
        _viewModel.EditorStatus = $"Loaded {Path.GetFileName(selectedClip.FilePath)}.";
        TimelinePositionTextBlock.Text = "Loading clip...";
        UpdateEditorVisuals();
        UpdateEditorControlState();
    }

    private void PreviewMediaElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!PreviewMediaElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        _viewModel.ErrorMessage = string.Empty;
        _selectedClipDurationSeconds = PreviewMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
        _selectedClipWidth = PreviewMediaElement.NaturalVideoWidth;
        _selectedClipHeight = PreviewMediaElement.NaturalVideoHeight;
        InitializeTimelineForLoadedClip();

        PreviewMediaElement.Pause();
        SeekToPlayhead(updatePreviewPosition: true);
        UpdateEditorVisuals();

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (PreviewMediaElement.Source is not null && !_isTimelinePlaybackActive)
                {
                    SeekToPlayhead(updatePreviewPosition: true);
                }
            }));
    }

    private void PreviewMediaElement_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        var selectedClip = GetSelectedClip();
        var clipName = selectedClip is null ? "the selected clip" : Path.GetFileName(selectedClip.FilePath);
        var message = string.IsNullOrWhiteSpace(e.ErrorException?.Message)
            ? $"DualClip could not render {clipName} in the editor preview."
            : $"DualClip could not render {clipName} in the editor preview: {e.ErrorException.Message}";

        ClearLoadedClipEditorState();
        _viewModel.Editor.SelectedClipTitle = selectedClip is null ? "No clip selected" : Path.GetFileName(selectedClip.FilePath);
        _viewModel.ErrorMessage = message;
        _viewModel.EditorStatus = $"Preview failed for {clipName}.";
        TimelinePositionTextBlock.Text = "Preview unavailable";
        UpdateEditorControlState();
    }

    private void PreviewMediaElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_isTimelinePlaybackActive && TryContinuePreviewPlaybackAtNextSegment())
        {
            return;
        }

        _isTimelinePlaybackActive = false;
        _previewTimer.Stop();
        UpdatePreviewPlaybackButtonVisualState();
        SeekToPlayhead(updatePreviewPosition: true);
    }

    private void PlayPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedClip() is null || PreviewMediaElement.Source is null)
        {
            return;
        }

        if (_isTimelinePlaybackActive)
        {
            PausePreviewPlayback();
            SeekToPlayhead(updatePreviewPosition: true);
            return;
        }

        var timelineDuration = GetTimelineDurationSeconds();

        if (timelineDuration <= 0)
        {
            return;
        }

        if (_playheadSeconds >= timelineDuration - PreviewPlaybackSegmentEndToleranceSeconds)
        {
            _playheadSeconds = 0;
        }

        if (!TryFindSegmentAtTimelineTime(_playheadSeconds, out var segment, out var segmentTimelineStart, out var localOffsetSeconds)
            || segment is null)
        {
            _playheadSeconds = 0;

            if (!TryFindSegmentAtTimelineTime(_playheadSeconds, out segment, out segmentTimelineStart, out localOffsetSeconds)
                || segment is null)
            {
                return;
            }
        }

        if (!ReferenceEquals(segment, _selectedTimelineSegment))
        {
            SelectTimelineSegment(segment);
        }

        StartPreviewPlayback(segment, localOffsetSeconds, segmentTimelineStart + localOffsetSeconds);
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isTimelinePlaybackActive)
        {
            return;
        }

        if (_selectedTimelineSegment is null || PreviewMediaElement.Source is null)
        {
            PausePreviewPlayback();
            return;
        }

        var segmentTimelineStart = GetSelectedSegmentTimelineStartSeconds();
        var segmentDuration = GetSelectedSegmentDurationSeconds();
        var mediaLocalOffsetSeconds = PreviewMediaElement.Position.TotalSeconds - _selectedTimelineSegment.SourceStartSeconds;

        if (segmentDuration <= 0 || mediaLocalOffsetSeconds >= segmentDuration - PreviewPlaybackSegmentEndToleranceSeconds)
        {
            if (TryContinuePreviewPlaybackAtNextSegment())
            {
                return;
            }

            _playheadSeconds = GetTimelineDurationSeconds();
            PausePreviewPlayback();
            SeekToPlayhead(updatePreviewPosition: true);
            return;
        }

        _playheadSeconds = Math.Clamp(
            segmentTimelineStart + Math.Clamp(mediaLocalOffsetSeconds, 0, segmentDuration),
            0,
            GetTimelineDurationSeconds());

        UpdateTimelinePlaybackVisuals();
    }

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineVisuals();
    }

    private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GetTimelineDurationSeconds() <= 0 || TimelineCanvas.ActualWidth <= 0)
        {
            return;
        }

        var clickX = e.GetPosition(TimelineCanvas).X;
        _playheadSeconds = TimelineXToTime(clickX);
        if (TryFindSegmentAtTimelineTime(_playheadSeconds, out var segment, out _, out _))
        {
            SelectTimelineSegment(segment);
        }
        SeekToPlayhead(updatePreviewPosition: true);
    }

    private void UpdateTimelineFromMedia()
    {
        if (!PreviewMediaElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        _playheadSeconds = Math.Clamp(PreviewMediaElement.Position.TotalSeconds, 0, _selectedClipDurationSeconds);
        UpdateTimelineLabel(_playheadSeconds);
        UpdateTimelineVisuals();
    }

    private void UpdateTimelineLabel(double currentSeconds)
    {
        TimelinePositionTextBlock.Text = $"{currentSeconds:0.00}s / {GetTimelineDurationSeconds():0.00}s";
    }

    private void UpdateTimelinePlaybackVisuals()
    {
        if (TimelineCanvas is null || PlayheadLine is null || PlayheadThumb is null || GetTimelineDurationSeconds() <= 0)
        {
            return;
        }

        const double playheadTop = 12d;
        var playheadX = TimeToTimelineX(_playheadSeconds);
        Canvas.SetLeft(PlayheadLine, playheadX - (PlayheadLine.Width / 2d));
        Canvas.SetTop(PlayheadLine, playheadTop);
        PositionTimelineThumb(PlayheadThumb, playheadX, playheadTop - 2d);
        ScrollPlayheadIntoView();
        UpdateTimelineLabel(_playheadSeconds);
    }

    private void PlayheadThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        PausePreviewPlayback();
        _playheadSeconds = Math.Clamp(_playheadSeconds + TimelineDeltaToTime(e.HorizontalChange), 0, GetTimelineDurationSeconds());
        _playheadSeconds = SnapTimelineTimeValue(_playheadSeconds, 0, GetTimelineDurationSeconds());
        if (TryFindSegmentAtTimelineTime(_playheadSeconds, out var segment, out _, out _))
        {
            SelectTimelineSegment(segment);
        }
        SeekToPlayhead(updatePreviewPosition: true);
    }

    private void TrimStartThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        PausePreviewPlayback();
        var localPlayhead = GetPlayheadOffsetWithinSelectedSegment();
        _trimStartSeconds = Math.Clamp(
            _trimStartSeconds + TimelineDeltaToTime(e.HorizontalChange),
            0,
            Math.Max(0, _trimEndSeconds - MinimumTrimDurationSeconds));
        _trimStartSeconds = SnapLocalSegmentTime(_trimStartSeconds, 0, localPlayhead, Math.Max(0, _trimEndSeconds - MinimumTrimDurationSeconds));
        ApplyCurrentEditorStateToSelectedSegment();

        if (_playheadSeconds < GetSelectedSegmentTimelineStartSeconds())
        {
            _playheadSeconds = GetSelectedSegmentTimelineStartSeconds();
            SeekToPlayhead(updatePreviewPosition: true);
            return;
        }

        UpdateTimelineVisuals();
        UpdateTimelineLabel(_playheadSeconds);
    }

    private void TrimEndThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        PausePreviewPlayback();
        var localPlayhead = GetPlayheadOffsetWithinSelectedSegment();
        _trimEndSeconds = Math.Clamp(
            _trimEndSeconds + TimelineDeltaToTime(e.HorizontalChange),
            Math.Min(_selectedClipDurationSeconds, _trimStartSeconds + MinimumTrimDurationSeconds),
            _selectedClipDurationSeconds);
        _trimEndSeconds = SnapLocalSegmentTime(_trimEndSeconds, _selectedClipDurationSeconds, localPlayhead, _trimStartSeconds + MinimumTrimDurationSeconds);
        ApplyCurrentEditorStateToSelectedSegment();

        if (_playheadSeconds > GetSelectedSegmentTimelineStartSeconds() + GetSelectedSegmentDurationSeconds())
        {
            _playheadSeconds = GetSelectedSegmentTimelineStartSeconds() + GetSelectedSegmentDurationSeconds();
            SeekToPlayhead(updatePreviewPosition: true);
            return;
        }

        UpdateTimelineVisuals();
        UpdateTimelineLabel(_playheadSeconds);
    }

    private void ZoomKeyframe1Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        PausePreviewPlayback();
        _zoomKeyframe1Seconds = Math.Clamp(
            _zoomKeyframe1Seconds + TimelineDeltaToTime(e.HorizontalChange),
            0,
            Math.Max(0, GetSelectedSegmentDurationSeconds() == 0 ? 0 : _zoomKeyframe2Seconds - MinimumTrimDurationSeconds));
        _zoomKeyframe1Seconds = SnapLocalSegmentTime(_zoomKeyframe1Seconds, 0, GetPlayheadOffsetWithinSelectedSegment(), Math.Max(0, _zoomKeyframe2Seconds - MinimumTrimDurationSeconds));
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTimelineVisuals();
    }

    private void ZoomKeyframe2Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        PausePreviewPlayback();
        _zoomKeyframe2Seconds = Math.Clamp(
            _zoomKeyframe2Seconds + TimelineDeltaToTime(e.HorizontalChange),
            Math.Min(GetSelectedSegmentDurationSeconds(), _zoomKeyframe1Seconds + MinimumTrimDurationSeconds),
            GetSelectedSegmentDurationSeconds());
        _zoomKeyframe2Seconds = SnapLocalSegmentTime(_zoomKeyframe2Seconds, GetSelectedSegmentDurationSeconds(), GetPlayheadOffsetWithinSelectedSegment(), _zoomKeyframe1Seconds + MinimumTrimDurationSeconds);
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTimelineVisuals();
    }

    private void MoveZoomKeyframe1Button_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        var localPlayhead = GetPlayheadOffsetWithinSelectedSegment();
        _zoomKeyframe1Seconds = Math.Clamp(localPlayhead, 0, Math.Max(0, _zoomKeyframe2Seconds - MinimumTrimDurationSeconds));
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTimelineVisuals();
    }

    private void MoveZoomKeyframe2Button_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        var localPlayhead = GetPlayheadOffsetWithinSelectedSegment();
        _zoomKeyframe2Seconds = Math.Clamp(localPlayhead, Math.Min(GetSelectedSegmentDurationSeconds(), _zoomKeyframe1Seconds + MinimumTrimDurationSeconds), GetSelectedSegmentDurationSeconds());
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateTimelineVisuals();
    }

    private void SetZoomFromPlayheadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
        {
            return;
        }

        if (tag == "1")
        {
            MoveZoomKeyframe1Button_Click(sender, e);
        }
        else if (tag == "2")
        {
            MoveZoomKeyframe2Button_Click(sender, e);
        }
    }

    private void ZoomKeyframeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCropOverlay();
    }

    private void CropMoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!TryGetPreviewSourceDelta(e.HorizontalChange, e.VerticalChange, out var deltaX, out var deltaY))
        {
            return;
        }

        var nextX = Math.Clamp(_cropRectSource.X + deltaX, 0, Math.Max(0, _selectedClipWidth - _cropRectSource.Width));
        var nextY = Math.Clamp(_cropRectSource.Y + deltaY, 0, Math.Max(0, _selectedClipHeight - _cropRectSource.Height));
        _cropRectSource = new Rect(nextX, nextY, _cropRectSource.Width, _cropRectSource.Height);
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateCropOverlay();
    }

    private void CropTopLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(e.HorizontalChange, e.VerticalChange, resizeLeft: true, resizeTop: true);
    }

    private void CropTopRightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: true);
    }

    private void CropBottomLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(e.HorizontalChange, e.VerticalChange, resizeLeft: true, resizeTop: false);
    }

    private void CropBottomRightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: false);
    }

    private void ResetCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClipWidth <= 0 || _selectedClipHeight <= 0)
        {
            return;
        }

        CaptureTimelineUndoSnapshot();
        _cropRectSource = new Rect(0, 0, _selectedClipWidth, _selectedClipHeight);
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateCropOverlay();
    }

    private async void SaveEditedAsNewButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveEditedClipAsync(overwriteSelected: false);
    }

    private async void OverwriteEditedClipButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveEditedClipAsync(overwriteSelected: true);
    }

    private async Task SaveEditedClipAsync(bool overwriteSelected)
    {
        var selectedClip = GetSelectedClip();

        if (selectedClip is null)
        {
            return;
        }

        _viewModel.ErrorMessage = string.Empty;
        _viewModel.EditorStatus = overwriteSelected
            ? "Overwriting selected clip..."
            : "Saving edited clip...";
        _isEditorBusy = true;
        UpdateEditorControlState();

        var targetPath = selectedClip.FilePath;
        var temporaryOutputPath = overwriteSelected
            ? Path.Combine(Path.GetTempPath(), $"dualclip_edit_{Guid.NewGuid():N}.mp4")
            : BuildEditedOutputPath(selectedClip.FilePath);

        try
        {
            StopPreview(clearSource: true);
            await Dispatcher.Yield(DispatcherPriority.Background);

            var request = BuildTimelineEditRequest(selectedClip.FilePath, temporaryOutputPath);
            await _timelineEditor.ExportAsync(request, CancellationToken.None);

            if (overwriteSelected)
            {
                File.Move(temporaryOutputPath, targetPath, overwrite: true);
            }
            else
            {
                targetPath = temporaryOutputPath;
            }

            RefreshClipLibrary(targetPath);
            ClipListBox.SelectedItem = _viewModel.ClipLibrary.FirstOrDefault(item =>
                string.Equals(item.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
            _viewModel.EditorStatus = overwriteSelected
                ? $"Rewrote {Path.GetFileName(targetPath)}."
                : $"Saved {Path.GetFileName(targetPath)}.";
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.EditorStatus = $"Edit failed: {ex.Message}";
            TryDeleteFile(temporaryOutputPath);
        }
        finally
        {
            _isEditorBusy = false;
            UpdateEditorControlState();
        }
    }

    private void ClearLoadedClipEditorState()
    {
        ClearTimeline();
        _selectedClipDurationSeconds = 0;
        _selectedClipWidth = 0;
        _selectedClipHeight = 0;
        _trimStartSeconds = 0;
        _trimEndSeconds = 0;
        _playheadSeconds = 0;
        _zoomKeyframe1Seconds = 0;
        _zoomKeyframe2Seconds = 0;
        _zoomKeyframe1Percent = 100;
        _zoomKeyframe2Percent = 100;
        _cropRectSource = Rect.Empty;
        _displayedVideoRect = Rect.Empty;
        _rotationDegrees = 0;
        _scalePercent = 100d;
        _translateX = 0;
        _translateY = 0;
        _opacityPercent = 100d;
        _flipHorizontal = false;
        _flipVertical = false;
        UpdateZoomSlidersFromState();
        UpdateTransformControlsFromState();
        UpdateEditorVisuals();
    }

    private void StopPreview(bool clearSource)
    {
        _isTimelinePlaybackActive = false;
        _previewPlaybackRequestId++;
        _previewTimer.Stop();
        UpdatePreviewPlaybackButtonVisualState();

        try
        {
            PreviewMediaElement.Stop();
        }
        catch
        {
        }

        if (clearSource)
        {
            PreviewMediaElement.Source = null;
        }
    }

    private void PausePreviewPlayback()
    {
        _isTimelinePlaybackActive = false;
        _previewPlaybackRequestId++;
        UpdatePreviewPlaybackButtonVisualState();
        try
        {
            PreviewMediaElement.Pause();
        }
        catch
        {
        }

        _previewTimer.Stop();
    }

    private bool TryContinuePreviewPlaybackAtNextSegment()
    {
        if (!TryGetSelectedSegmentIndex(out var currentIndex))
        {
            return false;
        }

        var nextIndex = currentIndex + 1;

        if (nextIndex < 0 || nextIndex >= _timelineSegments.Count)
        {
            return false;
        }

        var nextSegment = _timelineSegments[nextIndex];
        StartPreviewPlayback(nextSegment, 0d, nextSegment.TimelineStartSeconds);
        return true;
    }

    private void SeekToPlayhead(bool updatePreviewPosition)
    {
        _playheadSeconds = Math.Clamp(_playheadSeconds, 0, GetTimelineDurationSeconds());

        if (updatePreviewPosition && PreviewMediaElement.Source is not null &&
            TryFindSegmentAtTimelineTime(_playheadSeconds, out var segment, out _, out var localOffsetSeconds) &&
            segment is not null)
        {
            if (!ReferenceEquals(segment, _selectedTimelineSegment))
            {
                SelectTimelineSegment(segment);
            }

            SetPreviewPositionSeconds(segment.SourceStartSeconds + localOffsetSeconds);
        }

        ScrollPlayheadIntoView();
        UpdateTimelineLabel(_playheadSeconds);
        UpdateTimelineVisuals();
    }

    private void StartPreviewPlayback(TimelineSegment segment, double localOffsetSeconds, double timelineTimeSeconds)
    {
        PausePreviewPlayback();

        if (!ReferenceEquals(segment, _selectedTimelineSegment))
        {
            SelectTimelineSegment(segment);
        }

        _playheadSeconds = Math.Clamp(timelineTimeSeconds, 0, GetTimelineDurationSeconds());
        PreviewMediaElement.SpeedRatio = 1.0d;
        SetPreviewPositionSeconds(segment.SourceStartSeconds + localOffsetSeconds);
        _isTimelinePlaybackActive = true;
        UpdatePreviewPlaybackButtonVisualState();
        UpdateTimelineVisuals();
        QueuePreviewPlaybackStart();
    }

    private void QueuePreviewPlaybackStart()
    {
        var playbackRequestId = ++_previewPlaybackRequestId;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_isTimelinePlaybackActive || playbackRequestId != _previewPlaybackRequestId || PreviewMediaElement.Source is null)
                {
                    return;
                }

                PreviewMediaElement.Play();
                _previewTimer.Start();
            }));
    }

    private void SetPreviewPositionSeconds(double positionSeconds)
    {
        PreviewMediaElement.Position = TimeSpan.FromSeconds(Math.Clamp(positionSeconds, 0, _selectedClipDurationSeconds));
    }

    private void UpdateEditorVisuals()
    {
        UpdateTimelineLabel(_playheadSeconds);
        UpdateTimelineVisuals();
        UpdateCropOverlay();
        UpdateEditorToolButtons();
        UpdateEditorControlState();
    }

    private void UpdateTimelineVisuals()
    {
        if (TimelineCanvas is null || TimelineSegmentsCanvas is null)
        {
            return;
        }

        const double trackTop = 34d;
        const double playheadTop = 12d;
        const double trimThumbTop = 24d;
        const double timelineHeight = 110d;

        var timelineWidth = GetTimelineCanvasWidth();
        TimelineCanvas.Width = timelineWidth;
        TimelineCanvas.Height = timelineHeight;
        TimelineSegmentsCanvas.Width = timelineWidth;
        TimelineSegmentsCanvas.Height = timelineHeight;

        RenderTimelineSegments();

        var width = timelineWidth;
        var trackWidth = Math.Max(0, width - TimelineLeftPaddingPixels - TimelineRightPaddingPixels);
        Canvas.SetLeft(TimelineTrackRectangle, TimelineLeftPaddingPixels);
        Canvas.SetTop(TimelineTrackRectangle, trackTop);
        TimelineTrackRectangle.Width = trackWidth;

        var hasSelectedSegment = _selectedTimelineSegment is not null;
        var selectedSegmentStart = hasSelectedSegment ? GetSelectedSegmentTimelineStartSeconds() : 0;
        var selectedSegmentDuration = hasSelectedSegment ? GetSelectedSegmentDurationSeconds() : 0;
        var trimStartX = TimeToTimelineX(selectedSegmentStart);
        var trimEndX = TimeToTimelineX(selectedSegmentStart + selectedSegmentDuration);
        var playheadX = TimeToTimelineX(_playheadSeconds);
        Canvas.SetLeft(TrimSelectionRectangle, trimStartX);
        Canvas.SetTop(TrimSelectionRectangle, trackTop);
        TrimSelectionRectangle.Width = Math.Max(0, trimEndX - trimStartX);

        Canvas.SetLeft(PlayheadLine, playheadX - (PlayheadLine.Width / 2d));
        Canvas.SetTop(PlayheadLine, playheadTop);

        PositionTimelineThumb(TrimStartThumb, trimStartX, trimThumbTop);
        PositionTimelineThumb(TrimEndThumb, trimEndX, trimThumbTop);
        PositionTimelineThumb(PlayheadThumb, playheadX, playheadTop - 2d);
        var hasTimeline = GetTimelineDurationSeconds() > 0;
        var editorVisibility = hasTimeline ? Visibility.Visible : Visibility.Collapsed;
        TimelineTrackRectangle.Visibility = editorVisibility;
        TrimSelectionRectangle.Visibility = hasSelectedSegment ? Visibility.Visible : Visibility.Collapsed;
        PlayheadLine.Visibility = editorVisibility;
        TrimStartThumb.Visibility = hasSelectedSegment ? Visibility.Visible : Visibility.Collapsed;
        TrimEndThumb.Visibility = hasSelectedSegment ? Visibility.Visible : Visibility.Collapsed;
        PlayheadThumb.Visibility = editorVisibility;

        TrimRangeTextBlock.Text = hasSelectedSegment
            ? $"Selected clip piece: {_trimStartSeconds:0.00}s to {_trimEndSeconds:0.00}s ({Math.Max(0, _trimEndSeconds - _trimStartSeconds):0.00}s)"
            : "Timeline: no segment selected";

        if (!_isTimelineSegmentDragging)
        {
            UpdateTimelineDropIndicator();
        }

        RenderTimelineRuler();
    }

    private void UpdateCropOverlay()
    {
        if (_selectedClipWidth <= 0 || _selectedClipHeight <= 0 || PreviewHost.ActualWidth <= 0 || PreviewHost.ActualHeight <= 0)
        {
            SetCropOverlayVisibility(Visibility.Collapsed);
            CropSummaryTextBlock.Text = "Crop: full frame";
            PositionCropShades(Rect.Empty);

            if (PreviewVideoPresenter is not null)
            {
                PreviewVideoPresenter.Clip = null;
                PreviewVideoPresenter.RenderTransform = System.Windows.Media.Transform.Identity;
            }

            if (TransformSelectionBorder is not null)
            {
                TransformSelectionBorder.Visibility = Visibility.Collapsed;
            }

            if (TransformMoveThumb is not null)
            {
                TransformMoveThumb.Visibility = Visibility.Collapsed;
            }

            if (CropOverlayCanvas is not null)
            {
                CropOverlayCanvas.Visibility = Visibility.Collapsed;
                CropOverlayCanvas.Width = 0;
                CropOverlayCanvas.Height = 0;
                CropOverlayCanvas.RenderTransform = System.Windows.Media.Transform.Identity;
            }

            return;
        }

        var previewScale = Math.Min(PreviewHost.ActualWidth / _selectedClipWidth, PreviewHost.ActualHeight / _selectedClipHeight);
        var displayWidth = _selectedClipWidth * previewScale;
        var displayHeight = _selectedClipHeight * previewScale;
        _displayedVideoRect = new Rect(
            (PreviewHost.ActualWidth - displayWidth) / 2d,
            (PreviewHost.ActualHeight - displayHeight) / 2d,
            displayWidth,
            displayHeight);

        var cropLocalRect = new Rect(
            _cropRectSource.X * previewScale,
            _cropRectSource.Y * previewScale,
            _cropRectSource.Width * previewScale,
            _cropRectSource.Height * previewScale);

        CropSelectionBorder.Width = cropLocalRect.Width;
        CropSelectionBorder.Height = cropLocalRect.Height;
        Canvas.SetLeft(CropSelectionBorder, cropLocalRect.X);
        Canvas.SetTop(CropSelectionBorder, cropLocalRect.Y);

        CropMoveThumb.Width = cropLocalRect.Width;
        CropMoveThumb.Height = cropLocalRect.Height;
        Canvas.SetLeft(CropMoveThumb, cropLocalRect.X);
        Canvas.SetTop(CropMoveThumb, cropLocalRect.Y);

        PositionCropThumb(CropTopLeftThumb, cropLocalRect.Left, cropLocalRect.Top);
        PositionCropThumb(CropTopRightThumb, cropLocalRect.Right, cropLocalRect.Top);
        PositionCropThumb(CropBottomLeftThumb, cropLocalRect.Left, cropLocalRect.Bottom);
        PositionCropThumb(CropBottomRightThumb, cropLocalRect.Right, cropLocalRect.Bottom);
        PositionCropThumb(CropTopThumb, cropLocalRect.Left + (cropLocalRect.Width / 2d), cropLocalRect.Top);
        PositionCropThumb(CropRightThumb, cropLocalRect.Right, cropLocalRect.Top + (cropLocalRect.Height / 2d));
        PositionCropThumb(CropBottomThumb, cropLocalRect.Left + (cropLocalRect.Width / 2d), cropLocalRect.Bottom);
        PositionCropThumb(CropLeftThumb, cropLocalRect.Left, cropLocalRect.Top + (cropLocalRect.Height / 2d));

        SetCropOverlayVisibility(Visibility.Visible);
        ApplyPreviewPresenterVisuals(cropLocalRect);
        CropSummaryTextBlock.Text = IsCropActive()
            ? $"Crop: {Math.Round(_cropRectSource.X)} x {Math.Round(_cropRectSource.Y)} at {Math.Round(_cropRectSource.Width)} x {Math.Round(_cropRectSource.Height)}"
            : "Crop: full frame";
    }

    private void UpdateZoomSlidersFromState()
    {
    }

    private void UpdateZoomValueText()
    {
    }

    private void UpdateEditorControlState()
    {
        var hasClip = GetSelectedClip() is not null;
        var hasSelectedSegment = _selectedTimelineSegment is not null;
        var isEnabled = hasClip && hasSelectedSegment && !_isEditorBusy;
        var hasTimeline = hasClip && GetTimelineDurationSeconds() > 0;

        PlayPreviewButton.IsEnabled = isEnabled;
        StepBackButton.IsEnabled = hasTimeline && !_isEditorBusy;
        StepForwardButton.IsEnabled = hasTimeline && !_isEditorBusy;
        SplitSegmentButton.IsEnabled = isEnabled;
        CopySegmentButton.IsEnabled = isEnabled;
        PasteSegmentButton.IsEnabled = _copiedTimelineSegment is not null && !_isEditorBusy;
        DeleteSegmentButton.IsEnabled = isEnabled;
        ResetCropButton.IsEnabled = isEnabled;
        RotationSlider.IsEnabled = isEnabled;
        RotationTextBox.IsEnabled = isEnabled;
        ScaleSlider.IsEnabled = isEnabled;
        ScaleTextBox.IsEnabled = isEnabled;
        PositionXSlider.IsEnabled = isEnabled;
        PositionXTextBox.IsEnabled = isEnabled;
        PositionYSlider.IsEnabled = isEnabled;
        PositionYTextBox.IsEnabled = isEnabled;
        OpacitySlider.IsEnabled = isEnabled;
        OpacityTextBox.IsEnabled = isEnabled;
        DuplicateSegmentButton.IsEnabled = isEnabled;
        CopySegmentSettingsButton.IsEnabled = isEnabled;
        PasteSegmentSettingsButton.IsEnabled = _copiedSegmentSettings is not null && isEnabled;
        FitToFrameButton.IsEnabled = isEnabled;
        FillFrameButton.IsEnabled = isEnabled;
        FlipHorizontalButton.IsEnabled = isEnabled;
        FlipVerticalButton.IsEnabled = isEnabled;
        ResetTransformButton.IsEnabled = isEnabled;
        TimelineZoomSlider.IsEnabled = hasTimeline && !_isEditorBusy;
        ToolCropButton.IsEnabled = hasClip && !_isEditorBusy;
        ToolTransformButton.IsEnabled = hasClip && !_isEditorBusy;
        SaveEditedAsNewButton.IsEnabled = hasTimeline && !_isEditorBusy;
        OverwriteEditedClipButton.IsEnabled = hasTimeline && !_isEditorBusy;
        TimelineCanvas.IsEnabled = hasTimeline;
        TimelineSegmentsCanvas.IsEnabled = hasTimeline;
        TimelineScrollViewer.IsEnabled = hasTimeline;
        UpdatePreviewPlaybackButtonVisualState();
    }

    private void UpdatePreviewPlaybackButtonVisualState()
    {
        if (PlayPreviewButton is null || PlayPreviewButtonGlyph is null || PlayPreviewButtonLabel is null)
        {
            return;
        }

        PlayPreviewButtonGlyph.Text = _isTimelinePlaybackActive ? "\uE769" : "\uE768";
        PlayPreviewButtonLabel.Text = _isTimelinePlaybackActive ? "Pause (Space)" : "Play (Space)";
        PlayPreviewButton.ToolTip = _isTimelinePlaybackActive ? "Pause preview (Space)" : "Play preview (Space)";
    }

    private ClipLibraryItem? GetSelectedClip()
    {
        return ClipListBox?.SelectedItem as ClipLibraryItem;
    }

    private static string BuildEditedOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}_edited_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
    }

    private double TimelineDeltaToTime(double deltaX)
    {
        if (GetTimelineDurationSeconds() <= 0)
        {
            return 0;
        }

        return deltaX / Math.Max(1d, GetTimelinePixelsPerSecond());
    }

    private double TimeToTimelineX(double seconds)
    {
        if (GetTimelineDurationSeconds() <= 0)
        {
            return TimelineLeftPaddingPixels;
        }

        return TimelineLeftPaddingPixels + (Math.Clamp(seconds, 0, GetTimelineDurationSeconds()) * GetTimelinePixelsPerSecond());
    }

    private double TimelineXToTime(double x)
    {
        if (GetTimelineDurationSeconds() <= 0)
        {
            return 0;
        }

        return Math.Clamp((x - TimelineLeftPaddingPixels) / Math.Max(1d, GetTimelinePixelsPerSecond()), 0, GetTimelineDurationSeconds());
    }

    private static void PositionTimelineThumb(FrameworkElement element, double centerX, double top)
    {
        Canvas.SetLeft(element, centerX - (element.Width / 2d));
        Canvas.SetTop(element, top);
    }

    private static void PositionCropThumb(FrameworkElement element, double centerX, double centerY)
    {
        Canvas.SetLeft(element, centerX - (element.Width / 2d));
        Canvas.SetTop(element, centerY - (element.Height / 2d));
    }

    private void SetCropOverlayVisibility(Visibility visibility)
    {
        var showCropTool = visibility == Visibility.Visible && _viewModel.Editor.IsCropToolActive;
        var showCropBorder = visibility == Visibility.Visible && (_viewModel.Editor.IsCropToolActive || IsCropActive());

        if (CropOverlayCanvas is not null)
        {
            CropOverlayCanvas.Visibility = showCropBorder ? Visibility.Visible : Visibility.Collapsed;
        }

        CropSelectionBorder.Visibility = showCropBorder ? Visibility.Visible : Visibility.Collapsed;
        CropMoveThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropTopLeftThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropTopRightThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropBottomLeftThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropBottomRightThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropTopThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropRightThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropBottomThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropLeftThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool TryGetPreviewScale(out double scaleX, out double scaleY)
    {
        scaleX = 0;
        scaleY = 0;

        if (_selectedClipWidth <= 0 || _selectedClipHeight <= 0 || _displayedVideoRect.Width <= 0 || _displayedVideoRect.Height <= 0)
        {
            return false;
        }

        scaleX = _displayedVideoRect.Width / _selectedClipWidth;
        scaleY = _displayedVideoRect.Height / _selectedClipHeight;
        return true;
    }

    private bool TryGetPreviewSourceDelta(double horizontalChange, double verticalChange, out double deltaX, out double deltaY)
    {
        deltaX = 0;
        deltaY = 0;

        if (!TryGetPreviewScale(out var scaleX, out var scaleY))
        {
            return false;
        }

        var localDeltaX = horizontalChange;
        var localDeltaY = verticalChange;

        if (CropOverlayCanvas is not null && CropOverlayCanvas.Visibility == Visibility.Visible)
        {
            try
            {
                var hostToOverlay = PreviewHost.TransformToDescendant(CropOverlayCanvas);

                if (hostToOverlay.TryTransform(new System.Windows.Point(0, 0), out var localOrigin)
                    && hostToOverlay.TryTransform(new System.Windows.Point(horizontalChange, verticalChange), out var localPoint))
                {
                    localDeltaX = localPoint.X - localOrigin.X;
                    localDeltaY = localPoint.Y - localOrigin.Y;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        deltaX = localDeltaX / scaleX;
        deltaY = localDeltaY / scaleY;
        return true;
    }

    private void ResizeCropFromCorner(double horizontalChange, double verticalChange, bool resizeLeft, bool resizeTop)
    {
        if (!TryGetPreviewSourceDelta(horizontalChange, verticalChange, out var deltaX, out var deltaY))
        {
            return;
        }

        var x = _cropRectSource.X;
        var y = _cropRectSource.Y;
        var width = _cropRectSource.Width;
        var height = _cropRectSource.Height;

        if (resizeLeft)
        {
            var proposedX = Math.Clamp(x + deltaX, 0, x + width - MinimumCropSizePixels);
            width += x - proposedX;
            x = proposedX;
        }
        else
        {
            width = Math.Clamp(width + deltaX, MinimumCropSizePixels, _selectedClipWidth - x);
        }

        if (resizeTop)
        {
            var proposedY = Math.Clamp(y + deltaY, 0, y + height - MinimumCropSizePixels);
            height += y - proposedY;
            y = proposedY;
        }
        else
        {
            height = Math.Clamp(height + deltaY, MinimumCropSizePixels, _selectedClipHeight - y);
        }

        var aspectRatio = GetLockedCropAspectRatio();

        if (aspectRatio is not null)
        {
            var widthDrivenHeight = width / aspectRatio.Value;
            var heightDrivenWidth = height * aspectRatio.Value;

            if (Math.Abs(horizontalChange) >= Math.Abs(verticalChange))
            {
                height = Math.Clamp(widthDrivenHeight, MinimumCropSizePixels, _selectedClipHeight);
                if (resizeTop)
                {
                    y = Math.Clamp((_cropRectSource.Bottom - height), 0, _selectedClipHeight - height);
                }
            }
            else
            {
                width = Math.Clamp(heightDrivenWidth, MinimumCropSizePixels, _selectedClipWidth);
                if (resizeLeft)
                {
                    x = Math.Clamp((_cropRectSource.Right - width), 0, _selectedClipWidth - width);
                }
            }
        }

        _cropRectSource = ClampCropRect(new Rect(x, y, width, height));
        ApplyCurrentEditorStateToSelectedSegment();
        UpdateCropOverlay();
    }

    private bool IsCropActive()
    {
        if (_selectedClipWidth <= 0 || _selectedClipHeight <= 0 || _cropRectSource.IsEmpty)
        {
            return false;
        }

        return Math.Abs(_cropRectSource.X) > 0.5
            || Math.Abs(_cropRectSource.Y) > 0.5
            || Math.Abs(_cropRectSource.Width - _selectedClipWidth) > 0.5
            || Math.Abs(_cropRectSource.Height - _selectedClipHeight) > 0.5;
    }

    private bool IsZoomActive()
    {
        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void EnsureUniqueHotkeys(params HotkeyGesture[] hotkeys)
    {
        var duplicate = hotkeys
            .Where(hotkey => hotkey.IsEnabled)
            .GroupBy(hotkey => hotkey.ToStableId(), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException("Each enabled hotkey must be unique.");
        }
    }

    private string? BrowseForFolder(string currentPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            InitialDirectory = string.IsNullOrWhiteSpace(currentPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : currentPath,
            ShowNewFolderButton = true,
        };

        var owner = new WindowInteropHelper(this).Handle;
        var result = dialog.ShowDialog(new Win32WindowHandle(owner));
        return result == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private sealed class Win32WindowHandle(nint handle) : Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    private static string BuildMonitorHotkeyRegistrationName(string nodeId)
    {
        return $"{MonitorHotkeyPrefix}{nodeId}";
    }

    private static string BuildSessionSlotName(MonitorNodeViewModel node)
    {
        var rawName = string.IsNullOrWhiteSpace(node.DisplayTitle)
            ? $"Monitor_{node.Id[..Math.Min(6, node.Id.Length)]}"
            : node.DisplayTitle;

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitizedCharacters = rawName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();

        var sanitizedName = new string(sanitizedCharacters).Trim().Replace(' ', '_');
        return string.IsNullOrWhiteSpace(sanitizedName)
            ? $"Monitor_{node.Id[..Math.Min(6, node.Id.Length)]}"
            : sanitizedName;
    }

    private bool TryReserveClipCooldown(MonitorNodeViewModel node, out TimeSpan remainingCooldown)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_clipCooldownLock)
        {
            if (_nextClipAllowedAtByNodeId.TryGetValue(node.Id, out var nextAllowedAt)
                && nextAllowedAt > now)
            {
                remainingCooldown = nextAllowedAt - now;
                return false;
            }

            _nextClipAllowedAtByNodeId[node.Id] = now.Add(ClipSaveCooldown);
        }

        remainingCooldown = TimeSpan.Zero;
        return true;
    }

    private static string BuildClipCooldownStatus(MonitorNodeViewModel node, TimeSpan remainingCooldown)
    {
        var remainingSeconds = Math.Max(1, (int)Math.Ceiling(remainingCooldown.TotalSeconds));
        return $"{node.DisplayTitle} is on cooldown. Wait {remainingSeconds}s before clipping again.";
    }

    private IReadOnlyList<string> GetAudioSegments(int replayLengthSeconds)
    {
        return _audioSession?.GetRecentStableSegments(replayLengthSeconds + AudioAlignmentContextSeconds) ?? [];
    }

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open DualClip", null, (_, _) => RestoreFromTray());
        contextMenu.Items.Add("Check for Updates", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = CheckForUpdatesAsync(isManual: true, installWhenAvailable: false))));
        contextMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = GetApplicationNotifyIcon(),
            Text = "DualClip - Ready",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };

        notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
        return notifyIcon;
    }

    private static System.Drawing.Icon GetApplicationNotifyIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);

                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void HideToTray()
    {
        Hide();
        UpdateNotifyIconText();

        if (_hasShownTrayTip)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "DualClip";
        _notifyIcon.BalloonTipText = _viewModel.IsCapturing
            ? "DualClip is still running in the tray. Capture and hotkeys stay active."
            : "DualClip is still running in the tray. Double-click the icon to reopen.";
        _notifyIcon.ShowBalloonTip(3000);
        _hasShownTrayTip = true;
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    internal void RestoreFromExternalLaunch()
    {
        RestoreFromTray();
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        Close();
    }

    private void UpdateNotifyIconText()
    {
        _notifyIcon.Text = _viewModel.IsCapturing
            ? "DualClip - Capturing"
            : "DualClip - Idle";
    }

    private bool IsAnyHotkeyEditorRecording()
    {
        return _viewModel.MonitorNodes.Any(node => node.Hotkey.IsRecording);
    }

    private static HotkeyEditorState? GetHotkeyEditorState(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as HotkeyEditorState;
    }

    private static Key GetActualKey(WpfKeyEventArgs e)
    {
        return e.Key == Key.System ? e.SystemKey : e.Key;
    }

    private static HotkeyModifiers GetCurrentHotkeyModifiers(Key currentKey = Key.None)
    {
        var modifiers = HotkeyModifiers.None;
        var keyboardModifiers = Keyboard.Modifiers;

        if (keyboardModifiers.HasFlag(ModifierKeys.Control) || currentKey is Key.LeftCtrl or Key.RightCtrl)
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Alt) || currentKey is Key.LeftAlt or Key.RightAlt)
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Shift) || currentKey is Key.LeftShift or Key.RightShift)
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin) || currentKey is Key.LWin or Key.RWin)
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }

    private static uint TryGetVirtualKey(Key key)
    {
        return key == Key.None ? 0u : (uint)KeyInterop.VirtualKeyFromKey(key);
    }
}
