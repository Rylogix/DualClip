using System.Runtime.InteropServices;
using DualClip.Core.Models;

namespace DualClip.Infrastructure;

public sealed class MonitorEnumerationService
{
    private const int MonitorInfoPrimary = 0x00000001;

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();

        if (!EnumDisplayMonitors(nint.Zero, nint.Zero, MonitorEnumProc, nint.Zero))
        {
            throw new InvalidOperationException("Failed to enumerate connected monitors.");
        }

        return monitors;

        bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData)
        {
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
            };

            if (!GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            var width = info.rcMonitor.Right - info.rcMonitor.Left;
            var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            var isPrimary = (info.dwFlags & MonitorInfoPrimary) != 0;
            var bounds = new ScreenBounds(info.rcMonitor.Left, info.rcMonitor.Top, width, height);

            monitors.Add(new MonitorDescriptor
            {
                Handle = hMonitor,
                DeviceName = info.szDevice,
                DisplayName = $"{(isPrimary ? "Primary" : "Monitor")} {info.szDevice} ({bounds})",
                Bounds = bounds,
                IsPrimary = isPrimary,
            });

            return true;
        }
    }

    private delegate bool MonitorEnumDelegate(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        nint hdc,
        nint lprcClip,
        MonitorEnumDelegate lpfnEnum,
        nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
