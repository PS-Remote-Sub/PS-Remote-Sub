using Avalonia.Controls;
using Avalonia.Platform;
using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.App.Views;

/// <summary>
/// Hosts the capture card's <see cref="IVideoSurface"/> (a native child view: an HWND on Windows) in the Avalonia tree.
/// UI thread only.
/// </summary>
/// <remarks>
/// Avalonia creates the native control when the host joins a window's visual tree and destroys it when it leaves (it
/// may also recreate it), so the surface factory is given at construction. <see cref="SurfaceChanged"/> is raised after
/// a surface is created and before one is disposed, with <see cref="Surface"/> already null, so a listener can let go of
/// it while it still exists; disposing it stops what draws into it before the native view goes. Nothing can be drawn
/// over the surface (airspace): hide the host to show Avalonia content in its place.
/// </remarks>
internal sealed class VideoHost(Func<nint, IVideoSurface> create) : NativeControlHost
{
    private readonly Func<nint, IVideoSurface> _create = create ?? throw new ArgumentNullException(nameof(create));

    /// <summary>The live surface, or null when there is none.</summary>
    public IVideoSurface? Surface { get; private set; }

    /// <summary>Raised when <see cref="Surface"/> changes (created, or about to be disposed).</summary>
    public event EventHandler? SurfaceChanged;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var surface = _create(parent.Handle);
        Surface = surface;
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
        return new PlatformHandle(surface.Handle, surface.HandleDescriptor);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        var surface = Surface;
        Surface = null;
        if (surface is null)
            return;
        // Listeners let go of the surface first; then it goes (its presenter, then the native view).
        try
        {
            SurfaceChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            surface.Dispose();
        }
    }
}
