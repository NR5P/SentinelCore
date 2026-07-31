#!/usr/bin/env python3
"""Regenerate the tvOS layered app icon from SentinelCoreAppIcon-1024.png.

A tvOS app icon is a parallax stack, not a flat image: tvOS composites Back, Middle and Front
and slides them against each other as the icon takes focus. Each layer therefore has to hold a
*different* part of the picture. Putting the whole artwork into all three - which is what you get
by exporting the master three times - renders the shield three times at three offsets, which is
what "the icon looks copy-pasted" means in practice.

So: Back holds the background and nothing else, Front holds the shield cut out of that background,
Middle stays empty. The cut-out is a flood fill inward from the border, which only removes
background connected to the edge - the dark areas *inside* the shield are enclosed by its bright
rim and survive, where a plain brightness threshold would punch holes through them.

Usage: python3 branding/make-tvos-icon.py   (requires Pillow; writes into the tvOS asset catalog)
"""

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageOps

ROOT = Path(__file__).resolve().parent.parent
MASTER = Path(__file__).resolve().parent / "SentinelCoreAppIcon-1024.png"
BRAND = (ROOT / "appletv/SentinelCoreTV/Resources/Assets.xcassets"
              / "App Icon & Top Shelf Image.brandassets")

# Colour the flood fill paints the background with before it becomes alpha. Anything absent from
# the artwork works; magenta is not a colour this icon contains.
KEY = (255, 0, 255)
# How far from the corner colour still counts as background. Generous enough to follow the glow a
# little way out from the shield, low enough to stop dead at its lit rim.
FLOOD_THRESHOLD = 60
# Below this luminance a pixel is treated as unlit background for framing purposes, so the shield
# gets measured by its visible extent rather than by the faint halo still attached to it.
VISIBLE_LUMA = 40
# Fraction of the layer's height the shield occupies. The stack shifts by a few percent under
# parallax, so the front layer needs margin it can move into without clipping.
SHIELD_HEIGHT_FRACTION = 0.80


def extract_shield(master: Image.Image) -> Image.Image:
    """Return the master as RGBA with the background connected to the border made transparent."""
    filled = master.copy()
    for corner in ((0, 0), (master.width - 1, 0), (0, master.height - 1),
                   (master.width - 1, master.height - 1)):
        ImageDraw.floodfill(filled, corner, KEY, thresh=FLOOD_THRESHOLD)

    key_plate = Image.new("RGB", master.size, KEY)
    # Zero exactly where the fill reached, non-zero everywhere it didn't.
    alpha = ImageChops.difference(filled, key_plate).convert("L").point(lambda v: 0 if v == 0 else 255)

    shield = master.copy()
    shield.putalpha(alpha)
    return shield


def visible_bbox(shield: Image.Image) -> tuple[int, int, int, int]:
    """Bounding box of the lit part of the shield, ignoring the dark halo left around it."""
    luma = shield.convert("L").point(lambda v: 255 if v > VISIBLE_LUMA else 0)
    opaque = shield.getchannel("A").point(lambda v: 255 if v > 0 else 0)
    return ImageChops.multiply(luma, opaque).getbbox()


def front_layer(shield: Image.Image, box: tuple[int, int, int, int], size: tuple[int, int]) -> Image.Image:
    width, height = size
    target_height = int(height * SHIELD_HEIGHT_FRACTION)
    cropped = shield.crop(box)
    scale = target_height / cropped.height
    # A shield wider than the frame would be cropped by it; scale to whichever axis binds first.
    if cropped.width * scale > width * SHIELD_HEIGHT_FRACTION:
        scale = (width * SHIELD_HEIGHT_FRACTION) / cropped.width
    resized = cropped.resize((max(1, round(cropped.width * scale)),
                              max(1, round(cropped.height * scale))), Image.LANCZOS)

    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    layer.paste(resized, ((width - resized.width) // 2, (height - resized.height) // 2), resized)
    return layer


def back_layer(master: Image.Image, size: tuple[int, int]) -> Image.Image:
    """Opaque background: the master's own near-black, lifted by the glow the shield sits in.

    The glow belongs here rather than on the front layer precisely because it should *not* travel
    with the shield - light staying put while the object moves over it is what sells the parallax.
    """
    width, height = size
    base = master.getpixel((4, 4))[:3]
    layer = Image.new("RGB", size, base)

    # radial_gradient is black at the centre and white at the edge; inverted it becomes a soft
    # pool of light in the middle, which is then used as the mask for the brand green.
    glow_mask = ImageOps.invert(Image.radial_gradient("L")).resize(size, Image.LANCZOS)
    glow_mask = glow_mask.point(lambda v: int((v / 255) ** 2 * 70))
    glow_mask = glow_mask.filter(ImageFilter.GaussianBlur(max(size) / 40))
    layer.paste(Image.new("RGB", size, (26, 120, 70)), (0, 0), glow_mask)
    return layer.convert("RGBA")


def write(image: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path)
    print(f"  {image.width}x{image.height}  {path.relative_to(ROOT)}")


def main() -> None:
    master = Image.open(MASTER).convert("RGB")
    shield = extract_shield(master)
    box = visible_bbox(shield)
    print(f"shield bounds in master: {box}")

    # (imagestack, layer size, suffixes) - the App Store icon ships 1x only, the app icon 1x and 2x.
    targets = [
        ("App Icon.imagestack", (400, 240), [("", 1), ("@2x", 2)]),
        ("App Icon - App Store.imagestack", (1280, 768), [("", 1)]),
    ]

    for stack, base_size, scales in targets:
        print(stack)
        for suffix, factor in scales:
            size = (base_size[0] * factor, base_size[1] * factor)
            layers = {
                "Front": front_layer(shield, box, size),
                "Middle": Image.new("RGBA", size, (0, 0, 0, 0)),
                "Back": back_layer(master, size),
            }
            for name, image in layers.items():
                write(image, BRAND / stack / f"{name}.imagestacklayer" / "Content.imageset"
                      / f"{name.lower()}{suffix}.png")


if __name__ == "__main__":
    main()
