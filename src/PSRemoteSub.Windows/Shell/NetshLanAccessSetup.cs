using System.ComponentModel;
using System.Diagnostics;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Windows.Shell;

public sealed class NetshLanAccessSetup : ILanAccessSetup
{
    /// <summary>Absolute path, so an elevated launch can never pick up a <c>netsh.exe</c> from the current folder or PATH.</summary>
    private static readonly string NetshPath = Path.Combine(Environment.SystemDirectory, "netsh.exe");

    private static string RuleName(int port) => $"PS Remote Sub (TCP {port})";

    /// <remarks>Blocks for up to 10 s if netsh hangs; must not be called on the UI thread.</remarks>
    public bool IsPortOpen(int port)
    {
        try
        {
            var psi = new ProcessStartInfo(NetshPath, $"advfirewall firewall show rule name=\"{RuleName(port)}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(10_000)) { try { p.Kill(entireProcessTree: true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Firewall rule check failed", ex);
            return false;
        }
    }

    public async Task<bool> OpenPortAsync(int port)
    {
        if (port is < 1024 or > 65535) return false;
        if (await Task.Run(() => IsPortOpen(port)).ConfigureAwait(false)) return true;
        var args = $"advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=TCP localport={port} profile=private";
        try
        {
            var psi = new ProcessStartInfo(NetshPath, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync().ConfigureAwait(false);
            Log.Info($"Firewall rule add exited with code {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) // includes UAC declined (NativeErrorCode 1223)
        {
            Log.Warn("Firewall rule add not started", ex);
            return false;
        }
    }
}
