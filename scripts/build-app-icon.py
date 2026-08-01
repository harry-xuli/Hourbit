from pathlib import Path
import sys
from PIL import Image

SIZES = [(16, 16), (20, 20), (24, 24), (32, 32),
         (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]

if len(sys.argv) != 3:
    raise SystemExit("usage: build-app-icon.py <rgba-png> <ico-path>")

source = Path(sys.argv[1]).resolve()
destination = Path(sys.argv[2]).resolve()
with Image.open(source) as image:
    rgba = image.convert("RGBA")
    if rgba.width != rgba.height or rgba.width < 1024:
        raise SystemExit("source logo must be square and at least 1024 px")
    destination.parent.mkdir(parents=True, exist_ok=True)
    rgba.save(destination, format="ICO", sizes=SIZES)
