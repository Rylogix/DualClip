namespace DualClip.Buffering;

public sealed class RollingSegmentBuffer : IAsyncDisposable
{
    private readonly int _paddingSegments;
    private readonly string _segmentSearchPattern;
    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;

    public RollingSegmentBuffer(
        string bufferDirectory,
        int replayLengthSeconds,
        int paddingSegments = 3,
        string segmentSearchPattern = "*.ts")
    {
        BufferDirectory = bufferDirectory;
        ReplayLengthSeconds = replayLengthSeconds;
        _paddingSegments = paddingSegments;
        _segmentSearchPattern = segmentSearchPattern;
    }

    public string BufferDirectory { get; }

    public int ReplayLengthSeconds { get; }

    public void Prepare()
    {
        Directory.CreateDirectory(BufferDirectory);

        foreach (var file in Directory.EnumerateFiles(BufferDirectory, _segmentSearchPattern, SearchOption.TopDirectoryOnly))
        {
            TryDelete(file);
        }
    }

    public void Start()
    {
        if (_cleanupTask is not null)
        {
            return;
        }

        _cleanupCts = new CancellationTokenSource();
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token));
    }

    public async Task StopAsync()
    {
        if (_cleanupCts is null)
        {
            return;
        }

        _cleanupCts.Cancel();

        if (_cleanupTask is not null)
        {
            try
            {
                await _cleanupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cleanupTask = null;
        _cleanupCts.Dispose();
        _cleanupCts = null;
    }

    public IReadOnlyList<string> GetRecentStableSegments(int replayLengthSeconds)
    {
        var stableSegments = Directory
            .EnumerateFiles(BufferDirectory, _segmentSearchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(IsStableSegment)
            .ToList();

        return stableSegments.TakeLast(Math.Max(1, replayLengthSeconds)).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            Prune();
        }
    }

    private void Prune()
    {
        var files = Directory
            .EnumerateFiles(BufferDirectory, _segmentSearchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var maxSegments = ReplayLengthSeconds + _paddingSegments;
        var overflowCount = files.Count - maxSegments;

        for (var index = 0; index < overflowCount; index++)
        {
            TryDelete(files[index]);
        }
    }

    private static bool IsStableSegment(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
