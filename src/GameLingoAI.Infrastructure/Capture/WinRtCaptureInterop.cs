using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GameLingoAI.Infrastructure.Capture;

internal static class WinRtCaptureInterop
{
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private const string GraphicsCaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    private static readonly Guid GraphicsCaptureItemId =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        using var factory = ActivationFactory.Get(GraphicsCaptureItemRuntimeClass);
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, GraphicsCaptureItemId, out var itemPointer));
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice()
    {
        var result = CreateNativeDevice(D3DDriverTypeHardware, out var nativeDevice, out var context);
        if (result < 0)
        {
            result = CreateNativeDevice(D3DDriverTypeWarp, out nativeDevice, out context);
        }

        Marshal.ThrowExceptionForHR(result);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in DxgiDeviceId, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDxgiDevice(dxgiDevice, out var inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }

            if (nativeDevice != IntPtr.Zero)
            {
                Marshal.Release(nativeDevice);
            }
        }
    }

    private static int CreateNativeDevice(int driverType, out IntPtr device, out IntPtr context) =>
        D3D11CreateDevice(
            IntPtr.Zero,
            driverType,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out device,
            out _,
            out context);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, in Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, in Guid interfaceId, out IntPtr result);
    }

    [DllImport("d3d11.dll", SetLastError = false)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDxgiDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
