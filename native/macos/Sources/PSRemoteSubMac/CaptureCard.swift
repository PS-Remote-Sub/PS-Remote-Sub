import AppKit
import AVFoundation
import CoreMedia
import CoreVideo
import Foundation

private struct AvVideoDto: Encodable { let id: String; let name: String; let modelId: String; let builtIn: Bool }
private struct AvAudioDto: Encodable { let id: String; let name: String; let modelId: String }
private struct AvDevicesDto: Encodable { let video: [AvVideoDto]; let audio: [AvAudioDto] }
private struct AvModeDto: Encodable { let w: Int; let h: Int; let fps: Double; let fourcc: UInt32 }

private func writeAvJson<T: Encodable>(_ value: T, _ out: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>) -> Int32 {
    guard let data = try? JSONEncoder().encode(value), let json = String(data: data, encoding: .utf8),
          let dup = strdup(json) else { return 2 }
    out.pointee = dup
    return 0
}

/// The external-device type: `.external` from macOS 14, `.externalUnknown` before.
private var externalType: AVCaptureDevice.DeviceType {
    if #available(macOS 14.0, *) { return .external }
    return .externalUnknown
}

private func discoverVideo() -> [AVCaptureDevice] {
    AVCaptureDevice.DiscoverySession(
        deviceTypes: [externalType, .builtInWideAngleCamera], mediaType: .video, position: .unspecified).devices
}

private func discoverAudio() -> [AVCaptureDevice] {
    let types: [AVCaptureDevice.DeviceType]
    if #available(macOS 14.0, *) { types = [.microphone, .external] } else { types = [.builtInMicrophone, .externalUnknown] }
    return AVCaptureDevice.DiscoverySession(deviceTypes: types, mediaType: .audio, position: .unspecified).devices
}

/// Built into the Mac or not a capture card: the built-in camera and an iPhone's Continuity Camera.
private func isBuiltInOrContinuity(_ d: AVCaptureDevice) -> Bool {
    if d.deviceType == .builtInWideAngleCamera || d.isContinuityCamera { return true }
    if #available(macOS 14.0, *), d.deviceType == .continuityCamera { return true }
    return false
}

/// Lists the video capture devices and the audio inputs, unpaired (the managed side pairs them by USB ids), as
/// `{video:[{id,name,modelId,builtIn}], audio:[{id,name,modelId}]}`. `id` is the AVCaptureDevice uniqueID; `builtIn`
/// is true for the Mac's camera and for Continuity Camera. 0 ok (free outJson with psrs_free), 1 bad argument, 2
/// failed. Opens no device and needs no camera or microphone permission.
@_cdecl("psrs_av_devices")
public func psrs_av_devices(_ outJson: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?) -> Int32 {
    guard let outJson else { return 1 }
    let video = discoverVideo().map {
        AvVideoDto(id: $0.uniqueID, name: $0.localizedName, modelId: $0.modelID, builtIn: isBuiltInOrContinuity($0))
    }
    let audio = discoverAudio().map { AvAudioDto(id: $0.uniqueID, name: $0.localizedName, modelId: $0.modelID) }
    return writeAvJson(AvDevicesDto(video: video, audio: audio), outJson)
}

/// Lists a device's modes as `[{w,h,fps,fourcc}]`: one entry per distinct (size, max frame rate, pixel format).
/// `fourcc` is the CoreMedia media subtype (e.g. 0x34323076 for '420v'). 0 ok, 1 bad argument, 2 unknown device.
@_cdecl("psrs_av_modes")
public func psrs_av_modes(_ id: UnsafePointer<CChar>?,
                          _ outJson: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?) -> Int32 {
    guard let id, let outJson else { return 1 }
    guard let device = AVCaptureDevice(uniqueID: String(cString: id)) else { return 2 }
    var seen = Set<String>()
    var modes: [AvModeDto] = []
    for format in device.formats {
        let desc = format.formatDescription
        let dims = CMVideoFormatDescriptionGetDimensions(desc)
        let fourcc = CMFormatDescriptionGetMediaSubType(desc)
        let fps = format.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 0
        guard fps > 0, seen.insert("\(dims.width)x\(dims.height)@\(fps)/\(fourcc)").inserted else { continue }
        modes.append(AvModeDto(w: Int(dims.width), h: Int(dims.height), fps: fps, fourcc: fourcc))
    }
    return writeAvJson(modes, outJson)
}

// MARK: - Opening a device: picture, frames, state

