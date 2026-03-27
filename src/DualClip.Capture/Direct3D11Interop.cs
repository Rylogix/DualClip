using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace DualClip.Capture;

internal static class Direct3D11Interop
{
    private static readonly Guid Id3D11DeviceGuid = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid Id3D11Texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDevice(bool useWarp = false)
    {
        using var d3dDevice = new SharpDX.Direct3D11.Device(
            useWarp ? SharpDX.Direct3D.DriverType.Software : SharpDX.Direct3D.DriverType.Hardware,
            SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);

        using var dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device3>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtPointer);

        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR((int)hr);
        }

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(winrtPointer);
        }
        finally
        {
            Marshal.Release(winrtPointer);
        }
    }

    public static SharpDX.Direct3D11.Device CreateSharpDxDevice(IDirect3DDevice device)
    {
        var access = device.As<IDirect3DDxgiInterfaceAccess>();
        var deviceGuid = Id3D11DeviceGuid;
        var pointer = access.GetInterface(ref deviceGuid);
        return new SharpDX.Direct3D11.Device(pointer);
    }

    public static SharpDX.Direct3D11.Texture2D CreateTexture2D(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var textureGuid = Id3D11Texture2DGuid;
        var pointer = access.GetInterface(ref textureGuid);
        return new SharpDX.Direct3D11.Texture2D(pointer);
    }
}
