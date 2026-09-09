"""Rebuild structured Markdown from Windows OCR line boxes over stitched page strips.

Text is never invented: every emitted character comes from OCR output, and any inked
region that is not recognized as text is preserved verbatim as a cropped figure asset.
"""
import argparse
import json
import os
import re
import statistics
from collections import Counter

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

BULLET_PREFIX = re.compile(r"^\s*([\u2022\u25aa\u25cf\u00b7\u2023\u2043o]|[-\u2013\u2014])\s+")
ORDERED_PREFIX = re.compile(r"^\s*(\d{1,2})[.)]\s+")
PURE_NUMBER = re.compile(r"^\s*\d{1,4}\s*$")
NOISE_LINES = {
    "visit- tutflix.org for more premium resources",
    "visit:- tutflix.org for more premium resources",
    "visit tutflix.org for more premium resources",
}


def load_pages(ocr_directory):
    pages = []
    for name in sorted(os.listdir(ocr_directory)):
        if not name.endswith(".ocr.json"):
            continue
        with open(os.path.join(ocr_directory, name), encoding="utf-8-sig") as handle:
            pages.append(json.load(handle))
    return pages


def line_metrics(lines):
    heights = [l["bottom"] - l["top"] for l in lines if l["bottom"] > l["top"]]
    body_height = statistics.median(heights) if heights else 12.0
    lefts = Counter(round(l["left"] / 4) * 4 for l in lines)
    body_left = lefts.most_common(1)[0][0] if lefts else 0
    rights = [l["right"] for l in lines]
    body_right = np.percentile(rights, 90) if rights else 0
    return body_height, float(body_left), float(body_right)


def leading_gap(lines, body_height):
    """Most common baseline-to-baseline gap between wrapped body lines."""
    gaps = []
    for current, following in zip(lines, lines[1:]):
        gap = following["top"] - current["bottom"]
        if not 0 <= gap < body_height * 4:
            continue
        if abs((current["bottom"] - current["top"]) - body_height) > 2:
            continue
        if abs((following["bottom"] - following["top"]) - body_height) > 2:
            continue
        gaps.append(round(gap))
    if not gaps:
        return body_height * 0.9
    return float(Counter(gaps).most_common(1)[0][0])


def ink_density(gray, line):
    top = max(int(line["top"]), 0)
    bottom = min(int(line["bottom"]) + 1, gray.shape[0])
    left = max(int(line["left"]), 0)
    right = min(int(line["right"]) + 1, gray.shape[1])
    if bottom <= top or right <= left:
        return 0.0
    box = gray[top:bottom, left:right]
    return float((box < 160).sum()) / box.size


def detect_figures(gray, lines, page_height, page_width, body_height, body_left, min_height=34):
    ink_rows = (gray < 236).sum(axis=1)
    inked = ink_rows > 2
    text_rows = np.zeros(page_height, dtype=bool)
    for line in lines:
        top = max(int(line["top"]) - 2, 0)
        bottom = min(int(line["bottom"]) + 3, page_height)
        text_rows[top:bottom] = True

    candidate = inked & ~text_rows
    regions = []
    start = None
    gap = 0
    for row in range(page_height):
        if candidate[row]:
            if start is None:
                start = row
            gap = 0
        elif start is not None:
            gap += 1
            if gap > 26:
                regions.append([start, row - gap])
                start = None
                gap = 0
    if start is not None:
        regions.append([start, page_height - 1])

    regions = [r for r in regions if r[1] - r[0] >= min_height]

    widths = [l["right"] - l["left"] for l in lines]
    body_width = float(np.percentile(widths, 85)) if widths else page_width

    # Absorb only figure/table labels: short or indented lines that physically
    # overlap the inked region. Full-width prose lines are never absorbed.
    for _ in range(4):
        changed = False
        for region in regions:
            for line in lines:
                if line.get("_consumed"):
                    continue
                height = line["bottom"] - line["top"]
                if height > body_height * 1.2:
                    continue
                width = line["right"] - line["left"]
                if width > body_width * 0.72 and line["left"] <= body_left + 12:
                    continue
                if line["bottom"] < region[0] - 8 or line["top"] > region[1] + 8:
                    continue
                region[0] = min(region[0], int(line["top"]) - 2)
                region[1] = max(region[1], int(line["bottom"]) + 2)
                line["_consumed"] = True
                changed = True
        merged = []
        for region in sorted(regions):
            if merged and region[0] <= merged[-1][1] + 6:
                merged[-1][1] = max(merged[-1][1], region[1])
            else:
                merged.append(list(region))
        regions = merged
        if not changed:
            break
    return regions


def classify(line, body_height, body_left, density, body_density):
    height = line["bottom"] - line["top"]
    ratio = height / body_height if body_height else 1.0
    text = line["text"].strip()
    if ratio >= 2.2:
        return 1
    if ratio >= 1.55:
        return 2
    if ratio >= 1.22:
        return 3
    if (
        density > body_density * 1.22
        and len(text) < 110
        and abs(line["left"] - body_left) < 18
        and not text.endswith((".", ",", ";", ":"))
    ):
        return 4
    return 0


def is_noise(text):
    return text.strip().lower().rstrip(".") in NOISE_LINES


