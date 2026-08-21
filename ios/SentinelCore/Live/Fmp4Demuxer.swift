import CoreMedia
import Foundation

// Turns the fragmented-MP4 byte stream go2rtc sends over the MSE WebSocket into CMSampleBuffers a
// AVSampleBufferDisplayLayer (or audio renderer) can be handed directly.
//
// Why this exists at all: the browsers hand fMP4 to MediaSource and are done, but there is no
// MediaSource on iOS/tvOS/macOS, and AVPlayer only speaks HLS - which is what put every Apple
// client on the slow rung of the live ladder while the dashboard sat a few hundred milliseconds
// behind the camera. Parsing the same bytes ourselves is what closes that gap: the samples inside
// an fMP4 fragment are already in exactly the length-prefixed (AVCC) layout a CMSampleBuffer wants,
// so nothing here re-packages video - it slices the mdat and describes what it sliced.
//
// go2rtc emits one moof+mdat per incoming RTP packet rather than one per keyframe interval, so a
// fragment is a frame, not a GOP, and the transport imposes no latency of its own. The demuxer is
// written to that grain but doesn't depend on it: multiple trun entries and multiple trafs per
// fragment are handled.
//
// Deliberately not a general MP4 reader. It handles what a live fMP4 stream contains - ftyp, moov
// with avc1/hvc1/mp4a sample entries, then moof/mdat forever - and skips everything else. There is
// no seeking, no edit lists, no fallback to the sample tables in stbl, because a stream that never
// ends has none of that.
enum Fmp4TrackKind {
    case video
    case audio
}

struct Fmp4Sample {
    let kind: Fmp4TrackKind
    let buffer: CMSampleBuffer
}

enum Fmp4Error: LocalizedError {
    case malformed(String)
    case unsupportedCodec(String)

    var errorDescription: String? {
        switch self {
        case .malformed(let what): "the stream's \(what) could not be read"
        case .unsupportedCodec(let name): "the stream uses \(name), which this app can't decode"
        }
    }
}

final class Fmp4Demuxer {
    // What the init segment said about one track. `formatDescription` is nil for a track whose
    // sample entry we couldn't turn into something CoreMedia understands - its samples are then
    // dropped rather than failing the whole stream, so an unusable audio track still leaves video
    // playing.
    private struct Track {
        let kind: Fmp4TrackKind
        let timescale: CMTimeScale
        let formatDescription: CMFormatDescription?
    }

    // One trun entry, resolved to absolute positions in `buffer` so the mdat that follows can be
    // sliced without re-walking the moof.
    private struct PendingSample {
        let kind: Fmp4TrackKind
        let format: CMFormatDescription?
        let timescale: CMTimeScale
        let offset: Int
        let size: Int
        let decodeTime: Int64
        let presentationTime: Int64
        let duration: Int64
        let isSync: Bool
    }

    private(set) var hasVideoTrack = false
    private(set) var hasAudioTrack = false

    private var buffer = [UInt8]()
    private var tracks: [UInt32: Track] = [:]
    // Set while a moof has been read and its mdat hasn't arrived yet. Nothing may be dropped off
    // the front of `buffer` while this is set - the offsets inside it are indexes into it.
    private var pending: [PendingSample]?
    // How much of `buffer` has already been walked. Normally zero, because a completed pass drops
    // what it consumed - but a fragment whose mdat hasn't arrived pins the buffer in place, and
    // restarting the walk from zero would re-emit every sample already delivered from it.
    private var scanned = 0

    // Feed one WebSocket binary message. Returns every sample that completed as a result, which is
    // usually one video frame and occasionally none (an init segment, or a fragment split across
    // two messages).
    func append(_ data: Data) throws -> [Fmp4Sample] {
        buffer.append(contentsOf: data)

        var samples: [Fmp4Sample] = []
        var cursor = scanned

        while let box = readBoxHeader(at: cursor) {
            switch box.type {
            case "moov":
                try parseMoov(box.payload)
            case "moof":
                pending = try parseMoof(box, boxStart: cursor)
            case "mdat":
                if let pending {
                    samples.append(contentsOf: makeSamples(pending, mdat: box.payload))
                    self.pending = nil
                }
            default:
                break // ftyp, styp, sidx, free - nothing here needs them
            }
            cursor = box.end
        }

        // Only safe once no fragment is half-read: `pending` holds indexes into `buffer`.
        if pending == nil {
            if cursor > 0 { buffer.removeFirst(cursor) }
            scanned = 0
        } else {
            scanned = cursor
        }
        return samples
    }

