using System.Diagnostics;
using System.Text;

namespace PSRemoteSub.Core.Tv;

/// <summary>Outcome of a finished process.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs an external program to completion; a seam so adb logic can be tested without adb.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="exe"/> with <paramref name="args"/> (one argument per element, never a shell string).
    /// Throws <see cref="TimeoutException"/> when it runs longer than <paramref name="timeout"/> and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires; in both cases the process tree is killed first.
    /// A program that cannot be started throws <see cref="System.ComponentModel.Win32Exception"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>: no window, UTF-8, both streams read concurrently.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// How long to keep waiting for the pipes to drain once the process has exited. A daemon that adb spawns
    /// (<c>adb start-server</c>) inherits the pipes and would otherwise hold them open indefinitely.
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close(); // adb must never wait for input

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var pumps = Task.WhenAll(Pump(process.StandardOutput, stdout), Pump(process.StandardError, stderr));

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {timeout.TotalSeconds:0} s.");
        }

        await Task.WhenAny(pumps, Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, Snapshot(stdout), Snapshot(stderr));
    }

    private static async Task Pump(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (sink)
                    sink.Append(buffer, 0, n);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe was torn down (process killed); keep what we have.
        }
    }

    private static string Snapshot(StringBuilder sink)
    {
        lock (sink)
            return sink.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
