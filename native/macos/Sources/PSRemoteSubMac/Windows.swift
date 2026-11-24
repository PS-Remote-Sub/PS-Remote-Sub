import AppKit
import CoreGraphics
import Foundation

private struct WindowDto: Encodable {
    let id: UInt32; let pid: Int32; let owner: String; let bundle: String?
    let x: Double; let y: Double; let w: Double; let h: Double; let layer: Int; let onScreen: Bool
}

private struct ScreenDto: Encodable { let x: Double; let y: Double; let w: Double; let h: Double; let scale: Double }

private func writeJson<T: Encodable>(_ value: T, _ out: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>) -> Int32 {
    guard let data = try? JSONEncoder().encode(value), let json = String(data: data, encoding: .utf8),
          let dup = strdup(json) else { return 2 }
    out.pointee = dup
    return 0
}

/// Lists every normal (layer 0) window, on screen or not (minimised / other Space), front to back. Bounds are Quartz
/// global points: origin at the top-left of the main display, Y down. 0 ok (free outJson with psrs_free), 1 bad
/// argument, 2 failed. JSON is `[{id,pid,owner,bundle,x,y,w,h,layer,onScreen}]`. Needs no permission (window titles,
/// which do, are not read). Safe on any thread.
@_cdecl("psrs_windows_list")
public func psrs_windows_list(_ outJson: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?) -> Int32 {
    guard let outJson else { return 1 }
    guard let list = CGWindowListCopyWindowInfo([.optionAll, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]
    else { return 2 }
    var bundles: [Int32: String?] = [:]
    var windows: [WindowDto] = []
    for info in list {
        guard let layer = info[kCGWindowLayer as String] as? Int, layer == 0,
              let id = info[kCGWindowNumber as String] as? UInt32,
              let pid = info[kCGWindowOwnerPID as String] as? Int32,
              let boundsDict = info[kCGWindowBounds as String] as? NSDictionary,
              let bounds = CGRect(dictionaryRepresentation: boundsDict as CFDictionary) else { continue }
        let bundle: String?
        if let cached = bundles[pid] {
            bundle = cached
        } else {
            bundle = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier
            bundles[pid] = bundle
        }
        windows.append(WindowDto(
            id: id, pid: pid, owner: info[kCGWindowOwnerName as String] as? String ?? "", bundle: bundle,
            x: bounds.minX, y: bounds.minY, w: bounds.width, h: bounds.height, layer: layer,
            onScreen: info[kCGWindowIsOnscreen as String] as? Bool ?? false))
    }
    return writeJson(windows, outJson)
}

/// Backing pixels per point of a display (2 on Retina, also in its scaled "looks like" modes).
func displayScale(_ display: CGDirectDisplayID) -> Double {
    guard let mode = CGDisplayCopyDisplayMode(display), mode.width > 0 else { return 1 }
    return max(1, Double(mode.pixelWidth) / Double(mode.width))
}

/// Active displays with their bounds in Quartz global points (main display at 0,0).
func activeDisplays() -> [(id: CGDirectDisplayID, bounds: CGRect)] {
    var count: UInt32 = 0
    guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
    var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
    guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return [] }
    return ids.prefix(Int(count)).map { ($0, CGDisplayBounds($0)) }
}

/// Active displays as `[{x,y,w,h,scale}]`, bounds in Quartz global points. 0 ok (free with psrs_free), 1 bad argument,
/// 2 failed. Uses CoreGraphics only, so it is safe on any thread (NSScreen is main-thread only).
@_cdecl("psrs_screens")
public func psrs_screens(_ outJson: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?) -> Int32 {
    guard let outJson else { return 1 }
    let screens = activeDisplays().map {
        ScreenDto(x: $0.bounds.minX, y: $0.bounds.minY, w: $0.bounds.width, h: $0.bounds.height, scale: displayScale($0.id))
    }
    return writeJson(screens, outJson)
}

private typealias FrameRectFn = @convention(c) (AnyClass, Selector, NSRect, UInt) -> NSRect

/// Height in points of a standard titled window's title bar on this macOS version, from
/// `+[NSWindow frameRectForContentRect:styleMask:]` (frame height minus content height). That class method is pure
/// arithmetic, but Swift 6 isolates all of NSWindow to the main actor; it is called through the Objective-C runtime so
/// the window watcher's background thread can ask without hopping to (or deadlocking on) the main thread.
private let titleBarHeight: Double = {
    let selector = NSSelectorFromString("frameRectForContentRect:styleMask:")
    guard let metaclass = object_getClass(NSWindow.self),
          let imp = class_getMethodImplementation(metaclass, selector) else { return 0 }
    let frameRect = unsafeBitCast(imp, to: FrameRectFn.self)
    let content = NSRect(x: 0, y: 0, width: 100, height: 100)
    let frame = frameRect(NSWindow.self, selector, content, NSWindow.StyleMask.titled.rawValue)
    return max(0, Double(frame.height - content.height))
}()

/// Title bar height (points) of a standard titled window; see `titleBarHeight`.
@_cdecl("psrs_title_bar_points")
public func psrs_title_bar_points() -> Double {
    titleBarHeight
}
