using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class AdbLocalNetworkTests
{
    private const string Endpoint = "192.168.1.50:5555";

    private sealed class Runner(Func<string[], ProcessResult> handler) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(handler([.. args.Skip(2)]));
    }

    private static ProcessResult Connect(string[] a, string stdout, string stderr = "") =>
        a[0] == "connect" ? new ProcessResult(0, stdout, stderr) : new ProcessResult(0, "", "");

    private static AdbClient Client(Func<string[], ProcessResult> h) => new("adb", new Runner(h));

    [Fact]
    public async Task Connect_NoRouteToHost_SetsLocalNetworkBlocked()
    {
        var adb = Client(a => Connect(a, "", "failed to connect to '192.168.1.50:5555': No route to host"));
        Assert.Equal(AdbConnect.Failed, await adb.ConnectAsync(Endpoint));
        Assert.True(adb.LocalNetworkBlocked);
    }

    [Fact]
    public async Task Connect_OtherFailure_NotBlocked()
    {
        var adb = Client(a => Connect(a, "failed to connect to '192.168.1.50:5555': Connection refused"));
        Assert.Equal(AdbConnect.Failed, await adb.ConnectAsync(Endpoint));
        Assert.False(adb.LocalNetworkBlocked);
    }

    [Fact]
    public async Task Connect_SuccessAfterBlocked_Clears()
    {
        bool fail = true;
        var adb = Client(a => fail
            ? Connect(a, "", "No route to host")
            : Connect(a, "connected to 192.168.1.50:5555"));
        await adb.ConnectAsync(Endpoint);
        Assert.True(adb.LocalNetworkBlocked);
        fail = false;
        Assert.Equal(AdbConnect.Connected, await adb.ConnectAsync(Endpoint));
        Assert.False(adb.LocalNetworkBlocked);
    }

    [Fact]
    public async Task Installer_ConnectNoRoute_ResultCarriesFlag()
    {
        var adb = Client(a => Connect(a, "", "failed to connect: No route to host"));
        var installer = new TvInstaller(adb, "tv.apk", 2);
        var r = await installer.InstallAsync(Endpoint, new Progress<TvInstallStep>(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("TvConnectFailed", r.ErrorKey);
        Assert.True(r.LocalNetworkBlocked);
    }

    [Fact]
    public async Task Installer_ConnectRefused_ResultFlagFalse()
    {
        var adb = Client(a => Connect(a, "failed to connect: Connection refused"));
        var installer = new TvInstaller(adb, "tv.apk", 2);
        var r = await installer.InstallAsync(Endpoint, new Progress<TvInstallStep>(), CancellationToken.None);
        Assert.Equal("TvConnectFailed", r.ErrorKey);
        Assert.False(r.LocalNetworkBlocked);
    }

    [Fact]
    public async Task Update_ConnectNoRoute_ResultCarriesFlag()
    {
        var adb = Client(a => Connect(a, "", "No route to host"));
        var installer = new TvInstaller(adb, "tv.apk", 2);
        var r = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);
        Assert.True(r.LocalNetworkBlocked);
    }
}
