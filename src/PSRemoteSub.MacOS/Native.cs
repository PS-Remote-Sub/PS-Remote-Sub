using System.Runtime.InteropServices;

namespace PSRemoteSub.MacOS;

/// <summary>P/Invoke surface of libPSRemoteSubMac.dylib (built from native/macos). Every Mac feature adds its imports here.</summary>
internal static partial class Native
{
    private const string Lib = "PSRemoteSubMac";

    [LibraryImport(Lib)]
    internal static partial int psrs_native_version();

    [LibraryImport(Lib)]
    internal static partial void psrs_free(nint ptr);

    [LibraryImport(Lib)]
    internal static unsafe partial int psrs_ocr_recognize(byte* bgra, int width, int height, [MarshalAs(UnmanagedType.LPUTF8Str)] string lang, out nint outJson);

    [LibraryImport(Lib)]
    internal static partial int psrs_ocr_supported([MarshalAs(UnmanagedType.LPUTF8Str)] string lang);

    [LibraryImport(Lib)]
    internal static unsafe partial int psrs_png_save(byte* bgra, int width, int height, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport(Lib)]
    internal static partial int psrs_png_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint outBgra, out int outWidth, out int outHeight);

    [LibraryImport(Lib)]
    internal static partial int psrs_windows_list(out nint outJson);

    [LibraryImport(Lib)]
    internal static partial int psrs_screens(out nint outJson);

    [LibraryImport(Lib)]
    internal static partial double psrs_title_bar_points();

    [LibraryImport(Lib)]
    internal static partial nint psrs_sck_start(uint windowId);

    [LibraryImport(Lib)]
    internal static unsafe partial int psrs_sck_copy_latest(nint handle, byte* dst, int capacity, out int outW, out int outH, out long outTicks);

    [LibraryImport(Lib)]
    internal static partial void psrs_sck_stop(nint handle);

    [LibraryImport(Lib)]
    internal static partial int psrs_overlay_configure(nint window);

    [LibraryImport(Lib)]
    internal static partial int psrs_toast_configure(nint window);

    [LibraryImport(Lib)]
    internal static partial int psrs_keepawake_set(int active);

    [LibraryImport(Lib)]
    internal static partial int psrs_hotkey_register(int id, uint keyCode, uint modifiers);

    [LibraryImport(Lib)]
    internal static partial void psrs_hotkey_unregister_all();

    [LibraryImport(Lib)]
    internal static unsafe partial void psrs_hotkey_set_callback(delegate* unmanaged<int, void> callback);

    [LibraryImport(Lib)]
    internal static partial int psrs_perm_check(int kind);

    [LibraryImport(Lib)]
    internal static unsafe partial int psrs_perm_request(int kind, delegate* unmanaged<int, int, void> callback);

    [LibraryImport(Lib)]
    internal static partial int psrs_perm_open_settings(int kind);

    [LibraryImport(Lib)]
    internal static partial int psrs_av_devices(out nint outJson);

    [LibraryImport(Lib)]
    internal static partial int psrs_av_modes([MarshalAs(UnmanagedType.LPUTF8Str)] string id, out nint outJson);

    [LibraryImport(Lib)]
    internal static unsafe partial nint psrs_av_open([MarshalAs(UnmanagedType.LPUTF8Str)] string id, int width, int height, double fps,
        int subtype, int owner, int attempt, delegate* unmanaged<int, int, int, void> callback);

    [LibraryImport(Lib)]
    internal static partial void psrs_av_close(nint handle);

    [LibraryImport(Lib)]
    internal static unsafe partial int psrs_av_copy_latest(nint handle, byte* dst, int capacity, out int outW, out int outH, out long outTicks);

    [LibraryImport(Lib)]
    internal static unsafe partial nint psrs_av_view_create(nint session, int token, delegate* unmanaged<int, int, void> callback);

    [LibraryImport(Lib)]
    internal static partial void psrs_av_view_attach(nint view, nint session);

    [LibraryImport(Lib)]
    internal static partial void psrs_av_view_destroy(nint view);

    [LibraryImport(Lib)]
    internal static partial int psrs_av_view_screen_rect(nint view, out double x, out double y, out double w, out double h, out int flags);

    [LibraryImport(Lib)]
    internal static unsafe partial nint psrs_av_audio_start([MarshalAs(UnmanagedType.LPUTF8Str)] string id, double volume, int muted,
        int owner, int attempt, delegate* unmanaged<int, int, int, void> callback);

    [LibraryImport(Lib)]
    internal static partial void psrs_av_audio_set(nint handle, double volume, int muted);

    [LibraryImport(Lib)]
    internal static partial void psrs_av_audio_stop(nint handle);
}