// AvEvent values, as in the C# AvEvent enum (AvStateMapping.cs). Opening (0) is the managed side's own.
private let avRunning: Int32 = 1
private let avNoFrames3s: Int32 = 2
private let avDeviceInUse: Int32 = 3
private let avNotAuthorized: Int32 = 4
private let avDisconnected: Int32 = 5
private let avRuntimeError: Int32 = 6
private let avDeviceInUseError: Int32 = 7  // in use when opened, or a runtime error saying so: the session is dead

// psrs_av_copy_latest results (as psrs_sck_copy_latest).
private let avCopyOk: Int32 = 0
private let avCopyBadArgument: Int32 = 1
private let avCopyNoFrame: Int32 = 2
private let avCopySizeOnly: Int32 = 6

/// `(owner, attempt, value)`: the managed object, which of its opens, and an event (or running 0/1 for audio).
public typealias AvCallback = @convention(c) (Int32, Int32, Int32) -> Void

private let noFramesAfterNs: UInt64 = 3_000_000_000
private let storeEveryNs: UInt64 = 95_000_000   // ~10 fps for the OCR frames

/// One queue for every video session, and one for every audio session: a reopen's set-up then starts only after the
/// previous session of the same device has stopped and let go of it.
private let videoSessionQueue = DispatchQueue(label: "psrs.av.session")
private let audioSessionQueue = DispatchQueue(label: "psrs.av.audio")

private func uptimeNs() -> UInt64 { DispatchTime.now().uptimeNanoseconds }

/// .NET DateTime ticks (100 ns since 0001-01-01 UTC) of now.
private func avTicksNow() -> Int64 {
    Int64(Date().timeIntervalSince1970 * 10_000_000) + 621_355_968_000_000_000
}

/// The event an AVFoundation error stands for.
private func avEvent(for error: Error?) -> Int32 {
    guard let error = error as NSError?, error.domain == AVFoundationErrorDomain else { return avRuntimeError }
    switch error.code {
    case AVError.Code.deviceWasDisconnected.rawValue: return avDisconnected
    case AVError.Code.deviceInUseByAnotherApplication.rawValue, AVError.Code.deviceAlreadyUsedByAnotherSession.rawValue:
        return avDeviceInUseError
    case AVError.Code.applicationIsNotAuthorizedToUseDevice.rawValue: return avNotAuthorized
    default: return avRuntimeError
    }
}

/// As the managed CaptureModeChooser ranks pixel formats: 0 NV12, 1 YUY2, 2 MJPG, 3 anything else.
private func subtypeRank(_ fourcc: FourCharCode) -> Int32 {
    switch fourcc {
    case 0x3432_3076, 0x3432_3066: return 0 // 420v, 420f
    case 0x7975_7673, 0x3276_7579: return 1 // yuvs, 2vuy
    case 0x646D_6231: return 2              // dmb1
    default: return 3
    }
}

/// The device format of the mode the managed side chose: the same size and maximum rate (as psrs_av_modes lists them),
/// preferring the chosen pixel format; nil when there is none (the device's own default is used).
private func pickFormat(_ device: AVCaptureDevice, width: Int32, height: Int32, fps: Double, subtype: Int32)
    -> (format: AVCaptureDevice.Format, range: AVFrameRateRange)? {
    var best: (format: AVCaptureDevice.Format, range: AVFrameRateRange, score: Int32)?
    for format in device.formats {
        let dims = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
        guard dims.width == width, dims.height == height,
              let range = format.videoSupportedFrameRateRanges.max(by: { $0.maxFrameRate < $1.maxFrameRate }),
              abs(range.maxFrameRate - fps) < 0.5 else { continue }
        let rank = subtypeRank(CMFormatDescriptionGetMediaSubType(format.formatDescription))
        let score = rank == subtype ? -1 : rank
        if best == nil || score < best!.score { best = (format, range, score) }
    }
    return best.map { ($0.format, $0.range) }
}

/// One open of a capture device: an AVCaptureSession with the device's input and a BGRA video data output, set up and
/// run on `queue` (shared by all video sessions; startRunning blocks: never on the main thread). Reports what happens through `callback`, each event
/// once in a row: Running on the first frame (and again after frames stopped), NoFrames3s after 3 s without a frame,
/// DeviceInUse while interrupted (AVFoundation resumes it), and NotAuthorized, Disconnected, RuntimeError or
/// DeviceInUseError (in use when opened, or a runtime error saying so), after which it reports nothing more (the
/// managed side closes it and opens again). Keeps the newest frame, sampled to ~10 fps, copied tightly packed for
/// psrs_av_copy_latest. Nothing is reported after `close`. Mutable state is under `lock`; `reportLock` keeps the
/// decision and the call of each report in order across threads. The callback is never made while `lock` is held,
/// and the functions the managed side calls never take `reportLock`.
private final class VideoSession: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate, @unchecked Sendable {
    let session = AVCaptureSession()

