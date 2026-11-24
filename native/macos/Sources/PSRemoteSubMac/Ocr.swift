import CoreGraphics
import Foundation
import Vision

private struct OcrWordDto: Encodable { let text: String; let x: Int; let y: Int; let w: Int; let h: Int }
private struct OcrLineDto: Encodable { let text: String; let x: Int; let y: Int; let w: Int; let h: Int; let words: [OcrWordDto] }

/// Vision's normalised, bottom-left box -> whole pixels, top-left origin (outward rounding, clamped to the image).
private func pixelBox(_ n: CGRect, _ width: Int, _ height: Int) -> (x: Int, y: Int, w: Int, h: Int) {
    let left = max(0, Int((n.minX * Double(width)).rounded(.down)))
    let right = min(width, Int((n.maxX * Double(width)).rounded(.up)))
    let top = max(0, Int(((1 - n.maxY) * Double(height)).rounded(.down)))
    let bottom = min(height, Int(((1 - n.minY) * Double(height)).rounded(.up)))
    return (left, top, max(0, right - left), max(0, bottom - top))
}

private func supported(_ lang: String) -> Bool {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    guard let langs = try? request.supportedRecognitionLanguages() else { return false }
    return langs.contains(lang)
}

/// 1 when Vision can recognise `lang` (e.g. "en-US", "zh-Hans"), else 0.
@_cdecl("psrs_ocr_supported")
public func psrs_ocr_supported(_ lang: UnsafePointer<CChar>?) -> Int32 {
    guard let lang else { return 0 }
    return supported(String(cString: lang)) ? 1 : 0
}

/// Recognises text in BGRA8 premultiplied pixels. 0 ok (free outJson with psrs_free), 1 bad argument,
/// 2 language unsupported, 3 recognition failed. JSON is `[{text,x,y,w,h,words:[{text,x,y,w,h}]}]` in pixels, top-left origin.
@_cdecl("psrs_ocr_recognize")
public func psrs_ocr_recognize(
    _ bgra: UnsafePointer<UInt8>?, _ width: Int32, _ height: Int32, _ lang: UnsafePointer<CChar>?,
    _ outJson: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?
) -> Int32 {
    guard let bgra, let lang, let outJson, width > 0, height > 0,
          let image = makeCGImage(bgra: bgra, width: Int(width), height: Int(height)) else { return 1 }
    let language = String(cString: lang)
    guard supported(language) else { return 2 }

    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.usesLanguageCorrection = true
    request.recognitionLanguages = [language]
    do { try VNImageRequestHandler(cgImage: image, options: [:]).perform([request]) } catch { return 3 }

    let w = Int(width), h = Int(height)
    var lines: [OcrLineDto] = []
    for obs in request.results ?? [] {
        guard let cand = obs.topCandidates(1).first else { continue }
        let text = cand.string
        var words: [OcrWordDto] = []
        var start = text.startIndex
        while start < text.endIndex {
            if text[start] == " " { start = text.index(after: start); continue }
            var end = start
            while end < text.endIndex, text[end] != " " { end = text.index(after: end) }
            if let box = try? cand.boundingBox(for: start..<end) {
                let b = pixelBox(box.boundingBox, w, h)
                words.append(OcrWordDto(text: String(text[start..<end]), x: b.x, y: b.y, w: b.w, h: b.h))
            }
            start = end
        }
        let b = pixelBox(obs.boundingBox, w, h)
        lines.append(OcrLineDto(text: text, x: b.x, y: b.y, w: b.w, h: b.h, words: words))
    }
    // Vision does not promise an order: top to bottom.
    lines.sort { $0.y < $1.y }

    guard let data = try? JSONEncoder().encode(lines), let json = String(data: data, encoding: .utf8),
          let dup = strdup(json) else { return 3 }
    outJson.pointee = dup
    return 0
}
