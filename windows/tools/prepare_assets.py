#!/usr/bin/env python3
"""Builds the Windows assets from the shared macOS assets in ../../assets.

Run from anywhere:  python windows/tools/prepare_assets.py
Needs Pillow (pip install pillow). The output is committed, so a normal build
does not need Python; re-run this only when the source assets change.
"""
import os
import shutil
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
SRC = os.path.join(ROOT, "assets")
OUT = os.path.join(ROOT, "windows", "TinyHand", "Assets")

# The originals are 1024x1536. 1024 rows keep the hand sharp up to the largest
# pointer size on a 4K screen while the PNGs shrink from ~2 MB to ~150 KB each.
HAND_HEIGHT = 1024


def main() -> int:
    hands = os.path.join(OUT, "Hands")
    sounds = os.path.join(OUT, "Sounds")
    os.makedirs(hands, exist_ok=True)
    os.makedirs(sounds, exist_ok=True)

    names = sorted(n for n in os.listdir(SRC) if n.startswith("hand_") and n.endswith(".png"))
    for name in names:
        im = Image.open(os.path.join(SRC, name)).convert("RGBA")
        if im.height > HAND_HEIGHT:
            w = round(im.width * HAND_HEIGHT / im.height)
            im = im.resize((w, HAND_HEIGHT), Image.LANCZOS)
        im.save(os.path.join(hands, name), optimize=True)
    print(f"{len(names)} hands")

    wavs = sorted(n for n in os.listdir(os.path.join(SRC, "sounds")) if n.endswith(".wav"))
    for name in wavs:
        shutil.copyfile(os.path.join(SRC, "sounds", name), os.path.join(sounds, name))
    print(f"{len(wavs)} sounds")

    # Tray icons: the menu bar template images (black on transparent); tinted at runtime.
    for state in ("on", "off"):
        shutil.copyfile(os.path.join(SRC, f"menubar_{state}@3x.png"), os.path.join(OUT, f"tray_{state}.png"))

    icon = Image.open(os.path.join(SRC, "AppIcon.icns"))
    icon.load()  # the largest representation by default
    icon = icon.convert("RGBA")
    icon.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "AppIcon.png"), optimize=True)
    icon.save(os.path.join(OUT, "TinyHand.ico"), sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("icons")
    return 0


if __name__ == "__main__":
    sys.exit(main())
