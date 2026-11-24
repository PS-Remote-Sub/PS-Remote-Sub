import Foundation
import IOKit.pwr_mgt

private let assertionLock = NSLock()
nonisolated(unsafe) private var assertionId: IOPMAssertionID = 0
nonisolated(unsafe) private var assertionHeld = false

/// Keeps the display (and so the Mac) awake while `active` is non-zero; releases the hold when it is 0. Idempotent
/// both ways. 0 ok, 2 the system refused the assertion. Safe on any thread.
@_cdecl("psrs_keepawake_set")
public func psrs_keepawake_set(_ active: Int32) -> Int32 {
    assertionLock.lock()
    defer { assertionLock.unlock() }
    if active != 0 {
        if assertionHeld { return 0 }
        var id: IOPMAssertionID = 0
        let result = IOPMAssertionCreateWithName(
            kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
            IOPMAssertionLevel(kIOPMAssertionLevelOn),
            "PS Remote Sub" as CFString, &id)
        guard result == kIOReturnSuccess else { return 2 }
        assertionId = id
        assertionHeld = true
    } else if assertionHeld {
        IOPMAssertionRelease(assertionId)
        assertionHeld = false
        assertionId = 0
    }
    return 0
}
