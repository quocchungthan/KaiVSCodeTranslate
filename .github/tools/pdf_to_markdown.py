"""Structure-preserving PDF -> Markdown converter for the translation pipeline.

Uses only locally installed open-source packages (pdfplumber / pdfminer.six, pypdf, Pillow).
Headings are inferred from the font-size and boldness profile of the document, inline bold and
italic runs are preserved, images are extracted to an assets directory and re-anchored at their
original position in the reading flow, and wrapped lines are rejoined into paragraphs, list items
and reference entries.

Emits a JSON summary on stdout for the calling PowerShell adapter.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys

import pdfplumber
from pypdf import PdfReader

BULLET_PATTERN = re.compile(r"^\s*([\u2022\u2023\u25aa\u25cf\u00b7\u2043\u2219])\s*")
ORDERED_PATTERN = re.compile(r"^\s*(\d{1,2}[.)])\s+")
REFERENCE_PATTERN = re.compile(r"^\[\d{1,3}\]\s")
SENTENCE_END = tuple(".!?:;\u201d\u2019)")
LOW_TEXT_THRESHOLD = 40
WORD_ATTRIBUTES = ["fontname", "size"]


class Block:
    __slots__ = ("kind", "level", "marker", "text", "path", "page")

    def __init__(self, kind, text="", level=0, marker="", path="", page=0):
        self.kind = kind
        self.text = text
        self.level = level
        self.marker = marker
        self.path = path
        self.page = page


class Line:
    __slots__ = ("text", "plain", "size", "bold", "x0", "top", "bottom")

    def __init__(self, text, plain, size, bold, x0, top, bottom):
        self.text = text
        self.plain = plain
        self.size = size
        self.bold = bold
        self.x0 = x0
        self.top = top
        self.bottom = bottom


def is_bold(fontname: str) -> bool:
    return "Bold" in fontname or "Black" in fontname or "Heavy" in fontname


def is_italic(fontname: str) -> bool:
    return "Italic" in fontname or "Oblique" in fontname


def group_words_into_lines(words: list[dict], tolerance: float) -> list[list[dict]]:
    lines: list[list[dict]] = []
    current: list[dict] = []
    anchor_top = None
    for word in sorted(words, key=lambda w: (round(w["top"], 1), w["x0"])):
        if anchor_top is not None and abs(word["top"] - anchor_top) > tolerance:
            lines.append(sorted(current, key=lambda w: w["x0"]))
            current = []
            anchor_top = None
        if anchor_top is None:
            anchor_top = word["top"]
        current.append(word)
    if current:
        lines.append(sorted(current, key=lambda w: w["x0"]))
    return lines


def render_line(words: list[dict], inline_emphasis: bool) -> tuple[str, str]:
    """Return (markdown_text, plain_text) for one visual line."""
    pieces: list[str] = []
    previous = None
    for word in words:
        gap = 0.0 if previous is None else word["x0"] - previous["x1"]
        separator = " " if previous is not None and gap > word["size"] * 0.12 else ""
        pieces.append(separator + word["text"])
        previous = word

    plain = "".join(pieces).strip()
    if not inline_emphasis or not plain:
        return plain, plain

    output: list[str] = []
    index = 0
    while index < len(words):
        bold = is_bold(words[index]["fontname"])
        italic = is_italic(words[index]["fontname"])
        end = index
        while (end + 1 < len(words)
               and is_bold(words[end + 1]["fontname"]) == bold
               and is_italic(words[end + 1]["fontname"]) == italic):
            end += 1

        run = "".join(pieces[index:end + 1])
        if bold or italic:
            core = run.strip()
            if core and any(character.isalnum() for character in core):
                marker = "***" if bold and italic else ("**" if bold else "*")
                leading = run[:len(run) - len(run.lstrip())]
                trailing = run[len(run.rstrip()):]
                run = f"{leading}{marker}{core}{marker}{trailing}"
        output.append(run)
        index = end + 1

    return "".join(output).strip(), plain


def infer_profile(pdf: pdfplumber.PDF, sample_pages: int) -> dict:
    sizes: collections.Counter = collections.Counter()
    left_edges: collections.Counter = collections.Counter()
    gaps: collections.Counter = collections.Counter()

    pages = pdf.pages[:sample_pages] if sample_pages > 0 else pdf.pages
    for page in pages:
        for char in page.chars:
            sizes[round(char["size"], 1)] += 1
        previous_top = None
        for line in page.extract_text_lines():
            left_edges[round(line["x0"], 1)] += 1
            if previous_top is not None:
                gap = round(line["top"] - previous_top, 1)
                if 0 < gap < 60:
                    gaps[gap] += 1
            previous_top = line["top"]

    if not sizes:
        raise SystemExit("No text layer found; this converter requires an extractable text layer.")

    body_size = sizes.most_common(1)[0][0]
    body_left = left_edges.most_common(1)[0][0] if left_edges else 0.0
    line_gap = gaps.most_common(1)[0][0] if gaps else round(body_size * 1.2, 1)

    heading_sizes = sorted({s for s in sizes if s > body_size + 0.5}, reverse=True)
    heading_levels = {size: index + 1 for index, size in enumerate(heading_sizes[:5])}

    return {
        "bodySize": body_size,
        "bodyLeft": body_left,
        "lineGap": line_gap,
        "paragraphGap": round(line_gap * 1.25, 1),
        "headingLevels": heading_levels,
        "boldBodyHeadingLevel": len(heading_levels) + 1,
    }


def join_wrapped(previous: str, addition: str) -> str:
    if not previous:
        return addition
    if previous.endswith("-") and not previous.endswith(" -"):
        return previous + addition
    if previous.endswith("/") and "://" in previous.rsplit(" ", 1)[-1]:
        return previous + addition
    return previous + " " + addition


def extract_images(reader: PdfReader, page_index: int, assets_directory: str, prefix: str) -> dict:
    resolved: dict[str, str] = {}
    try:
        images = reader.pages[page_index].images
    except Exception:
        return resolved

    for order, image in enumerate(images):
        resource = image.name.rsplit(".", 1)[0].lstrip("/")
        extension = os.path.splitext(image.name)[1].lower() or ".png"
        filename = f"{prefix}-p{page_index + 1:03d}-{order + 1:02d}{extension}"
        target = os.path.join(assets_directory, filename)
        if not os.path.exists(target):
            with open(target, "wb") as handle:
                handle.write(image.data)
        resolved[resource] = filename
    return resolved


def read_page_lines(page, body_size: float, inline_emphasis: bool) -> list[Line]:
    words = page.extract_words(extra_attrs=WORD_ATTRIBUTES, use_text_flow=False)
    result: list[Line] = []
    for group in group_words_into_lines(words, tolerance=body_size * 0.3):
        markdown_text, plain = render_line(group, inline_emphasis)
        if not plain:
            continue
        result.append(Line(
            text=markdown_text,
            plain=plain,
            size=round(max(word["size"] for word in group), 1),
            bold=all(is_bold(word["fontname"]) for word in group),
            x0=round(min(word["x0"] for word in group), 1),
            top=min(word["top"] for word in group),
            bottom=max(word["bottom"] for word in group),
        ))
    return result


def convert(pdf_path: str, markdown_path: str, assets_directory: str, prefix: str,
            sample_pages: int, title: str, inline_emphasis: bool) -> dict:
    os.makedirs(assets_directory, exist_ok=True)
    os.makedirs(os.path.dirname(markdown_path) or ".", exist_ok=True)

    reader = PdfReader(pdf_path)
    blocks: list[Block] = []
    warnings: list[str] = []
    unresolved: list[dict] = []
    image_only_pages: list[int] = []
    heading_counts: collections.Counter = collections.Counter()
    written_images = 0

    with pdfplumber.open(pdf_path) as pdf:
        profile = infer_profile(pdf, sample_pages)
        body_size = profile["bodySize"]
        body_left = profile["bodyLeft"]
        line_gap = profile["lineGap"]
        paragraph_gap = profile["paragraphGap"]
        heading_levels = profile["headingLevels"]
        bold_level = profile["boldBodyHeadingLevel"]
        flow_tolerance = (paragraph_gap - line_gap) + 3

        for page_index, page in enumerate(pdf.pages):
            page_number = page_index + 1
            name_map = extract_images(reader, page_index, assets_directory, prefix)
            written_images += len(name_map)

            page_lines = read_page_lines(page, body_size, inline_emphasis)
            text_length = sum(len(line.plain) for line in page_lines)

            items: list[tuple[float, str, object]] = []
            for line in page_lines:
                items.append((line.top, "line", line))
            for image in page.images:
                resource = str(image.get("name") or "").lstrip("/")
                filename = name_map.get(resource)
                if filename:
                    items.append((image["top"], "image", filename))
                else:
                    unresolved.append({
                        "page": page_number,
                        "reason": f"Embedded image '{resource}' could not be decoded by pypdf.",
                    })
            items.sort(key=lambda entry: entry[0])

            if text_length < LOW_TEXT_THRESHOLD and name_map:
                image_only_pages.append(page_number)

            previous_bottom = None
            for _, kind, payload in items:
                if kind == "image":
                    blocks.append(Block("image", path=payload, page=page_number))
                    previous_bottom = None
                    continue

                line: Line = payload
                level = heading_levels.get(line.size)
                if level is None and line.bold and abs(line.size - body_size) < 0.5:
                    level = bold_level
                if level is not None:
                    blocks.append(Block("heading", text=line.plain, level=level, page=page_number))
                    heading_counts[level] += 1
                    previous_bottom = line.bottom
                    continue

                indented = line.x0 > body_left + 4
                gap = None if previous_bottom is None else line.top - previous_bottom
                continues_flow = gap is not None and gap <= flow_tolerance
                previous_block = blocks[-1] if blocks else None

                bullet = BULLET_PATTERN.match(line.plain)
                ordered = ORDERED_PATTERN.match(line.plain)
                reference = REFERENCE_PATTERN.match(line.plain)

                if bullet:
                    blocks.append(Block("list", text=BULLET_PATTERN.sub("", line.text, count=1),
                                        marker="-", page=page_number))
                elif ordered and indented:
                    blocks.append(Block("list", text=ORDERED_PATTERN.sub("", line.text, count=1),
                                        marker=ordered.group(1), page=page_number))
                elif reference:
                    blocks.append(Block("reference", text=line.text, page=page_number))
                elif previous_block is not None and continues_flow and (
                        previous_block.kind == "reference"
                        or (previous_block.kind == "list" and indented)
                        or (previous_block.kind == "paragraph" and not indented)):
                    previous_block.text = join_wrapped(previous_block.text, line.text)
                else:
                    blocks.append(Block("paragraph", text=line.text, page=page_number))

                previous_bottom = line.bottom

    blocks = merge_across_pages(blocks)
    markdown = render(blocks, title)

    with open(markdown_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(markdown)

    referenced = set(re.findall(r"!\[[^\]]*\]\(assets/([^)]+)\)", markdown))
    for filename in sorted(referenced):
        if not os.path.exists(os.path.join(assets_directory, filename)):
            warnings.append(f"Referenced asset is missing on disk: {filename}")

    for page_number in image_only_pages:
        unresolved.append({
            "page": page_number,
            "reason": "Page has no text layer; content preserved as image only. OCR not run (tesseract unavailable).",
        })

    return {
        "pdf": pdf_path,
        "markdown": markdown_path,
        "assets": assets_directory,
        "pageCount": len(reader.pages),
        "profile": {**profile, "headingLevels": {str(k): v for k, v in profile["headingLevels"].items()}},
        "blockCount": len(blocks),
        "blockKinds": dict(collections.Counter(block.kind for block in blocks)),
        "headingCounts": {str(level): count for level, count in sorted(heading_counts.items())},
        "imagesWritten": written_images,
        "imagesReferenced": len(referenced),
        "imageOnlyPages": image_only_pages,
        "unresolved": unresolved,
        "warnings": warnings,
        "characters": len(markdown),
    }


def merge_across_pages(blocks: list[Block]) -> list[Block]:
    merged: list[Block] = []
    for block in blocks:
        previous = merged[-1] if merged else None
        if (previous is not None and block.kind == previous.kind
                and block.kind in ("paragraph", "reference", "list")
                and block.page != previous.page
                and not previous.text.rstrip().endswith(SENTENCE_END)
                and block.text[:1].islower()):
            previous.text = join_wrapped(previous.text, block.text)
            continue
        merged.append(block)
    return merged


def render(blocks: list[Block], title: str) -> str:
    lines: list[str] = []
    if title:
        lines.append(f"# {title}")
        lines.append("")

    offset = 1 if title else 0
    previous_kind = None
    for block in blocks:
        if block.kind == "heading":
            if lines and lines[-1] != "":
                lines.append("")
            lines.append(f"{'#' * min(block.level + offset, 6)} {block.text}")
            lines.append("")
        elif block.kind == "image":
            if lines and lines[-1] != "":
                lines.append("")
            lines.append(f"![](assets/{block.path})")
            lines.append("")
        elif block.kind == "list":
            if previous_kind != "list" and lines and lines[-1] != "":
                lines.append("")
            lines.append(f"{block.marker} {block.text}")
        elif block.kind == "reference":
            if previous_kind != "reference" and lines and lines[-1] != "":
                lines.append("")
            lines.append(f"{block.text}  ")
        else:
            if lines and lines[-1] != "":
                lines.append("")
            lines.append(block.text)
            lines.append("")
        previous_kind = block.kind

    text = "\n".join(lines)
    text = re.sub(r"\n{3,}", "\n\n", text).strip()
    return text + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Convert a text-layer PDF to structured Markdown.")
    parser.add_argument("--pdf", required=True)
    parser.add_argument("--markdown", required=True)
    parser.add_argument("--assets", required=True)
    parser.add_argument("--asset-prefix", default="img")
    parser.add_argument("--title", default="")
    parser.add_argument("--profile-sample-pages", type=int, default=60,
                        help="Pages sampled for font/gap profiling; 0 samples every page.")
    parser.add_argument("--no-inline-emphasis", action="store_true",
                        help="Do not emit ** / * markers for bold and italic runs.")
    args = parser.parse_args()

    summary = convert(args.pdf, args.markdown, args.assets, args.asset_prefix,
                      args.profile_sample_pages, args.title, not args.no_inline_emphasis)
    sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