    private let deviceId: String
    private let width: Int32, height: Int32, fps: Double, subtype: Int32
    private let owner: Int32, attempt: Int32
    private let callback: AvCallback
    private let queue = videoSessionQueue
    private let frameQueue = DispatchQueue(label: "psrs.av.frames")
    private let lock = NSLock()
    private let reportLock = NSLock()

    // Under lock.
    private var closed = false
    private var finished = false        // a final event was reported
    private var interrupted = false
    private var lastEvent: Int32 = -1
    private var startedAt: UInt64 = 0   // uptime when running was asked for; 0 before
    private var lastFrameAt: UInt64 = 0
    private var lastStoredAt: UInt64 = 0
    private var pixels: UnsafeMutablePointer<UInt8>?
    private var pixelsCapacity = 0
    private var frameW = 0, frameH = 0
    private var ticks: Int64 = 0

    // On queue only.
    private var observers: [NSObjectProtocol] = []
    private var timer: DispatchSourceTimer?
    private var output: AVCaptureVideoDataOutput?

    init(deviceId: String, width: Int32, height: Int32, fps: Double, subtype: Int32,
         owner: Int32, attempt: Int32, callback: @escaping AvCallback) {
        self.deviceId = deviceId
        self.width = width
        self.height = height
        self.fps = fps
        self.subtype = subtype
        self.owner = owner
        self.attempt = attempt
        self.callback = callback
    }

    deinit { free(pixels) }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    func start() { queue.async { self.run() } }

    func close() {
        locked { closed = true }
        queue.async { self.tearDown() }
    }

    private func report(_ event: Int32) {
        reportLock.lock()
        defer { reportLock.unlock() }
        let send = locked { () -> Bool in
            guard !closed, !finished, lastEvent != event else { return false }
            lastEvent = event
            if event == avNotAuthorized || event == avDisconnected || event == avRuntimeError || event == avDeviceInUseError {
                finished = true
            }
            return true
        }
        if send { callback(owner, attempt, event) }
    }

    private func run() {
        guard !locked({ closed }) else { return }
        // The device first, so that an unknown id never reaches the authorization check.
        guard let device = AVCaptureDevice(uniqueID: deviceId) else { report(avDisconnected); return }
        guard AVCaptureDevice.authorizationStatus(for: .video) == .authorized else { report(avNotAuthorized); return }
        let input: AVCaptureDeviceInput
        do { input = try AVCaptureDeviceInput(device: device) } catch { report(avEvent(for: error)); return }

        let output = AVCaptureVideoDataOutput()
        output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
        output.alwaysDiscardsLateVideoFrames = true
        output.setSampleBufferDelegate(self, queue: frameQueue)
        session.beginConfiguration()
        guard session.canAddInput(input), session.canAddOutput(output) else {
            session.commitConfiguration()
            report(avRuntimeError)
            return
        }
        session.addInput(input)
        session.addOutput(output)
        session.commitConfiguration()
        self.output = output
        observe(device)

        // The device stays locked until the session runs, so the session preset cannot replace the chosen format.
        var lockedDevice = false
        if width > 0, let picked = pickFormat(device, width: width, height: height, fps: fps, subtype: subtype),
           (try? device.lockForConfiguration()) != nil {
            lockedDevice = true
            device.activeFormat = picked.format
            device.activeVideoMinFrameDuration = picked.range.minFrameDuration
        }
        guard !locked({ closed }) else {
            if lockedDevice { device.unlockForConfiguration() }
            return
        }
        locked { startedAt = uptimeNs() }
        session.startRunning()
        if lockedDevice { device.unlockForConfiguration() }
        startTimer()
    }

