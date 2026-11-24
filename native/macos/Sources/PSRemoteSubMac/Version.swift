/// Native ABI version; the managed side (Native.psrs_native_version) checks it to detect a stale dylib.
@_cdecl("psrs_native_version")
public func psrs_native_version() -> Int32 { 1 }
