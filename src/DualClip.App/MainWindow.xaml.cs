using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
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
    private const double PreviewSeekEndGuardSeconds = 0.01d;
    private const int AudioAlignmentContextSeconds = 12;
    private static readonly TimeSpan ClipSaveCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EditorSaveTimeout = TimeSpan.FromMinutes(3);
    private const int OverwriteReplaceRetryCount = 6;
    private static readonly TimeSpan OverwriteReplaceInitialDelay = TimeSpan.FromMilliseconds(250);

    private readonly JsonAppConfigStore _configStore = new();
    private readonly GitHubReleaseUpdateService _updateService = new();
    private readonly MonitorEnumerationService _monitorService = new();
    private readonly GlobalHotkeyManager _hotkeyManager = new();
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly StartupLaunchService _startupLaunchService = new();
    private readonly MainWindowViewModel _viewModel = new();
    private readonly FfmpegTimelineEditor _timelineEditor = new();
    private readonly ClipThumbnailCache _clipThumbnailCache = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _clipLibraryRefreshTimer;
    private readonly DispatcherTimer _hotkeyHealthTimer;
    private readonly Dictionary<string, MonitorCaptureSession> _monitorSessionsByNodeId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextClipAllowedAtByNodeId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MonitorCaptureSession, MonitorNodeViewModel> _monitorNodesBySession = [];
    private readonly HashSet<string> _monitorNodesRecovering = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processingClipPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _clipCooldownLock = new();
    private readonly object _clipDeleteQueueLock = new();
    private readonly object _monitorRecoveryLock = new();
    private readonly Queue<QueuedClipDeletion> _clipDeleteQueue = new();
    private readonly HashSet<string> _pendingClipDeletionPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _settingsAutoSaveTimer;
    private readonly MediaElement _previewMediaElement;
    private readonly DateTimeOffset _startupOverlayShownAt = DateTimeOffset.UtcNow;
    private long _previewPlaybackRequestId;
    private bool _isExitRequested;
    private bool _hasShownTrayTip;
    private bool _isEditorBusy = false;
    private bool _isEditorSaveInProgress;
    private bool _isPreviewMediaReady;
    private bool _isProcessingClipDeleteQueue;
    private bool _isUpdatingTransformControls;
    private bool _isLoadingSettings;
    private bool _isSavingSettings;
    private bool _isSettingsSaveQueued;
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
    private double _previewVolumePercent = 100d;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private AudioReplaySession? _audioSession;
    private AudioReplaySession? _microphoneAudioSession;
    private CancellationTokenSource? _clipLibraryThumbnailCts;
    private GitHubUpdateRelease? _pendingUpdate;
    private LogViewerWindow? _logViewerWindow;
    private readonly bool _isPackagedApp = AppRuntimeInfo.IsPackaged;

    public MainWindow()
    {
        StartupDiagnostics.Write("MainWindow ctor entered.");
        InitializeComponent();
        StartupDiagnostics.Write("MainWindow InitializeComponent completed.");
        _previewMediaElement = CreatePreviewMediaElement();
        PreviewMediaHost.Children.Add(_previewMediaElement);
        RefreshPreviewVolume();
        StartupDiagnostics.Write("MainWindow preview media element created.");
        DataContext = _viewModel;
        _viewModel.IsPackagedApp = _isPackagedApp;
        _viewModel.CurrentVersionText = $"Current version: v{_updateService.CurrentVersionText}";
        if (_isPackagedApp)
        {
            _viewModel.UpdateStatusText = "This packaged build receives updates through Microsoft Store.";
            _viewModel.InstallUpdateButtonText = "Store Managed";
        }
        PreviewMediaElement.SpeedRatio = 1.0d;
        StartupDiagnostics.Write("MainWindow creating notify icon.");
        _notifyIcon = CreateNotifyIcon();
        StartupDiagnostics.Write("MainWindow notify icon created.");
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.MonitorNodes.CollectionChanged += MonitorNodes_CollectionChanged;
        _settingsAutoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _settingsAutoSaveTimer.Tick += SettingsAutoSaveTimer_Tick;
        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _previewTimer.Tick += PreviewTimer_Tick;
        _clipLibraryRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _clipLibraryRefreshTimer.Tick += ClipLibraryRefreshTimer_Tick;
        _hotkeyHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _hotkeyHealthTimer.Tick += HotkeyHealthTimer_Tick;
        UpdateWindowFrameState();
        UpdateEditorControlState();
        StartupDiagnostics.Write("MainWindow ctor completed.");
    }

    private MediaElement PreviewMediaElement => _previewMediaElement;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartupDiagnostics.Write("Window_Loaded entered.");
        AppLog.Info("MainWindow", "Window loaded.", ("startup_overlay_visible", _viewModel.IsStartupOverlayVisible));

        try
        {
            await LoadStateAsync();
            UpdateClipLibraryAutoRefreshState();
        }
        finally
        {
            var remainingOverlayTime = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - _startupOverlayShownAt);

            if (remainingOverlayTime > TimeSpan.Zero)
            {
                await Task.Delay(remainingOverlayTime);
            }

            _viewModel.IsStartupOverlayVisible = false;
            AppLog.Info("MainWindow", "Startup overlay dismissed.");
        }

        StartupDiagnostics.Write("Window_Loaded completed.");
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateClipLibraryAutoRefreshState();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        UpdateClipLibraryAutoRefreshState();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        _hotkeyManager.Attach(windowHandle);
        _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CenterWindowWithinWorkingArea));
    }

    private void CenterWindowWithinWorkingArea()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        var workingArea = Forms.Screen.FromHandle(windowHandle).WorkingArea;
        var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;

        if (double.IsNaN(windowWidth) || windowWidth <= 0)
        {
            windowWidth = MinWidth;
        }

        if (double.IsNaN(windowHeight) || windowHeight <= 0)
        {
            windowHeight = MinHeight;
        }

        Left = workingArea.Left + Math.Max(0d, (workingArea.Width - windowWidth) / 2d);
        Top = workingArea.Top + Math.Max(0d, (workingArea.Height - windowHeight) / 2d);
    }

    private void OpenDiscordButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://discord.gg/8E5qhMNhsR",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel.AppStatus = $"Could not open Discord invite: {ex.Message}";
        }
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
        _clipLibraryRefreshTimer.Stop();
        _hotkeyHealthTimer.Stop();
        _settingsAutoSaveTimer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.MonitorNodes.CollectionChanged -= MonitorNodes_CollectionChanged;
        _clipLibraryThumbnailCts?.Cancel();
        _clipLibraryThumbnailCts?.Dispose();
        foreach (var node in _viewModel.MonitorNodes)
        {
            node.PropertyChanged -= MonitorNode_PropertyChanged;
            node.Hotkey.PropertyChanged -= MonitorNodeHotkey_PropertyChanged;
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

    private async void ToggleMonitorNodeCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MonitorNodeViewModel node)
        {
            return;
        }

        if (node.IsCapturing)
        {
            await StopMonitorNodeCaptureAsync(node);
            return;
        }

        await StartMonitorNodeCaptureAsync(node);
    }

    private async void SaveMonitorNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MonitorNodeViewModel node)
        {
            await SaveMonitorNodeClipAsync(node);
        }
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(isManual: true, installWhenAvailable: false);
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallPendingUpdateAsync(isAutomatic: false);
    }

    private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallPendingUpdateAsync(isAutomatic: false);
    }

    private void UpdateLaterButton_Click(object sender, RoutedEventArgs e)
    {
        HideUpdateOverlay();
    }

    private void AddMonitorNodeButton_Click(object sender, RoutedEventArgs e)
    {
        var node = CreateMonitorNode(
            _viewModel.MonitorNodes.Count + 1,
            _viewModel.Monitors.FirstOrDefault(monitor =>
                _viewModel.MonitorNodes.All(nodeVm => nodeVm.SelectedMonitor?.DeviceName != monitor.DeviceName))
            ?? _viewModel.Monitors.FirstOrDefault());

        _viewModel.MonitorNodes.Add(node);
    }

    private void AutoDetectMonitorsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var monitors = _monitorService.GetMonitors();
            SetAvailableMonitors(monitors);

            _viewModel.MonitorNodes.Clear();

            for (var index = 0; index < _viewModel.Monitors.Count; index++)
            {
                _viewModel.MonitorNodes.Add(CreateMonitorNode(index + 1, _viewModel.Monitors[index]));
            }

            _viewModel.ErrorMessage = _viewModel.Monitors.Count == 0
                ? "DualClip could not find any connected monitors."
                : string.Empty;
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
        }
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

        SelectMonitorNodeOutputFolder(node);
    }

    private void OutputFolderTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MonitorNodeViewModel node)
        {
            return;
        }

        e.Handled = true;
        SelectMonitorNodeOutputFolder(node);
    }

    private void OpenMonitorNodeOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MonitorNodeViewModel node)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(node.OutputFolder))
            {
                throw new InvalidOperationException($"Choose an output folder for {node.DisplayTitle} first.");
            }

            Directory.CreateDirectory(node.OutputFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = node.OutputFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
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
        _isLoadingSettings = true;
        try
        {
            var config = await _configStore.LoadAsync();
            StartupDiagnostics.Write($"LoadStateAsync loaded config. StartCaptureOnStartup={config.StartCaptureOnStartup}, LaunchOnStartup={config.LaunchOnStartup}.");
            ApplyLaunchOnStartupSetting(config.LaunchOnStartup);
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

            if (_isPackagedApp)
            {
                StartupDiagnostics.Write("LoadStateAsync skipped GitHub update check for packaged app.");
            }
            else
            {
                StartupDiagnostics.Write("LoadStateAsync checking for updates.");
                await CheckForUpdatesAsync(isManual: false, installWhenAvailable: false);
                StartupDiagnostics.Write("LoadStateAsync finished update check.");
            }

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
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private async Task StartCaptureAsync()
    {
        StartupDiagnostics.Write("StartCaptureAsync entered.");
        AppLog.Info("Capture", "Start all capture requested.");
        _viewModel.ErrorMessage = string.Empty;

        try
        {
            if (_viewModel.IsCapturing)
            {
                StartupDiagnostics.Write("StartCaptureAsync detected an active capture session. Restarting capture.");
                await StopCaptureAsync();
            }

            var borderlessCaptureAllowed = await BorderlessCaptureAccessService.RequestAsync();
            StartupDiagnostics.Write($"StartCaptureAsync borderless access result: {borderlessCaptureAllowed}.");
            var config = BuildValidatedConfig();
            StartupDiagnostics.Write("StartCaptureAsync validated config.");
            ApplyLaunchOnStartupSetting(config.LaunchOnStartup);
            await _configStore.SaveAsync(config);
            StartupDiagnostics.Write("StartCaptureAsync saved config.");
            foreach (var node in _viewModel.MonitorNodes)
            {
                Directory.CreateDirectory(node.OutputFolder);
                node.Status = $"{node.DisplayTitle} idle.";
            }

            await EnsureSharedAudioSessionsAsync(config);
            var monitorSessions = new List<(MonitorNodeViewModel Node, MonitorCaptureSession Session)>(_viewModel.MonitorNodes.Count);

            try
            {
                foreach (var node in _viewModel.MonitorNodes)
                {
                    var monitorSession = CreateSession(node, config, borderlessCaptureAllowed);
                    SubscribeMonitorStatus(monitorSession, node);

                    try
                    {
                        await monitorSession.StartAsync();
                        monitorSessions.Add((node, monitorSession));
                        StartupDiagnostics.Write($"StartCaptureAsync monitor session started for {node.DisplayTitle}.");
                    }
                    catch
                    {
                        UnsubscribeMonitorStatus(monitorSession);
                        await monitorSession.DisposeAsync();
                        throw;
                    }
                }
            }
            catch
            {
                foreach (var monitorSession in monitorSessions)
                {
                    UnsubscribeMonitorStatus(monitorSession.Session);
                    await monitorSession.Session.DisposeAsync();
                }

                await StopSharedAudioSessionsAsync();
                throw;
            }

            _monitorSessionsByNodeId.Clear();

            foreach (var monitorSession in monitorSessions)
            {
                _monitorSessionsByNodeId[monitorSession.Node.Id] = monitorSession.Session;
            }

            UpdateCaptureState();
            RefreshHotkeysFromViewModel();
            _viewModel.AppStatus = string.Empty;
            UpdateNotifyIconText();
            AppLog.Info("Capture", "Start all capture completed.", ("active_monitor_count", _monitorSessionsByNodeId.Count));
            StartupDiagnostics.Write("StartCaptureAsync completed successfully.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"StartCaptureAsync failed: {ex}");
            AppLog.Error("Capture", "Start all capture failed.", ex);
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.AppStatus = "Capture did not start.";
            UpdateNotifyIconText();
        }
    }

    private async Task StopCaptureAsync()
    {
        AppLog.Info("Capture", "Stop all capture requested.", ("active_monitor_count", _monitorSessionsByNodeId.Count));
        _hotkeyManager.UnregisterAll();

        var monitorSessions = _monitorSessionsByNodeId.ToList();
        _monitorSessionsByNodeId.Clear();
        lock (_monitorRecoveryLock)
        {
            _monitorNodesRecovering.Clear();
        }

        foreach (var monitorSession in monitorSessions)
        {
            UnsubscribeMonitorStatus(monitorSession.Value);
            await monitorSession.Value.DisposeAsync();
        }

        foreach (var node in _viewModel.MonitorNodes)
        {
            node.Status = $"{node.DisplayTitle} idle.";
        }

        await StopSharedAudioSessionsAsync();

        UpdateCaptureState();
        _viewModel.AppStatus = string.Empty;
        lock (_clipCooldownLock)
        {
            _nextClipAllowedAtByNodeId.Clear();
        }
        UpdateNotifyIconText();
        AppLog.Info("Capture", "Stop all capture completed.");
    }

    private async Task StartMonitorNodeCaptureAsync(MonitorNodeViewModel node)
    {
        StartupDiagnostics.Write($"StartMonitorNodeCaptureAsync entered for {node.DisplayTitle}.");
        AppLog.Info("Capture", "Start monitor capture requested.", ("monitor_node", node.DisplayTitle));
        _viewModel.ErrorMessage = string.Empty;

        if (_monitorSessionsByNodeId.ContainsKey(node.Id))
        {
            return;
        }

        try
        {
            var captureNodes = _viewModel.MonitorNodes
                .Where(item => item.IsCapturing || ReferenceEquals(item, node))
                .ToList();
            var config = BuildValidatedCaptureConfig(captureNodes);
            var borderlessCaptureAllowed = await BorderlessCaptureAccessService.RequestAsync();
            Directory.CreateDirectory(node.OutputFolder);
            node.Status = $"{node.DisplayTitle} idle.";

            await EnsureSharedAudioSessionsAsync(config);

            var session = CreateSession(node, config, borderlessCaptureAllowed);
            SubscribeMonitorStatus(session, node);

            try
            {
                await session.StartAsync();
            }
            catch
            {
                UnsubscribeMonitorStatus(session);
                await session.DisposeAsync();
                if (_monitorSessionsByNodeId.Count == 0)
                {
                    await StopSharedAudioSessionsAsync();
                }

                throw;
            }

            _monitorSessionsByNodeId[node.Id] = session;
            UpdateCaptureState();
            RefreshHotkeysFromViewModel();
            _viewModel.AppStatus = string.Empty;
            UpdateNotifyIconText();
            AppLog.Info("Capture", "Start monitor capture completed.", ("monitor_node", node.DisplayTitle));
            StartupDiagnostics.Write($"StartMonitorNodeCaptureAsync completed for {node.DisplayTitle}.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"StartMonitorNodeCaptureAsync failed for {node.DisplayTitle}: {ex}");
            AppLog.Error("Capture", "Start monitor capture failed.", ex, ("monitor_node", node.DisplayTitle));
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.AppStatus = "Capture did not start.";
            UpdateNotifyIconText();
        }
    }

    private async Task StopMonitorNodeCaptureAsync(MonitorNodeViewModel node)
    {
        StartupDiagnostics.Write($"StopMonitorNodeCaptureAsync entered for {node.DisplayTitle}.");
        AppLog.Info("Capture", "Stop monitor capture requested.", ("monitor_node", node.DisplayTitle));

        if (!_monitorSessionsByNodeId.Remove(node.Id, out var session))
        {
            var cancelledRecovery = false;
            lock (_monitorRecoveryLock)
            {
                cancelledRecovery = _monitorNodesRecovering.Remove(node.Id);
            }

            if (cancelledRecovery)
            {
                _hotkeyManager.UnregisterAll();
                node.Status = $"{node.DisplayTitle} idle.";
                node.IsCapturing = false;
                ClearClipCooldown(node.Id);
                UpdateCaptureState();
                RefreshHotkeysFromViewModel();
                await StopSharedAudioSessionsIfIdleAsync();
                UpdateNotifyIconText();
            }

            return;
        }

        _hotkeyManager.UnregisterAll();

        lock (_monitorRecoveryLock)
        {
            _monitorNodesRecovering.Remove(node.Id);
        }

        UnsubscribeMonitorStatus(session);
        await session.DisposeAsync();

        node.Status = $"{node.DisplayTitle} idle.";
        ClearClipCooldown(node.Id);

        await StopSharedAudioSessionsIfIdleAsync();
        UpdateCaptureState();
        RefreshHotkeysFromViewModel();
        _viewModel.AppStatus = string.Empty;
        UpdateNotifyIconText();
        AppLog.Info("Capture", "Stop monitor capture completed.", ("monitor_node", node.DisplayTitle));
        StartupDiagnostics.Write($"StopMonitorNodeCaptureAsync completed for {node.DisplayTitle}.");
    }

    private async Task EnsureSharedAudioSessionsAsync(AppConfig config)
    {
        if (_audioSession is not null)
        {
            return;
        }

        var audioSession = CreatePrimaryAudioSession(config);
        var microphoneAudioSession = CreateSecondaryMicrophoneSession(config);
        SubscribeAudioStatus(audioSession);
        if (microphoneAudioSession is not null)
        {
            SubscribeAudioStatus(microphoneAudioSession);
        }

        try
        {
            await audioSession.StartAsync();
            StartupDiagnostics.Write("EnsureSharedAudioSessionsAsync audio session started.");
            if (microphoneAudioSession is not null)
            {
                await microphoneAudioSession.StartAsync();
                StartupDiagnostics.Write("EnsureSharedAudioSessionsAsync microphone session started.");
            }
        }
        catch
        {
            UnsubscribeAudioStatus(audioSession);
            if (microphoneAudioSession is not null)
            {
                UnsubscribeAudioStatus(microphoneAudioSession);
            }

            await audioSession.DisposeAsync();
            if (microphoneAudioSession is not null)
            {
                await microphoneAudioSession.DisposeAsync();
            }

            throw;
        }

        _audioSession = audioSession;
        _microphoneAudioSession = microphoneAudioSession;
    }

    private async Task StopSharedAudioSessionsIfIdleAsync()
    {
        if (_monitorSessionsByNodeId.Count > 0)
        {
            return;
        }

        await StopSharedAudioSessionsAsync();
    }

    private async Task StopSharedAudioSessionsAsync()
    {
        var audioSession = _audioSession;
        _audioSession = null;
        var microphoneAudioSession = _microphoneAudioSession;
        _microphoneAudioSession = null;

        if (audioSession is not null)
        {
            UnsubscribeAudioStatus(audioSession);
            await audioSession.DisposeAsync();
        }

        if (microphoneAudioSession is not null)
        {
            UnsubscribeAudioStatus(microphoneAudioSession);
            await microphoneAudioSession.DisposeAsync();
        }
    }

    private async Task<string?> SaveMonitorNodeClipAsync(
        MonitorNodeViewModel node,
        IReadOnlyList<string>? systemAudioSegments = null,
        IReadOnlyList<string>? microphoneAudioSegments = null,
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
        AppLog.Info("Capture", "Saving monitor clip.", ("monitor_node", node.DisplayTitle), ("output_folder", node.OutputFolder));

        try
        {
            var selectedSystemAudioSegments = systemAudioSegments ?? GetAudioSegments(monitorSession.Options.ReplayLengthSeconds);
            var selectedMicrophoneAudioSegments = microphoneAudioSegments ?? GetMicrophoneAudioSegments(monitorSession.Options.ReplayLengthSeconds);
            var outputPath = await monitorSession.SaveClipAsync(
                node.OutputFolder,
                selectedSystemAudioSegments,
                selectedMicrophoneAudioSegments,
                Math.Clamp(_viewModel.ClipAudioVolumePercent, 0d, 200d));
            node.Status = $"Saved clip: {outputPath}";

            if (refreshClipLibrary)
            {
                RefreshClipLibrary(outputPath);
            }

            AppLog.Info("Capture", "Monitor clip saved.", ("monitor_node", node.DisplayTitle), ("output_path", outputPath));
            return outputPath;
        }
        catch (Exception ex)
        {
            node.Status = $"{node.DisplayTitle} save failed: {ex.Message}";
            _viewModel.ErrorMessage = ex.Message;
            AppLog.Error("Capture", "Monitor clip save failed.", ex, ("monitor_node", node.DisplayTitle));
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

        AppLog.Info("Capture", "Save all active monitor clips requested.", ("active_monitor_count", activeNodes.Count));

        var replayLengthSeconds = _monitorSessionsByNodeId[activeNodes[0].Id].Options.ReplayLengthSeconds;
        var audioSegments = GetAudioSegments(replayLengthSeconds);
        var microphoneAudioSegments = GetMicrophoneAudioSegments(replayLengthSeconds);
        string? preferredPath = null;

        foreach (var node in activeNodes)
        {
            var outputPath = await SaveMonitorNodeClipAsync(
                node,
                audioSegments,
                microphoneAudioSegments,
                refreshClipLibrary: false);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                preferredPath ??= outputPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            RefreshClipLibrary(preferredPath);
        }

        AppLog.Info("Capture", "Save all active monitor clips completed.", ("preferred_path", preferredPath ?? "<none>"));
    }

    private async Task SaveSettingsAsync()
    {
        _viewModel.ErrorMessage = string.Empty;
        AppLog.Info("Settings", "Saving settings requested.", ("is_capturing", _viewModel.IsCapturing));

        try
        {
            var config = BuildValidatedConfig();
            foreach (var node in config.MonitorNodes)
            {
                Directory.CreateDirectory(node.OutputFolder);
            }

            ApplyLaunchOnStartupSetting(config.LaunchOnStartup);
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
            AppLog.Info("Settings", "Settings saved successfully.");
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.AppStatus = "Settings were not saved.";
            AppLog.Error("Settings", "Settings save failed.", ex);
        }
    }

    private async Task CheckForUpdatesAsync(bool isManual, bool installWhenAvailable)
    {
        if (_isPackagedApp)
        {
            SetPendingUpdate(null, isUpdateAvailable: false);
            _viewModel.UpdateStatusText = "This packaged build receives updates through Microsoft Store.";
            HideUpdateOverlay();
            return;
        }

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
            SetPendingUpdate(result.Release, result.IsUpdateAvailable);
            _viewModel.UpdateStatusText = result.StatusMessage;

            if (result.IsUpdateAvailable && result.Release is not null && installWhenAvailable)
            {
                _viewModel.IsCheckingForUpdates = false;
                await InstallReleaseUpdateAsync(result.Release, isAutomatic: true);
                return;
            }

            if (result.IsUpdateAvailable && result.Release is not null)
            {
                ShowUpdateFoundOverlay(result.Release);
            }
            else
            {
                HideUpdateOverlay();
            }
        }
        catch (Exception ex)
        {
            SetPendingUpdate(null, isUpdateAvailable: false);
            _viewModel.UpdateStatusText = isManual
                ? $"GitHub update check failed: {ex.Message}"
                : "Automatic update check could not reach GitHub.";
            HideUpdateOverlay();
        }
        finally
        {
            _viewModel.IsCheckingForUpdates = false;
        }
    }

    private async Task InstallPendingUpdateAsync(bool isAutomatic)
    {
        if (_isPackagedApp)
        {
            _viewModel.UpdateStatusText = "This packaged build receives updates through Microsoft Store.";
            return;
        }

        if (_pendingUpdate is null)
        {
            return;
        }

        await InstallReleaseUpdateAsync(_pendingUpdate, isAutomatic);
    }

    private async Task InstallReleaseUpdateAsync(GitHubUpdateRelease release, bool isAutomatic)
    {
        if (_isPackagedApp)
        {
            _viewModel.UpdateStatusText = "This packaged build receives updates through Microsoft Store.";
            HideUpdateOverlay();
            return;
        }

        if (_viewModel.IsCheckingForUpdates)
        {
            return;
        }

        _viewModel.IsCheckingForUpdates = true;
        ShowUpdateBusyOverlay("Initializing...");
        _viewModel.UpdateStatusText = isAutomatic
            ? $"Update v{release.VersionText} found on GitHub. Installing automatically..."
            : $"Downloading v{release.VersionText} from GitHub...";

        try
        {
            var preparedUpdate = _updateService.TryGetPreparedUpdate(release);

            if (preparedUpdate is not null)
            {
                StartupDiagnostics.Write($"Reusing staged update v{release.VersionText} from '{preparedUpdate.DownloadedAssetPath}'.");
                ShowUpdateBusyOverlay("Initializing...");
                _viewModel.UpdateStatusText = isAutomatic
                    ? $"Installing previously downloaded v{release.VersionText} and restarting DualClip..."
                    : $"Installing previously downloaded v{release.VersionText}...";
            }
            else
            {
                var progress = new Progress<double>(value =>
                {
                    ShowUpdateBusyOverlay($"Downloading... {value:P0}");
                    _viewModel.UpdateStatusText = $"Downloading v{release.VersionText} from GitHub... {value:P0}";
                });

                ShowUpdateBusyOverlay("Downloading...");
                preparedUpdate = await _updateService.DownloadUpdateAsync(release, progress);
            }

            ShowUpdateBusyOverlay("Installing...");
            await RestartForPreparedUpdateAsync(preparedUpdate);
        }
        catch (Exception ex)
        {
            _viewModel.UpdateStatusText = isAutomatic
                ? $"Automatic update failed: {ex.Message}"
                : $"Update install failed: {ex.Message}";
            ShowUpdatePromptOverlay($"Update failed: {ex.Message}");
        }
        finally
        {
            if (!_isExitRequested)
            {
                _viewModel.IsCheckingForUpdates = false;
            }
        }
    }

    private void SetPendingUpdate(GitHubUpdateRelease? release, bool isUpdateAvailable)
    {
        _pendingUpdate = isUpdateAvailable ? release : null;
        _viewModel.IsUpdateAvailable = isUpdateAvailable;
        _viewModel.InstallUpdateButtonText = isUpdateAvailable && release is not null
            ? $"Install v{release.VersionText}"
            : "Install Update";

        if (release is null)
        {
            _viewModel.UpdateNotesTitle = "Update Notes";
            _viewModel.UpdateNotesText = string.Empty;
            return;
        }

        _viewModel.UpdateNotesTitle = $"Update Notes for v{release.VersionText}";
        _viewModel.UpdateNotesText = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "No update notes for this update"
            : release.ReleaseNotes;
    }

    private void SetPendingUpdate(GitHubUpdateRelease? release)
    {
        SetPendingUpdate(release, release is not null);
    }

    private async Task RestartForPreparedUpdateAsync(GitHubPreparedUpdate preparedUpdate)
    {
        ShowUpdateBusyOverlay("Installing...");
        _viewModel.UpdateStatusText = $"Installing v{preparedUpdate.Release.VersionText} and restarting DualClip...";

        if (_viewModel.IsCapturing)
        {
            _viewModel.AppStatus = "Stopping capture for update...";
            UpdateNotifyIconText();
            await StopCaptureAsync();
        }

        StartupDiagnostics.Write($"Launching updater helper for v{preparedUpdate.Release.VersionText} from '{preparedUpdate.DownloadedAssetPath}'.");
        _updateService.LaunchUpdaterAndRestart(preparedUpdate);
        _isExitRequested = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowUpdateFoundOverlay(GitHubUpdateRelease release)
    {
        _viewModel.UpdateOverlayText = "Update found";
        _viewModel.IsUpdateOverlayBusy = false;
        _viewModel.IsUpdateOverlayVisible = true;
    }

    private void ShowUpdatePromptOverlay(string text)
    {
        _viewModel.UpdateOverlayText = text;
        _viewModel.IsUpdateOverlayBusy = false;
        _viewModel.IsUpdateOverlayVisible = true;
    }

    private void ShowUpdateBusyOverlay(string text)
    {
        _viewModel.UpdateOverlayText = text;
        _viewModel.IsUpdateOverlayBusy = true;
        _viewModel.IsUpdateOverlayVisible = true;
    }

    private void HideUpdateOverlay()
    {
        _viewModel.UpdateOverlayText = "Update found";
        _viewModel.IsUpdateOverlayBusy = false;
        _viewModel.IsUpdateOverlayVisible = false;
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
            var resolvedFfmpegPath = AppPaths.ResolveDefaultFfmpegPath();

            if (string.IsNullOrWhiteSpace(resolvedFfmpegPath) || !File.Exists(resolvedFfmpegPath))
            {
                throw new InvalidOperationException($"ffmpeg.exe was not found at '{_viewModel.FfmpegPath}'.");
            }

            _viewModel.FfmpegPath = resolvedFfmpegPath;
        }

        var selectedVideoQuality = _viewModel.SelectedVideoQuality?.Value
            ?? throw new InvalidOperationException("Choose a clip quality before starting capture.");
        var clipAudioVolumePercent = (int)Math.Round(Math.Clamp(_viewModel.ClipAudioVolumePercent, 0d, 200d), MidpointRounding.AwayFromZero);

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
            AudioMode = _viewModel.SelectedAudioMode?.Value ?? AudioCaptureMode.System,
            ClipAudioVolumePercent = clipAudioVolumePercent,
            MicrophoneDeviceId = string.IsNullOrWhiteSpace(_viewModel.SelectedMicrophone?.Id)
                ? null
                : _viewModel.SelectedMicrophone.Id,
            OutputFolderA = monitorNodeConfigs.ElementAtOrDefault(0)?.OutputFolder ?? string.Empty,
            OutputFolderB = monitorNodeConfigs.ElementAtOrDefault(1)?.OutputFolder ?? monitorNodeConfigs.ElementAtOrDefault(0)?.OutputFolder ?? string.Empty,
            UseUnifiedOutputFolder = false,
            StartCaptureOnStartup = _viewModel.StartCaptureOnStartup,
            LaunchOnStartup = _viewModel.LaunchOnStartup,
            FfmpegPath = _viewModel.FfmpegPath,
            HotkeyA = monitorNodeConfigs.ElementAtOrDefault(0)?.Hotkey ?? HotkeyGesture.Disabled(),
            HotkeyB = monitorNodeConfigs.ElementAtOrDefault(1)?.Hotkey ?? HotkeyGesture.Disabled(),
            HotkeyBoth = HotkeyGesture.Disabled(),
        };
    }

    private AppConfig BuildValidatedCaptureConfig(IEnumerable<MonitorNodeViewModel> monitorNodes)
    {
        var captureNodes = monitorNodes.ToList();

        if (captureNodes.Count == 0)
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
            var resolvedFfmpegPath = AppPaths.ResolveDefaultFfmpegPath();

            if (string.IsNullOrWhiteSpace(resolvedFfmpegPath) || !File.Exists(resolvedFfmpegPath))
            {
                throw new InvalidOperationException($"ffmpeg.exe was not found at '{_viewModel.FfmpegPath}'.");
            }

            _viewModel.FfmpegPath = resolvedFfmpegPath;
        }

        var selectedVideoQuality = _viewModel.SelectedVideoQuality?.Value
            ?? throw new InvalidOperationException("Choose a clip quality before starting capture.");
        var clipAudioVolumePercent = (int)Math.Round(Math.Clamp(_viewModel.ClipAudioVolumePercent, 0d, 200d), MidpointRounding.AwayFromZero);
        var seenMonitorDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monitorNodeConfigs = new List<MonitorNodeConfig>(captureNodes.Count);

        foreach (var node in captureNodes)
        {
            if (node.SelectedMonitor is null)
            {
                throw new InvalidOperationException($"Select a display for {node.DisplayTitle}.");
            }

            if (!seenMonitorDeviceNames.Add(node.SelectedMonitor.DeviceName))
            {
                throw new InvalidOperationException("Each active monitor node must target a different display.");
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
            AudioMode = _viewModel.SelectedAudioMode?.Value ?? AudioCaptureMode.System,
            ClipAudioVolumePercent = clipAudioVolumePercent,
            MicrophoneDeviceId = string.IsNullOrWhiteSpace(_viewModel.SelectedMicrophone?.Id)
                ? null
                : _viewModel.SelectedMicrophone.Id,
            OutputFolderA = monitorNodeConfigs.ElementAtOrDefault(0)?.OutputFolder ?? string.Empty,
            OutputFolderB = monitorNodeConfigs.ElementAtOrDefault(1)?.OutputFolder ?? monitorNodeConfigs.ElementAtOrDefault(0)?.OutputFolder ?? string.Empty,
            UseUnifiedOutputFolder = false,
            StartCaptureOnStartup = _viewModel.StartCaptureOnStartup,
            LaunchOnStartup = _viewModel.LaunchOnStartup,
            FfmpegPath = _viewModel.FfmpegPath,
            HotkeyA = monitorNodeConfigs.ElementAtOrDefault(0)?.Hotkey ?? HotkeyGesture.Disabled(),
            HotkeyB = monitorNodeConfigs.ElementAtOrDefault(1)?.Hotkey ?? HotkeyGesture.Disabled(),
            HotkeyBoth = HotkeyGesture.Disabled(),
        };
    }

    private void RegisterHotkeys(AppConfig config)
    {
        _hotkeyManager.ReplaceAll(BuildHotkeyRegistrations(config.MonitorNodes));
        UpdateHotkeyHealthTimerState();
    }

    private static IReadOnlyList<KeyValuePair<string, HotkeyGesture>> BuildHotkeyRegistrations(IEnumerable<MonitorNodeConfig> monitorNodes)
    {
        return monitorNodes
            .Where(node => node.Hotkey.IsEnabled)
            .Select(node => KeyValuePair.Create(BuildMonitorHotkeyRegistrationName(node.Id), node.Hotkey))
            .ToList();
    }

    private void RefreshHotkeysFromViewModel()
    {
        if (!_viewModel.IsCapturing || IsAnyHotkeyEditorRecording())
        {
            return;
        }

        try
        {
            var hotkeyRegistrations = BuildHotkeyRegistrations(
                _viewModel.MonitorNodes
                    .Where(node => node.IsCapturing)
                    .Select(node => new MonitorNodeConfig
                {
                    Id = node.Id,
                    Hotkey = node.Hotkey.ToModel(),
                }));
            _hotkeyManager.ReplaceAll(hotkeyRegistrations);
            _viewModel.ErrorMessage = string.Empty;
            UpdateHotkeyHealthTimerState();
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
        }
    }

    private void UpdateCaptureState()
    {
        var activeNodeIds = new HashSet<string>(_monitorSessionsByNodeId.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var node in _viewModel.MonitorNodes)
        {
            node.IsCapturing = activeNodeIds.Contains(node.Id);
        }

        _viewModel.IsCapturing = activeNodeIds.Count > 0;
    }

    private void ClearClipCooldown(string nodeId)
    {
        lock (_clipCooldownLock)
        {
            _nextClipAllowedAtByNodeId.Remove(nodeId);
        }
    }

    private void HotkeyHealthTimer_Tick(object? sender, EventArgs e)
    {
        if (!_viewModel.IsCapturing || IsAnyHotkeyEditorRecording())
        {
            UpdateHotkeyHealthTimerState();
            return;
        }

        try
        {
            _hotkeyManager.RefreshAll();
        }
        catch
        {
            RefreshHotkeysFromViewModel();
        }
    }

    private void UpdateHotkeyHealthTimerState()
    {
        var shouldRun = _viewModel.IsCapturing && !IsAnyHotkeyEditorRecording();

        if (shouldRun)
        {
            if (!_hotkeyHealthTimer.IsEnabled)
            {
                _hotkeyHealthTimer.Start();
            }

            return;
        }

        if (_hotkeyHealthTimer.IsEnabled)
        {
            _hotkeyHealthTimer.Stop();
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

    private MonitorCaptureSession CreateReplacementSession(
        MonitorNodeViewModel node,
        MonitorCaptureSessionOptions previousOptions)
    {
        return new MonitorCaptureSession(new MonitorCaptureSessionOptions
        {
            SlotName = previousOptions.SlotName,
            Monitor = node.SelectedMonitor ?? previousOptions.Monitor,
            FfmpegPath = previousOptions.FfmpegPath,
            BufferDirectory = previousOptions.BufferDirectory,
            ReplayLengthSeconds = previousOptions.ReplayLengthSeconds,
            FpsTarget = previousOptions.FpsTarget,
            VideoQuality = previousOptions.VideoQuality,
            PreferBorderlessCapture = previousOptions.PreferBorderlessCapture,
        });
    }

    private AudioReplaySession CreatePrimaryAudioSession(AppConfig config)
    {
        return new AudioReplaySession(new AudioReplaySessionOptions
        {
            BufferDirectory = AppPaths.GetBufferDirectory(config.AudioMode == AudioCaptureMode.Microphone ? "Microphone" : "Audio"),
            ReplayLengthSeconds = config.ReplayLengthSeconds,
            AudioMode = config.AudioMode,
            MicrophoneDeviceId = config.AudioMode == AudioCaptureMode.Microphone
                ? config.MicrophoneDeviceId
                : null,
        });
    }

    private AudioReplaySession? CreateSecondaryMicrophoneSession(AppConfig config)
    {
        if (config.AudioMode != AudioCaptureMode.System || string.IsNullOrWhiteSpace(config.MicrophoneDeviceId))
        {
            return null;
        }

        return new AudioReplaySession(new AudioReplaySessionOptions
        {
            BufferDirectory = AppPaths.GetBufferDirectory("Microphone"),
            ReplayLengthSeconds = config.ReplayLengthSeconds,
            AudioMode = AudioCaptureMode.Microphone,
            MicrophoneDeviceId = config.MicrophoneDeviceId,
        });
    }

    private void SubscribeMonitorStatus(MonitorCaptureSession session, MonitorNodeViewModel node)
    {
        _monitorNodesBySession[session] = node;
        session.StatusChanged += MonitorSession_StatusChanged;
        session.RecoveryRequested += MonitorSession_RecoveryRequested;
    }

    private void UnsubscribeMonitorStatus(MonitorCaptureSession session)
    {
        session.StatusChanged -= MonitorSession_StatusChanged;
        session.RecoveryRequested -= MonitorSession_RecoveryRequested;
        _monitorNodesBySession.Remove(session);
    }

    private void MonitorSession_StatusChanged(object? sender, string message)
    {
        if (sender is not MonitorCaptureSession session || !_monitorNodesBySession.TryGetValue(session, out var node))
        {
            return;
        }

        AppLog.Info("CaptureSession", "Monitor session status updated.", ("monitor_node", node.DisplayTitle), ("status", message));

        Dispatcher.BeginInvoke(() =>
        {
            node.Status = message;
            UpdateNotifyIconText();
        });
    }

    private void MonitorSession_RecoveryRequested(object? sender, MonitorCaptureRecoveryRequestedEventArgs e)
    {
        if (sender is not MonitorCaptureSession session || !_monitorNodesBySession.TryGetValue(session, out var node))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => _ = RecoverMonitorSessionAsync(session, node, e.StaleDuration)));
    }

    private async Task RecoverMonitorSessionAsync(
        MonitorCaptureSession staleSession,
        MonitorNodeViewModel node,
        TimeSpan staleDuration)
    {
        if (!_viewModel.IsCapturing || _isExitRequested)
        {
            return;
        }

        if (!_monitorSessionsByNodeId.TryGetValue(node.Id, out var currentSession)
            || !ReferenceEquals(currentSession, staleSession))
        {
            return;
        }

        lock (_monitorRecoveryLock)
        {
            if (!_monitorNodesRecovering.Add(node.Id))
            {
                return;
            }
        }

        try
        {
            var staleSeconds = Math.Max(1, (int)Math.Ceiling(staleDuration.TotalSeconds));
            node.Status = $"{node.DisplayTitle} stopped receiving fresh frames for {staleSeconds}s. Restarting capture...";
            StartupDiagnostics.Write($"RecoverMonitorSessionAsync restarting {node.DisplayTitle} after {staleSeconds}s without a new frame.");

            _monitorSessionsByNodeId.Remove(node.Id);
            UnsubscribeMonitorStatus(staleSession);
            await staleSession.DisposeAsync();

            if (!_viewModel.IsCapturing || _isExitRequested)
            {
                return;
            }

            var replacementSession = CreateReplacementSession(node, staleSession.Options);
            SubscribeMonitorStatus(replacementSession, node);

            try
            {
                await replacementSession.StartAsync();
                _monitorSessionsByNodeId[node.Id] = replacementSession;
                UpdateCaptureState();
                RefreshHotkeysFromViewModel();
                StartupDiagnostics.Write($"RecoverMonitorSessionAsync restarted {node.DisplayTitle} successfully.");
            }
            catch
            {
                UnsubscribeMonitorStatus(replacementSession);
                await replacementSession.DisposeAsync();
                UpdateCaptureState();
                RefreshHotkeysFromViewModel();
                await StopSharedAudioSessionsIfIdleAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"RecoverMonitorSessionAsync failed for {node.DisplayTitle}: {ex}");
            node.Status = $"{node.DisplayTitle} recovery failed: {ex.Message}";
            _viewModel.ErrorMessage = ex.Message;
            UpdateCaptureState();
            RefreshHotkeysFromViewModel();
        }
        finally
        {
            lock (_monitorRecoveryLock)
            {
                _monitorNodesRecovering.Remove(node.Id);
            }
        }
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
        AppLog.Info("AudioSession", "Audio session status updated.", ("status", message));
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
        QueueSettingsAutoSave();
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
            QueueSettingsAutoSave();
        }
    }

    private void MonitorNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MonitorNodeViewModel node in e.OldItems)
            {
                node.PropertyChanged -= MonitorNode_PropertyChanged;
                node.Hotkey.PropertyChanged -= MonitorNodeHotkey_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MonitorNodeViewModel node in e.NewItems)
            {
                node.PropertyChanged += MonitorNode_PropertyChanged;
                node.Hotkey.PropertyChanged += MonitorNodeHotkey_PropertyChanged;
            }
        }

        RefreshClipLibrary(GetSelectedClip()?.FilePath);
        RefreshHotkeysFromViewModel();
        QueueSettingsAutoSave();
    }

    private void MonitorNode_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorNodeViewModel.OutputFolder))
        {
            RefreshClipLibrary(GetSelectedClip()?.FilePath);
        }

        if (e.PropertyName is nameof(MonitorNodeViewModel.Name)
            or nameof(MonitorNodeViewModel.SelectedMonitor)
            or nameof(MonitorNodeViewModel.OutputFolder))
        {
            QueueSettingsAutoSave();
        }
    }

    private void MonitorNodeHotkey_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is HotkeyEditorState state && !state.IsRecording)
        {
            RefreshHotkeysFromViewModel();
            QueueSettingsAutoSave();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsCapturing))
        {
            UpdateNotifyIconText();
            UpdateHotkeyHealthTimerState();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.ReplayLengthSecondsText)
            or nameof(MainWindowViewModel.FpsTargetText)
            or nameof(MainWindowViewModel.SelectedVideoQuality)
            or nameof(MainWindowViewModel.SelectedAudioMode)
            or nameof(MainWindowViewModel.SelectedMicrophone)
            or nameof(MainWindowViewModel.ClipAudioVolumePercent)
            or nameof(MainWindowViewModel.StartCaptureOnStartup)
            or nameof(MainWindowViewModel.LaunchOnStartup)
            or nameof(MainWindowViewModel.FfmpegPath))
        {
            QueueSettingsAutoSave();
        }
    }

    private void ApplyLaunchOnStartupSetting(bool launchOnStartup)
    {
        _startupLaunchService.Apply(launchOnStartup);
    }

    private void QueueSettingsAutoSave()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _isSettingsSaveQueued = true;

        if (_isSavingSettings)
        {
            return;
        }

        _settingsAutoSaveTimer.Stop();
        _settingsAutoSaveTimer.Start();
    }

    private async void SettingsAutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _settingsAutoSaveTimer.Stop();

        if (_isLoadingSettings || _isSavingSettings || !_isSettingsSaveQueued)
        {
            return;
        }

        _isSettingsSaveQueued = false;
        _isSavingSettings = true;

        try
        {
            await SaveSettingsAsync();
        }
        finally
        {
            _isSavingSettings = false;

            if (_isSettingsSaveQueued && !_isLoadingSettings)
            {
                _settingsAutoSaveTimer.Start();
            }
        }
    }

    private static string GetDefaultMonitorOutputFolder(int monitorNumber)
    {
        var videosRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Path.Combine(videosRoot, "DualClip", $"Monitor{monitorNumber}");
    }

    private void SelectMonitorNodeOutputFolder(MonitorNodeViewModel node)
    {
        var selectedFolder = BrowseForFolder(node.OutputFolder);

        if (!string.IsNullOrWhiteSpace(selectedFolder))
        {
            node.OutputFolder = selectedFolder;
            _viewModel.ErrorMessage = string.Empty;
        }
    }

    private MonitorNodeViewModel CreateMonitorNode(int nodeNumber, MonitorDescriptor? selectedMonitor)
    {
        var node = new MonitorNodeViewModel(
            Guid.NewGuid().ToString("N"),
            $"Monitor {nodeNumber}",
            GetDefaultMonitorOutputFolder(nodeNumber));

        node.SelectedMonitor = selectedMonitor;
        node.LoadHotkey(HotkeyGesture.Disabled());
        return node;
    }

    private void SetAvailableMonitors(IReadOnlyList<MonitorDescriptor> monitors)
    {
        _viewModel.Monitors.Clear();

        foreach (var monitor in monitors.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.DeviceName))
        {
            _viewModel.Monitors.Add(monitor);
        }
    }

    private void RefreshClipLibrary(string? preferredPath = null)
    {
        var selectedPath = preferredPath ?? GetSelectedClip()?.FilePath;
        var items = GetClipLibraryItems();
        var thumbnailRefreshItems = ApplyClipLibrarySnapshot(items);

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

        if (thumbnailRefreshItems.Count > 0)
        {
            StartClipLibraryThumbnailRefresh(thumbnailRefreshItems);
        }
    }

    private IReadOnlyList<ClipLibraryItem> ApplyClipLibrarySnapshot(IReadOnlyList<ClipLibraryItem> items)
    {
        var existingByPath = _viewModel.ClipLibrary.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
        var desiredItems = new List<ClipLibraryItem>(items.Count);
        var thumbnailRefreshItems = new List<ClipLibraryItem>();

        foreach (var snapshot in items)
        {
            if (existingByPath.TryGetValue(snapshot.FilePath, out var existing))
            {
                var metadataChanged = existing.ModifiedAt != snapshot.ModifiedAt
                    || existing.FileSizeBytes != snapshot.FileSizeBytes
                    || !string.Equals(existing.DisplayName, snapshot.DisplayName, StringComparison.Ordinal);

                existing.UpdateFileDetails(snapshot.DisplayName, snapshot.ModifiedAt, snapshot.FileSizeBytes);
                ApplyClipTransientState(existing);
                desiredItems.Add(existing);

                if (metadataChanged || !existing.HasThumbnail)
                {
                    thumbnailRefreshItems.Add(existing);
                }

                continue;
            }

            ApplyClipTransientState(snapshot);
            desiredItems.Add(snapshot);
            thumbnailRefreshItems.Add(snapshot);
        }

        var desiredPaths = desiredItems
            .Select(item => item.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = _viewModel.ClipLibrary.Count - 1; index >= 0; index--)
        {
            if (!desiredPaths.Contains(_viewModel.ClipLibrary[index].FilePath))
            {
                _viewModel.ClipLibrary.RemoveAt(index);
            }
        }

        for (var index = 0; index < desiredItems.Count; index++)
        {
            var desiredItem = desiredItems[index];

            if (index >= _viewModel.ClipLibrary.Count)
            {
                _viewModel.ClipLibrary.Add(desiredItem);
                continue;
            }

            if (ReferenceEquals(_viewModel.ClipLibrary[index], desiredItem))
            {
                continue;
            }

            var existingIndex = _viewModel.ClipLibrary.IndexOf(desiredItem);

            if (existingIndex >= 0)
            {
                _viewModel.ClipLibrary.Move(existingIndex, index);
            }
            else
            {
                _viewModel.ClipLibrary.Insert(index, desiredItem);
            }
        }

        while (_viewModel.ClipLibrary.Count > desiredItems.Count)
        {
            _viewModel.ClipLibrary.RemoveAt(_viewModel.ClipLibrary.Count - 1);
        }

        return thumbnailRefreshItems;
    }

    private bool IsClipProcessing(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return _processingClipPaths.Contains(filePath);
    }

    private void ApplyClipTransientState(ClipLibraryItem item)
    {
        item.IsProcessing = IsClipProcessing(item.FilePath);
    }

    private void SetClipProcessingState(string? filePath, bool isProcessing)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (isProcessing)
        {
            _processingClipPaths.Add(filePath);
        }
        else
        {
            _processingClipPaths.Remove(filePath);
        }

        foreach (var item in _viewModel.ClipLibrary.Where(item =>
                     string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            item.IsProcessing = isProcessing;
        }

        var selectedClip = GetSelectedClip();
        if (string.Equals(selectedClip?.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            if (isProcessing)
            {
                StopPreview(clearSource: true);
            }
            else if (PreviewMediaElement.Source is null && File.Exists(filePath))
            {
                LoadSelectedClip();
                return;
            }
        }

        UpdateEditorControlState();
    }

    private bool HasClipLibraryChanged(IReadOnlyList<ClipLibraryItem> items)
    {
        if (_viewModel.ClipLibrary.Count != items.Count)
        {
            return true;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var current = _viewModel.ClipLibrary[index];
            var next = items[index];

            if (!string.Equals(current.FilePath, next.FilePath, StringComparison.OrdinalIgnoreCase)
                || current.ModifiedAt != next.ModifiedAt
                || current.FileSizeBytes != next.FileSizeBytes)
            {
                return true;
            }
        }

        return false;
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
                if (IsClipPendingDeletion(filePath) || IsInternalClipLibraryArtifact(filePath))
                {
                    continue;
                }

                var fileInfo = new FileInfo(filePath);
                var item = new ClipLibraryItem
                {
                    FilePath = filePath,
                };
                item.UpdateFileDetails(
                    $"{fileInfo.Name}   [{fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}]",
                    fileInfo.LastWriteTime,
                    fileInfo.Length);
                ApplyClipTransientState(item);
                clips.Add(item);
            }
        }

        return clips
            .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.ModifiedAt)
            .ToList();
    }

    private bool IsClipPendingDeletion(string filePath)
    {
        lock (_clipDeleteQueueLock)
        {
            return _pendingClipDeletionPaths.Contains(filePath);
        }
    }

    private static bool IsInternalClipLibraryArtifact(string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        return fileName.Contains("_overwrite_stage_", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("_overwrite_backup_", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".dualclipbak", StringComparison.OrdinalIgnoreCase);
    }

    private void ClipLibraryRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!CanAutoRefreshClipLibrary())
        {
            UpdateClipLibraryAutoRefreshState();
            return;
        }

        RefreshClipLibrary(GetSelectedClip()?.FilePath);
    }

    private void UpdateClipLibraryAutoRefreshState()
    {
        if (CanAutoRefreshClipLibrary())
        {
            if (!_clipLibraryRefreshTimer.IsEnabled)
            {
                _clipLibraryRefreshTimer.Start();
            }

            return;
        }

        if (_clipLibraryRefreshTimer.IsEnabled)
        {
            _clipLibraryRefreshTimer.Stop();
        }
    }

    private bool CanAutoRefreshClipLibrary()
    {
        return IsLoaded
            && IsVisible
            && IsActive
            && !_isLoadingSettings
            && !_isEditorBusy
            && !_viewModel.ClipLibrary.Any(item => item.IsRenaming);
    }

    private void StartClipLibraryThumbnailRefresh(IReadOnlyList<ClipLibraryItem> items)
    {
        _clipLibraryThumbnailCts?.Cancel();
        _clipLibraryThumbnailCts?.Dispose();

        if (items.Count == 0)
        {
            _clipLibraryThumbnailCts = null;
            return;
        }

        var ffmpegPath = ResolveClipLibraryThumbnailFfmpegPath();

        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            _clipLibraryThumbnailCts = null;
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _clipLibraryThumbnailCts = cancellationTokenSource;
        _ = PopulateClipLibraryThumbnailsAsync(items, ffmpegPath, cancellationTokenSource.Token);
    }

    private async Task PopulateClipLibraryThumbnailsAsync(
        IReadOnlyList<ClipLibraryItem> items,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var thumbnailPath = await _clipThumbnailCache
                    .EnsureThumbnailAsync(ffmpegPath, item, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(thumbnailPath))
                {
                    continue;
                }

                await Dispatcher.InvokeAsync(() => item.SetThumbnailPath(thumbnailPath), DispatcherPriority.Background, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string ResolveClipLibraryThumbnailFfmpegPath()
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.FfmpegPath) && File.Exists(_viewModel.FfmpegPath))
        {
            return _viewModel.FfmpegPath;
        }

        return AppPaths.ResolveDefaultFfmpegPath();
    }

    private void ClipListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AppLog.Info(
            "Editor",
            "Clip library selection changed.",
            ("selected_clip", GetSelectedClip()?.FilePath ?? "<none>"));
        LoadSelectedClip();
    }

    private void RenameClipTitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ClipLibraryItem item)
        {
            return;
        }

        if (item.IsProcessing)
        {
            return;
        }

        foreach (var clip in _viewModel.ClipLibrary.Where(clip => !ReferenceEquals(clip, item)))
        {
            clip.CancelRename();
        }

        item.BeginRename();
        UpdateClipLibraryAutoRefreshState();
    }

    private void ClipTitleRenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfTextBox textBox
            || textBox.DataContext is not ClipLibraryItem item
            || !item.IsRenaming)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }),
            DispatcherPriority.Input);
    }

    private async void ClipTitleRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await CommitClipRenameAsync((sender as FrameworkElement)?.DataContext as ClipLibraryItem);
    }

    private async void ClipTitleRenameTextBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipLibraryItem item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitClipRenameAsync(item);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            item.CancelRename();
            UpdateClipLibraryAutoRefreshState();
        }
    }

    private async Task CommitClipRenameAsync(ClipLibraryItem? item)
    {
        if (item is null || !item.IsRenaming)
        {
            return;
        }

        var proposedName = (item.EditableFileName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(proposedName))
        {
            item.CancelRename();
            UpdateClipLibraryAutoRefreshState();
            _viewModel.ErrorMessage = "Clip name cannot be empty.";
            return;
        }

        if (proposedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            item.CancelRename();
            UpdateClipLibraryAutoRefreshState();
            _viewModel.ErrorMessage = "Clip name contains invalid file name characters.";
            return;
        }

        var directoryPath = Path.GetDirectoryName(item.FilePath)
            ?? throw new InvalidOperationException("Clip folder could not be resolved.");
        var newPath = Path.Combine(directoryPath, $"{proposedName}{item.FileExtension}");

        item.EndRename();
        UpdateClipLibraryAutoRefreshState();

        if (string.Equals(item.FilePath, newPath, StringComparison.Ordinal))
        {
            _viewModel.ErrorMessage = string.Empty;
            return;
        }

        var selectedClip = GetSelectedClip();
        var renamedSelectedClip = string.Equals(selectedClip?.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);

        if (item.IsProcessing)
        {
            item.CancelRename();
            UpdateClipLibraryAutoRefreshState();
            return;
        }

        if (renamedSelectedClip)
        {
            StopPreview(clearSource: true);
        }

        try
        {
            RenameClipFile(item.FilePath, newPath);
            _viewModel.ErrorMessage = string.Empty;
            RefreshClipLibrary(newPath);

            if (renamedSelectedClip)
            {
                LoadSelectedClip();
            }
        }
        catch (Exception ex)
        {
            item.CancelRename();
            UpdateClipLibraryAutoRefreshState();
            _viewModel.ErrorMessage = ex.Message;
        }
    }

    private static void RenameClipFile(string sourcePath, string destinationPath)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            var sourceFileName = Path.GetFileName(sourcePath);
            var destinationFileName = Path.GetFileName(destinationPath);

            if (string.Equals(sourceFileName, destinationFileName, StringComparison.Ordinal))
            {
                return;
            }

            var directoryPath = Path.GetDirectoryName(sourcePath)
                ?? throw new InvalidOperationException("Clip folder could not be resolved.");
            var temporaryPath = Path.Combine(directoryPath, $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
            File.Move(sourcePath, temporaryPath);
            File.Move(temporaryPath, destinationPath);
            return;
        }

        if (File.Exists(destinationPath))
        {
            throw new InvalidOperationException("A clip with that name already exists.");
        }

        File.Move(sourcePath, destinationPath);
    }

    private async void DeleteClipButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement element
                || element.DataContext is not ClipLibraryItem item
                || string.IsNullOrWhiteSpace(item.FilePath))
            {
                return;
            }

            if (item.IsProcessing)
            {
                return;
            }

            var filePath = item.FilePath;
            var clipName = Path.GetFileName(filePath);
            var shouldStartDeleteProcessor = false;

            lock (_clipDeleteQueueLock)
            {
                if (!_pendingClipDeletionPaths.Add(filePath))
                {
                    return;
                }

                _clipDeleteQueue.Enqueue(new QueuedClipDeletion
                {
                    FilePath = filePath,
                    ClipName = clipName,
                });

                if (!_isProcessingClipDeleteQueue)
                {
                    _isProcessingClipDeleteQueue = true;
                    shouldStartDeleteProcessor = true;
                }
            }

            RemoveClipFromLibraryForQueuedDeletion(item);
            _viewModel.ErrorMessage = string.Empty;
            _viewModel.EditorStatus = BuildClipDeleteQueuedStatus(clipName);

            if (shouldStartDeleteProcessor)
            {
                await ProcessQueuedClipDeletionsAsync();
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.EditorStatus = $"Delete failed: {ex.Message}";
        }
    }

    private void RemoveClipFromLibraryForQueuedDeletion(ClipLibraryItem item)
    {
        if (!_viewModel.ClipLibrary.Contains(item))
        {
            return;
        }

        var selectedClip = GetSelectedClip();
        var deletedSelectedClip = string.Equals(selectedClip?.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);
        var deletedIndex = _viewModel.ClipLibrary.IndexOf(item);

        if (item.IsRenaming)
        {
            item.CancelRename();
        }

        if (deletedSelectedClip)
        {
            StopPreview(clearSource: true);
            ClearTimelineUndoHistory();
            ClearLoadedClipEditorState();
        }

        _viewModel.ClipLibrary.Remove(item);

        if (_viewModel.ClipLibrary.Count == 0)
        {
            ClipListBox.SelectedItem = null;
            _viewModel.Editor.SelectedClipTitle = "No clip selected";
            _viewModel.EditorStatus = "No clips found in the export folders yet.";
            UpdateEditorControlState();
            return;
        }

        if (deletedSelectedClip)
        {
            ClipListBox.SelectedIndex = Math.Clamp(deletedIndex, 0, _viewModel.ClipLibrary.Count - 1);
            return;
        }

        UpdateEditorControlState();
    }

    private async Task ProcessQueuedClipDeletionsAsync()
    {
        while (true)
        {
            QueuedClipDeletion deletion;

            lock (_clipDeleteQueueLock)
            {
                if (_clipDeleteQueue.Count == 0)
                {
                    _isProcessingClipDeleteQueue = false;
                    return;
                }

                deletion = _clipDeleteQueue.Dequeue();
            }

            await Dispatcher.InvokeAsync(
                () =>
                {
                    _viewModel.ErrorMessage = string.Empty;
                    _viewModel.EditorStatus = BuildClipDeleteInProgressStatus(deletion.ClipName);
                },
                DispatcherPriority.Background);

            string? deleteError = null;

            try
            {
                await Task.Run(() =>
                {
                    if (File.Exists(deletion.FilePath))
                    {
                        File.Delete(deletion.FilePath);
                    }
                });
            }
            catch (Exception ex)
            {
                deleteError = ex.Message;
            }

            lock (_clipDeleteQueueLock)
            {
                _pendingClipDeletionPaths.Remove(deletion.FilePath);
            }

            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (string.IsNullOrWhiteSpace(deleteError))
                    {
                        _viewModel.ErrorMessage = string.Empty;
                        _viewModel.EditorStatus = BuildClipDeleteCompletedStatus(deletion.ClipName);
                        return;
                    }

                    _viewModel.ErrorMessage = deleteError;
                    RefreshClipLibrary(GetSelectedClip()?.FilePath);
                    _viewModel.EditorStatus = $"Delete failed: {deleteError}";
                },
                DispatcherPriority.Background);
        }
    }

    private string BuildClipDeleteQueuedStatus(string clipName)
    {
        var remaining = GetPendingClipDeletionCount();
        return remaining > 1
            ? $"Queued {clipName} for deletion. {remaining} clips are pending."
            : $"Queued {clipName} for deletion.";
    }

    private string BuildClipDeleteInProgressStatus(string clipName)
    {
        var remaining = GetPendingClipDeletionCount();
        return remaining > 1
            ? $"Deleting {clipName}... {remaining - 1} more queued."
            : $"Deleting {clipName}...";
    }

    private string BuildClipDeleteCompletedStatus(string clipName)
    {
        var remaining = GetPendingClipDeletionCount();
        return remaining > 0
            ? $"Deleted {clipName}. {remaining} clip deletes still running."
            : $"Deleted {clipName}.";
    }

    private int GetPendingClipDeletionCount()
    {
        lock (_clipDeleteQueueLock)
        {
            return _pendingClipDeletionPaths.Count;
        }
    }

    private void LoadSelectedClip()
    {
        SaveEditedClipPopup.IsOpen = false;
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

        if (selectedClip.IsProcessing)
        {
            ClearLoadedClipEditorState();
            _viewModel.Editor.SelectedClipTitle = Path.GetFileName(selectedClip.FilePath);
            _viewModel.EditorStatus = $"Processing {Path.GetFileName(selectedClip.FilePath)}...";
            TimelinePositionTextBlock.Text = "Processing clip...";
            UpdateEditorVisuals();
            UpdateEditorControlState();
            return;
        }

        ClearTimelineUndoHistory();
        _viewModel.Editor.SelectedClipTitle = Path.GetFileName(selectedClip.FilePath);
        _selectedClipDurationSeconds = 0;
        _selectedClipWidth = 0;
        _selectedClipHeight = 0;
        ClearLoadedClipEditorState();
        _isPreviewMediaReady = false;
        PreviewMediaElement.Source = new Uri(selectedClip.FilePath);
        RefreshPreviewVolume();
        AppLog.Info("Editor", "Loading selected clip into preview.", ("clip_path", selectedClip.FilePath));
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
        _isPreviewMediaReady = true;
        _selectedClipDurationSeconds = PreviewMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
        _selectedClipWidth = PreviewMediaElement.NaturalVideoWidth;
        _selectedClipHeight = PreviewMediaElement.NaturalVideoHeight;
        InitializeTimelineForLoadedClip();
        RefreshPreviewVolume();
        AppLog.Info(
            "Editor",
            "Preview media opened.",
            ("clip_title", _viewModel.Editor.SelectedClipTitle),
            ("duration_seconds", _selectedClipDurationSeconds),
            ("width", _selectedClipWidth),
            ("height", _selectedClipHeight));

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
        _isPreviewMediaReady = false;
        _viewModel.Editor.SelectedClipTitle = selectedClip is null ? "No clip selected" : Path.GetFileName(selectedClip.FilePath);
        _viewModel.ErrorMessage = message;
        _viewModel.EditorStatus = $"Preview failed for {clipName}.";
        TimelinePositionTextBlock.Text = "Preview unavailable";
        UpdateEditorControlState();
        AppLog.Error("Editor", "Preview media failed.", e.ErrorException, ("clip_name", clipName));
    }

    private void PreviewMediaElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_isTimelinePlaybackActive && TryContinuePreviewPlaybackAtNextSegment())
        {
            return;
        }

        StopPreviewPlaybackForPlaybackCompletion();
        SeekToPlayhead(updatePreviewPosition: true);
    }

    private void PlayPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedClip = GetSelectedClip();

        if (selectedClip is null)
        {
            return;
        }

        if (selectedClip.IsProcessing)
        {
            _viewModel.EditorStatus = $"Processing {Path.GetFileName(selectedClip.FilePath)}...";
            return;
        }

        if (PreviewMediaElement.Source is null || !_isPreviewMediaReady)
        {
            AppLog.Warn(
                "Editor",
                "Preview play requested before media was ready. Reloading clip preview.",
                ("clip_path", selectedClip.FilePath),
                ("has_source", PreviewMediaElement.Source is not null),
                ("is_preview_ready", _isPreviewMediaReady));
            LoadSelectedClip();
            return;
        }

        if (_isTimelinePlaybackActive)
        {
            PausePreviewPlayback();
            SeekToPlayhead(updatePreviewPosition: true);
            return;
        }

        var timelineDuration = GetTimelineDisplayDurationSeconds();

        if (timelineDuration <= 0)
        {
            return;
        }

        _playheadSeconds = ClampTimelinePlayhead(_playheadSeconds);

        if (_playheadSeconds >= GetTimelinePlayableMaximumSeconds() - PreviewPlaybackSegmentEndToleranceSeconds)
        {
            _playheadSeconds = GetTimelinePlayableMinimumSeconds();
        }

        if (!TryFindSegmentAtTimelineTime(_playheadSeconds, out var segment, out var segmentTimelineStart, out var localOffsetSeconds)
            || segment is null)
        {
            _playheadSeconds = GetTimelinePlayableMinimumSeconds();

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

        AppLog.Info(
            "Editor",
            "Starting preview playback.",
            ("clip_path", selectedClip.FilePath),
            ("playhead_seconds", _playheadSeconds),
            ("segment_start_seconds", segment.SourceStartSeconds),
            ("segment_end_seconds", segment.SourceEndSeconds));
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

        if (UsesSingleSegmentSourceTimeline())
        {
            var maxPlayableSeconds = GetTimelinePlayableMaximumSeconds();

            if (_selectedTimelineSegment.SourceEndSeconds <= _selectedTimelineSegment.SourceStartSeconds
                || PreviewMediaElement.Position.TotalSeconds >= maxPlayableSeconds - PreviewPlaybackSegmentEndToleranceSeconds)
            {
                _playheadSeconds = maxPlayableSeconds;
                StopPreviewPlaybackForPlaybackCompletion();
                SeekToPlayhead(updatePreviewPosition: true);
                return;
            }

            _playheadSeconds = ClampTimelinePlayhead(PreviewMediaElement.Position.TotalSeconds);
            UpdateTimelinePlaybackVisuals();
            return;
        }

        if (segmentDuration <= 0 || mediaLocalOffsetSeconds >= segmentDuration - PreviewPlaybackSegmentEndToleranceSeconds)
        {
            if (TryContinuePreviewPlaybackAtNextSegment())
            {
                return;
            }

            _playheadSeconds = GetTimelineDisplayDurationSeconds();
            StopPreviewPlaybackForPlaybackCompletion();
            SeekToPlayhead(updatePreviewPosition: true);
            return;
        }

        _playheadSeconds = Math.Clamp(
            segmentTimelineStart + Math.Clamp(mediaLocalOffsetSeconds, 0, segmentDuration),
            0,
            GetTimelineDisplayDurationSeconds());

        UpdateTimelinePlaybackVisuals();
    }

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineVisuals();
    }

    private void TimelineRulerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GetTimelineDurationSeconds() <= 0 || TimelineRulerCanvas.ActualWidth <= 0)
        {
            return;
        }

        MovePlayheadToTimelineX(e.GetPosition(TimelineRulerCanvas).X);
    }

    private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GetTimelineDurationSeconds() <= 0 || TimelineCanvas.ActualWidth <= 0)
        {
            return;
        }

        var clickPosition = e.GetPosition(TimelineCanvas);
        if (clickPosition.Y > TimelineSegmentTopPixels)
        {
            return;
        }

        MovePlayheadToTimelineX(clickPosition.X);
    }

    private void MovePlayheadToTimelineX(double timelineX)
    {
        _playheadSeconds = ClampTimelinePlayhead(TimelineXToTime(timelineX));

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

        _playheadSeconds = ClampTimelinePlayhead(PreviewMediaElement.Position.TotalSeconds);
        UpdateTimelineLabel(_playheadSeconds);
        UpdateTimelineVisuals();
    }

    private void UpdateTimelineLabel(double currentSeconds)
    {
        TimelinePositionTextBlock.Text = $"{currentSeconds:0.00}s / {GetTimelineDisplayDurationSeconds():0.00}s";
    }

    private void UpdateTimelinePlaybackVisuals()
    {
        if (TimelineOverlayCanvas is null || PlayheadLine is null || PlayheadThumb is null || GetTimelineDurationSeconds() <= 0)
        {
            return;
        }

        var playheadX = TimeToTimelineX(_playheadSeconds);
        Canvas.SetLeft(PlayheadLine, playheadX - (PlayheadLine.Width / 2d));
        Canvas.SetTop(PlayheadLine, TimelinePlayheadTopPixels);
        PositionTimelineThumb(PlayheadThumb, playheadX, TimelinePlayheadTopPixels - 2d);
        ScrollPlayheadIntoView();
        UpdateTimelineLabel(_playheadSeconds);
    }

    private void PlayheadThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        PausePreviewPlayback();
        _playheadSeconds = ClampTimelinePlayhead(_playheadSeconds + TimelineDeltaToTime(e.HorizontalChange));
        _playheadSeconds = ClampTimelinePlayhead(SnapTimelineTimeValue(_playheadSeconds, GetTimelinePlayableMinimumSeconds(), GetTimelinePlayableMaximumSeconds()));
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
        _trimStartSeconds = Math.Clamp(
            _trimStartSeconds + TimelineDeltaToTime(e.HorizontalChange),
            0,
            Math.Max(0, _trimEndSeconds - MinimumTrimDurationSeconds));
        _trimStartSeconds = SnapSourceClipTime(_trimStartSeconds, 0, _playheadSeconds, Math.Max(0, _trimEndSeconds - MinimumTrimDurationSeconds));
        ApplyCurrentEditorStateToSelectedSegment();

        if (!UsesSingleSegmentSourceTimeline())
        {
            NormalizeTimelineSegmentPositions();
        }

        _playheadSeconds = GetSelectedSegmentTimelineStartSeconds();
        SeekToPlayhead(updatePreviewPosition: true);
        return;
    }

    private void TrimEndThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        PausePreviewPlayback();
        _trimEndSeconds = Math.Clamp(
            _trimEndSeconds + TimelineDeltaToTime(e.HorizontalChange),
            Math.Min(_selectedClipDurationSeconds, _trimStartSeconds + MinimumTrimDurationSeconds),
            _selectedClipDurationSeconds);
        _trimEndSeconds = SnapSourceClipTime(_trimEndSeconds, _selectedClipDurationSeconds, _playheadSeconds, _trimStartSeconds + MinimumTrimDurationSeconds);
        ApplyCurrentEditorStateToSelectedSegment();

        if (!UsesSingleSegmentSourceTimeline())
        {
            NormalizeTimelineSegmentPositions();
        }

        _playheadSeconds = ClampTimelinePlayhead(_playheadSeconds);
        SeekToPlayhead(updatePreviewPosition: true);
        return;
    }

    private void TrimThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_selectedTimelineSegment is null)
        {
            return;
        }

        if (!UsesSingleSegmentSourceTimeline())
        {
            NormalizeTimelineSegmentPositions();
        }

        _playheadSeconds = sender == TrimStartThumb
            ? GetSelectedSegmentTimelineStartSeconds()
            : ClampTimelinePlayhead(_playheadSeconds);
        SeekToPlayhead(updatePreviewPosition: true);
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

    private void PreviewOverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.Editor.IsSelectToolActive || GetSelectedClip() is null || PreviewMediaElement.Source is null)
        {
            return;
        }

        PlayPreviewButton_Click(PlayPreviewButton, new RoutedEventArgs());
        e.Handled = true;
    }

    private void CropTopLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(sender as FrameworkElement, e.HorizontalChange, e.VerticalChange, resizeLeft: true, resizeTop: true);
    }

    private void CropTopRightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(sender as FrameworkElement, e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: true);
    }

    private void CropBottomLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(sender as FrameworkElement, e.HorizontalChange, e.VerticalChange, resizeLeft: true, resizeTop: false);
    }

    private void CropBottomRightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeCropFromCorner(sender as FrameworkElement, e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: false);
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
        SaveEditedClipPopup.IsOpen = false;
        await SaveEditedClipAsync(overwriteSelected: false);
    }

    private async void OverwriteEditedClipButton_Click(object sender, RoutedEventArgs e)
    {
        SaveEditedClipPopup.IsOpen = false;
        await SaveEditedClipAsync(overwriteSelected: true);
    }

    private async Task SaveEditedClipAsync(bool overwriteSelected)
    {
        if (_isEditorSaveInProgress)
        {
            return;
        }

        var selectedClip = GetSelectedClip();

        if (selectedClip is null)
        {
            return;
        }

        var sourcePath = selectedClip.FilePath;
        var currentSelectedPath = GetSelectedClip()?.FilePath;
        var fallbackPreferredPath = string.Equals(currentSelectedPath, sourcePath, StringComparison.OrdinalIgnoreCase)
            ? overwriteSelected ? sourcePath : BuildEditedOutputPath(sourcePath)
            : currentSelectedPath;
        var targetPath = sourcePath;
        var temporaryOutputPath = overwriteSelected
            ? BuildOverwriteStagingPath(sourcePath)
            : fallbackPreferredPath!;
        var request = BuildTimelineEditRequest(sourcePath, temporaryOutputPath);
        var operationId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var saveStopwatch = Stopwatch.StartNew();

        _viewModel.ErrorMessage = string.Empty;
        _viewModel.EditorStatus = overwriteSelected
            ? "Overwriting selected clip in the background..."
            : "Saving edited clip in the background...";
        AppLog.Info(
            "Editor",
            overwriteSelected ? "Background overwrite requested." : "Background save-as-new requested.",
            ("operation_id", operationId),
            ("operation_kind", overwriteSelected ? "overwrite" : "save_as_new"),
            ("source_path", sourcePath),
            ("target_path", temporaryOutputPath),
            ("timeline_duration_seconds", GetTimelineDurationSeconds()),
            ("segment_count", _timelineSegments.Count),
            ("selected_clip_path", currentSelectedPath ?? "<none>"),
            ("save_timeout_seconds", EditorSaveTimeout.TotalSeconds));
        SetClipProcessingState(sourcePath, isProcessing: true);
        _isEditorSaveInProgress = true;
        UpdateEditorControlState();

        try
        {
            using var exportTimeoutCts = new CancellationTokenSource(EditorSaveTimeout);

            try
            {
                await _timelineEditor.ExportAsync(request, exportTimeoutCts.Token);
            }
            catch (OperationCanceledException) when (exportTimeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"DualClip stopped the edit because ffmpeg did not finish within {EditorSaveTimeout.TotalMinutes:0} minutes.");
            }

            AppLog.Info(
                "Editor",
                "Edited clip export completed.",
                ("operation_id", operationId),
                ("operation_kind", overwriteSelected ? "overwrite" : "save_as_new"),
                ("staged_output_path", temporaryOutputPath),
                ("elapsed_ms", saveStopwatch.ElapsedMilliseconds));

            if (overwriteSelected)
            {
                await PrepareClipForOverwriteAsync(targetPath);
                await Task.Run(() => ReplaceClipWithStagedExport(targetPath, temporaryOutputPath, operationId));
            }
            else
            {
                targetPath = temporaryOutputPath;
            }

            await FinalizeEditedClipSaveAsync(sourcePath, targetPath, fallbackPreferredPath, overwriteSelected);
            AppLog.Info(
                "Editor",
                overwriteSelected ? "Background overwrite completed." : "Background save-as-new completed.",
                ("operation_id", operationId),
                ("operation_kind", overwriteSelected ? "overwrite" : "save_as_new"),
                ("source_path", sourcePath),
                ("target_path", targetPath),
                ("elapsed_ms", saveStopwatch.ElapsedMilliseconds));
            _viewModel.EditorStatus = overwriteSelected
                ? $"Rewrote {Path.GetFileName(targetPath)}."
                : $"Saved {Path.GetFileName(targetPath)}.";
        }
        catch (Exception ex)
        {
            AppLog.Error(
                "Editor",
                overwriteSelected ? "Background overwrite failed." : "Background save-as-new failed.",
                ex,
                ("operation_id", operationId),
                ("operation_kind", overwriteSelected ? "overwrite" : "save_as_new"),
                ("source_path", sourcePath),
                ("target_path", temporaryOutputPath),
                ("elapsed_ms", saveStopwatch.ElapsedMilliseconds));
            _viewModel.ErrorMessage = ex.Message;
            _viewModel.EditorStatus = $"Edit failed: {ex.Message}";
            TryDeleteFile(temporaryOutputPath);
        }
        finally
        {
            _isEditorSaveInProgress = false;
            SetClipProcessingState(sourcePath, isProcessing: false);
            UpdateEditorControlState();
        }
    }

    private void SaveEditedClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditorSaveInProgress || GetSelectedClip() is null || GetTimelineDurationSeconds() <= 0)
        {
            return;
        }

        SaveEditedClipPopup.IsOpen = !SaveEditedClipPopup.IsOpen;
    }

    private void OpenClipLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedClip = GetSelectedClip();

        if (selectedClip is null || string.IsNullOrWhiteSpace(selectedClip.FilePath))
        {
            return;
        }

        try
        {
            if (!File.Exists(selectedClip.FilePath))
            {
                throw new FileNotFoundException("The selected clip file could not be found.", selectedClip.FilePath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{selectedClip.FilePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = ex.Message;
        }
    }

    private static void ReplaceClipWithStagedExport(string targetPath, string stagedPath, string operationId)
    {
        if (!File.Exists(stagedPath))
        {
            throw new FileNotFoundException("The staged edited clip could not be found.", stagedPath);
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("The original clip could not be found for overwrite.", targetPath);
        }

        var backupPath = BuildOverwriteBackupPath(targetPath);
        var delay = OverwriteReplaceInitialDelay;
        IOException? lastIoException = null;

        for (var attempt = 1; attempt <= OverwriteReplaceRetryCount; attempt++)
        {
            try
            {
                TryDeleteFile(backupPath);
                File.Replace(stagedPath, targetPath, backupPath, ignoreMetadataErrors: true);
                TryDeleteFile(backupPath);
                AppLog.Info(
                    "Editor",
                    "Overwrite replace completed.",
                    ("operation_id", operationId),
                    ("attempt", attempt),
                    ("target_path", targetPath));
                return;
            }
            catch (IOException ex) when (attempt < OverwriteReplaceRetryCount)
            {
                lastIoException = ex;
                AppLog.Warn(
                    "Editor",
                    "Overwrite replace hit a file lock. Retrying.",
                    ("operation_id", operationId),
                    ("attempt", attempt),
                    ("retry_delay_ms", delay.TotalMilliseconds),
                    ("target_path", targetPath),
                    ("staged_path", stagedPath),
                    ("backup_path", backupPath),
                    ("exception_message", ex.Message));
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2d, 4000d));
            }
        }

        throw new IOException(
            $"DualClip could not replace the original clip after {OverwriteReplaceRetryCount} attempts because the file stayed locked.",
            lastIoException);
    }

    private async Task PrepareClipForOverwriteAsync(string targetPath)
    {
        await Dispatcher.InvokeAsync(
            () =>
            {
                if (string.Equals(GetSelectedClip()?.FilePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    StopPreview(clearSource: true);
                }
            },
            DispatcherPriority.Send);

        await Task.Delay(120);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(120);
        AppLog.Info(
            "Editor",
            "Prepared clip for overwrite swap.",
            ("target_path", targetPath));
    }

    private async Task FinalizeEditedClipSaveAsync(
        string sourcePath,
        string targetPath,
        string? fallbackPreferredPath,
        bool overwriteSelected)
    {
        await Dispatcher.InvokeAsync(
            () =>
            {
                var preferredPath = ResolvePreferredPathAfterSave(sourcePath, targetPath, fallbackPreferredPath);
                RefreshClipLibrary(preferredPath);

                if (!overwriteSelected)
                {
                    return;
                }

                var selectedClip = GetSelectedClip();

                if (!string.Equals(selectedClip?.FilePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                AppLog.Info(
                    "Editor",
                    "Reloading overwritten clip from disk after save.",
                    ("clip_path", targetPath));
                LoadSelectedClip();
            },
            DispatcherPriority.Background);
    }

    private string? ResolvePreferredPathAfterSave(string sourcePath, string targetPath, string? fallbackPreferredPath)
    {
        var selectedPath = GetSelectedClip()?.FilePath;

        if (string.Equals(selectedPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return targetPath;
        }

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            return selectedPath;
        }

        return fallbackPreferredPath;
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
        _isPreviewMediaReady = false;
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
            _isPreviewMediaReady = false;
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

    private void StopPreviewPlaybackForPlaybackCompletion()
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
        _playheadSeconds = ClampTimelinePlayhead(_playheadSeconds);

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

        _playheadSeconds = ClampTimelinePlayhead(timelineTimeSeconds);
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
        var maxSeekSeconds = _selectedClipDurationSeconds <= 0
            ? 0
            : Math.Max(0, _selectedClipDurationSeconds - PreviewSeekEndGuardSeconds);
        PreviewMediaElement.Position = TimeSpan.FromSeconds(Math.Clamp(positionSeconds, 0, maxSeekSeconds));
    }

    private void PreviewVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _previewVolumePercent = e.NewValue;
        RefreshPreviewVolume();
    }

    private void RefreshPreviewVolume()
    {
        try
        {
            PreviewMediaElement.Volume = Math.Clamp(_previewVolumePercent / 100d, 0d, 1d);
        }
        catch
        {
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Logs", "Opening log viewer window.");
        if (_logViewerWindow is null || !_logViewerWindow.IsLoaded)
        {
            _logViewerWindow = new LogViewerWindow
            {
                Owner = this,
            };
            _logViewerWindow.Closed += (_, _) => _logViewerWindow = null;
            _logViewerWindow.Show();
            return;
        }

        _logViewerWindow.Activate();
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
        if (TimelineCanvas is null || TimelineSegmentsCanvas is null || TimelineOverlayCanvas is null)
        {
            return;
        }

        const double timelineHeight = 132d;

        var timelineWidth = GetTimelineCanvasWidth();
        TimelineCanvas.Width = timelineWidth;
        TimelineCanvas.Height = timelineHeight;
        TimelineSegmentsCanvas.Width = timelineWidth;
        TimelineSegmentsCanvas.Height = timelineHeight;
        TimelineOverlayCanvas.Width = timelineWidth;
        TimelineOverlayCanvas.Height = timelineHeight;

        RenderTimelineSegments();

        var width = timelineWidth;
        var trackWidth = Math.Max(0, width - TimelineLeftPaddingPixels - TimelineRightPaddingPixels);
        Canvas.SetLeft(TimelineTrackRectangle, TimelineLeftPaddingPixels);
        Canvas.SetTop(TimelineTrackRectangle, TimelineTrackTopPixels);
        TimelineTrackRectangle.Width = trackWidth;

        var hasSelectedSegment = _selectedTimelineSegment is not null;
        var selectedSegmentStart = hasSelectedSegment ? GetSelectedSegmentTimelineStartSeconds() : 0;
        var selectedSegmentDuration = hasSelectedSegment ? GetSelectedSegmentDurationSeconds() : 0;
        var trimStartX = TimeToTimelineX(selectedSegmentStart);
        var trimEndX = TimeToTimelineX(selectedSegmentStart + selectedSegmentDuration);
        var playheadX = TimeToTimelineX(_playheadSeconds);
        Canvas.SetLeft(TrimSelectionRectangle, trimStartX);
        Canvas.SetTop(TrimSelectionRectangle, TimelineTrackTopPixels);
        TrimSelectionRectangle.Width = Math.Max(0, trimEndX - trimStartX);

        PlayheadLine.Height = Math.Max(24d, TimelineTrackTopPixels + TimelineTrackRectangle.Height - TimelinePlayheadTopPixels + 10d);
        Canvas.SetLeft(PlayheadLine, playheadX - (PlayheadLine.Width / 2d));
        Canvas.SetTop(PlayheadLine, TimelinePlayheadTopPixels);

        PositionTimelineThumb(TrimStartThumb, trimStartX, TimelineTrimThumbTopPixels);
        PositionTimelineThumb(TrimEndThumb, trimEndX, TimelineTrimThumbTopPixels);
        PositionTimelineThumb(PlayheadThumb, playheadX, TimelinePlayheadTopPixels - 2d);
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

            if (PreviewMediaHost is not null)
            {
                PreviewMediaHost.Width = 0;
                PreviewMediaHost.Height = 0;
                PreviewMediaHost.Margin = new Thickness(0);
                PreviewMediaHost.RenderTransform = System.Windows.Media.Transform.Identity;
                PreviewMediaHost.Opacity = 1d;
            }

            if (TransformSelectionBorder is not null)
            {
                TransformSelectionBorder.Visibility = Visibility.Collapsed;
            }

            if (TransformMoveThumb is not null)
            {
                TransformMoveThumb.Visibility = Visibility.Collapsed;
            }

            if (CropMoveThumb is not null)
            {
                CropMoveThumb.Visibility = Visibility.Collapsed;
                CropMoveThumb.Width = 0;
                CropMoveThumb.Height = 0;
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
        Canvas.SetLeft(CropSelectionBorder, 0);
        Canvas.SetTop(CropSelectionBorder, 0);

        CropMoveThumb.Width = cropLocalRect.Width;
        CropMoveThumb.Height = cropLocalRect.Height;
        Canvas.SetLeft(CropMoveThumb, 0);
        Canvas.SetTop(CropMoveThumb, 0);

        PositionCropThumb(CropTopLeftThumb, 0, 0);
        PositionCropThumb(CropTopRightThumb, cropLocalRect.Width, 0);
        PositionCropThumb(CropBottomLeftThumb, 0, cropLocalRect.Height);
        PositionCropThumb(CropBottomRightThumb, cropLocalRect.Width, cropLocalRect.Height);
        PositionCropThumb(CropTopThumb, cropLocalRect.Width / 2d, 0);
        PositionCropThumb(CropRightThumb, cropLocalRect.Width, cropLocalRect.Height / 2d);
        PositionCropThumb(CropBottomThumb, cropLocalRect.Width / 2d, cropLocalRect.Height);
        PositionCropThumb(CropLeftThumb, 0, cropLocalRect.Height / 2d);

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
        var selectedClip = GetSelectedClip();
        var hasClip = selectedClip is not null;
        var hasSelectedSegment = _selectedTimelineSegment is not null;
        var isSelectedClipProcessing = selectedClip?.IsProcessing ?? false;
        var isEnabled = hasClip && hasSelectedSegment && !_isEditorBusy && !isSelectedClipProcessing;
        var hasTimeline = hasClip && GetTimelineDurationSeconds() > 0;
        var canSaveEditedClip = hasTimeline && !_isEditorSaveInProgress && !isSelectedClipProcessing;
        var canInteractWithTimeline = hasTimeline && !_isEditorBusy && !isSelectedClipProcessing;

        PlayPreviewButton.IsEnabled = isEnabled;
        StepBackButton.IsEnabled = canInteractWithTimeline;
        StepForwardButton.IsEnabled = canInteractWithTimeline;
        SplitSegmentButton.IsEnabled = isEnabled;
        CopySegmentButton.IsEnabled = isEnabled;
        PasteSegmentButton.IsEnabled = _copiedTimelineSegment is not null && !_isEditorBusy && !isSelectedClipProcessing;
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
        ToolSelectButton.IsEnabled = hasClip && !_isEditorBusy && !isSelectedClipProcessing;
        ToolCropButton.IsEnabled = hasClip && !_isEditorBusy && !isSelectedClipProcessing;
        ToolTransformButton.IsEnabled = hasClip && !_isEditorBusy && !isSelectedClipProcessing;
        OpenClipLocationButton.IsEnabled = hasClip && !isSelectedClipProcessing;
        SaveEditedClipButton.IsEnabled = canSaveEditedClip;
        SaveEditedAsNewButton.IsEnabled = canSaveEditedClip;
        OverwriteEditedClipButton.IsEnabled = canSaveEditedClip;
        TimelineCanvas.IsEnabled = canInteractWithTimeline;
        TimelineSegmentsCanvas.IsEnabled = canInteractWithTimeline;
        TimelineScrollViewer.IsEnabled = canInteractWithTimeline;

        if (!canSaveEditedClip)
        {
            SaveEditedClipPopup.IsOpen = false;
        }

        UpdatePreviewPlaybackButtonVisualState();
        UpdateClipLibraryAutoRefreshState();
    }

    private void UpdatePreviewPlaybackButtonVisualState()
    {
        if (PlayPreviewButton is null || PlayPreviewButtonGlyph is null || PlayPreviewButtonLabel is null)
        {
            return;
        }

        PlayPreviewButtonGlyph.Text = _isTimelinePlaybackActive ? "\uE769" : "\uE768";
        PlayPreviewButtonLabel.Text = _isTimelinePlaybackActive ? "Pause" : "Play";
        PlayPreviewButton.ToolTip = _isTimelinePlaybackActive ? "Pause preview" : "Play preview";
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

    private static string BuildOverwriteStagingPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}_overwrite_stage_{Guid.NewGuid():N}.mp4");
    }

    private static string BuildOverwriteBackupPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}_overwrite_backup_{Guid.NewGuid():N}.dualclipbak");
    }

    private double TimelineDeltaToTime(double deltaX)
    {
        if (GetTimelineDisplayDurationSeconds() <= 0)
        {
            return 0;
        }

        return deltaX / Math.Max(1d, GetTimelinePixelsPerSecond());
    }

    private double TimeToTimelineX(double seconds)
    {
        if (GetTimelineDisplayDurationSeconds() <= 0)
        {
            return TimelineLeftPaddingPixels;
        }

        return TimelineLeftPaddingPixels + (Math.Clamp(seconds, 0, GetTimelineDisplayDurationSeconds()) * GetTimelinePixelsPerSecond());
    }

    private double TimelineXToTime(double x)
    {
        if (GetTimelineDisplayDurationSeconds() <= 0)
        {
            return 0;
        }

        return Math.Clamp((x - TimelineLeftPaddingPixels) / Math.Max(1d, GetTimelinePixelsPerSecond()), 0, GetTimelineDisplayDurationSeconds());
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
        CropTopLeftThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropTopRightThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropBottomLeftThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropBottomRightThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropTopThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropRightThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropBottomThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropLeftThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
        CropMoveThumb.Visibility = showCropTool ? Visibility.Visible : Visibility.Collapsed;
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

    private bool TryGetPreviewSourceDelta(FrameworkElement? dragSource, double horizontalChange, double verticalChange, out double deltaX, out double deltaY)
    {
        deltaX = 0;
        deltaY = 0;

        if (!TryGetPreviewScale(out var scaleX, out var scaleY))
        {
            return false;
        }

        var localDeltaX = horizontalChange;
        var localDeltaY = verticalChange;

        if (dragSource is not null)
        {
            try
            {
                var dragToPreview = dragSource.TransformToVisual(PreviewHost);

                var localOrigin = dragToPreview.Transform(new System.Windows.Point(0, 0));
                var localPoint = dragToPreview.Transform(new System.Windows.Point(horizontalChange, verticalChange));
                localDeltaX = localPoint.X - localOrigin.X;
                localDeltaY = localPoint.Y - localOrigin.Y;
            }
            catch (InvalidOperationException)
            {
            }
        }

        deltaX = localDeltaX / scaleX;
        deltaY = localDeltaY / scaleY;
        return true;
    }

    private void ResizeCropFromCorner(FrameworkElement? dragSource, double horizontalChange, double verticalChange, bool resizeLeft, bool resizeTop)
    {
        if (!TryGetPreviewSourceDelta(dragSource, horizontalChange, verticalChange, out var deltaX, out var deltaY))
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

    private IReadOnlyList<string> GetMicrophoneAudioSegments(int replayLengthSeconds)
    {
        return _microphoneAudioSession?.GetRecentStableSegments(replayLengthSeconds + AudioAlignmentContextSeconds) ?? [];
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
            return;
        }

        _isExitRequested = true;
        _notifyIcon.Visible = false;
        System.Windows.Application.Current.Shutdown();
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

    private sealed class QueuedClipDeletion
    {
        public required string FilePath { get; init; }

        public required string ClipName { get; init; }
    }
}