    // MARK: - Box walking

    private struct Box {
        let type: String
        let payload: Range<Int>
        let end: Int
    }

    // nil means "not all here yet" - the caller stops and waits for more bytes.
    private func readBoxHeader(at offset: Int) -> Box? {
        guard offset + 8 <= buffer.count else { return nil }
        var size = Int(be32(offset))
        var headerSize = 8
        let type = String(decoding: buffer[(offset + 4)..<(offset + 8)], as: UTF8.self)

        if size == 1 {
            guard offset + 16 <= buffer.count else { return nil }
            size = Int(be64(offset + 8))
            headerSize = 16
        } else if size == 0 {
            // "to end of file" - meaningless in a stream that never ends, and go2rtc never sends
            // it. Treating it as incomplete parks the parser instead of mis-slicing the stream.
            return nil
        }

        guard size >= headerSize, offset + size <= buffer.count else { return nil }
        return Box(type: type, payload: (offset + headerSize)..<(offset + size), end: offset + size)
    }

    private func children(of range: Range<Int>) -> [Box] {
        var boxes: [Box] = []
        var cursor = range.lowerBound
        while cursor + 8 <= range.upperBound {
            var size = Int(be32(cursor))
            var headerSize = 8
            let type = String(decoding: buffer[(cursor + 4)..<(cursor + 8)], as: UTF8.self)
            if size == 1 {
                guard cursor + 16 <= range.upperBound else { break }
                size = Int(be64(cursor + 8))
                headerSize = 16
            }
            guard size >= headerSize, cursor + size <= range.upperBound else { break }
            boxes.append(Box(type: type, payload: (cursor + headerSize)..<(cursor + size), end: cursor + size))
            cursor += size
        }
        return boxes
    }

    private func child(_ type: String, of range: Range<Int>) -> Box? {
        children(of: range).first { $0.type == type }
    }

    // Walks a slash-separated path of single children, e.g. "mdia/minf/stbl/stsd".
    private func descend(_ path: String, from range: Range<Int>) -> Box? {
        var current = range
        var found: Box?
        for name in path.split(separator: "/") {
            guard let next = child(String(name), of: current) else { return nil }
            found = next
            current = next.payload
        }
        return found
    }

    // MARK: - Init segment

    private func parseMoov(_ range: Range<Int>) throws {
        tracks.removeAll()
        hasVideoTrack = false
        hasAudioTrack = false

        for trak in children(of: range) where trak.type == "trak" {
            guard let tkhd = child("tkhd", of: trak.payload),
                  let mdhd = descend("mdia/mdhd", from: trak.payload),
                  let hdlr = descend("mdia/hdlr", from: trak.payload),
                  let stsd = descend("mdia/minf/stbl/stsd", from: trak.payload) else { continue }

            let trackID = try readTrackID(tkhd.payload)
            let timescale = try readTimescale(mdhd.payload)
            // hdlr: version/flags(4) + pre_defined(4) + handler_type(4)
            guard hdlr.payload.lowerBound + 12 <= hdlr.payload.upperBound else { continue }
            let handler = String(
                decoding: buffer[(hdlr.payload.lowerBound + 8)..<(hdlr.payload.lowerBound + 12)], as: UTF8.self)

            let kind: Fmp4TrackKind
            switch handler {
            case "vide": kind = .video
            case "soun": kind = .audio
            default: continue // hint, meta, subtitles - nothing to render
            }

            // stsd: version/flags(4) + entry_count(4), then the sample entries.
            let entries = children(of: (stsd.payload.lowerBound + 8)..<stsd.payload.upperBound)
            guard let entry = entries.first else { continue }

            let format = kind == .video
                ? try makeVideoFormat(entry)
                : makeAudioFormat(entry)

            tracks[trackID] = Track(kind: kind, timescale: timescale, formatDescription: format)
            if kind == .video, format != nil { hasVideoTrack = true }
            if kind == .audio, format != nil { hasAudioTrack = true }
        }

        guard hasVideoTrack else {
            throw Fmp4Error.malformed("initialization segment (no decodable video track)")
        }
    }

