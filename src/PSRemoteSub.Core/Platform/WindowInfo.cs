using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Platform;

/// <summary>Window description in physical pixels.</summary>
public sealed record WindowInfo(nint Handle, int ProcessId, PixelRect ClientScreenRect, bool IsMinimized);
