"use strict";

// Live video for one HomeKit camera accessory.
//
// HomeKit doesn't pull a stream the way a browser does. When someone opens a camera in Home, the
// iPhone (or the home hub, when they're away) tells us where to send video and which keys to
// encrypt it with, and we push SRTP at that address until it says stop. This file is the
// CameraStreamingDelegate that answers those requests: it asks the dashboard for a loopback RTSP
// address from go2rtc and runs one ffmpeg per viewer that re-encodes to whatever size, frame
// rate and bitrate the viewer asked for and sends it as SRTP. That re-encode is the same thing
// homebridge-camera-ffmpeg does, and it's needed: Home asks for specific H.264 profiles and
// sizes, and the cameras' own streams are whatever the camera felt like.

const dgram = require("node:dgram");
const { spawn } = require("node:child_process");

const {
  AudioStreamingCodecType,
  AudioStreamingSamplerate,
  CameraController,
  H264Level,
  H264Profile,
  SRTPCryptoSuites,
} = require("@homebridge/hap-nodejs");

const FFMPEG_PATH = process.env.HOMEKIT_FFMPEG_PATH || "ffmpeg";

// Home picks from this list. Requests at or below SUB_STREAM_MAX_WIDTH (the Home tiles, Control
// Center, the Watch) are made from the camera's low-res sub stream, which costs a fraction of the
// decode that the full-resolution main stream does.
const RESOLUTIONS = [
  [1920, 1080, 30], [1600, 1200, 30], [1280, 960, 30], [1280, 720, 30], [1024, 768, 30],
  [640, 480, 30], [640, 360, 30], [480, 360, 30], [480, 270, 30], [320, 240, 15], [320, 180, 30],
];
const SUB_STREAM_MAX_WIDTH = 640;

// An iPhone that walks out of Wi-Fi or is locked mid-view never sends STOP. It does stop sending
// RTCP receiver reports, so a viewer that has gone quiet this long is treated as gone and its
// ffmpeg is stopped rather than left encoding for nobody.
const VIEWER_SILENCE_MS = 30_000;

function streamingOptions() {
  return {
    supportedCryptoSuites: [SRTPCryptoSuites.AES_CM_128_HMAC_SHA1_80],
    video: {
      resolutions: RESOLUTIONS,
      codec: {
        profiles: [H264Profile.BASELINE, H264Profile.MAIN, H264Profile.HIGH],
        levels: [H264Level.LEVEL3_1, H264Level.LEVEL3_2, H264Level.LEVEL4_0],
      },
    },
    audio: {
      twoWayAudio: false,
      codecs: [
        { type: AudioStreamingCodecType.OPUS, samplerate: AudioStreamingSamplerate.KHZ_24 },
        { type: AudioStreamingCodecType.OPUS, samplerate: AudioStreamingSamplerate.KHZ_16 },
      ],
    },
  };
}

function profileName(profile) {
  switch (profile) {
    case H264Profile.BASELINE: return "baseline";
    case H264Profile.MAIN: return "main";
    default: return "high";
  }
}

function levelName(level) {
  switch (level) {
    case H264Level.LEVEL3_1: return "3.1";
    case H264Level.LEVEL3_2: return "3.2";
    default: return "4.0";
  }
}

function srtpParams(key, salt) {
  return Buffer.concat([key, salt]).toString("base64");
}