    private func observe(_ device: AVCaptureDevice) {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVCaptureSession.runtimeErrorNotification, object: session, queue: nil) {
            [weak self] note in
            self?.report(avEvent(for: note.userInfo?[AVCaptureSessionErrorKey] as? NSError))
        })
        observers.append(center.addObserver(forName: AVCaptureSession.wasInterruptedNotification, object: session, queue: nil) {
            [weak self] _ in
            guard let self else { return }
            self.locked { self.interrupted = true }
            self.report(avDeviceInUse)
        })
        observers.append(center.addObserver(forName: AVCaptureSession.interruptionEndedNotification, object: session, queue: nil) {
            [weak self] _ in
            guard let self else { return }
            // Frames report Running again; until then the no-frame clock starts over.
            self.locked {
                self.interrupted = false
                self.lastFrameAt = uptimeNs()
            }
        })
        observers.append(center.addObserver(forName: AVCaptureDevice.wasDisconnectedNotification, object: device, queue: nil) {
            [weak self] _ in
            self?.report(avDisconnected)
        })
    }

    /// Every 0.5 s: NoFrames3s once 3 s pass without a frame while running and not interrupted.
    private func startTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + .milliseconds(500), repeating: .milliseconds(500))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let silent = self.locked { () -> Bool in
                guard !self.closed, !self.finished, !self.interrupted, self.startedAt != 0 else { return false }
                let since = max(self.startedAt, self.lastFrameAt)
                return uptimeNs() - since >= noFramesAfterNs
            }
            if silent { self.report(avNoFrames3s) }
        }
        self.timer = timer
        timer.resume()
    }

    private func tearDown() {
        timer?.cancel()
        timer = nil
        for o in observers { NotificationCenter.default.removeObserver(o) }
        observers.removeAll()
        output?.setSampleBufferDelegate(nil, queue: nil)
        output = nil
        if session.isRunning { session.stopRunning() }
        session.beginConfiguration()
        for input in session.inputs { session.removeInput(input) }
        for out in session.outputs { session.removeOutput(out) }
        session.commitConfiguration()
    }

    // MARK: AVCaptureVideoDataOutputSampleBufferDelegate (on frameQueue)

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        let now = uptimeNs()
        let (running, store) = locked { () -> (Bool, Bool) in
            guard !closed else { return (false, false) }
            lastFrameAt = now
            let store = now - lastStoredAt >= storeEveryNs
            if store { lastStoredAt = now }
            return (lastEvent != avRunning, store)
        }
        if running { report(avRunning) }
        if store, let image = sampleBuffer.imageBuffer { storeFrame(image) }
    }

    private func storeFrame(_ image: CVImageBuffer) {
        guard CVPixelBufferGetPixelFormatType(image) == kCVPixelFormatType_32BGRA,
              CVPixelBufferLockBaseAddress(image, .readOnly) == kCVReturnSuccess else { return }
        defer { CVPixelBufferUnlockBaseAddress(image, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(image) else { return }
        let w = CVPixelBufferGetWidth(image), h = CVPixelBufferGetHeight(image)
        let stride = CVPixelBufferGetBytesPerRow(image)
        guard w > 0, h > 0 else { return }

        lock.lock()
        defer { lock.unlock() }
        guard !closed else { return }
        let needed = w * h * 4
        if needed > pixelsCapacity {
            guard let grown = realloc(pixels, needed)?.assumingMemoryBound(to: UInt8.self) else { return }
            pixels = grown
            pixelsCapacity = needed
        }
        guard let pixels else { return }
        let src = base.assumingMemoryBound(to: UInt8.self)
        if stride == w * 4 {
            memcpy(pixels, src, needed)
        } else {
            for row in 0..<h { memcpy(pixels + row * w * 4, src + row * stride, w * 4) }
        }
        frameW = w
        frameH = h
        ticks = avTicksNow()
    }

    /// See psrs_av_copy_latest.
    func copyLatest(_ dst: UnsafeMutablePointer<UInt8>?, _ capacity: Int,
                    _ outW: UnsafeMutablePointer<Int32>, _ outH: UnsafeMutablePointer<Int32>,
                    _ outTicks: UnsafeMutablePointer<Int64>) -> Int32 {
        lock.lock()
        defer { lock.unlock() }
        guard ticks != 0, let pixels else { return avCopyNoFrame }
        outW.pointee = Int32(frameW)
        outH.pointee = Int32(frameH)
        outTicks.pointee = ticks
        let size = frameW * frameH * 4
        guard let dst, capacity >= size else { return avCopySizeOnly }
        memcpy(dst, pixels, size)
        return avCopyOk
    }
}

/// Opens capture device `id` (an AVCaptureDevice uniqueID) in the mode `width`×`height` at `fps` (its maximum rate, as
/// psrs_av_modes lists it), preferring pixel format `subtype` (0 NV12, 1 YUY2, 2 MJPG); `width` 0 keeps the device's
/// own format. Returns at once with a handle for psrs_av_close / psrs_av_copy_latest / psrs_av_view_attach (null only
/// for a bad argument); the session is set up on its own queue and reports `callback(owner, attempt, event)` from any
/// thread, event as in the managed AvEvent: 1 Running (frames arrive), 2 NoFrames3s, 3 DeviceInUse (interrupted),
/// 4 NotAuthorized, 5 Disconnected (also an unknown id), 6 RuntimeError, 7 DeviceInUseError (in use when opened, or a
/// runtime error saying so); after 4, 5, 6 or 7 nothing more. Never asks for camera access:
/// without it the result is NotAuthorized.
@_cdecl("psrs_av_open")
public func psrs_av_open(_ id: UnsafePointer<CChar>?, _ width: Int32, _ height: Int32, _ fps: Double, _ subtype: Int32,
                         _ owner: Int32, _ attempt: Int32, _ callback: AvCallback?) -> UnsafeMutableRawPointer? {
    guard let id, let callback else { return nil }
    let session = VideoSession(deviceId: String(cString: id), width: width, height: height, fps: fps, subtype: subtype,
                               owner: owner, attempt: attempt, callback: callback)
    session.start()
    return Unmanaged.passRetained(session).toOpaque()
}

/// Stops the session, releases the device and frees the handle (do not use it afterwards). Returns at once; nothing is
/// reported afterwards. A view still showing the session goes black once it is detached (psrs_av_view_attach).
@_cdecl("psrs_av_close")
public func psrs_av_close(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    Unmanaged<VideoSession>.fromOpaque(handle).takeRetainedValue().close()
}

/// Copies the newest frame (BGRA8, stride width*4, top row first, sampled to ~10 fps) into `dst`. 0 copied (outW/outH
/// = size, outTicks = arrival time in .NET UTC ticks), 1 bad argument, 2 no frame yet, 6 `capacity` too small or `dst`
/// null (outW/outH/outTicks set, nothing copied: pass a null `dst` to ask for the size and time only).
@_cdecl("psrs_av_copy_latest")
public func psrs_av_copy_latest(
    _ handle: UnsafeMutableRawPointer?, _ dst: UnsafeMutablePointer<UInt8>?, _ capacity: Int32,
    _ outW: UnsafeMutablePointer<Int32>?, _ outH: UnsafeMutablePointer<Int32>?, _ outTicks: UnsafeMutablePointer<Int64>?
) -> Int32 {
    guard let handle, let outW, let outH, let outTicks, capacity >= 0 else { return avCopyBadArgument }
    return Unmanaged<VideoSession>.fromOpaque(handle).takeUnretainedValue().copyLatest(dst, Int(capacity), outW, outH, outTicks)
}

// MARK: - The video view

// View events reported through the view callback: (token, kind).
private let viewDoubleClicked: Int32 = 0
private let viewToggleFullScreen: Int32 = 1   // F11
private let viewLeaveFullScreen: Int32 = 2    // Esc
private let keyCodeF11: UInt16 = 103
private let keyCodeEscape: UInt16 = 53

public typealias AvViewCallback = @convention(c) (Int32, Int32) -> Void

/// Where the picture last was, for readers off the main thread (AppKit is main-thread only). Under its lock.
private final class ViewGeometry: @unchecked Sendable {
    private let lock = NSLock()
    private var rect: CGRect?
    private var letterboxed = false
    private var minimized = false

    func set(_ rect: CGRect?, letterboxed: Bool, minimized: Bool) {
        lock.lock()
        defer { lock.unlock() }
        self.rect = rect
        self.letterboxed = letterboxed
        self.minimized = minimized
    }

    func get() -> (rect: CGRect?, letterboxed: Bool, minimized: Bool) {
        lock.lock()
        defer { lock.unlock() }
        return (rect, letterboxed, minimized)
    }
}

/// The capture card's picture: a layer-backed view holding an AVCaptureVideoPreviewLayer (aspect fit on black).
/// Reports a double-click and F11 / Esc through the callback, on the main thread. Keeps its on-screen picture rect in
/// `geometry`, refreshed on the main thread whenever it or its window moves or changes size.
@MainActor
private final class VideoView: NSView {
    let preview = AVCaptureVideoPreviewLayer()
    nonisolated let geometry = ViewGeometry()
    private let token: Int32
    private let callback: AvViewCallback
    private var windowObservers: [NSObjectProtocol] = []
    private(set) var destroyed = false

    init(token: Int32, callback: @escaping AvViewCallback) {
        self.token = token
        self.callback = callback
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = CGColor.black
        preview.videoGravity = .resizeAspect
        preview.backgroundColor = CGColor.black
        preview.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        preview.frame = bounds
        layer?.addSublayer(preview)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        if event.clickCount == 2 { callback(token, viewDoubleClicked) }
    }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case keyCodeF11: callback(token, viewToggleFullScreen)
        case keyCodeEscape: callback(token, viewLeaveFullScreen)
        default: super.keyDown(with: event)
        }
    }

    override func layout() {
        super.layout()
        preview.frame = bounds
        updateGeometry()
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        updateGeometry()
    }

    override func setFrameOrigin(_ newOrigin: NSPoint) {
        super.setFrameOrigin(newOrigin)
        updateGeometry()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        observeWindow()
        updateGeometry()
    }

    private func observeWindow() {
        for o in windowObservers { NotificationCenter.default.removeObserver(o) }
        windowObservers.removeAll()
        guard let window, !destroyed else { return }
        let names: [Notification.Name] = [
            NSWindow.didMoveNotification, NSWindow.didResizeNotification, NSWindow.didMiniaturizeNotification,
            NSWindow.didDeminiaturizeNotification, NSWindow.didChangeScreenNotification,
            NSWindow.didEnterFullScreenNotification, NSWindow.didExitFullScreenNotification,
        ]
        for name in names {
            windowObservers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) {
                [weak self] _ in
                MainActor.assumeIsolated { self?.updateGeometry() }
            })
        }
    }

    func attach(_ session: AVCaptureSession?) {
        guard !destroyed else { return }
        preview.session = session
        updateGeometry()
    }

    /// Recomputes `geometry`: the picture rect in Quartz global points (top-left of the primary display, Y down). The
    /// letterboxed picture when the layer can tell it (a session showing frames), else the whole view.
    func updateGeometry() {
        guard !destroyed, let window else {
            geometry.set(nil, letterboxed: false, minimized: false)
            return
        }
        var rect = bounds
        var letterboxed = false
        if preview.session?.isRunning == true {
            let picture = preview.layerRectConverted(fromMetadataOutputRect: CGRect(x: 0, y: 0, width: 1, height: 1))
            let inView = preview.convert(picture, to: layer).intersection(bounds)
            if inView.width.isFinite, inView.height.isFinite, inView.width >= 1, inView.height >= 1 {
                rect = inView
                letterboxed = true
            }
        }
        let onScreen = window.convertToScreen(convert(rect, to: nil))
        let primaryTop = NSScreen.screens.first?.frame.maxY ?? onScreen.maxY
        let quartz = CGRect(x: onScreen.minX, y: primaryTop - onScreen.maxY, width: onScreen.width, height: onScreen.height)
        geometry.set(quartz, letterboxed: letterboxed, minimized: window.isMiniaturized)
    }

    func destroy() {
        destroyed = true
        for o in windowObservers { NotificationCenter.default.removeObserver(o) }
        windowObservers.removeAll()
        preview.session = nil
        geometry.set(nil, letterboxed: false, minimized: false)
        removeFromSuperview()
    }
}

