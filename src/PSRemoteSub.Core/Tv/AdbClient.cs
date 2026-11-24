using System.Text.RegularExpressions;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Tv;

/// <summary>
/// Thin wrapper over the bundled adb.exe. Every call passes arguments as a list, never a shell string. The serial of
/// a network device is its "ip:port" endpoint. Process failures (timeout, adb missing) propagate as exceptions from
/// <see cref="IProcessRunner"/>. Every command talks to a private adb server on <see cref="ServerPort"/>, never the
/// default 5037 one a developer's own adb uses; <see cref="KillServerAsync"/> stops it when the work is done.
/// </summary>
/// <param name="delay">Wait between approval polls; injectable so tests run instantly. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
/// <param name="time">Clock for the approval time limit; defaults to <see cref="TimeProvider.System"/>.</param>
public sealed partial class AdbClient(string adbPath, IProcessRunner runner, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? time = null)
{
    /// <summary>The TV app's package.</summary>
    public const string TvPackage = "th.psrs.tvoverlay";

    /// <summary>
    /// The port of the bundled adb's own server (<c>adb -P</c>). Not the default 5037, so it never shares (or, on a
    /// version mismatch, kills) the server of another adb on the PC.
    /// </summary>
    public const int ServerPort = 5038;

    private static readonly string ServerPortArg = ServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((t, ct) => Task.Delay(t, ct));

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    [GeneratedRegex(@"^[A-Za-z0-9._:/\-]+\z")]
    private static partial Regex ShellSafe();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*:(?<port>[0-9]{1,5})\z")]
    private static partial Regex EndpointShape();

    /// <summary>Whether <paramref name="endpoint"/> is <c>host:port</c> (IPv4 or hostname, port 1-65535) and safe to hand to adb as an argument.</summary>
    public static bool IsValidEndpoint(string? endpoint)
    {
        var m = endpoint is null ? null : EndpointShape().Match(endpoint);
        return m is { Success: true } && int.Parse(m.Groups["port"].Value) is >= 1 and <= 65535;
    }

    /// <summary>The ADB-over-network endpoints (<c>ip:port</c>) the TVs on the LAN advertise over mDNS.</summary>
    public async Task<IReadOnlyList<(string Name, string Endpoint)>> FindTvsAsync(CancellationToken ct = default)
    {
        var r = await RunAsync(["mdns", "services"], QuickTimeout, ct).ConfigureAwait(false);
        return AdbParsers.MdnsServices(r.StdOut);
    }

