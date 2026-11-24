using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace PSRemoteSub.Windows.Capture.Interop;

/// <summary>
/// <c>GraphicsCaptureSession.IsBorderRequired</c> (UniversalApiContract 12) is not in the 10.0.19041 projection,
/// so it is set through <c>IGraphicsCaptureSession3</c> directly.
/// </summary>
internal static unsafe class GraphicsCaptureSessionInterop
{
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    /// <summary>Returns false when the OS lacks the property or rejects the value.</summary>
    public static bool TrySetIsBorderRequired(GraphicsCaptureSession session, bool value)
    {
        IObjectReference objRef = ((IWinRTObject)session).NativeObject;
        if (Marshal.QueryInterface(objRef.ThisPtr, in IID_IGraphicsCaptureSession3, out nint session3) < 0)
            return false;
        try
        {
            // IGraphicsCaptureSession3 vtable: IUnknown (0-2), IInspectable (3-5),
            // get_IsBorderRequired (6), put_IsBorderRequired (7).
            var put = (delegate* unmanaged[Stdcall]<nint, byte, int>)(*(nint**)session3)[7];
            return put(session3, value ? (byte)1 : (byte)0) >= 0;
        }
        finally
        {
            Marshal.Release(session3);
            GC.KeepAlive(objRef);
        }
    }
}
