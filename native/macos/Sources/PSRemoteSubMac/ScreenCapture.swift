import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

// psrs_sck_copy_latest results.
private let sckOk: Int32 = 0             // newest frame copied
private let sckBadArgument: Int32 = 1
private let sckNoFrame: Int32 = 2        // starting, or running with no frame yet
private let sckNoPermission: Int32 = 3   // Screen Recording not granted: capture was never attempted
private let sckWindowNotFound: Int32 = 4
private let sckFailed: Int32 = 5         // the stream could not start, or stopped with an error
private let sckTooSmall: Int32 = 6       // a frame exists; outW/outH/outTicks are set, nothing copied

/// .NET DateTime ticks (100 ns since 0001-01-01 UTC) of now.
private func dotnetTicksNow() -> Int64 {
    Int64(Date().timeIntervalSince1970 * 10_000_000) + 621_355_968_000_000_000
}

/// One ScreenCaptureKit stream of one window. ScreenCaptureKit is asynchronous: `start` returns at once and the stream
/// comes up (or fails) on a Task; frames arrive on `queue`. Only the newest frame is kept, copied tightly packed
/// (BGRA8) into a buffer this object owns, under `lock`; C# copies it out on demand. Everything mutable is under `lock`.
private final class WindowCapture: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let windowId: CGWindowID
    private let queue = DispatchQueue(label: "psrs.sck.frames")
    private let lock = NSLock()

    private var stream: SCStream?
    private var stopped = false
    private var status = sckNoFrame
    private var configured = (w: 0, h: 0)   // the stream's output size in pixels
    private var pixels: UnsafeMutablePointer<UInt8>?
    private var pixelsCapacity = 0
    private var frameW = 0, frameH = 0
    private var ticks: Int64 = 0

    init(windowId: CGWindowID) { self.windowId = windowId }

    deinit { free(pixels) }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    private func fail(_ code: Int32) { locked { if !stopped { status = code } } }

    func start() {
        // Never prompt: without Screen Recording access ScreenCaptureKit would show the system dialog.
        guard CGPreflightScreenCaptureAccess() else { fail(sckNoPermission); return }
        Task { await self.run() }
    }

    private func run() async {
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            guard let window = content.windows.first(where: { $0.windowID == windowId }) else { fail(sckWindowNotFound); return }
            let size = pixelSize(of: window.frame)
            let stream = SCStream(filter: SCContentFilter(desktopIndependentWindow: window),
                                  configuration: makeConfiguration(size), delegate: self)
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
            let go = locked { () -> Bool in
                guard !stopped else { return false }
                self.stream = stream
                configured = size
                return true
            }
            guard go else { return }
            try await stream.startCapture()
            // Stopped while starting: stop() saw no running stream to stop, so stop it here.
            if locked({ stopped }) { try? await stream.stopCapture() }
        } catch {
            fail(sckFailed)
        }
    }

    func stop() {
        let running = locked { () -> SCStream? in
            stopped = true
            defer { stream = nil }
            return stream
        }
        running?.stopCapture { _ in }
    }

    /// The window's size in backing pixels: its frame (points) times the scale of the display under its centre.
    private func pixelSize(of frame: CGRect) -> (w: Int, h: Int) {
        let centre = CGPoint(x: frame.midX, y: frame.midY)
        let display = activeDisplays().first { $0.bounds.contains(centre) }?.id ?? CGMainDisplayID()
        let scale = displayScale(display)
        return (clampSize(frame.width * scale), clampSize(frame.height * scale))
    }

    private func clampSize(_ v: Double) -> Int { min(16_384, max(2, Int(v.rounded()))) }

    private func makeConfiguration(_ size: (w: Int, h: Int)) -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        config.width = size.w
        config.height = size.h
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.showsCursor = false
        config.minimumFrameInterval = CMTime(value: 1, timescale: 10) // 10 fps max
        config.queueDepth = 3
        return config
    }

    // MARK: SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) { fail(sckFailed) }

    // MARK: SCStreamOutput (on `queue`)

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let info = attachments.first,
              let rawStatus = info[.status] as? Int, SCFrameStatus(rawValue: rawStatus) == .complete,
              let imageBuffer = sampleBuffer.imageBuffer else { return }

        // Where the window sits in the buffer and how big it really is: after a resize the stream scales the window
        // into the old size until it is reconfigured, so resize it and drop this (scaled) frame.
        var content = CGRect(x: 0, y: 0, width: CVPixelBufferGetWidth(imageBuffer), height: CVPixelBufferGetHeight(imageBuffer))
        if let rectDict = info[.contentRect] as? NSDictionary, let rect = CGRect(dictionaryRepresentation: rectDict as CFDictionary),
           let contentScale = info[.contentScale] as? Double, contentScale > 0,
           let scaleFactor = info[.scaleFactor] as? Double, scaleFactor > 0 {
            let want = (w: clampSize(rect.width / contentScale * scaleFactor), h: clampSize(rect.height / contentScale * scaleFactor))
            let resize = locked { () -> Bool in
                guard !stopped, abs(want.w - configured.w) > 1 || abs(want.h - configured.h) > 1 else { return false }
                configured = want
                return true
            }
            if resize {
                stream.updateConfiguration(makeConfiguration(want)) { _ in }
                return
            }
            content = CGRect(x: rect.minX * scaleFactor, y: rect.minY * scaleFactor,
                             width: rect.width * scaleFactor, height: rect.height * scaleFactor)
        }
        store(imageBuffer, content)
    }

    /// Copies the `content` part (pixels, clamped to the buffer) of a BGRA buffer as the newest frame.
    private func store(_ imageBuffer: CVImageBuffer, _ content: CGRect) {
        guard CVPixelBufferLockBaseAddress(imageBuffer, .readOnly) == kCVReturnSuccess else { return }
        defer { CVPixelBufferUnlockBaseAddress(imageBuffer, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(imageBuffer) else { return }
        let bufW = CVPixelBufferGetWidth(imageBuffer), bufH = CVPixelBufferGetHeight(imageBuffer)
        let stride = CVPixelBufferGetBytesPerRow(imageBuffer)
        let x0 = max(0, min(bufW, Int(content.minX.rounded()))), y0 = max(0, min(bufH, Int(content.minY.rounded())))
        let w = min(bufW - x0, Int(content.width.rounded())), h = min(bufH - y0, Int(content.height.rounded()))
        guard w > 0, h > 0 else { return }

        lock.lock()
        defer { lock.unlock() }
        guard !stopped else { return }
        let needed = w * h * 4
        if needed > pixelsCapacity {
            guard let grown = realloc(pixels, needed)?.assumingMemoryBound(to: UInt8.self) else { return }
            pixels = grown
            pixelsCapacity = needed
        }
        guard let pixels else { return }
        let src = base.assumingMemoryBound(to: UInt8.self)
        for row in 0..<h {
            memcpy(pixels + row * w * 4, src + (y0 + row) * stride + x0 * 4, w * 4)
        }
        frameW = w
        frameH = h
        ticks = dotnetTicksNow()
        status = sckOk
    }

    /// See psrs_sck_copy_latest.
    func copyLatest(_ dst: UnsafeMutablePointer<UInt8>?, _ capacity: Int,
                    _ outW: UnsafeMutablePointer<Int32>, _ outH: UnsafeMutablePointer<Int32>,
                    _ outTicks: UnsafeMutablePointer<Int64>) -> Int32 {
        lock.lock()
        defer { lock.unlock() }
        guard status == sckOk, let pixels else { return status }
        outW.pointee = Int32(frameW)
        outH.pointee = Int32(frameH)
        outTicks.pointee = ticks
        let size = frameW * frameH * 4
        guard let dst, capacity >= size else { return sckTooSmall }
        memcpy(dst, pixels, size)
        return sckOk
    }
}

/// Starts capturing window `windowId` (a CGWindowID) with ScreenCaptureKit at most 10 fps, BGRA8, without the cursor,
/// at the window's own pixel size (it follows later resizes). Returns at once with a handle (never null) for
/// psrs_sck_copy_latest / psrs_sck_stop; whether the stream came up shows in psrs_sck_copy_latest's result. Never
/// prompts: without Screen Recording access nothing is captured (result 3).
@_cdecl("psrs_sck_start")
public func psrs_sck_start(_ windowId: UInt32) -> UnsafeMutableRawPointer {
    let capture = WindowCapture(windowId: windowId)
    capture.start()
    return Unmanaged.passRetained(capture).toOpaque()
}

/// Copies the newest frame (whole window, BGRA8 premultiplied, stride width*4, top row first) into `dst`.
/// 0 copied (outW/outH = size, outTicks = arrival time in .NET UTC ticks), 1 bad argument, 2 no frame yet,
/// 3 no Screen Recording permission, 4 window not found, 5 stream failed, 6 `capacity` too small or `dst` null
/// (outW/outH/outTicks set, nothing copied: pass a null `dst` to ask for the size and time only).
@_cdecl("psrs_sck_copy_latest")
public func psrs_sck_copy_latest(
    _ handle: UnsafeMutableRawPointer?, _ dst: UnsafeMutablePointer<UInt8>?, _ capacity: Int32,
    _ outW: UnsafeMutablePointer<Int32>?, _ outH: UnsafeMutablePointer<Int32>?, _ outTicks: UnsafeMutablePointer<Int64>?
) -> Int32 {
    guard let handle, let outW, let outH, let outTicks, capacity >= 0 else { return sckBadArgument }
    let capture = Unmanaged<WindowCapture>.fromOpaque(handle).takeUnretainedValue()
    return capture.copyLatest(dst, Int(capacity), outW, outH, outTicks)
}

/// Stops the capture and frees the handle (do not use it afterwards). The stream winds down asynchronously.
@_cdecl("psrs_sck_stop")
public func psrs_sck_stop(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    let capture = Unmanaged<WindowCapture>.fromOpaque(handle).takeRetainedValue()
    capture.stop()
}
