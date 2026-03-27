using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace DualClip.Capture;

public static class BorderlessCaptureAccessService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool? _isAllowed;

    public static async Task<bool> RequestAsync(CancellationToken cancellationToken = default)
    {
        if (_isAllowed.HasValue)
        {
            return _isAllowed.Value;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_isAllowed.HasValue)
            {
                return _isAllowed.Value;
            }

            try
            {
                var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                _isAllowed = status == AppCapabilityAccessStatus.Allowed;
            }
            catch
            {
                _isAllowed = false;
            }

            return _isAllowed.Value;
        }
        finally
        {
            Gate.Release();
        }
    }
}
