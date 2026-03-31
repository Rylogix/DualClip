using Windows.Foundation.Metadata;

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

            _isAllowed = await RequestBorderlessAccessCoreAsync(cancellationToken).ConfigureAwait(false);

            return _isAllowed.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> RequestBorderlessAccessCoreAsync(CancellationToken cancellationToken)
    {
        if (!ApiInformation.IsPropertyPresent(
            "Windows.Graphics.Capture.GraphicsCaptureSession",
            "IsBorderRequired"))
        {
            return false;
        }

        try
        {
            var captureAssembly = typeof(Windows.Graphics.Capture.GraphicsCaptureSession).Assembly;
            var accessType = captureAssembly.GetType("Windows.Graphics.Capture.GraphicsCaptureAccess");
            var accessKindType = captureAssembly.GetType("Windows.Graphics.Capture.GraphicsCaptureAccessKind");

            if (accessType is null || accessKindType is null)
            {
                return false;
            }

            var borderlessValue = Enum.Parse(accessKindType, "Borderless");
            var requestAccessMethod = accessType.GetMethod("RequestAccessAsync", [accessKindType]);

            if (requestAccessMethod is null)
            {
                return false;
            }

            var operation = requestAccessMethod.Invoke(null, [borderlessValue]);

            if (operation is null)
            {
                return false;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var statusText = operation.GetType().GetProperty("Status")?.GetValue(operation)?.ToString();

                if (string.Equals(statusText, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    var result = operation.GetType().GetMethod("GetResults")?.Invoke(operation, null)?.ToString();
                    return string.Equals(result, "Allowed", StringComparison.OrdinalIgnoreCase);
                }

                if (string.Equals(statusText, "Canceled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(statusText, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            return false;
        }
    }
}
