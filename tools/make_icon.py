"""Renders Mendrain's icon: a teal rounded square with a white archive drawer.

Usage: python tools/make_icon.py [app-assets-dir] [branding-dir]
Defaults: src/Mendrain/Assets (Mendrain.ico 16-256 px, Logo.png 256 px) and assets (logo.png 512 px).
Requires Pillow.
"""
import os
import sys

from PIL import Image, ImageDraw

TEAL = (10, 124, 134, 255)
WHITE = (255, 255, 255, 255)
S = 1024  # master size; downscaled with LANCZOS for crisp small sizes


def rr(draw, box, radius, fill):
    draw.rounded_rectangle([int(v * S) for v in box], radius=int(radius * S), fill=fill)


def render() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    rr(d, (0.04, 0.04, 0.96, 0.96), 0.22, TEAL)          # tile
    rr(d, (0.20, 0.25, 0.80, 0.40), 0.045, WHITE)         # lid
    rr(d, (0.24, 0.43, 0.76, 0.77), 0.055, WHITE)         # drawer body
    rr(d, (0.41, 0.51, 0.59, 0.575), 0.032, TEAL)         # handle
    return img


def main(out: str, branding: str) -> None:
    os.makedirs(out, exist_ok=True)
    os.makedirs(branding, exist_ok=True)
    master = render()
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [master.resize((n, n), Image.LANCZOS) for n in sizes]
    frames[-1].save(os.path.join(out, "Mendrain.ico"), sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    master.resize((256, 256), Image.LANCZOS).save(os.path.join(out, "Logo.png"))
    master.resize((512, 512), Image.LANCZOS).save(os.path.join(branding, "logo.png"))
    print("wrote", os.listdir(out), os.listdir(branding))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join("src", "Mendrain", "Assets"),
         sys.argv[2] if len(sys.argv) > 2 else "assets")