// The ffmpeg argument list for one viewer. Exported for the tests: this is where a wrong flag
// turns into a black tile in Home with nothing in the journal, so it is checked directly.
function buildFfmpegArgs({ sourceUrl, session, video, audio, includeAudio }) {
  const fps = Math.max(1, Math.min(video.fps || 30, 30));
  const bitrate = Math.max(100, video.max_bit_rate || 300);
  const args = [
    "-hide_banner", "-loglevel", "error", "-nostats",
    "-fflags", "+genpts+nobuffer", "-flags", "low_delay",
    "-rtsp_transport", "tcp", "-i", sourceUrl,

    "-map", "0:v:0", "-an", "-sn", "-dn",
    "-codec:v", "libx264", "-pix_fmt", "yuv420p", "-color_range", "mpeg",
    "-preset", "ultrafast", "-tune", "zerolatency",
    "-profile:v", profileName(video.profile), "-level:v", levelName(video.level),
    // Fit inside the requested box without stretching, and keep both sides even for x264.
    "-filter:v",
    `scale=w=${video.width}:h=${video.height}:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2`,
    "-r", String(fps), "-g", String(fps * 2),
    "-b:v", `${bitrate}k`, "-maxrate", `${bitrate}k`, "-bufsize", `${bitrate * 2}k`,
    "-payload_type", String(video.pt), "-ssrc", String(session.videoSsrc),
    "-f", "rtp", "-srtp_out_suite", "AES_CM_128_HMAC_SHA1_80",
    "-srtp_out_params", srtpParams(session.videoKey, session.videoSalt),
    `srtp://${session.address}:${session.videoPort}?rtcpport=${session.videoPort}&pkt_size=${video.mtu || 1316}`,
  ];

  if (includeAudio && audio && audio.codec === AudioStreamingCodecType.OPUS) {
    const sampleRate = audio.sample_rate === AudioStreamingSamplerate.KHZ_16 ? 16_000 : 24_000;
    args.push(
      "-map", "0:a:0?", "-vn", "-sn", "-dn",
      "-codec:a", "libopus", "-application", "lowdelay",
      "-ar", String(sampleRate), "-ac", "1",
      "-b:a", `${Math.max(16, audio.max_bit_rate || 24)}k`,
      "-frame_duration", String(audio.packet_time || 20),
      "-payload_type", String(audio.pt), "-ssrc", String(session.audioSsrc),
      "-f", "rtp", "-srtp_out_suite", "AES_CM_128_HMAC_SHA1_80",
      "-srtp_out_params", srtpParams(session.audioKey, session.audioSalt),
      `srtp://${session.address}:${session.audioPort}?rtcpport=${session.audioPort}&pkt_size=188`,
    );
  }

  return args;
}

function bindEphemeralSocket(type) {
  return new Promise((resolve, reject) => {
    const socket = dgram.createSocket(type);
    socket.once("error", reject);
    socket.bind(0, () => {
      socket.off("error", reject);
      resolve(socket);
    });
  });
}

class CameraStreamer {
  // camera: { id, name, audio }
  // api.streamUrl(id, quality) -> Promise<string>; api.snapshot(id) -> Promise<Buffer>
  constructor(camera, api) {
    this.camera = camera;
    this.api = api;
    this.pending = new Map();
    this.sessions = new Map();
    this.controller = new CameraController({
      cameraStreamCount: 2,
      delegate: this,
      streamingOptions: streamingOptions(),
      sensors: { motion: true },
    });
  }

  get motionService() {
    return this.controller.motionService;
  }

  handleSnapshotRequest(request, callback) {
    this.api.snapshot(this.camera.id)
      .then((jpeg) => callback(undefined, jpeg))
      .catch((error) => {
        console.warn(`HomeKit snapshot for "${this.camera.name}" failed: ${error.message}`);
        callback(error);
      });
  }

  async prepareStream(request, callback) {
    try {
      const ipv6 = request.addressVersion === "ipv6";
      const videoSocket = await bindEphemeralSocket(ipv6 ? "udp6" : "udp4");
      const audioSocket = await bindEphemeralSocket(ipv6 ? "udp6" : "udp4");
      const session = {
        address: request.targetAddress,
        videoPort: request.video.port,
        videoKey: request.video.srtp_key,
        videoSalt: request.video.srtp_salt,
        videoSsrc: CameraController.generateSynchronisationSource(),
        audioPort: request.audio.port,
        audioKey: request.audio.srtp_key,
        audioSalt: request.audio.srtp_salt,
        audioSsrc: CameraController.generateSynchronisationSource(),
        videoSocket,
        audioSocket,
      };
      this.pending.set(request.sessionID, session);
      callback(undefined, {
        video: {
          port: videoSocket.address().port,
          ssrc: session.videoSsrc,
          srtp_key: session.videoKey,
          srtp_salt: session.videoSalt,
        },
        audio: {
          port: audioSocket.address().port,
          ssrc: session.audioSsrc,
          srtp_key: session.audioKey,
          srtp_salt: session.audioSalt,
        },
      });
    } catch (error) {
      console.warn(`HomeKit stream setup for "${this.camera.name}" failed: ${error.message}`);
      callback(error);
    }
  }