private func sessionOf(_ handle: UnsafeMutableRawPointer?) -> AVCaptureSession? {
    guard let handle else { return nil }
    return Unmanaged<VideoSession>.fromOpaque(handle).takeUnretainedValue().session
}

/// Creates the picture view (an NSView*, retained: free it with psrs_av_view_destroy) showing session `handle` (null:
/// black until psrs_av_view_attach). `callback(token, kind)` reports, on the main thread, 0 a double-click, 1 F11, 2 Esc.
/// Main thread only: null from any other thread.
@_cdecl("psrs_av_view_create")
public func psrs_av_view_create(_ handle: UnsafeMutableRawPointer?, _ token: Int32,
                                _ callback: AvViewCallback?) -> UnsafeMutableRawPointer? {
    guard let callback, Thread.isMainThread else { return nil }
    nonisolated(unsafe) let session = sessionOf(handle)
    let address = MainActor.assumeIsolated { () -> Int in
        let view = VideoView(token: token, callback: callback)
        view.attach(session)
        return Int(bitPattern: Unmanaged.passRetained(view).toOpaque())
    }
    return UnsafeMutableRawPointer(bitPattern: address)
}

/// Points the view at session `handle` (null: none, the view goes black). Any thread: the session is taken now, the
/// view changes on the main thread (at once on it, else queued). The handle must not be closed during this call.
@_cdecl("psrs_av_view_attach")
public func psrs_av_view_attach(_ view: UnsafeMutableRawPointer?, _ handle: UnsafeMutableRawPointer?) {
    guard let view else { return }
    let target = Unmanaged<VideoView>.fromOpaque(view).takeUnretainedValue()
    nonisolated(unsafe) let session = sessionOf(handle)
    if Thread.isMainThread {
        MainActor.assumeIsolated { target.attach(session) }
    } else {
        DispatchQueue.main.async { MainActor.assumeIsolated { target.attach(session) } }
    }
}