def render_page(page, manifest_page, strip_gray, strip_image, assets_dir, asset_counter, title_seen):
    lines = [l for l in page["lines"] if l["text"].strip()]
    if not lines:
        return [], asset_counter, []

    body_height, body_left, body_right = line_metrics(lines)
    densities = [ink_density(strip_gray, l) for l in lines]
    for line, density in zip(lines, densities):
        line["_density"] = density
    heights = [l["bottom"] - l["top"] for l in lines]
    body_lines = [l for l, h in zip(lines, heights) if abs(h - body_height) <= 2]
    body_density = statistics.median([l["_density"] for l in body_lines]) if body_lines else 0.12
    leading = leading_gap(lines, body_height)
    break_gap = leading + max(2.0, body_height * 0.22)

    regions = detect_figures(
        strip_gray, lines, page["height"], page["width"], body_height, body_left
    )

    blocks = []
    for region in regions:
        blocks.append({"kind": "figure", "top": region[0], "bottom": region[1]})
    for line in lines:
        if line.get("_consumed"):
            continue
        blocks.append({"kind": "line", "top": line["top"], "bottom": line["bottom"], "line": line})
    blocks.sort(key=lambda b: (b["top"], b.get("line", {}).get("left", 0)))

    markdown = []
    unresolved = []
    paragraph = []
    paragraph_kind = None
    previous_bottom = None

    def flush():
        nonlocal paragraph, paragraph_kind
        if paragraph:
            markdown.append(("paragraph", " ".join(paragraph).strip(), paragraph_kind))
            paragraph = []
            paragraph_kind = None

    for block in blocks:
        if block["kind"] == "figure":
            flush()
            asset_counter += 1
            name = f"figure-{asset_counter:04d}.png"
            crop = strip_image.crop(
                (0, max(block["top"] - 4, 0), strip_image.width, min(block["bottom"] + 5, strip_image.height))
            )
            crop.save(os.path.join(assets_dir, name))
            crop.close()
            markdown.append(("figure", f"assets/{name}", None))
            previous_bottom = block["bottom"]
            continue

        line = block["line"]
        text = line["text"].strip()
        if is_noise(text):
            previous_bottom = line["bottom"]
            continue
        if PURE_NUMBER.match(text) and line["left"] > body_left + 40 and len(text) <= 4:
            previous_bottom = line["bottom"]
            continue

        level = classify(line, body_height, body_left, line["_density"], body_density)
        gap = (line["top"] - previous_bottom) if previous_bottom is not None else 999

        if level:
            flush()
            if level == 1 and title_seen:
                level = 2
            if level == 1:
                title_seen = True
            markdown.append(("heading", text, level))
            previous_bottom = line["bottom"]
            continue

        bullet = BULLET_PREFIX.match(text)
        ordered = ORDERED_PREFIX.match(text)
        indented = line["left"] > body_left + 12

        if bullet:
            flush()
            paragraph_kind = "ul"
            paragraph = [BULLET_PREFIX.sub("", text)]
        elif ordered and indented:
            flush()
            paragraph_kind = f"ol:{ordered.group(1)}"
            paragraph = [ORDERED_PREFIX.sub("", text)]
        else:
            list_open = paragraph_kind is not None and paragraph_kind != "p"
            if paragraph and gap > break_gap:
                flush()
            elif list_open and not indented:
                flush()
            if not paragraph:
                paragraph_kind = "p"
            paragraph.append(text)
        previous_bottom = line["bottom"]

    flush()
    return markdown, asset_counter, unresolved, title_seen


def emit(markdown_blocks):
    output = []
    for kind, value, meta in markdown_blocks:
        if kind == "heading":
            output.append(f"{'#' * meta} {value}")
        elif kind == "figure":
            output.append(f"![]({value})")
        else:
            if meta == "ul":
                output.append(f"- {value}")
            elif meta and meta.startswith("ol:"):
                output.append(f"{meta.split(':')[1]}. {value}")
            else:
                output.append(value)
    return "\n\n".join(output)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--ocr", required=True)
    parser.add_argument("--assets", required=True)
    parser.add_argument("--markdown", required=True)
    parser.add_argument("--title", required=True)
    args = parser.parse_args()

    with open(args.manifest, encoding="utf-8") as handle:
        manifest = json.load(handle)
    manifest_pages = {p["page"]: p for p in manifest["pages"]}

    os.makedirs(args.assets, exist_ok=True)
    os.makedirs(os.path.dirname(args.markdown), exist_ok=True)

    pages = load_pages(args.ocr)
    document = [f"# {args.title}"]
    asset_counter = 0
    title_seen = True
    figure_count = 0

    for page in pages:
        manifest_page = manifest_pages[page["page"]]
        strip = Image.open(manifest_page["strip"]).convert("RGB")
        gray = np.asarray(strip.convert("L"), dtype=np.uint8)
        blocks, asset_counter, _unresolved, title_seen = render_page(
            page, manifest_page, gray, strip, args.assets, asset_counter, title_seen
        )
        figure_count += sum(1 for b in blocks if b[0] == "figure")
        rendered = emit(blocks)
        if rendered.strip():
            document.append(rendered)
        strip.close()

    text = "\n\n".join(document).rstrip() + "\n"
    with open(args.markdown, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)

    headings = Counter(len(m.group(1)) for m in re.finditer(r"^(#{1,6}) ", text, re.MULTILINE))
    print(json.dumps({
        "pages": len(pages),
        "characters": len(text),
        "figures": figure_count,
        "headings": {str(k): v for k, v in sorted(headings.items())},
    }))


if __name__ == "__main__":
    main()
