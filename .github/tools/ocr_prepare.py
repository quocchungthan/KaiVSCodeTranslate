"""Stitch per-page scan tiles into full-height strips and cut them into OCR-safe bands.

Bands are cut only on near-blank pixel rows so no glyph line is split across bands.
"""
import argparse
import json
import os
import re
from collections import defaultdict

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

TILE_PATTERN = re.compile(r"^fig-p(\d+)-(\d+)\.(jpg|png)$", re.IGNORECASE)


def load_page_tiles(assets_dir):
    pages = defaultdict(list)
    for name in sorted(os.listdir(assets_dir)):
        match = TILE_PATTERN.match(name)
        if not match:
            continue
        pages[int(match.group(1))].append((int(match.group(2)), name))
    for page in pages:
        pages[page].sort()
    return pages


def stitch(assets_dir, names):
    images = [Image.open(os.path.join(assets_dir, n)).convert("RGB") for n in names]
    width = max(i.width for i in images)
    height = sum(i.height for i in images)
    strip = Image.new("RGB", (width, height), (255, 255, 255))
    offsets = []
    y = 0
    for image, name in zip(images, names):
        strip.paste(image, (0, y))
        offsets.append({"asset": name, "top": y, "height": image.height, "width": image.width})
        y += image.height
        image.close()
    return strip, offsets


def blank_rows(strip):
    gray = np.asarray(strip.convert("L"), dtype=np.uint8)
    return (gray.min(axis=1) > 235), gray


def choose_cuts(is_blank, height, target, minimum):
    cuts = [0]
    while height - cuts[-1] > target:
        ideal = cuts[-1] + target
        found = None
        for offset in range(0, target - minimum):
            for candidate in (ideal - offset, ideal + offset):
                if candidate <= cuts[-1] + minimum or candidate >= height:
                    continue
                if is_blank[candidate] and is_blank[max(candidate - 2, 0)] and is_blank[min(candidate + 2, height - 1)]:
                    found = candidate
                    break
            if found is not None:
                break
        cuts.append(found if found is not None else min(ideal, height))
    cuts.append(height)
    return cuts


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", required=True)
    parser.add_argument("--work", required=True)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--band-target", type=int, default=4200)
    parser.add_argument("--band-minimum", type=int, default=800)
    parser.add_argument("--scale", type=float, default=2.0)
    args = parser.parse_args()

    os.makedirs(args.work, exist_ok=True)
    pages = load_page_tiles(args.assets)
    manifest = {"scale": args.scale, "pages": []}

    for page in sorted(pages):
        names = [n for _, n in pages[page]]
        strip, offsets = stitch(args.assets, names)
        strip_path = os.path.join(args.work, f"page{page:03d}.png")
        strip.save(strip_path)
        is_blank, gray = blank_rows(strip)
        cuts = choose_cuts(is_blank, strip.height, args.band_target, args.band_minimum)

        bands = []
        for index in range(len(cuts) - 1):
            top, bottom = cuts[index], cuts[index + 1]
            if bottom - top <= 0:
                continue
            band = strip.crop((0, top, strip.width, bottom))
            scaled = band.resize(
                (int(band.width * args.scale), int(band.height * args.scale)), Image.LANCZOS
            )
            band_path = os.path.join(args.work, f"page{page:03d}-band{index:03d}.png")
            scaled.save(band_path)
            bands.append({"index": index, "top": top, "bottom": bottom, "path": band_path})
            band.close()
            scaled.close()

        ink = (gray < 236).sum(axis=1)
        manifest["pages"].append(
            {
                "page": page,
                "strip": strip_path,
                "width": strip.width,
                "height": strip.height,
                "tiles": offsets,
                "bands": bands,
                "inkPerRow": ink.astype(int).tolist(),
            }
        )
        strip.close()

    with open(args.manifest, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)

    print(json.dumps({
        "pages": len(manifest["pages"]),
        "bands": sum(len(p["bands"]) for p in manifest["pages"]),
    }))


if __name__ == "__main__":
    main()
