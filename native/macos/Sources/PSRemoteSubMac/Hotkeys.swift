import Carbon.HIToolbox
import Foundation

private let hotkeyLock = NSLock()
nonisolated(unsafe) private var hotkeyRefs: [EventHotKeyRef] = []
nonisolated(unsafe) private var hotkeyHandler: EventHandlerRef?
nonisolated(unsafe) private var hotkeyCallback: (@convention(c) (Int32) -> Void)?

private let hotkeySignature: OSType = 0x5053_5253 // 'PSRS'

private func installHandlerLocked() -> Bool {
    if hotkeyHandler != nil { return true }
    var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    var handler: EventHandlerRef?
    let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, _ -> OSStatus in
        var hotKeyID = EventHotKeyID()
        let got = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                    nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
        guard got == noErr, hotKeyID.signature == hotkeySignature else { return OSStatus(eventNotHandledErr) }
        hotkeyLock.lock()
        let callback = hotkeyCallback
        hotkeyLock.unlock()
        callback?(Int32(bitPattern: hotKeyID.id))
        return noErr
    }, 1, &spec, nil, &handler)
    guard status == noErr, let handler else { return false }
    hotkeyHandler = handler
    return true
}

/// Sets the function called (on the main thread, from the app's event loop) with the id of a pressed hotkey.
@_cdecl("psrs_hotkey_set_callback")
public func psrs_hotkey_set_callback(_ cb: (@convention(c) (Int32) -> Void)?) {
    hotkeyLock.lock()
    hotkeyCallback = cb
    hotkeyLock.unlock()
}

/// Registers a global hotkey (Carbon modifier mask: controlKey 0x1000, optionKey 0x800, shiftKey 0x200, cmdKey 0x100).
/// 0 ok, 1 the handler could not be installed, 2 the combination is taken or refused. Call from the main thread.
@_cdecl("psrs_hotkey_register")
public func psrs_hotkey_register(_ id: Int32, _ keyCode: UInt32, _ modifiers: UInt32) -> Int32 {
    hotkeyLock.lock()
    defer { hotkeyLock.unlock() }
    guard installHandlerLocked() else { return 1 }
    var ref: EventHotKeyRef?
    let hotKeyID = EventHotKeyID(signature: hotkeySignature, id: UInt32(bitPattern: id))
    let status = RegisterEventHotKey(keyCode, modifiers, hotKeyID, GetApplicationEventTarget(), 0, &ref)
    guard status == noErr, let ref else { return 2 }
    hotkeyRefs.append(ref)
    return 0
}

/// Releases every registered hotkey. Safe to call with none registered.
@_cdecl("psrs_hotkey_unregister_all")
public func psrs_hotkey_unregister_all() {
    hotkeyLock.lock()
    defer { hotkeyLock.unlock() }
    for ref in hotkeyRefs { UnregisterEventHotKey(ref) }
    hotkeyRefs.removeAll()
}
