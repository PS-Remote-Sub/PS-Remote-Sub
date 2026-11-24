using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Security;

/// <summary>
/// macOS privacy permissions (TCC): Screen Recording, Camera, Microphone. <see cref="Check"/> never prompts. Local Network
/// has no API, so it is always <see cref="PermissionState.Unknown"/>. Never throws.
/// </summary>
public sealed class MacPermissions : IPermissions
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> s_pending = new();

    public PermissionState Check(PlatformPermission p)
    {
        try
        {
            return Native.psrs_perm_check((int)p) switch
            {
                0 => PermissionState.Granted,
                1 => PermissionState.NotDetermined,
                2 => PermissionState.Denied,
                _ => PermissionState.Unknown,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Permission check failed for {p}: {ex.Message}");
            return PermissionState.Unknown;
        }
    }

    public unsafe Task<bool> RequestAsync(PlatformPermission p)
    {
        if (p == PlatformPermission.LocalNetwork)
            return Task.FromResult(false); // no API: the OS asks by itself on first LAN use
        try
        {
            var tcs = s_pending.GetOrAdd((int)p, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Native.psrs_perm_request((int)p, &OnNativeAnswered) != 0)
            {
                s_pending.TryRemove((int)p, out _);
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }
        catch (Exception ex)
        {
            Log.Warn($"Permission request failed for {p}: {ex.Message}");
            s_pending.TryRemove((int)p, out _);
            return Task.FromResult(false);
        }
    }

    public void OpenSettings(PlatformPermission p)
    {
        try
        {
            if (Native.psrs_perm_open_settings((int)p) != 0)
                Log.Warn($"Could not open System Settings for {p}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening System Settings failed for {p}: {ex.Message}");
        }
    }

    // Called by native code on any thread. Must never throw.
    [UnmanagedCallersOnly]
    private static void OnNativeAnswered(int kind, int granted)
    {
        try
        {
            if (s_pending.TryRemove(kind, out var tcs))
                tcs.TrySetResult(granted != 0);
        }
        catch (Exception ex)
        {
            try { Log.Warn($"Permission callback failed: {ex.Message}"); } catch { /* nothing more to do */ }
        }
    }
}
