using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Platform;

public interface IImageFileWriter
{
    Task SavePngAsync(Frame frame, string path);
}