/// Detaches the view from its session, removes it from its superview and releases it (do not use it afterwards). On
/// the main thread at once, else queued to it.
@_cdecl("psrs_av_view_destroy")
public func psrs_av_view_destroy(_ view: UnsafeMutableRawPointer?) {
    guard let view else { return }
    let target = Unmanaged<VideoView>.fromOpaque(view).takeRetainedValue()
    if Thread.isMainThread {
        MainActor.assumeIsolated { target.destroy() }
    } else {
        DispatchQueue.main.async { MainActor.assumeIsolated { target.destroy() } }
    }
}

/// The picture's rect on screen in Quartz global points (top-left of the primary display, Y down; Avalonia's screen
/// space on macOS). outFlags: bit 0 the rect is the letterboxed picture (else the whole view), bit 1 the view's window
/// is minimised. 0 ok, 1 bad argument, 2 the view is in no window. Any thread: on the main thread it is measured now,
/// elsewhere the last measurement is returned (the view measures itself whenever it or its window moves or resizes).
/// The view must not be destroyed during this call.
@_cdecl("psrs_av_view_screen_rect")
public func psrs_av_view_screen_rect(
    _ view: UnsafeMutableRawPointer?, _ outX: UnsafeMutablePointer<Double>?, _ outY: UnsafeMutablePointer<Double>?,
    _ outW: UnsafeMutablePointer<Double>?, _ outH: UnsafeMutablePointer<Double>?, _ outFlags: UnsafeMutablePointer<Int32>?
) -> Int32 {
    guard let view, let outX, let outY, let outW, let outH, let outFlags else { return 1 }
    let target = Unmanaged<VideoView>.fromOpaque(view).takeUnretainedValue()
    if Thread.isMainThread { MainActor.assumeIsolated { target.updateGeometry() } }
    let measured = target.geometry.get()
    guard let rect = measured.rect else { return 2 }
    outX.pointee = rect.minX
    outY.pointee = rect.minY
    outW.pointee = rect.width
    outH.pointee = rect.height
    outFlags.pointee = (measured.letterboxed ? 1 : 0) | (measured.minimized ? 2 : 0)
    return 0
}

