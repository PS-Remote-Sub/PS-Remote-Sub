using System.Diagnostics;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.App;

/// <summary>Shows a file in the OS file manager.</summary>
internal static class FileReveal
{
    /// <summary>
    /// Windows: Explorer with the file selected; macOS: Finder with it revealed; elsewhere: its folder. Never throws; a
    /// failure is logged without the path.
    /// </summary>
    internal static void Reveal(string path)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                // Explorer parses its own command line, so this one is an argument string, with the path quoted.
                psi = new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" };
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo("open");
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
            }
            else
            {
                psi = new ProcessStartInfo(Path.GetDirectoryName(path) ?? ".") { UseShellExecute = true };
            }
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Reveal in file manager failed: {ex.GetType().Name}");
        }
    }
}
