using System.Runtime.InteropServices;
using PSRemoteSub.Core.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// Reads a BGRA8 GPU texture back into a <see cref="Frame"/> through a staging texture it owns (recreated on size
/// change). Not thread-safe: the caller serialises its own calls and holds whatever lock guards the device context.
/// </summary>
internal sealed class TextureReadback(ID3D11Device device) : IDisposable
{
    private ID3D11Texture2D? _staging;  // STAGING usage: CPU-readable copy of the source

    /// <summary>Copies <paramref name="source"/> to the staging texture and into a new <see cref="Frame"/>.</summary>
    public Frame Read(ID3D11DeviceContext context, ID3D11Texture2D source)
    {
        var (width, height) = CopyToStaging(context, source);
        return ReadStaging(context, width, height);
    }

    /// <summary>
    /// The GPU half of <see cref="Read"/>: queues a copy of <paramref name="source"/> into the staging texture and returns
    /// its size. Cheap; the caller holds the lock that guards <paramref name="source"/> and the context.
    /// </summary>
    public (int Width, int Height) CopyToStaging(ID3D11DeviceContext context, ID3D11Texture2D source)
    {
        Texture2DDescription d = source.Description;
        int width = (int)d.Width, height = (int)d.Height;
        ID3D11Texture2D staging = EnsureTexture(device, ref _staging, width, height, ResourceUsage.Staging, CpuAccessFlags.Read);
        context.CopyResource(staging, source);
        return (width, height);
    }

    /// <summary>
    /// The CPU half of <see cref="Read"/>: maps the staging texture filled by <see cref="CopyToStaging"/> (waiting for
    /// that copy) and copies it into a new <see cref="Frame"/>. Touches only the staging texture, so on a
    /// multithread-protected context it needs no lock beyond the caller's own serialisation.
    /// </summary>
    public Frame ReadStaging(ID3D11DeviceContext context, int width, int height)
    {
        ID3D11Texture2D staging = _staging ?? throw new InvalidOperationException("Nothing was copied to the staging texture.");
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = width * 4;
            var bgra = new byte[rowBytes * height];
            for (int row = 0; row < height; row++)
                Marshal.Copy(mapped.DataPointer + row * (nint)mapped.RowPitch, bgra, row * rowBytes, rowBytes);
            return new Frame(width, height, bgra);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _staging = null;
    }

    /// <summary>
    /// Returns <paramref name="slot"/>, (re)created as a B8G8R8A8 texture of this size when needed. Caller holds the
    /// lock that guards <paramref name="slot"/>.
    /// </summary>
    internal static ID3D11Texture2D EnsureTexture(
        ID3D11Device device,
        ref ID3D11Texture2D? slot,
        int width,
        int height,
        ResourceUsage usage,
        CpuAccessFlags cpuAccess,
        BindFlags bindFlags = BindFlags.None)
    {
        if (slot is { } existing)
        {
            Texture2DDescription d = existing.Description;
            if (d.Width == (uint)width && d.Height == (uint)height)
                return existing;
            existing.Dispose();
            slot = null;
        }

        var description = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: bindFlags,
            usage: usage,
            cpuAccessFlags: cpuAccess);
        slot = device.CreateTexture2D(description);
        return slot;
    }
}
