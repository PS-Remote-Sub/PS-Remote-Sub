using System.Net;
using System.Net.Sockets;
using System.Text;
using PSRemoteSub.Core.Net;

namespace PSRemoteSub.Core.Tests.Net;

public class TvBeaconTests
{
    [Fact]
    public void Encode_MatchesContract() =>
        Assert.Equal("""{"app":"psrs","v":1,"id":"x","name":"PC","port":5000}""",
            Encoding.UTF8.GetString(TvBeacon.Encode(new TvBeaconInfo("x", "PC", 5000))));

    [Fact]
    public async Task Beacon_SendsToTarget()
    {
        using var rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)rx.Client.LocalEndPoint!).Port;
        await using var beacon = TvBeacon.Start(
            () => new TvBeaconInfo("x", "PC", 5000), () => [IPAddress.Loopback], port, TimeSpan.FromMilliseconds(50));

        var result = await rx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TvBeacon.Encode(new TvBeaconInfo("x", "PC", 5000)), result.Buffer);
    }

    [Fact]
    public async Task Beacon_NullInfo_SendsNothing()
    {
        using var rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)rx.Client.LocalEndPoint!).Port;
        await using var beacon = TvBeacon.Start(
            () => null, () => [IPAddress.Loopback], port, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(
            () => rx.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Beacon_ReportsPassWithFailures()
    {
        var bad = IPAddress.Parse("192.168.1.255");
        var good = IPAddress.Parse("192.168.1.20");
        var seen = new TaskCompletionSource<BeaconPassResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var beacon = TvBeacon.Start(
            () => new TvBeaconInfo("x", "PC", 5000), () => [good, bad], 47610, TimeSpan.FromMilliseconds(50),
            onPass: p => seen.TrySetResult(p),
            send: (_, ep, _) => ep.Address.Equals(bad)
                ? throw new SocketException((int)SocketError.HostUnreachable)
                : ValueTask.CompletedTask);

        var pass = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, pass.Attempted);
        Assert.Equal([(bad, SocketError.HostUnreachable)], pass.Failures);
    }

    [Fact]
    public async Task Beacon_CleanPass_ReportsNoFailures()
    {
        var seen = new TaskCompletionSource<BeaconPassResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var beacon = TvBeacon.Start(
            () => new TvBeaconInfo("x", "PC", 5000), () => [IPAddress.Loopback], 47610, TimeSpan.FromMilliseconds(50),
            onPass: p => seen.TrySetResult(p), send: (_, _, _) => ValueTask.CompletedTask);

        var pass = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, pass.Attempted);
        Assert.Empty(pass.Failures);
    }

    [Fact]
    public async Task Beacon_NullInfo_ReportsNoPass()
    {
        int passes = 0;
        await using var beacon = TvBeacon.Start(
            () => null, () => [IPAddress.Loopback], 47610, TimeSpan.FromMilliseconds(30),
            onPass: _ => Interlocked.Increment(ref passes), send: (_, _, _) => ValueTask.CompletedTask);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref passes));
    }
}
