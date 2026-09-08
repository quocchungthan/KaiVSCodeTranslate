"""Reusable PDF structure inspector for the translation pipeline.

Reports page/text/image coverage, font-size profile, outline, glyph anomalies and
optional per-page layout dumps so a conversion profile can be chosen deterministically.
Emits a JSON report on stdout when --json is passed, otherwise a human-readable report.
"""

from __future__ import annotations

import argparse
import collections
import json
import sys

import pdfplumber
from pypdf import PdfReader

LOW_TEXT_THRESHOLD = 40


def parse_page_list(value: str | None, page_count: int) -> list[int]:
    if not value:
        return []
    pages: list[int] = []
    for part in value.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part[1:]:
            start, end = part.split("-", 1)
            pages.extend(range(int(start), int(end) + 1))
        else:
            pages.append(int(part))
    return [p for p in pages if 1 <= p <= page_count]


def read_outline(path: str) -> list[dict]:
    reader = PdfReader(path)
    entries: list[dict] = []

    def walk(items, depth: int) -> None:
        for item in items:
            if isinstance(item, list):
                walk(item, depth + 1)
            else:
                title = getattr(item, "title", None)
                if title:
                    entries.append({"depth": depth, "title": str(title)})

    try:
        walk(reader.outline, 0)
    except Exception as exc:  # outlines are optional and often malformed
        entries.append({"depth": 0, "title": f"<outline unavailable: {exc}>"})
    return entries


def line_profile(line: dict) -> tuple[float, str, bool]:
    chars = line["chars"]
    size = round(max(c["size"] for c in chars), 1)
    font = collections.Counter(c["fontname"].split("+")[-1] for c in chars).most_common(1)[0][0]
    bold = all("Bold" in c["fontname"] for c in chars if c["text"].strip())
    return size, font, bold


def build_report(path: str, dump_pages: list[int]) -> dict:
    fonts: collections.Counter = collections.Counter()
    sizes: collections.Counter = collections.Counter()
    line_sizes: collections.Counter = collections.Counter()
    glyphs: collections.Counter = collections.Counter()
    left_edges: collections.Counter = collections.Counter()
    line_gaps: collections.Counter = collections.Counter()

    low_text_pages: list[int] = []
    image_pages: dict[int, int] = {}
    table_pages: dict[int, int] = {}
    dumps: dict[int, list[str]] = {}
    total_images = 0
    total_characters = 0

    with pdfplumber.open(path) as pdf:
        page_count = len(pdf.pages)
        dump_pages = [p for p in dump_pages if 1 <= p <= page_count]

        for page in pdf.pages:
            number = page.page_number

            for char in page.chars:
                fonts[(char["fontname"].split("+")[-1], round(char["size"], 1))] += 1
                sizes[round(char["size"], 1)] += 1

            text = page.extract_text() or ""
            total_characters += len(text)
            if len(text.strip()) < LOW_TEXT_THRESHOLD:
                low_text_pages.append(number)
            for character in text:
                if ord(character) > 0x2000 or character == "\ufffd":
                    glyphs[character] += 1

            if page.images:
                image_pages[number] = len(page.images)
                total_images += len(page.images)

            tables = page.find_tables()
            if tables:
                table_pages[number] = len(tables)

            lines = page.extract_text_lines(return_chars=True)
            previous_top = None
            for line in lines:
                size, font, bold = line_profile(line)
                line_sizes[(size, font, bold)] += 1
                left_edges[round(line["x0"], 1)] += 1
                if previous_top is not None:
                    line_gaps[round(line["top"] - previous_top, 1)] += 1
                previous_top = line["top"]

            if number in dump_pages:
                items: list[tuple[float, str]] = []
                for line in lines:
                    size, font, bold = line_profile(line)
                    items.append((
                        line["top"],
                        f"[{size:>5} {font:<26} bold={str(bold):<5} x0={line['x0']:>6.1f}] {line['text']}",
                    ))
                for image in page.images:
                    items.append((
                        image["top"],
                        f"<<< IMAGE name={image.get('name')} w={image['width']:.0f} "
                        f"h={image['height']:.0f} x0={image['x0']:.0f} >>>",
                    ))
                dumps[number] = [text for _, text in sorted(items, key=lambda kv: kv[0])]

    return {
        "path": path,
        "pageCount": page_count,
        "totalCharacters": total_characters,
        "lowTextPages": low_text_pages,
        "pagesWithImages": len(image_pages),
        "totalImages": total_images,
        "imagesPerPage": image_pages,
        "tablePages": table_pages,
        "outline": read_outline(path),
        "charFonts": [
            {"font": key[0], "size": key[1], "count": count}
            for key, count in sorted(fonts.items(), key=lambda kv: -kv[1])
        ],
        "charSizes": [
            {"size": size, "count": count}
            for size, count in sorted(sizes.items(), key=lambda kv: -kv[1])
        ],
        "lineStyles": [
            {"size": key[0], "font": key[1], "bold": key[2], "lines": count}
            for key, count in sorted(line_sizes.items(), key=lambda kv: -kv[1])
        ],
        "leftEdges": [
            {"x0": edge, "lines": count}
            for edge, count in sorted(left_edges.items(), key=lambda kv: -kv[1])[:15]
        ],
        "lineGaps": [
            {"gap": gap, "count": count}
            for gap, count in sorted(line_gaps.items(), key=lambda kv: -kv[1])[:15]
        ],
        "nonAsciiGlyphs": [
            {"glyph": glyph, "codepoint": f"U+{ord(glyph):04X}", "count": count}
            for glyph, count in glyphs.most_common(40)
        ],
        "pageDumps": dumps,
    }


