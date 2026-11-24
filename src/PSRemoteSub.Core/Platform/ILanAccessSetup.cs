namespace PSRemoteSub.Core.Platform;

public interface ILanAccessSetup
{
    bool IsPortOpen(int port);
    Task<bool> OpenPortAsync(int port);
}
