using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace PSRemoteSub.Windows.Capture.Interop;

/// <summary>Calls <c>IGraphicsCaptureItemInterop</c> on the <see cref="GraphicsCaptureItem"/> activation factory.</summary>
internal static unsafe class GraphicsCaptureItemInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateForWindow(nint hwnd)
    {
        IObjectReference factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IID_IGraphicsCaptureItemInterop, out nint interop));
        try
        {
            // IGraphicsCaptureItemInterop vtable: IUnknown (0-2), CreateForWindow (3), CreateForMonitor (4).
            var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)interop)[3];
            Guid iid = IID_IGraphicsCaptureItem;
            nint itemAbi;
            Marshal.ThrowExceptionForHR(createForWindow(interop, hwnd, &iid, &itemAbi));
            try
            {
                return GraphicsCaptureItem.FromAbi(itemAbi);
            }
            finally
            {
                Marshal.Release(itemAbi);
            }
        }
        finally
        {
            Marshal.Release(interop);
            GC.KeepAlive(factory);
        }
    }
}
