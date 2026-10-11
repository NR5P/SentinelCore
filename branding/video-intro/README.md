# SentinelCore video intro

The 7-second logo intro used at the start of SentinelCore videos (first used on the project intro video, 2026-10-10).

`sentinelcore-intro.mp4` is the finished intro: 1280x720, 29.97 fps, H.264 + AAC 48 kHz stereo, 216 frames (7.2072 s), ending on black.

## Put it on a new video

```bash
branding/video-intro/add-intro.sh ~/Desktop/my-video.mp4 ~/Desktop/my-video.srt
```

The captions argument is optional. The script writes `my-video-with-intro.mp4` (and a `.srt` shifted by the intro length) next to the input.

## Change the intro

The intro is code, not a video project:

- `intro.html`: a canvas animation. `render(t)` draws the frame at `t` seconds. The logo comes from `../SentinelCoreAppIcon-1024.png`. The tagline, HUD wording and colors are near the top of each draw function, and the timing beats are in `render()` (impact at 2.35 s, title decode from 3.15 s, glitch-out over the last 0.55 s).
- `capture.mjs`: renders every frame through headless Brave over CDP. Run `node capture.mjs intro.html <outDir> 216`, or append a comma list of frame numbers to render just those for a quick check.
- `sfx.txt`: an ffmpeg filter graph that synthesizes the sound (drone, riser, impact boom, digital ticks, chime, glitch stutter). Nothing is sampled, so there are no licensing issues. Build it with `ffmpeg -filter_complex_script sfx.txt -map "[a]" -t 7.2072 sfx.wav`.

Then encode:

```bash
ffmpeg -framerate 30000/1001 -i frames/f%04d.png -i sfx.wav -af "volume=3dB,alimiter=limit=0.89,apad" -t 7.2072 \
  -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k -ar 48000 -ac 2 -movflags +faststart sentinelcore-intro.mp4
```

Gotcha: the icon's square background has to be feathered with a round mask, and the red/cyan glitch layers must be made from the *opaque* icon before that mask is applied. Otherwise `multiply` blending paints a white square or a grey ring around the shield.
