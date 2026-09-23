// In WebRTC's own package on purpose: the pieces this needs - HardwareVideoEncoder's constructor,
// MediaCodecUtils, VideoCodecMimeType - are package-private in the library, and there is no
// public way to ask it for a software H.264 encoder. See the class comment for why one is wanted.
package org.webrtc

import android.media.MediaCodecInfo
import android.media.MediaCodecList

// The H.264 encoder Sentinel Core's device camera offers, and nothing else.
//
// H.264 only, because everything downstream of the server's go2rtc sidecar that writes a clip
// does so by copying the stream into MP4 - VP8 or VP9 would stream fine and record nothing.
// Hardware when the phone has it (what HardwareVideoEncoderFactory already does), and when it
// does not - a MediaTek phone, the Android emulator - the platform's software AVC encoder
// (c2.android.avc.encoder) driven through the same HardwareVideoEncoder class, which is really
// "a MediaCodec-backed encoder" and works for either. The library refuses the software one only
// by policy, and a CPU-hungry stream beats no stream from a phone somebody set up as a camera.
class SentinelH264EncoderFactory(eglContext: EglBase.Context?) : VideoEncoderFactory {
    private val hardware = HardwareVideoEncoderFactory(eglContext, false, true)
    private val sharedContext = eglContext as? EglBase14.Context
    private val type = VideoCodecMimeType.H264
    private val hardwareCodecs: List<VideoCodecInfo> =
        hardware.supportedCodecs.filter { it.name.equals("H264", ignoreCase = true) }
    private val softwareCodec: MediaCodecInfo? = if (hardwareCodecs.isEmpty()) findSoftwareEncoder() else null

    val hasEncoder: Boolean get() = hardwareCodecs.isNotEmpty() || softwareCodec != null
    val usesSoftwareEncoder: Boolean get() = hardwareCodecs.isEmpty() && softwareCodec != null

    override fun getSupportedCodecs(): Array<VideoCodecInfo> {
        if (hardwareCodecs.isNotEmpty()) return hardwareCodecs.toTypedArray()
        if (softwareCodec == null) return emptyArray()
        // Constrained Baseline, packetization-mode 1: what the software encoder produces and what
        // go2rtc, and the browsers publishing alongside this, negotiate anyway.
        return arrayOf(VideoCodecInfo("H264", MediaCodecUtils.getCodecProperties(type, false), emptyList()))
    }

    override fun createEncoder(info: VideoCodecInfo): VideoEncoder? {
        if (!info.name.equals("H264", ignoreCase = true)) return null
        if (hardwareCodecs.isNotEmpty()) return hardware.createEncoder(info)
        val codec = softwareCodec ?: return null
        val capabilities = codec.getCapabilitiesForType(type.mimeType())
        val surfaceColorFormat = MediaCodecUtils.selectColorFormat(MediaCodecUtils.TEXTURE_COLOR_FORMATS, capabilities)
        val yuvColorFormat = MediaCodecUtils.selectColorFormat(MediaCodecUtils.ENCODER_COLOR_FORMATS, capabilities)
        if (surfaceColorFormat == null && yuvColorFormat == null) return null
        return HardwareVideoEncoder(
            MediaCodecWrapperFactoryImpl(),
            codec.name,
            type,
            surfaceColorFormat,
            yuvColorFormat,
            info.params,
            KEY_FRAME_INTERVAL_SECONDS,
            0,
            BaseBitrateAdjuster(),
            sharedContext,
        )
    }

    private fun findSoftwareEncoder(): MediaCodecInfo? =
        MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos.firstOrNull { info ->
            info.isEncoder && MediaCodecUtils.codecSupportsType(info, type) &&
                runCatching { info.getCapabilitiesForType(type.mimeType()) }.isSuccess
        }

    private companion object {
        // Matches the library's own periodic keyframe interval; go2rtc asks for one when a new
        // consumer needs it regardless.
        const val KEY_FRAME_INTERVAL_SECONDS = 20
    }
}
