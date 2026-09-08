"""Rasterize selected PDF pages to PNG images for visual quality inspection."""

from __future__ import annotations

import argparse
import json
import os
import sys

import pypdfium2 as pdfium


def parse_pages(value: str, page_count: int) -> list[int]:
    if not value or value.strip().lower() == "auto":
        middle = max(1, (page_count + 1) // 2)
        return sorted({1, middle, page_count})
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
    return sorted({p for p in pages if 1 <= p <= page_count})


def main() -> int:
    parser = argparse.ArgumentParser(description="Render PDF pages to PNG for inspection.")
    parser.add_argument("--pdf", required=True)
    parser.add_argument("--out-dir", required=True)
    parser.add_argument("--pages", default="auto", help="'auto' for first/middle/last, or 1,5,20-22")
    parser.add_argument("--scale", type=float, default=2.0)
    args = parser.parse_args()

    document = pdfium.PdfDocument(args.pdf)
    pages = parse_pages(args.pages, len(document))
    os.makedirs(args.out_dir, exist_ok=True)

    written = []
    for number in pages:
        image = document[number - 1].render(scale=args.scale).to_pil()
        target = os.path.join(args.out_dir, f"page-{number:04d}.png")
        image.save(target)
        written.append({"page": number, "path": target, "width": image.width, "height": image.height})

    sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps({"pdf": args.pdf, "pageCount": len(document), "rendered": written},
                     indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