  handleStreamRequest(request, callback) {
    switch (request.type) {
      case "start":
        this.startStream(request).then(() => callback(), (error) => {
          console.warn(`HomeKit stream for "${this.camera.name}" failed to start: ${error.message}`);
          this.stopStream(request.sessionID);
          callback(error);
        });
        return;
      case "reconfigure":
        // Home asks this when the viewer's link changes. Restarting ffmpeg for it costs a visible
        // freeze for little gain, so the stream carries on at the size it started with.
        callback();
        return;
      case "stop":
      default:
        this.stopStream(request.sessionID);
        callback();
    }
  }

  async startStream(request) {
    const session = this.pending.get(request.sessionID);
    if (!session) throw new Error("no prepared session");
    this.pending.delete(request.sessionID);
    this.sessions.set(request.sessionID, session);

    const quality = request.video.width <= SUB_STREAM_MAX_WIDTH ? "sub" : "main";
    const sourceUrl = await this.api.streamUrl(this.camera.id, quality);
    if (!this.sessions.has(request.sessionID)) return; // stopped while we were waiting

    const args = buildFfmpegArgs({
      sourceUrl,
      session,
      video: request.video,
      audio: request.audio,
      includeAudio: this.camera.audio === true,
    });

    const child = spawn(FFMPEG_PATH, args, { stdio: ["ignore", "ignore", "pipe"] });
    session.process = child;
    let stderr = "";
    child.stderr.on("data", (chunk) => {
      stderr = (stderr + chunk).slice(-2000);
    });
    child.on("exit", (code, signal) => {
      if (session.stopping) return;
      console.warn(`HomeKit stream for "${this.camera.name}" ended (code ${code}, signal ${signal}): ${stderr.trim()}`);
      this.controller.forceStopStreamingSession(request.sessionID);
      this.stopStream(request.sessionID);
    });

    // The viewer's RTCP reports land on the ports we answered prepareStream with. Silence on the
    // video one means nobody is watching any more. Armed by the first report rather than at start:
    // a firewall that drops them would otherwise cut every stream after VIEWER_SILENCE_MS, and
    // hap-nodejs already stops the session when the viewer's HAP connection closes.
    const touch = () => {
      clearTimeout(session.silenceTimer);
      session.silenceTimer = setTimeout(() => {
        console.log(`HomeKit viewer of "${this.camera.name}" went quiet; stopping its stream.`);
        this.controller.forceStopStreamingSession(request.sessionID);
        this.stopStream(request.sessionID);
      }, VIEWER_SILENCE_MS);
    };
    session.videoSocket.on("message", touch);

    await new Promise((resolve, reject) => {
      child.once("spawn", resolve);
      child.once("error", reject);
    });
    console.log(`HomeKit stream started for "${this.camera.name}" at ${request.video.width}x${request.video.height} (${quality} stream).`);
  }

  stopStream(sessionID) {
    const session = this.sessions.get(sessionID) || this.pending.get(sessionID);
    this.sessions.delete(sessionID);
    this.pending.delete(sessionID);
    if (!session) return;
    session.stopping = true;
    clearTimeout(session.silenceTimer);
    if (session.process && session.process.exitCode === null) session.process.kill("SIGKILL");
    for (const socket of [session.videoSocket, session.audioSocket]) {
      try { socket.close(); } catch { /* already closed */ }
    }
  }

  stopAll() {
    for (const id of [...this.sessions.keys(), ...this.pending.keys()]) this.stopStream(id);
  }
}

module.exports = { CameraStreamer, buildFfmpegArgs, streamingOptions, SUB_STREAM_MAX_WIDTH };
