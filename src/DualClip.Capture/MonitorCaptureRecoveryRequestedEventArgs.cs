namespace DualClip.Capture;

public sealed class MonitorCaptureRecoveryRequestedEventArgs(TimeSpan staleDuration) : EventArgs
{
    public TimeSpan StaleDuration { get; } = staleDuration;
}
