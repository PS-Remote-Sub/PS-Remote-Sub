using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PSRemoteSub.Windows.Capture.Interop;

/// <summary>Bridges native D3D11/DXGI objects and the WinRT Direct3D11 projection.</summary>
internal static unsafe partial class Direct3D11Interop
{
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    /// <summary>Wraps a native <c>IDXGIDevice*</c> as a WinRT <see cref="IDirect3DDevice"/>.</summary>
    public static IDirect3DDevice CreateDevice(nint dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>
    /// Returns an owned (AddRef'd) native pointer to the DXGI/D3D interface <paramref name="iid"/> behind a
    /// WinRT surface. The caller must release it.
    /// </summary>
    public static nint GetDxgiInterface(IDirect3DSurface surface, Guid iid)
    {
        nint surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfaceAbi, in IID_IDirect3DDxgiInterfaceAccess, out nint access));
            try
            {
                // IDirect3DDxgiInterfaceAccess vtable: IUnknown (0-2), GetInterface (3).
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
                nint result;
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &result));
                return result;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfaceAbi);
        }
    }
}
