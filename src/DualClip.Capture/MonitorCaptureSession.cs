using System.Diagnostics;
using System.Runtime.InteropServices;
using DualClip.Buffering;
using DualClip.Core.Models;
using DualClip.Encoding;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace DualClip.Capture;

public sealed class MonitorCaptureSession : IAsyncDisposable
{
    private static readonly TimeSpan MaximumFrameStaleness = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RecoveryRequestCooldown = TimeSpan.FromSeconds(10);
    private readonly FfmpegClipAssembler _clipAssembler = new();
    private readonly FfmpegSegmentWriter _segmentWriter = new();
    private readonly RollingSegmentBuffer _segmentBuffer;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly object _textureLock = new();
    private readonly IDirect3DDevice _winrtDevice;
    private readonly SharpDX.Direct3D11.Device _d3dDevice;
    private readonly SharpDX.Direct3D11.Multithread _multithread;
    private byte[]? _frameBuffer;
    private int _frameStride;
    private bool _hasCapturedFrame;
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Texture2D? _latestTexture;
    private Texture2D? _stagingTexture;
    private CancellationTokenSource? _captureCts;
    private Task? _encodeLoopTask;
    private SizeInt32 _captureSize;
    private readonly bool _preferBorderlessCapture;
    private int _disposeState;
    private long _lastFrameArrivalTicksUtc;
    private long _lastRecoveryRequestTicksUtc;

    public MonitorCaptureSession(MonitorCaptureSessionOptions options)
    {
        Options = options;
        _segmentBuffer = new RollingSegmentBuffer(options.BufferDirectory, options.ReplayLengthSeconds);
        _winrtDevice = Direct3D11Interop.CreateDevice();
        _d3dDevice = Direct3D11Interop.CreateSharpDxDevice(_winrtDevice);
        _multithread = _d3dDevice.QueryInterface<SharpDX.Direct3D11.Multithread>();
        _multithread.SetMultithreadProtected(true);
        _preferBorderlessCapture = options.PreferBorderlessCapture;
    }

    public MonitorCaptureSessionOptions Options { get; }

    public bool IsRunning { get; private set; }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<MonitorCaptureRecoveryRequestedEventArgs>? RecoveryRequested;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        if (!File.Exists(Options.FfmpegPath))
        {
            throw new FileNotFoundException($"ffmpeg.exe was not found at '{Options.FfmpegPath}'.");
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new InvalidOperationException("Windows Graphics Capture is not supported on this system.");
        }

        PublishStatus($"Starting capture on {Options.Monitor.DisplayName}...");

        _captureItem = CaptureItemFactory.CreateForMonitor(Options.Monitor.Handle);
        _captureSize = _captureItem.Size;

        if (_captureSize.Width <= 0 || _captureSize.Height <= 0)
        {
            throw new InvalidOperationException($"The selected monitor '{Options.Monitor.DisplayName}' returned an invalid size.");
        }

        CreateFrameTextures(_captureSize);
        _segmentBuffer.Prepare();
        _segmentBuffer.Start();
        Interlocked.Exchange(ref _lastFrameArrivalTicksUtc, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _lastRecoveryRequestTicksUtc, 0);

