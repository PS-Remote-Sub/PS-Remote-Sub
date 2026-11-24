import AppKit
import AVFoundation
import CoreGraphics
import Foundation

// Permission kinds, as in the C# PlatformPermission enum.
private let kindScreenRecording: Int32 = 0
private let kindCamera: Int32 = 1
private let kindMicrophone: Int32 = 2
private let kindLocalNetwork: Int32 = 3

private func mediaType(_ kind: Int32) -> AVMediaType? {
    switch kind {
    case kindCamera: return .video
    case kindMicrophone: return .audio
    default: return nil
    }
}

/// Where a permission stands: 0 granted, 1 not determined, 2 denied, 3 unknown (no way to tell). Never prompts.
/// Screen Recording only says granted or denied; Local Network has no API (always unknown).
@_cdecl("psrs_perm_check")
public func psrs_perm_check(_ kind: Int32) -> Int32 {
    if kind == kindScreenRecording { return CGPreflightScreenCaptureAccess() ? 0 : 2 }
    guard let type = mediaType(kind) else { return 3 }
    switch AVCaptureDevice.authorizationStatus(for: type) {
    case .authorized: return 0
    case .notDetermined: return 1
    case .denied, .restricted: return 2
    @unknown default: return 3
    }
}

/// Asks the OS for the permission (may show its dialog) and calls `cb(kind, granted)` once, from any thread.
/// Returns 0 when the request was started, 1 when it cannot be requested (unknown kind). Camera and Microphone need the
/// usage strings in the app's Info.plist: without them the OS kills the process, so only call this from the app bundle.
@_cdecl("psrs_perm_request")
public func psrs_perm_request(_ kind: Int32, _ cb: (@convention(c) (Int32, Int32) -> Void)?) -> Int32 {
    if kind == kindScreenRecording {
        // Shows the system prompt (once) and returns the current answer; the grant itself takes effect after a relaunch.
        let granted = CGRequestScreenCaptureAccess()
        cb?(kind, granted ? 1 : 0)
        return 0
    }
    guard let type = mediaType(kind) else { return 1 }
    AVCaptureDevice.requestAccess(for: type) { granted in
        cb?(kind, granted ? 1 : 0)
    }
    return 0
}

/// Opens the System Settings privacy pane of the permission. 0 ok, 1 unknown kind, 2 could not open.
@_cdecl("psrs_perm_open_settings")
public func psrs_perm_open_settings(_ kind: Int32) -> Int32 {
    let pane: String
    switch kind {
    case kindScreenRecording: pane = "Privacy_ScreenCapture"
    case kindCamera: pane = "Privacy_Camera"
    case kindMicrophone: pane = "Privacy_Microphone"
    case kindLocalNetwork: pane = "Privacy_LocalNetwork"
    default: return 1
    }
    guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?" + pane) else { return 2 }
    return NSWorkspace.shared.open(url) ? 0 : 2
}
