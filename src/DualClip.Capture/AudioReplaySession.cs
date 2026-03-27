using DualClip.Buffering;
using DualClip.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualClip.Capture;

public sealed class AudioReplaySession : IAsyncDisposable
{
    private const int ExtraAudioContextSegments = 12;
    private readonly RollingSegmentBuffer _segmentBuffer;
    private readonly object _writerLock = new();
    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private IWaveIn? _capture;
    private MMDevice? _captureDevice;
    private WaveFileWriter? _currentWriter;
    private TaskCompletionSource<bool>? _recordingStoppedTcs;
    private DateTime _currentSegmentTimestamp = DateTime.MinValue;

    public AudioReplaySession(AudioReplaySessionOptions options)
    {
        Options = options;
        _segmentBuffer = new RollingSegmentBuffer(
            options.BufferDirectory,
            options.ReplayLengthSeconds,
            paddingSegments: ExtraAudioContextSegments,
            segmentSearchPattern: "*.wav");
    }

    public AudioReplaySessionOptions Options { get; }

    public bool IsRunning { get; private set; }

    public event EventHandler<string>? StatusChanged;

    public Task StartAsync()
    {
        if (IsRunning || Options.AudioMode == AudioCaptureMode.None)
        {
            PublishStatus(Options.AudioMode == AudioCaptureMode.None ? "Audio capture disabled." : "Audio capture is already running.");
            return Task.CompletedTask;
        }

        _segmentBuffer.Prepare();
        _segmentBuffer.Start();
        _capture = CreateCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _recordingStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.StartRecording();
        IsRunning = true;
        PublishStatus($"Audio capture running: {DescribeMode()}.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_capture is null)
        {
            await _segmentBuffer.StopAsync().ConfigureAwait(false);
            CloseWriter();
            IsRunning = false;
            return;
        }

        var capture = _capture;
        _capture = null;

        capture.DataAvailable -= OnDataAvailable;

        try
        {
            capture.StopRecording();
        }
        catch
        {
        }

        if (_recordingStoppedTcs is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            try
            {
                await _recordingStoppedTcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        capture.RecordingStopped -= OnRecordingStopped;
        capture.Dispose();
        _captureDevice?.Dispose();
        _captureDevice = null;
        _recordingStoppedTcs = null;

        CloseWriter();
        await _segmentBuffer.StopAsync().ConfigureAwait(false);

        IsRunning = false;
        PublishStatus("Audio capture stopped.");
    }

    public IReadOnlyList<string> GetRecentStableSegments(int replayLengthSeconds)
    {
        if (Options.AudioMode == AudioCaptureMode.None)
        {
            return [];
        }

        return _segmentBuffer.GetRecentStableSegments(replayLengthSeconds);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _deviceEnumerator.Dispose();
        GC.SuppressFinalize(this);
    }

    private IWaveIn CreateCapture()
    {
        return Options.AudioMode switch
        {
            AudioCaptureMode.System => CreateSystemAudioCapture(),
            AudioCaptureMode.Microphone => CreateMicrophoneCapture(),
            _ => throw new InvalidOperationException($"Unsupported audio mode '{Options.AudioMode}'."),
        };
    }

    private IWaveIn CreateSystemAudioCapture()
    {
        _captureDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new WasapiLoopbackCapture(_captureDevice);
    }

    private IWaveIn CreateMicrophoneCapture()
    {
        _captureDevice = ResolveMicrophoneDevice()
            ?? throw new InvalidOperationException("No active microphone device was found.");

        return new WasapiCapture(_captureDevice);
    }

    private MMDevice? ResolveMicrophoneDevice()
    {
        if (!string.IsNullOrWhiteSpace(Options.MicrophoneDeviceId))
        {
            try
            {
                return _deviceEnumerator.GetDevice(Options.MicrophoneDeviceId);
            }
            catch
            {
            }
        }

        try
        {
            return _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        lock (_writerLock)
        {
            var timestamp = TruncateToSecond(DateTime.Now);

            if (_currentWriter is null || timestamp != _currentSegmentTimestamp)
            {
                RotateWriter(timestamp, ((IWaveIn)sender!).WaveFormat);
            }

            _currentWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            PublishStatus($"Audio capture error: {e.Exception.Message}");
        }

        _recordingStoppedTcs?.TrySetResult(true);
    }

    private void RotateWriter(DateTime timestamp, WaveFormat waveFormat)
    {
        CloseWriterCore();
        Directory.CreateDirectory(Options.BufferDirectory);
        var path = Path.Combine(Options.BufferDirectory, $"audio_{timestamp:yyyyMMdd_HHmmss}.wav");
        _currentWriter = new WaveFileWriter(path, waveFormat);
        _currentSegmentTimestamp = timestamp;
    }

    private void CloseWriter()
    {
        lock (_writerLock)
        {
            CloseWriterCore();
        }
    }

    private void CloseWriterCore()
    {
        _currentWriter?.Dispose();
        _currentWriter = null;
        _currentSegmentTimestamp = DateTime.MinValue;
    }

    private static DateTime TruncateToSecond(DateTime value)
    {
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
    }

    private string DescribeMode()
    {
        return Options.AudioMode switch
        {
            AudioCaptureMode.System => "System audio",
            AudioCaptureMode.Microphone when _captureDevice is not null => $"Microphone ({_captureDevice.FriendlyName})",
            AudioCaptureMode.Microphone => "Microphone",
            _ => "Disabled",
        };
    }

    private void PublishStatus(string message)
    {
        StatusChanged?.Invoke(this, message);
    }
}
