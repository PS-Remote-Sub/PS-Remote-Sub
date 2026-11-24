namespace PSRemoteSub.Core.Imaging;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}