    private func readTrackID(_ payload: Range<Int>) throws -> UInt32 {
        let version = buffer[payload.lowerBound]
        // version/flags(4) + creation(4|8) + modification(4|8), then track_ID.
        let offset = payload.lowerBound + 4 + (version == 1 ? 16 : 8)
        guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("tkhd") }
        return be32(offset)
    }

    private func readTimescale(_ payload: Range<Int>) throws -> CMTimeScale {
        let version = buffer[payload.lowerBound]
        let offset = payload.lowerBound + 4 + (version == 1 ? 16 : 8)
        guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("mdhd") }
        let timescale = be32(offset)
        guard timescale > 0, timescale <= UInt32(Int32.max) else { throw Fmp4Error.malformed("mdhd timescale") }
        return CMTimeScale(timescale)
    }

    // MARK: - Video format

    // A VisualSampleEntry is a fixed 78-byte preamble and then child boxes, one of which is the
    // decoder configuration record we actually want.
    private static let visualSampleEntryHeader = 78

    private func makeVideoFormat(_ entry: Box) throws -> CMFormatDescription? {
        let childRange = (entry.payload.lowerBound + Self.visualSampleEntryHeader)..<entry.payload.upperBound
        guard childRange.lowerBound <= childRange.upperBound else { throw Fmp4Error.malformed("sample entry") }

        switch entry.type {
        case "avc1", "avc3":
            guard let avcC = child("avcC", of: childRange) else { throw Fmp4Error.malformed("avcC") }
            return try makeH264Format(avcC.payload)
        case "hvc1", "hev1":
            guard let hvcC = child("hvcC", of: childRange) else { throw Fmp4Error.malformed("hvcC") }
            return try makeHevcFormat(hvcC.payload)
        default:
            throw Fmp4Error.unsupportedCodec(entry.type)
        }
    }

    // AVCDecoderConfigurationRecord: 5 fixed bytes (the last carrying the NAL length size), then
    // the SPS list, then the PPS list, each length-prefixed.
    private func makeH264Format(_ payload: Range<Int>) throws -> CMFormatDescription {
        var offset = payload.lowerBound
        guard offset + 6 <= payload.upperBound else { throw Fmp4Error.malformed("avcC") }
        let nalLengthSize = Int32(buffer[offset + 4] & 0x03) + 1
        let spsCount = Int(buffer[offset + 5] & 0x1F)
        offset += 6

        var parameterSets: [[UInt8]] = []
        for _ in 0..<spsCount {
            guard let set = readLengthPrefixed(&offset, limit: payload.upperBound) else {
                throw Fmp4Error.malformed("avcC SPS")
            }
            parameterSets.append(set)
        }
        guard offset < payload.upperBound else { throw Fmp4Error.malformed("avcC PPS count") }
        let ppsCount = Int(buffer[offset])
        offset += 1
        for _ in 0..<ppsCount {
            guard let set = readLengthPrefixed(&offset, limit: payload.upperBound) else {
                throw Fmp4Error.malformed("avcC PPS")
            }
            parameterSets.append(set)
        }
        guard parameterSets.count >= 2 else { throw Fmp4Error.malformed("avcC parameter sets") }

        return try makeFormat(parameterSets: parameterSets, nalLengthSize: nalLengthSize, isHevc: false)
    }

    // HEVCDecoderConfigurationRecord: 22 fixed bytes, then an array of NAL-unit arrays (VPS, SPS,
    // PPS, and often an SEI array that CoreMedia has no use for).
    private func makeHevcFormat(_ payload: Range<Int>) throws -> CMFormatDescription {
        var offset = payload.lowerBound
        guard offset + 23 <= payload.upperBound else { throw Fmp4Error.malformed("hvcC") }
        let nalLengthSize = Int32(buffer[offset + 21] & 0x03) + 1
        let arrayCount = Int(buffer[offset + 22])
        offset += 23

        var parameterSets: [[UInt8]] = []
        for _ in 0..<arrayCount {
            guard offset + 3 <= payload.upperBound else { throw Fmp4Error.malformed("hvcC array") }
            let nalType = buffer[offset] & 0x3F
            let nalCount = Int(be16(offset + 1))
            offset += 3
            for _ in 0..<nalCount {
                guard let set = readLengthPrefixed(&offset, limit: payload.upperBound) else {
                    throw Fmp4Error.malformed("hvcC NAL")
                }
                // 32/33/34 are VPS/SPS/PPS; the prefix/suffix SEI arrays are not parameter sets
                // and CMVideoFormatDescription rejects the record if they are passed as such.
                if nalType == 32 || nalType == 33 || nalType == 34 { parameterSets.append(set) }
            }
        }
        guard parameterSets.count >= 3 else { throw Fmp4Error.malformed("hvcC parameter sets") }

        return try makeFormat(parameterSets: parameterSets, nalLengthSize: nalLengthSize, isHevc: true)
    }

    private func makeFormat(
        parameterSets: [[UInt8]], nalLengthSize: Int32, isHevc: Bool
    ) throws -> CMFormatDescription {
        var pointers: [UnsafePointer<UInt8>] = []
        var sizes: [Int] = []
        // The parameter sets have to stay alive across the create call, so the pointers are taken
        // from allocations this function owns rather than from the (escaping) array elements.
        var allocations: [UnsafeMutablePointer<UInt8>] = []
        defer { for allocation in allocations { allocation.deallocate() } }

        for set in parameterSets {
            let allocation = UnsafeMutablePointer<UInt8>.allocate(capacity: set.count)
            allocation.update(from: set, count: set.count)
            allocations.append(allocation)
            pointers.append(UnsafePointer(allocation))
            sizes.append(set.count)
        }

        var format: CMFormatDescription?
        let status = pointers.withUnsafeBufferPointer { pointerBuffer in
            sizes.withUnsafeBufferPointer { sizeBuffer in
                isHevc
                    ? CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                        allocator: kCFAllocatorDefault,
                        parameterSetCount: pointerBuffer.count,
                        parameterSetPointers: pointerBuffer.baseAddress!,
                        parameterSetSizes: sizeBuffer.baseAddress!,
                        nalUnitHeaderLength: nalLengthSize,
                        extensions: nil,
                        formatDescriptionOut: &format)
                    : CMVideoFormatDescriptionCreateFromH264ParameterSets(
                        allocator: kCFAllocatorDefault,
                        parameterSetCount: pointerBuffer.count,
                        parameterSetPointers: pointerBuffer.baseAddress!,
                        parameterSetSizes: sizeBuffer.baseAddress!,
                        nalUnitHeaderLength: nalLengthSize,
                        formatDescriptionOut: &format)
            }
        }

        guard status == noErr, let format else {
            throw Fmp4Error.malformed("decoder configuration (OSStatus \(status))")
        }
        return format
    }

    private func readLengthPrefixed(_ offset: inout Int, limit: Int) -> [UInt8]? {
        guard offset + 2 <= limit else { return nil }
        let length = Int(be16(offset))
        guard offset + 2 + length <= limit, length > 0 else { return nil }
        let bytes = Array(buffer[(offset + 2)..<(offset + 2 + length)])
        offset += 2 + length
        return bytes
    }

    // MARK: - Audio format

    // An AudioSampleEntry is a fixed 28-byte preamble and then child boxes - `esds` for AAC.
    private static let audioSampleEntryHeader = 28

    // Returns nil rather than throwing: a stream whose audio we can't describe should still show
    // video. The caller never learns about the audio track, so nothing tries to render it.
    private func makeAudioFormat(_ entry: Box) -> CMFormatDescription? {
        guard entry.type == "mp4a" else { return nil }
        let childRange = (entry.payload.lowerBound + Self.audioSampleEntryHeader)..<entry.payload.upperBound
        guard childRange.lowerBound <= childRange.upperBound,
              let esds = child("esds", of: childRange) else { return nil }

        // esds: version/flags(4), then the ES_Descriptor - which is also exactly the magic cookie
        // AudioToolbox wants for MPEG-4 AAC.
        let cookieRange = (esds.payload.lowerBound + 4)..<esds.payload.upperBound
        guard cookieRange.lowerBound < cookieRange.upperBound else { return nil }
        let cookie = Array(buffer[cookieRange])

        guard let config = audioSpecificConfig(in: cookieRange),
              let (sampleRate, channels) = decodeAudioSpecificConfig(config) else { return nil }

        var asbd = AudioStreamBasicDescription(
            mSampleRate: sampleRate,
            mFormatID: kAudioFormatMPEG4AAC,
            mFormatFlags: 0,
            mBytesPerPacket: 0,
            mFramesPerPacket: 1024,
            mBytesPerFrame: 0,
            mChannelsPerFrame: channels,
            mBitsPerChannel: 0,
            mReserved: 0)

        var format: CMFormatDescription?
        let status = cookie.withUnsafeBytes { cookieBytes in
            CMAudioFormatDescriptionCreate(
                allocator: kCFAllocatorDefault,
                asbd: &asbd,
                layoutSize: 0,
                layout: nil,
                magicCookieSize: cookieBytes.count,
                magicCookie: cookieBytes.baseAddress,
                extensions: nil,
                formatDescriptionOut: &format)
        }
        return status == noErr ? format : nil
    }

    // Walks the ES_Descriptor tree for the DecoderSpecificInfo (tag 0x05), which holds the
    // AudioSpecificConfig. Descriptor lengths are the MPEG-4 variable-length kind: seven bits per
    // byte, high bit meaning "another byte follows".
    private func audioSpecificConfig(in range: Range<Int>) -> [UInt8]? {
        var offset = range.lowerBound
        while offset + 2 <= range.upperBound {
            let tag = buffer[offset]
            offset += 1
            var length = 0
            var lengthBytes = 0
            while offset < range.upperBound, lengthBytes < 4 {
                let byte = buffer[offset]
                offset += 1
                lengthBytes += 1
                length = (length << 7) | Int(byte & 0x7F)
                if byte & 0x80 == 0 { break }
            }
            guard offset + length <= range.upperBound else { return nil }

            switch tag {
            case 0x03: // ES_Descriptor: ES_ID(2) + flags(1), then nested descriptors
                guard offset + 3 <= range.upperBound else { return nil }
                let flags = buffer[offset + 2]
                var inner = offset + 3
                if flags & 0x80 != 0 { inner += 2 } // stream dependence
                if flags & 0x40 != 0 { // URL
                    guard inner < range.upperBound else { return nil }
                    inner += 1 + Int(buffer[inner])
                }
                if flags & 0x20 != 0 { inner += 2 } // OCR stream
                offset = inner
            case 0x04: // DecoderConfigDescriptor: 13 fixed bytes, then nested descriptors
                offset += 13
            case 0x05:
                return Array(buffer[offset..<(offset + length)])
            default:
                offset += length
            }
        }
        return nil
    }

    private static let samplingFrequencies: [Double] = [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350
    ]

    private func decodeAudioSpecificConfig(_ config: [UInt8]) -> (Double, UInt32)? {
        guard config.count >= 2 else { return nil }
        let bits = (UInt32(config[0]) << 8) | UInt32(config[1])
        let frequencyIndex = Int((bits >> 7) & 0x0F)
        let channelConfiguration = UInt32((bits >> 3) & 0x0F)

        let sampleRate: Double
        if frequencyIndex == 0x0F {
            // Explicit 24-bit rate, which needs a third byte and part of a fourth.
            guard config.count >= 5 else { return nil }
            let explicit = (UInt32(config[1] & 0x7F) << 17)
                | (UInt32(config[2]) << 9)
                | (UInt32(config[3]) << 1)
                | (UInt32(config[4]) >> 7)
            sampleRate = Double(explicit)
        } else {
            guard frequencyIndex < Self.samplingFrequencies.count else { return nil }
            sampleRate = Self.samplingFrequencies[frequencyIndex]
        }

        guard sampleRate > 0, channelConfiguration > 0, channelConfiguration <= 8 else { return nil }
        return (sampleRate, channelConfiguration)
    }

    // MARK: - Fragments

    private func parseMoof(_ moof: Box, boxStart: Int) throws -> [PendingSample] {
        var samples: [PendingSample] = []

        for traf in children(of: moof.payload) where traf.type == "traf" {
            guard let tfhd = child("tfhd", of: traf.payload) else { continue }
            let header = try parseTfhd(tfhd.payload)
            guard let track = tracks[header.trackID] else { continue }

            var decodeTime = Int64(0)
            if let tfdt = child("tfdt", of: traf.payload) {
                decodeTime = try parseTfdt(tfdt.payload)
            }

            for trun in children(of: traf.payload) where trun.type == "trun" {
                let entries = try parseTrun(trun.payload, defaults: header)
                // default-base-is-moof (0x020000) is what every fMP4 muxer in practice sets, and
                // the alternative - an absolute base_data_offset - is meaningless in a stream with
                // no file to be absolute within. Anything unexpected is caught by the bounds check
                // in makeSamples, which falls back to laying the samples out from the mdat's start.
                var offset = boxStart + entries.dataOffset
                for entry in entries.samples {
                    samples.append(PendingSample(
                        kind: track.kind,
                        format: track.formatDescription,
                        timescale: track.timescale,
                        offset: offset,
                        size: entry.size,
                        decodeTime: decodeTime,
                        presentationTime: decodeTime + Int64(entry.compositionOffset),
                        duration: Int64(entry.duration),
                        isSync: entry.isSync))
                    offset += entry.size
                    decodeTime += Int64(entry.duration)
                }
            }
        }

        return samples
    }

    private struct TfhdDefaults {
        let trackID: UInt32
        let defaultDuration: UInt32
        let defaultSize: UInt32
        let defaultFlags: UInt32
    }

    private func parseTfhd(_ payload: Range<Int>) throws -> TfhdDefaults {
        guard payload.lowerBound + 8 <= payload.upperBound else { throw Fmp4Error.malformed("tfhd") }
        let flags = be32(payload.lowerBound) & 0x00FF_FFFF
        let trackID = be32(payload.lowerBound + 4)
        var offset = payload.lowerBound + 8

        if flags & 0x000001 != 0 { offset += 8 } // base_data_offset
        if flags & 0x000002 != 0 { offset += 4 } // sample_description_index

        var defaultDuration: UInt32 = 0
        var defaultSize: UInt32 = 0
        var defaultFlags: UInt32 = 0
        if flags & 0x000008 != 0 {
            guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("tfhd") }
            defaultDuration = be32(offset); offset += 4
        }
        if flags & 0x000010 != 0 {
            guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("tfhd") }
            defaultSize = be32(offset); offset += 4
        }
        if flags & 0x000020 != 0 {
            guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("tfhd") }
            defaultFlags = be32(offset)
        }

        return TfhdDefaults(
            trackID: trackID, defaultDuration: defaultDuration,
            defaultSize: defaultSize, defaultFlags: defaultFlags)
    }

    private func parseTfdt(_ payload: Range<Int>) throws -> Int64 {
        let version = buffer[payload.lowerBound]
        let offset = payload.lowerBound + 4
        if version == 1 {
            guard offset + 8 <= payload.upperBound else { throw Fmp4Error.malformed("tfdt") }
            return Int64(bitPattern: be64(offset))
        }
        guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("tfdt") }
        return Int64(be32(offset))
    }

    private struct TrunEntry {
        let size: Int
        let duration: UInt32
        let compositionOffset: Int32
        let isSync: Bool
    }

    private struct TrunResult {
        let dataOffset: Int
        let samples: [TrunEntry]
    }

    private func parseTrun(_ payload: Range<Int>, defaults: TfhdDefaults) throws -> TrunResult {
        guard payload.lowerBound + 8 <= payload.upperBound else { throw Fmp4Error.malformed("trun") }
        let version = buffer[payload.lowerBound]
        let flags = be32(payload.lowerBound) & 0x00FF_FFFF
        let count = Int(be32(payload.lowerBound + 4))
        var offset = payload.lowerBound + 8

        var dataOffset = 0
        if flags & 0x000001 != 0 {
            guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("trun") }
            dataOffset = Int(Int32(bitPattern: be32(offset))); offset += 4
        }
        var firstSampleFlags: UInt32?
        if flags & 0x000004 != 0 {
            guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("trun") }
            firstSampleFlags = be32(offset); offset += 4
        }

        var samples: [TrunEntry] = []
        samples.reserveCapacity(count)
        for index in 0..<count {
            var duration = defaults.defaultDuration
            var size = defaults.defaultSize
            var sampleFlags = index == 0 ? (firstSampleFlags ?? defaults.defaultFlags) : defaults.defaultFlags
            var compositionOffset: Int32 = 0

            if flags & 0x000100 != 0 {
                guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("trun") }
                duration = be32(offset); offset += 4
            }
            if flags & 0x000200 != 0 {
                guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("trun") }
                size = be32(offset); offset += 4
            }
            if flags & 0x000400 != 0 {
                guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("trun") }
                sampleFlags = be32(offset); offset += 4
                // An explicit per-sample flags field overrides first_sample_flags for sample 0.
            }
            if flags & 0x000800 != 0 {
                guard offset + 4 <= payload.upperBound else { throw Fmp4Error.malformed("trun") }
                // Signed from version 1 on; before that the field was unsigned, so a value that
                // doesn't fit in Int32 is a broken stream rather than a negative offset.
                let raw = be32(offset)
                compositionOffset = version == 0
                    ? Int32(exactly: raw) ?? 0
                    : Int32(bitPattern: raw)
                offset += 4
            }

            // Bit 16 of the sample flags is sample_is_non_sync_sample.
            samples.append(TrunEntry(
                size: Int(size),
                duration: duration,
                compositionOffset: compositionOffset,
                isSync: sampleFlags & 0x0001_0000 == 0))
        }

        return TrunResult(dataOffset: dataOffset, samples: samples)
    }

    private func makeSamples(_ pending: [PendingSample], mdat: Range<Int>) -> [Fmp4Sample] {
        // Every sample should already point inside this mdat. When a muxer surprises us - an
        // absolute base_data_offset, a data_offset relative to something else - the samples are
        // still contiguous and in order, so laying them out from the payload start recovers the
        // fragment instead of dropping it.
        let usesDeclaredOffsets = pending.allSatisfy { sample in
            sample.offset >= mdat.lowerBound && sample.offset + sample.size <= mdat.upperBound
        }

        var cursor = mdat.lowerBound
        var results: [Fmp4Sample] = []
        results.reserveCapacity(pending.count)

        for sample in pending {
            let start = usesDeclaredOffsets ? sample.offset : cursor
            cursor = start + sample.size
            guard let format = sample.format,
                  start >= mdat.lowerBound, start + sample.size <= mdat.upperBound,
                  sample.size > 0 else { continue }

            if let buffer = makeSampleBuffer(
                bytes: start..<(start + sample.size), sample: sample, format: format) {
                results.append(Fmp4Sample(kind: sample.kind, buffer: buffer))
            }
        }
        return results
    }

    private func makeSampleBuffer(
        bytes: Range<Int>, sample: PendingSample, format: CMFormatDescription
    ) -> CMSampleBuffer? {
        var blockBuffer: CMBlockBuffer?
        let length = bytes.count
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: length,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: length,
            flags: 0,
            blockBufferOut: &blockBuffer) == noErr, let blockBuffer else { return nil }

        let copied = buffer.withUnsafeBufferPointer { source in
            CMBlockBufferReplaceDataBytes(
                with: source.baseAddress! + bytes.lowerBound,
                blockBuffer: blockBuffer,
                offsetIntoDestination: 0,
                dataLength: length)
        }
        guard copied == noErr else { return nil }

        var timing = CMSampleTimingInfo(
            duration: CMTime(value: sample.duration, timescale: sample.timescale),
            presentationTimeStamp: CMTime(value: sample.presentationTime, timescale: sample.timescale),
            decodeTimeStamp: CMTime(value: sample.decodeTime, timescale: sample.timescale))
        var size = length
        var sampleBuffer: CMSampleBuffer?
        guard CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: format,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &size,
            sampleBufferOut: &sampleBuffer) == noErr, let sampleBuffer else { return nil }

        // Without this the display layer treats every frame as a place it could start decoding,
        // which is only true of keyframes.
        if !sample.isSync,
           let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true)
               as? [NSMutableDictionary],
           let first = attachments.first {
            first[kCMSampleAttachmentKey_NotSync as NSString] = true
        }

        return sampleBuffer
    }

    // MARK: - Big-endian reads

    private func be16(_ offset: Int) -> UInt16 {
        (UInt16(buffer[offset]) << 8) | UInt16(buffer[offset + 1])
    }

    private func be32(_ offset: Int) -> UInt32 {
        (UInt32(buffer[offset]) << 24) | (UInt32(buffer[offset + 1]) << 16)
            | (UInt32(buffer[offset + 2]) << 8) | UInt32(buffer[offset + 3])
    }

    private func be64(_ offset: Int) -> UInt64 {
        (UInt64(be32(offset)) << 32) | UInt64(be32(offset + 4))
    }
}