def print_report(report: dict) -> None:
    print(f"path            : {report['path']}")
    print(f"pages           : {report['pageCount']}")
    print(f"characters      : {report['totalCharacters']}")
    print(f"low-text pages  : {len(report['lowTextPages'])} {report['lowTextPages'][:40]}")
    print(f"pages w/ images : {report['pagesWithImages']}  images: {report['totalImages']}")
    print(f"pages w/ tables : {len(report['tablePages'])} {list(report['tablePages'])[:20]}")

    print("\n=== OUTLINE ===")
    for entry in report["outline"][:80]:
        print("  " * entry["depth"], "-", entry["title"])

    print("\n=== LINE STYLES (size, font, all-bold) ===")
    for style in report["lineStyles"][:20]:
        print(f"{style['size']:>6}  {style['font']:<28} bold={str(style['bold']):<5} lines={style['lines']}")

    print("\n=== LEFT EDGES ===")
    for edge in report["leftEdges"]:
        print(f"x0={edge['x0']:>7}  lines={edge['lines']}")

    print("\n=== LINE GAPS ===")
    for gap in report["lineGaps"]:
        print(f"gap={gap['gap']:>7}  count={gap['count']}")

    print("\n=== NON-ASCII GLYPHS ===")
    for glyph in report["nonAsciiGlyphs"]:
        print(f"{glyph['glyph']!r} {glyph['codepoint']} x{glyph['count']}")

    for number, lines in report["pageDumps"].items():
        print(f"\n########## PAGE {number} ##########")
        for line in lines:
            print(line)


def main() -> int:
    parser = argparse.ArgumentParser(description="Inspect a PDF's text/layout structure.")
    parser.add_argument("--pdf", required=True)
    parser.add_argument("--dump-pages", default="", help="Comma list or ranges, e.g. 1,20,32-34")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--out", default="", help="Optional file to write the report to")
    args = parser.parse_args()

    reader = PdfReader(args.pdf)
    pages = parse_page_list(args.dump_pages, len(reader.pages))
    report = build_report(args.pdf, pages)

    if args.json:
        payload = json.dumps(report, indent=2, ensure_ascii=False)
        if args.out:
            with open(args.out, "w", encoding="utf-8") as handle:
                handle.write(payload)
        else:
            print(payload)
        return 0

    if args.out:
        import io

        buffer = io.StringIO()
        stdout = sys.stdout
        sys.stdout = buffer
        try:
            print_report(report)
        finally:
            sys.stdout = stdout
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(buffer.getvalue())
        print(buffer.getvalue())
    else:
        print_report(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
