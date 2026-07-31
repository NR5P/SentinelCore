# Sentinel Core app icon

`SentinelCoreAppIcon-1024.png` is the shared source artwork for the Android, iOS, iPadOS, and macOS app icons.

The icon was generated with the built-in image generation tool using this final prompt:

> Create a premium cross-platform security-camera app icon for Sentinel Core. Combine a protective shield with a camera lens/aperture or watchful sensor eye. Use a centered, symmetrical, small-size-readable silhouette on a full-bleed near-black background, with emerald green and restrained cyan illumination. Render it as a crisp, polished, subtly dimensional native app icon. No text, letters, numbers, watermark, transparency, interior rounded-square frame, or tiny decorative detail.

Platform-specific sizes are generated from the 1024px master. Keep the master free of alpha because Apple app icons do not accept transparent pixels.

## tvOS

tvOS is the exception to "just resize the master". Its app icon is a parallax stack of three layers that tvOS slides against each other as the icon takes focus, so each layer has to hold a *different* part of the picture — export the master into all three and the shield renders three times at three offsets, which reads as a smeared, copy-pasted icon.

`make-tvos-icon.py` builds the stack correctly: background on Back, shield cut out of that background on Front, Middle empty. Run it after any change to the master:

```bash
python3 branding/make-tvos-icon.py
```

It needs Pillow (`pip install pillow`) and writes straight into `appletv/…/App Icon & Top Shelf Image.brandassets`. The top shelf images are separate flat artwork and are not touched by it.

