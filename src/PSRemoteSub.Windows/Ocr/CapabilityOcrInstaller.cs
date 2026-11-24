using System.ComponentModel;
using System.Diagnostics;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Windows.Ocr;

/// <summary>Installs a Windows OCR language capability through an elevated PowerShell.</summary>
public sealed class CapabilityOcrInstaller : IOcrLanguageInstaller
{
    /// <summary>Absolute path, so the elevated launch can never pick up a <c>powershell.exe</c> from the current folder or PATH.</summary>
    private static readonly string PowerShellPath =
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public async Task<bool> InstallAsync(SourceLanguage language, CancellationToken ct)
    {
        var args = $"-NoProfile -Command \"Add-WindowsCapability -Online -Name '{language.CapabilityName}'\"";
        try
        {
            var psi = new ProcessStartInfo(PowerShellPath, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            Log.Info($"OCR language install ({language.OcrTag}) exited with code {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) // includes UAC declined (1223)
        {
            Log.Warn($"OCR language install ({language.OcrTag}) not started", ex);
            return false;
        }
    }
}
