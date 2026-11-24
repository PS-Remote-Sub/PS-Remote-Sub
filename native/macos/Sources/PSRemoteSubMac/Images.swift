import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Frees a buffer this library allocated (JSON from OCR, pixels from psrs_png_load).
@_cdecl("psrs_free")
public func psrs_free(_ ptr: UnsafeMutableRawPointer?) {
    free(ptr)
}

private let bgraInfo = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue

/// Wraps tightly packed BGRA8 (premultiplied) pixels as a CGImage (copies them).
func makeCGImage(bgra: UnsafePointer<UInt8>, width: Int, height: Int) -> CGImage? {
    guard width > 0, height > 0 else { return nil }
    let data = Data(bytes: bgra, count: width * height * 4)
    guard let provider = CGDataProvider(data: data as CFData) else { return nil }
    return CGImage(
        width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: bgraInfo),
        provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
}

/// Writes BGRA8 premultiplied pixels as a PNG. 0 ok, 1 bad argument, 2 write failed.
@_cdecl("psrs_png_save")
public func psrs_png_save(_ bgra: UnsafePointer<UInt8>?, _ width: Int32, _ height: Int32, _ path: UnsafePointer<CChar>?) -> Int32 {
    guard let bgra, let path, let image = makeCGImage(bgra: bgra, width: Int(width), height: Int(height)) else { return 1 }
    let url = URL(fileURLWithPath: String(cString: path))
    guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else { return 2 }
    CGImageDestinationAddImage(dest, image, nil)
    return CGImageDestinationFinalize(dest) ? 0 : 2
}

/// Decodes an image file to BGRA8 premultiplied. 0 ok (free outBgra with psrs_free), 1 bad argument, 2 unreadable.
@_cdecl("psrs_png_load")
public func psrs_png_load(
    _ path: UnsafePointer<CChar>?, _ outBgra: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>?,
    _ outW: UnsafeMutablePointer<Int32>?, _ outH: UnsafeMutablePointer<Int32>?
) -> Int32 {
    guard let path, let outBgra, let outW, let outH else { return 1 }
    let url = URL(fileURLWithPath: String(cString: path))
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return 2 }
    let w = image.width, h = image.height
    guard w > 0, h > 0, let buf = calloc(w * h, 4)?.assumingMemoryBound(to: UInt8.self) else { return 2 }
    guard let ctx = CGContext(
        data: buf, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: bgraInfo) else { free(buf); return 2 }
    ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
    outBgra.pointee = buf
    outW.pointee = Int32(w)
    outH.pointee = Int32(h)
    return 0
}
