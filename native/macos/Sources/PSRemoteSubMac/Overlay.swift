import AppKit
import ObjectiveC

private let overlayClassPrefix = "PSRSOverlay_"
private let overlayClassLock = NSLock()

/// A per-window subclass of the window's own class whose only difference is that it can never become key or main.
/// Such a window is shown without taking keyboard focus from the game, and clicks never activate this app, which is
/// the macOS counterpart of WS_EX_NOACTIVATE. Only the configured window instance is switched to it (class_setClass on
/// that object); Avalonia's window classes are untouched. Returns nil if the class cannot be built.
private func nonKeyClass(for window: NSWindow) -> AnyClass? {
    let base: AnyClass = object_getClass(window) ?? NSWindow.self
    let baseName = String(cString: class_getName(base))
    if baseName.hasPrefix(overlayClassPrefix) { return base }
    // A KVO-observed window already has a generated subclass; leave it alone rather than fight the runtime.
    if baseName.hasPrefix("NSKVONotifying_") { return nil }
    overlayClassLock.lock()
    defer { overlayClassLock.unlock() }
    let name = overlayClassPrefix + baseName
    if let existing = NSClassFromString(name) { return existing }
    guard let sub = objc_allocateClassPair(base, name, 0) else { return nil }
    let no: @convention(block) (AnyObject) -> Bool = { _ in false }
    let imp = imp_implementationWithBlock(no)
    class_addMethod(sub, NSSelectorFromString("canBecomeKeyWindow"), imp, "B@:")
    class_addMethod(sub, NSSelectorFromString("canBecomeMainWindow"), imp, "B@:")
    objc_registerClassPair(sub)
    return sub
}

private func configureOverlay(_ window: NSWindow, clickThrough: Bool = true) {
    window.ignoresMouseEvents = clickThrough
    window.level = .statusBar
    window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
    window.hidesOnDeactivate = false
    window.isOpaque = false
    window.backgroundColor = .clear
    window.hasShadow = false
    if let sub = nonKeyClass(for: window) { object_setClass(window, sub) }
    // Show above the other app without making this window key: no focus change.
    window.orderFrontRegardless()
}

/// Turns the window behind `ptr` (an NSWindow* or an NSView*, whichever Avalonia hands out) into a click-through,
/// focus-less overlay above full-screen apps. Must run on the main thread (Avalonia's UI thread is it); from another
/// thread the work is queued to the main thread. 0 ok, 1 bad argument (null, or not a window/view).
@_cdecl("psrs_overlay_configure")
public func psrs_overlay_configure(_ ptr: UnsafeMutableRawPointer?) -> Int32 {
    configure(ptr, clickThrough: true)
}

/// Like `psrs_overlay_configure`, but the window takes mouse clicks (a toast's buttons). It still never becomes key,
/// so showing it takes no keyboard focus from the game. 0 ok, 1 bad argument.
@_cdecl("psrs_toast_configure")
public func psrs_toast_configure(_ ptr: UnsafeMutableRawPointer?) -> Int32 {
    configure(ptr, clickThrough: false)
}

private func configure(_ ptr: UnsafeMutableRawPointer?, clickThrough: Bool) -> Int32 {
    guard let ptr else { return 1 }
    let object = Unmanaged<AnyObject>.fromOpaque(ptr).takeUnretainedValue()
    let window: NSWindow
    if let w = object as? NSWindow { window = w }
    else if let v = object as? NSView, let w = v.window { window = w }
    else { return 1 }
    if Thread.isMainThread {
        configureOverlay(window, clickThrough: clickThrough)
    } else {
        nonisolated(unsafe) let w = window
        DispatchQueue.main.async { configureOverlay(w, clickThrough: clickThrough) }
    }
    return 0
}
