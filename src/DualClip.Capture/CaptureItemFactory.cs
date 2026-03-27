using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace DualClip.Capture;

internal static class CaptureItemFactory
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
    }

    public static GraphicsCaptureItem CreateForMonitor(nint monitorHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForMonitor(monitorHandle, GraphicsCaptureItemGuid);

        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }
}