// MARK: - Audio

/// Plays one audio input on the default output through AVCaptureAudioPreviewOutput, set up and run on `queue`.
/// Reports `callback(owner, attempt, running)` whenever whether the session runs changes (it stops on an error, an
/// interruption or the device going away, and is not restarted here). The session is made only once the device is
/// found and access is granted, so an unknown id touches nothing. Nothing is reported after `close`.
private final class AudioSession: NSObject, @unchecked Sendable {
    private let deviceId: String
    private let owner: Int32, attempt: Int32
    private let callback: AvCallback
    private let queue = audioSessionQueue
    private let lock = NSLock()
    private let reportLock = NSLock()

    // Under lock.
    private var closed = false
    private var volume: Float
    private var running = false

    // On queue only.
    private var session: AVCaptureSession?
    private var preview: AVCaptureAudioPreviewOutput?
    private var observers: [NSObjectProtocol] = []
    private var interrupted = false

    init(deviceId: String, volume: Float, owner: Int32, attempt: Int32, callback: @escaping AvCallback) {
        self.deviceId = deviceId
        self.volume = volume
        self.owner = owner
        self.attempt = attempt
        self.callback = callback
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    func start() { queue.async { self.run() } }

    func setVolume(_ value: Float) {
        locked { volume = value }
        queue.async { self.preview?.volume = self.locked { self.volume } }
    }

    func close() {
        locked { closed = true }
        queue.async { self.tearDown() }
    }

    private func report(_ isRunning: Bool) {
        reportLock.lock()
        defer { reportLock.unlock() }
        let send = locked { () -> Bool in
            guard !closed, running != isRunning else { return false }
            running = isRunning
            return true
        }
        if send { callback(owner, attempt, isRunning ? 1 : 0) }
    }

    private func run() {
        guard !locked({ closed }) else { return }
        guard let device = AVCaptureDevice(uniqueID: deviceId), device.hasMediaType(.audio) else { return }
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .authorized else { return }
        guard let input = try? AVCaptureDeviceInput(device: device) else { return }
        let session = AVCaptureSession()
        let preview = AVCaptureAudioPreviewOutput()
        preview.volume = locked { volume }
        session.beginConfiguration()
        guard session.canAddInput(input), session.canAddOutput(preview) else {
            session.commitConfiguration()
            return
        }
        session.addInput(input)
        session.addOutput(preview)
        session.commitConfiguration()
        self.session = session
        self.preview = preview

        let center = NotificationCenter.default
        let watch = { [weak self] (name: Notification.Name, object: Any, interrupted: Bool?) in
            guard let self else { return }
            self.observers.append(center.addObserver(forName: name, object: object, queue: nil) { [weak self] _ in
                guard let self else { return }
                self.queue.async {
                    if let interrupted { self.interrupted = interrupted }
                    self.reportState()
                }
            })
        }
        watch(AVCaptureSession.didStartRunningNotification, session, nil)
        watch(AVCaptureSession.didStopRunningNotification, session, nil)
        watch(AVCaptureSession.runtimeErrorNotification, session, nil)
        watch(AVCaptureSession.wasInterruptedNotification, session, true)
        watch(AVCaptureSession.interruptionEndedNotification, session, false)
        observers.append(center.addObserver(forName: AVCaptureDevice.wasDisconnectedNotification, object: device, queue: nil) {
            [weak self] _ in
            guard let self else { return }
            self.queue.async {
                if self.session?.isRunning == true { self.session?.stopRunning() }
                self.report(false)
            }
        })
        guard !locked({ closed }) else { return }
        session.startRunning()
        reportState()
    }

    /// On queue.
    private func reportState() {
        report(session?.isRunning == true && !interrupted)
    }

    private func tearDown() {
        for o in observers { NotificationCenter.default.removeObserver(o) }
        observers.removeAll()
        if let session {
            if session.isRunning { session.stopRunning() }
            session.beginConfiguration()
            for input in session.inputs { session.removeInput(input) }
            for out in session.outputs { session.removeOutput(out) }
            session.commitConfiguration()
        }
        session = nil
        preview = nil
    }
}

private func audioVolume(_ volume: Double, _ muted: Int32) -> Float {
    muted != 0 || !volume.isFinite ? 0 : Float(min(1, max(0, volume)))
}

/// Starts playing audio input `id` (an AVCaptureDevice uniqueID) on the default output at `volume` (0 to 1; 0 when
/// `muted` is non-zero). Returns at once with a handle for psrs_av_audio_set / psrs_av_audio_stop (null only for a bad
/// argument); reports `callback(owner, attempt, running)` from any thread whenever playing starts or stops. An unknown
/// id, or no microphone access, simply never runs. Never asks for microphone access.
@_cdecl("psrs_av_audio_start")
public func psrs_av_audio_start(_ id: UnsafePointer<CChar>?, _ volume: Double, _ muted: Int32,
                                _ owner: Int32, _ attempt: Int32, _ callback: AvCallback?) -> UnsafeMutableRawPointer? {
    guard let id, let callback else { return nil }
    let audio = AudioSession(deviceId: String(cString: id), volume: audioVolume(volume, muted),
                             owner: owner, attempt: attempt, callback: callback)
    audio.start()
    return Unmanaged.passRetained(audio).toOpaque()
}

/// Sets the volume (0 to 1; 0 when `muted` is non-zero) of a playing audio input. Any thread.
@_cdecl("psrs_av_audio_set")
public func psrs_av_audio_set(_ handle: UnsafeMutableRawPointer?, _ volume: Double, _ muted: Int32) {
    guard let handle else { return }
    Unmanaged<AudioSession>.fromOpaque(handle).takeUnretainedValue().setVolume(audioVolume(volume, muted))
}

/// Stops playing and frees the handle (do not use it afterwards). Returns at once; nothing is reported afterwards.
@_cdecl("psrs_av_audio_stop")
public func psrs_av_audio_stop(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    Unmanaged<AudioSession>.fromOpaque(handle).takeRetainedValue().close()
}
