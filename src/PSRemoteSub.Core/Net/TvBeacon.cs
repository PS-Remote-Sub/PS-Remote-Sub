using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Net;

/// <summary>What the PC announces to TV apps: its id, display name and web server port.</summary>
public sealed record TvBeaconInfo(string Id, string Name, int Port);

/// <summary>One send pass: how many sends were tried and which failed (a non-socket failure reports <see cref="SocketError.SocketError"/>).</summary>
public sealed record BeaconPassResult(int Attempted, IReadOnlyList<(IPAddress Target, SocketError Error)> Failures);

/// <summary>
/// Announces this PC over UDP broadcast every few seconds so the TV app can find it without typing an IP.
/// Sends only while <c>info</c> returns non-null; the target list is re-read each tick because adapters change.
/// </summary>
public sealed class TvBeacon : IAsyncDisposable
{
    public const int Port = 47610;

    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(1);

    private readonly UdpClient _udp = new() { EnableBroadcast = true };
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<TvBeaconInfo?> _info;
    private readonly Func<IReadOnlyList<IPAddress>> _targets;
    private readonly Action<BeaconPassResult>? _onPass;
    private readonly Func<byte[], IPEndPoint, CancellationToken, ValueTask>? _send;
    private readonly int _port;
    private readonly TimeSpan _interval;
    private readonly Dictionary<IPAddress, DateTime> _lastLogged = []; // touched only by the loop
    private readonly Task _loop;

    private TvBeacon(Func<TvBeaconInfo?> info, Func<IReadOnlyList<IPAddress>> targets, int port, TimeSpan interval,
        Action<BeaconPassResult>? onPass, Func<byte[], IPEndPoint, CancellationToken, ValueTask>? send)
    {
        _onPass = onPass;
        _send = send;
        _info = info;
        _targets = targets;
        _port = port;
        _interval = interval;
        _loop = Task.Run(RunAsync);
    }

    public static TvBeacon Start(Func<TvBeaconInfo?> info, Func<IReadOnlyList<IPAddress>> targets,
        int port = Port, TimeSpan? interval = null, Action<BeaconPassResult>? onPass = null,
        Func<byte[], IPEndPoint, CancellationToken, ValueTask>? send = null) =>
        new(info, targets, port, interval ?? TimeSpan.FromSeconds(2), onPass, send);

    /// <summary>The datagram payload: <c>{"app":"psrs","v":1,"id":…,"name":…,"port":…}</c> (UTF-8).</summary>
    public static byte[] Encode(TvBeaconInfo info) =>
        JsonSerializer.SerializeToUtf8Bytes(new BeaconDto("psrs", 1, info.Id, info.Name, info.Port));

    // Property order here is the wire order.
    private sealed record BeaconDto(string app, int v, string id, string name, int port);

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await SendOnceAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            Log.Warn("TV beacon stopped", ex);
        }
    }

    private async Task SendOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<IPAddress> targets;
        byte[] payload;
        try
        {
            if (_info() is not { } info)
                return;
            payload = Encode(info);
            targets = _targets();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOncePerMinute(IPAddress.None, "TV beacon: could not read info or targets", ex);
            return;
        }
        var failures = new List<(IPAddress, SocketError)>();
        int attempted = 0;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                var endpoint = new IPEndPoint(target, _port);
                if (_send is { } send)
                    await send(payload, endpoint, ct).ConfigureAwait(false);
                else
                    await _udp.SendAsync(payload, endpoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add((target, ex is SocketException se ? se.SocketErrorCode : SocketError.SocketError));
                LogOncePerMinute(target, $"TV beacon: send to {target} failed", ex);
            }
        }
        if (attempted > 0)
            ReportPass(new BeaconPassResult(attempted, failures));
    }

    private void ReportPass(BeaconPassResult pass)
    {
        try
        {
            _onPass?.Invoke(pass);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("TV beacon: pass callback threw", ex);
        }
    }

    private void LogOncePerMinute(IPAddress key, string message, Exception ex)
    {
        var now = DateTime.UtcNow;
        if (_lastLogged.TryGetValue(key, out var last) && now - last < LogEvery)
            return;
        _lastLogged[key] = now;
        Log.Warn(message, ex);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RunAsync handles its own errors; nothing to add on the way out.
        }
        _udp.Dispose();
        _cts.Dispose();
    }
}
