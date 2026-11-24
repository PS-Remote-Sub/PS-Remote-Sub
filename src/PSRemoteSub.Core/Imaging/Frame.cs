namespace PSRemoteSub.Core.Imaging;

/// <summary>BGRA8 image. Stride is always <c>Width * 4</c>.</summary>
public sealed record Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public Frame(int Width, int Height, byte[] Bgra)
    {
        ArgumentNullException.ThrowIfNull(Bgra);
        if (Width < 0 || Height < 0 || (long)Width * Height * 4 != Bgra.Length)
            throw new ArgumentException("Bgra length must equal Width*Height*4.", nameof(Bgra));
        this.Width = Width;
        this.Height = Height;
        this.Bgra = Bgra;
    }

    public Frame Crop(PixelRect r)
    {
        if (r.Width < 0 || r.Height < 0 || r.X < 0 || r.Y < 0 || r.Right > Width || r.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(r), "Crop rectangle must lie inside the frame.");
        var dst = new byte[r.Width * r.Height * 4];
        int rowBytes = r.Width * 4;
        for (int row = 0; row < r.Height; row++)
            Buffer.BlockCopy(Bgra, ((r.Y + row) * Width + r.X) * 4, dst, row * rowBytes, rowBytes);
        return new Frame(r.Width, r.Height, dst);
    }
}
