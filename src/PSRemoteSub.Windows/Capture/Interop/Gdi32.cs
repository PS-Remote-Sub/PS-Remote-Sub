using System.Runtime.InteropServices;

namespace PSRemoteSub.Windows.Capture.Interop;

internal static partial class Gdi32
{
    public const int BLACK_BRUSH = 4;

    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int index);
}