        _captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _segmentWriter.StartAsync(
            Options.FfmpegPath,
            _segmentBuffer.BufferDirectory,
            Options.SlotName.ToLowerInvariant(),
            _captureSize.Width,
            _captureSize.Height,
            Options.VideoQuality,
            Options.FpsTarget,
            _captureCts.Token).ConfigureAwait(false);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _captureSize);

        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.IsCursorCaptureEnabled = true;
        ConfigureCaptureBorder();
        _captureItem.Closed += OnCaptureItemClosed;
        _session.StartCapture();

        _encodeLoopTask = Task.Run(() => EncodeLoopAsync(_captureCts.Token), _captureCts.Token);
        IsRunning = true;
        PublishStatus($"Capturing {Options.Monitor.DisplayName} at {Options.FpsTarget} FPS ({DescribeVideoQuality()}).");
    }

    public async Task StopAsync()
    {
        if (!IsRunning && _captureCts is null)
        {
            return;
        }

        PublishStatus($"Stopping capture on {Options.Monitor.DisplayName}...");

        if (_captureCts is not null && !_captureCts.IsCancellationRequested)
        {
            _captureCts.Cancel();
        }

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        if (_captureItem is not null)
        {
            _captureItem.Closed -= OnCaptureItemClosed;
        }

        _session?.Dispose();
        _session = null;

        _framePool?.Dispose();
        _framePool = null;

        if (_encodeLoopTask is not null)
        {
            try
            {
                await _encodeLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PublishStatus($"Error while stopping {Options.SlotName}: {ex.Message}");
            }
        }

        _encodeLoopTask = null;

        await _segmentWriter.StopAsync().ConfigureAwait(false);
        await _segmentBuffer.StopAsync().ConfigureAwait(false);

        _captureCts?.Dispose();
        _captureCts = null;

        lock (_textureLock)
        {
            _latestTexture?.Dispose();
            _latestTexture = null;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _frameBuffer = null;
        }

        _captureItem = null;
        _hasCapturedFrame = false;
        IsRunning = false;
        PublishStatus($"{Options.Monitor.DisplayName} stopped.");
    }

    public async Task<string> SaveClipAsync(
        string outputDirectory,
        IReadOnlyList<string>? systemAudioSegments = null,
        IReadOnlyList<string>? microphoneAudioSegments = null,
        double clipAudioVolumePercent = 100d,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeState) != 0 || _disposeCts.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Cannot save a clip for {Options.SlotName} while the capture session is stopping.");
        }

        Directory.CreateDirectory(outputDirectory);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var saveLockHeld = false;

        try
        {
            await _saveLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            saveLockHeld = true;

            if (_disposeCts.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Cannot save a clip for {Options.SlotName} while the capture session is stopping.");
            }

            var segments = _segmentBuffer.GetRecentStableSegments(Options.ReplayLengthSeconds);

            if (segments.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No completed replay buffer segments are available yet for {Options.SlotName}. Wait at least a couple of seconds after starting capture.");
            }

            if (TryGetFrameStaleness(out var frameStaleness)
                && frameStaleness >= MaximumFrameStaleness)
            {
                RequestRecovery(frameStaleness);
                var staleSeconds = Math.Max(1, (int)Math.Ceiling(frameStaleness.TotalSeconds));
                throw new InvalidOperationException(
                    $"{Options.Monitor.DisplayName} has not produced a fresh frame for {staleSeconds}s. DualClip is restarting that monitor capture. Wait a few seconds and clip again.");
            }

            if (TryGetNewestStableSegmentAge(segments, out var newestSegmentAge)
                && newestSegmentAge >= MaximumFrameStaleness)
            {
                var staleSeconds = Math.Max(1, (int)Math.Ceiling(newestSegmentAge.TotalSeconds));
                throw new InvalidOperationException(
                    $"{Options.Monitor.DisplayName} has not produced a fresh frame for {staleSeconds}s. DualClip is restarting that monitor capture. Wait a few seconds and clip again.");
            }

            var outputPath = Path.Combine(
                outputDirectory,
                $"DualClip_{Options.SlotName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            return await _clipAssembler.BuildClipAsync(
                Options.FfmpegPath,
                segments,
                systemAudioSegments,
                microphoneAudioSegments,
                clipAudioVolumePercent,
                outputPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (_disposeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Cannot save a clip for {Options.SlotName} while the capture session is stopping.", ex);
        }
        finally
        {
            if (saveLockHeld)
            {
                _saveLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        await StopAsync().ConfigureAwait(false);
        await WaitForPendingSavesAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
        _saveLock.Dispose();
        _multithread.Dispose();
        _d3dDevice.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task WaitForPendingSavesAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        _saveLock.Release();
    }

    private async Task EncodeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var frameStopwatch = Stopwatch.StartNew();
            long framesWritten = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / Options.FpsTarget));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (TryGetFrameStaleness(out var staleDuration) && staleDuration >= MaximumFrameStaleness)
                {
                    RequestRecovery(staleDuration);
                    continue;
                }

                if (!_hasCapturedFrame)
                {
                    continue;
                }

                var currentFrame = CopyLatestFrame();

                if (currentFrame.Length == 0)
                {
                    continue;
                }

                var expectedFrameCount = Math.Max(
                    1L,
                    (long)Math.Round(
                        frameStopwatch.Elapsed.TotalSeconds * Options.FpsTarget,
                        MidpointRounding.AwayFromZero));
                var framesToWrite = expectedFrameCount - framesWritten;

                if (framesToWrite <= 0)
                {
                    continue;
                }

                for (var index = 0L; index < framesToWrite; index++)
                {
                    await _segmentWriter.WriteFrameAsync(currentFrame, cancellationToken).ConfigureAwait(false);
                }

                framesWritten += framesToWrite;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PublishStatus($"Error while encoding {Options.SlotName}: {ex.Message}");

            if (_captureCts is not null && !_captureCts.IsCancellationRequested)
            {
                _captureCts.Cancel();
            }
        }
    }

    private ReadOnlyMemory<byte> CopyLatestFrame()
    {
        lock (_textureLock)
        {
            if (_latestTexture is null || _stagingTexture is null || _frameBuffer is null)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            _d3dDevice.ImmediateContext.CopyResource(_latestTexture, _stagingTexture);
            var dataBox = _d3dDevice.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

            try
            {
                for (var row = 0; row < _captureSize.Height; row++)
                {
                    var sourceRow = IntPtr.Add(dataBox.DataPointer, row * dataBox.RowPitch);
                    Marshal.Copy(sourceRow, _frameBuffer, row * _frameStride, _frameStride);
                }
            }
            finally
            {
                _d3dDevice.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
            }

            return new ReadOnlyMemory<byte>(_frameBuffer, 0, _frameStride * _captureSize.Height);
        }
    }

    private void CreateFrameTextures(SizeInt32 size)
    {
        lock (_textureLock)
        {
            _latestTexture?.Dispose();
            _stagingTexture?.Dispose();

            var sharedDescription = new Texture2DDescription
            {
                Width = size.Width,
                Height = size.Height,
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BindFlags = BindFlags.None,
                Usage = ResourceUsage.Default,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            };

            _latestTexture = new Texture2D(_d3dDevice, sharedDescription);

            var stagingDescription = sharedDescription;
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.CpuAccessFlags = CpuAccessFlags.Read;

            _stagingTexture = new Texture2D(_d3dDevice, stagingDescription);
            _frameStride = size.Width * 4;
            _frameBuffer = new byte[_frameStride * size.Height];
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_captureCts?.IsCancellationRequested == true)
        {
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();

            if (frame is null)
            {
                return;
            }

            if (frame.ContentSize.Width != _captureSize.Width || frame.ContentSize.Height != _captureSize.Height)
            {
                PublishStatus(
                    $"The resolution of {Options.Monitor.DisplayName} changed while capturing. Stop and start capture again to continue.");

                _captureCts?.Cancel();
                return;
            }

            lock (_textureLock)
            {
                if (_latestTexture is null)
                {
                    return;
                }

                using var frameTexture = Direct3D11Interop.CreateTexture2D(frame.Surface);
                _d3dDevice.ImmediateContext.CopyResource(frameTexture, _latestTexture);
                _hasCapturedFrame = true;
                Interlocked.Exchange(ref _lastFrameArrivalTicksUtc, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _lastRecoveryRequestTicksUtc, 0);
            }
        }
        catch (Exception ex)
        {
            PublishStatus($"Capture error on {Options.SlotName}: {ex.Message}");
            _captureCts?.Cancel();
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        PublishStatus($"{Options.Monitor.DisplayName} was closed by Windows.");
        _captureCts?.Cancel();
    }

    private void ConfigureCaptureBorder()
    {
        if (_session is null || !_preferBorderlessCapture)
        {
            return;
        }

        try
        {
            var isBorderRequiredProperty = _session.GetType().GetProperty("IsBorderRequired");

            if (isBorderRequiredProperty is null || !isBorderRequiredProperty.CanWrite)
            {
                PublishStatus($"Windows does not expose borderless capture on {Options.SlotName}.");
                return;
            }

            isBorderRequiredProperty.SetValue(_session, false);
            PublishStatus($"Capturing {Options.Monitor.DisplayName} without the Windows border.");
        }
        catch (Exception ex)
        {
            PublishStatus($"Windows did not allow borderless capture on {Options.SlotName}: {ex.Message}");
        }
    }

    private string DescribeVideoQuality()
    {
        return Options.VideoQuality switch
        {
            VideoQualityPreset.P1440 => "max 1440p",
            VideoQualityPreset.P1080 => "max 1080p",
            VideoQualityPreset.P720 => "max 720p",
            _ => "native resolution",
        };
    }

    private void PublishStatus(string message)
    {
        StatusChanged?.Invoke(this, message);
    }

    private bool TryGetFrameStaleness(out TimeSpan staleDuration)
    {
        staleDuration = TimeSpan.Zero;
        var lastFrameArrivalTicksUtc = Interlocked.Read(ref _lastFrameArrivalTicksUtc);

        if (lastFrameArrivalTicksUtc <= 0)
        {
            return false;
        }

        staleDuration = DateTime.UtcNow - new DateTime(lastFrameArrivalTicksUtc, DateTimeKind.Utc);
        return staleDuration > TimeSpan.Zero;
    }

    private void RequestRecovery(TimeSpan staleDuration)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastRequestTicksUtc = Interlocked.Read(ref _lastRecoveryRequestTicksUtc);

        if (lastRequestTicksUtc > 0
            && nowTicks - lastRequestTicksUtc < RecoveryRequestCooldown.Ticks)
        {
            return;
        }

        var observedTicks = Interlocked.CompareExchange(
            ref _lastRecoveryRequestTicksUtc,
            nowTicks,
            lastRequestTicksUtc);

        if (observedTicks != lastRequestTicksUtc)
        {
            return;
        }

        var staleSeconds = Math.Max(1, (int)Math.Ceiling(staleDuration.TotalSeconds));
        PublishStatus($"{Options.Monitor.DisplayName} has not delivered a new frame for {staleSeconds}s. Restarting this monitor capture.");
        RecoveryRequested?.Invoke(this, new MonitorCaptureRecoveryRequestedEventArgs(staleDuration));
    }

    private static bool TryGetNewestStableSegmentAge(IReadOnlyList<string> segments, out TimeSpan newestSegmentAge)
    {
        newestSegmentAge = TimeSpan.Zero;

        if (segments.Count == 0)
        {
            return false;
        }

        try
        {
            var newestSegmentTimeUtc = File.GetLastWriteTimeUtc(segments[^1]);

            if (newestSegmentTimeUtc == DateTime.MinValue || newestSegmentTimeUtc == DateTime.MaxValue)
            {
                return false;
            }

            newestSegmentAge = DateTime.UtcNow - newestSegmentTimeUtc;
            return newestSegmentAge > TimeSpan.Zero;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