    /// <summary>
    /// <c>adb connect</c>; <see cref="AdbConnect.Unauthorized"/> means the TV is showing its approval prompt.
    /// An endpoint that is not a valid <c>host:port</c> is <see cref="AdbConnect.Failed"/> without running adb.
    /// </summary>
    public async Task<AdbConnect> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        if (!IsValidEndpoint(endpoint))
            return AdbConnect.Failed;
        var r = await RunAsync(["connect", endpoint], QuickTimeout, ct).ConfigureAwait(false);
        var output = r.StdOut + "\n" + r.StdErr;
        LocalNetworkBlocked = LocalNetworkHint.AdbOutputLooksBlocked(output);
        return AdbParsers.Connect(output);
    }

    /// <summary>Whether the last <see cref="ConnectAsync"/> failed with "No route to host" (macOS blocks LAN access until Local Network is allowed).</summary>
    public bool LocalNetworkBlocked { get; private set; }

    /// <summary><c>adb get-state</c>: "device" when authorized, "unauthorized" before approval, null when not connected.</summary>
    public async Task<string?> GetStateAsync(string serial, CancellationToken ct = default)
    {
        var r = await RunAsync(["-s", serial, "get-state"], QuickTimeout, ct).ConfigureAwait(false);
        return r.ExitCode == 0 ? AdbParsers.DeviceState(r.StdOut) : null;
    }

    /// <summary>
    /// Polls (every second, reconnecting when the device dropped off) until the TV is authorized or
    /// <paramref name="timeout"/> of wall-clock time has passed, however slow the adb calls are.
    /// A runner timeout propagates as <see cref="TimeoutException"/>.
    /// </summary>
    public async Task<bool> WaitAuthorizedAsync(string endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            var state = await GetStateAsync(endpoint, ct).ConfigureAwait(false);
            if (state == "device")
                return true;
            if (_time.GetElapsedTime(start) >= timeout)
                return false;
            if (state is null)
                await ConnectAsync(endpoint, ct).ConfigureAwait(false);
            await _delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary><c>adb install -r</c> of <paramref name="apkPath"/>.</summary>
    public async Task<AdbInstallOutcome> InstallAsync(string serial, string apkPath, CancellationToken ct = default)
    {
        var r = await RunAsync(["-s", serial, "install", "-r", apkPath], InstallTimeout, ct).ConfigureAwait(false);
        return AdbParsers.Install(r.StdOut + "\n" + r.StdErr);
    }

    /// <summary>The installed <see cref="TvPackage"/> versionCode; null when it is not installed. Throws <see cref="AdbException"/> when dumpsys itself fails.</summary>
    public async Task<int?> GetVersionCodeAsync(string serial, CancellationToken ct = default)
    {
        var r = await RunAsync(["-s", serial, "shell", "dumpsys", "package", TvPackage], QuickTimeout, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new AdbException($"dumpsys failed ({r.ExitCode}): {r.StdErr.Trim()}");
        return AdbParsers.VersionCode(r.StdOut);
    }

    /// <summary>
    /// <c>settings get secure</c>, trimmed; adb prints "null" for an unset key. Null means the read failed (non-zero exit
    /// or no output, which a healthy device never produces), so the value is unknown and must not be written over.
    /// </summary>
    public async Task<string?> GetSecureAsync(string serial, string key, CancellationToken ct = default)
    {
        var r = await RunAsync(["-s", serial, "shell", "settings", "get", "secure", key], QuickTimeout, ct).ConfigureAwait(false);
        var value = r.StdOut.Trim();
        return r.ExitCode != 0 || value.Length == 0 ? null : value;
    }

    /// <summary>
    /// <c>settings put secure</c> as separate arguments. adb itself joins <c>shell</c> arguments with spaces and hands
    /// them to the device's shell, so a value with characters the shell treats specially (a <c>$</c> in an inner-class
    /// service name, a space) is single-quoted; plain values go through verbatim.
    /// </summary>
    public async Task<bool> PutSecureAsync(string serial, string key, string value, CancellationToken ct = default)
    {
        var arg = ShellSafe().IsMatch(value) ? value : "'" + value.Replace("'", "'\\''") + "'";
        var r = await RunAsync(["-s", serial, "shell", "settings", "put", "secure", key, arg], QuickTimeout, ct).ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    /// <summary>
    /// <c>adb kill-server</c> for the private server, so no adb process (holding the TV connection and a lock on the
    /// bundled files) outlives the work. Best effort: waits at most a few seconds, ignores cancellation and only logs a
    /// failure (adb missing, timeout, no server running).
    /// </summary>
    public async Task KillServerAsync()
    {
        try
        {
            var r = await RunAsync(["kill-server"], KillTimeout, CancellationToken.None).ConfigureAwait(false);
            if (r.ExitCode != 0)
                Log.Info($"adb kill-server exited {r.ExitCode}: {r.StdErr.Trim()}");
        }
        catch (Exception ex)
        {
            Log.Warn("adb kill-server failed", ex);
        }
    }

    /// <summary>Runs adb against the private server on <see cref="ServerPort"/>.</summary>
    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        runner.RunAsync(adbPath, ["-P", ServerPortArg, .. args], timeout, ct);
}

/// <summary>An adb command that ran but reported failure.</summary>
public sealed class AdbException(string message) : IOException(message);
