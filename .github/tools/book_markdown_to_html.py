"""Render a translated book Markdown file into a print-ready HTML document.

Uses markdown-it-py (CommonMark) plus Pygments for code highlighting and emits a single
self-contained HTML file with print CSS (A4, configurable margins, Noto font stack, table of
contents with anchors). The HTML is intended to be converted to PDF by a local headless
Chromium via --print-to-pdf.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
import unicodedata

from markdown_it import MarkdownIt

TEMPLATE = """<!DOCTYPE html>
<html lang="{lang}">
<head>
<meta charset="utf-8">
<title>{title}</title>
<style>
@page {{
  size: {page_size};
  margin: {margin}mm;
}}
html {{ -webkit-print-color-adjust: exact; print-color-adjust: exact; }}
body {{
  font-family: {body_font};
  font-size: 10.5pt;
  line-height: 1.55;
  color: #14171a;
  margin: 0;
  text-align: justify;
  hyphens: auto;
}}
h1, h2, h3, h4, h5, h6 {{
  font-family: {sans_font};
  color: #0f1b2d;
  line-height: 1.25;
  text-align: left;
  break-after: avoid-page;
  page-break-after: avoid;
}}
h1 {{ font-size: 22pt; margin: 0 0 1.2em; break-before: page; page-break-before: always; }}
h1.book-title {{ break-before: auto; page-break-before: auto; font-size: 28pt; margin-top: 18vh; text-align: center; }}
h2 {{ font-size: 17pt; margin: 0 0 0.8em; break-before: page; page-break-before: always;
      border-bottom: 2px solid #d7dee8; padding-bottom: 0.3em; }}
h3 {{ font-size: 13.5pt; margin: 1.6em 0 0.6em; }}
h4 {{ font-size: 11.5pt; margin: 1.3em 0 0.5em; color: #33405a; }}
h5, h6 {{ font-size: 10.5pt; margin: 1.1em 0 0.4em; color: #33405a; }}
p {{ margin: 0 0 0.75em; orphans: 2; widows: 2; }}
ul, ol {{ margin: 0 0 0.85em 1.3em; padding: 0; }}
li {{ margin-bottom: 0.3em; }}
img {{ max-width: 100%; height: auto; display: block; margin: 1em auto; break-inside: avoid; page-break-inside: avoid; }}
a {{ color: #14417a; text-decoration: none; word-break: break-word; }}
code {{ font-family: {mono_font}; font-size: 0.88em; background: #f2f4f7; padding: 0.1em 0.3em; border-radius: 3px; }}
pre {{ font-family: {mono_font}; font-size: 8.5pt; line-height: 1.4; background: #f7f9fb;
       border: 1px solid #dfe5ec; border-radius: 4px; padding: 0.7em 0.9em; overflow-wrap: break-word;
       white-space: pre-wrap; break-inside: avoid; page-break-inside: avoid; }}
pre code {{ background: none; padding: 0; }}
table {{ border-collapse: collapse; width: 100%; margin: 1em 0; font-size: 9.5pt;
         break-inside: avoid; page-break-inside: avoid; }}
th, td {{ border: 1px solid #cbd4e0; padding: 0.4em 0.55em; text-align: left; vertical-align: top; }}
th {{ background: #eef2f7; font-family: {sans_font}; }}
blockquote {{ margin: 1em 0; padding: 0.2em 1em; border-left: 3px solid #c3ccd9; color: #40506b; }}
hr {{ border: none; border-top: 1px solid #d7dee8; margin: 1.6em 0; }}
nav.toc {{ break-after: page; page-break-after: always; }}
nav.toc h2 {{ break-before: auto; page-break-before: auto; }}
nav.toc ol {{ list-style: none; margin-left: 0; }}
nav.toc li {{ margin-bottom: 0.25em; }}
nav.toc li.lvl-3 {{ margin-left: 1.2em; font-size: 0.95em; color: #40506b; }}
nav.toc li.lvl-4 {{ margin-left: 2.4em; font-size: 0.9em; color: #5a6880; }}
</style>
</head>
<body>
{body}
</body>
</html>
"""


def slugify(text: str, used: dict) -> str:
    normalized = unicodedata.normalize("NFD", text)
    ascii_text = "".join(c for c in normalized if unicodedata.category(c) != "Mn")
    ascii_text = ascii_text.replace("đ", "d").replace("Đ", "D")
    slug = re.sub(r"[^a-zA-Z0-9]+", "-", ascii_text).strip("-").lower() or "section"
    count = used.get(slug, 0)
    used[slug] = count + 1
    return slug if count == 0 else f"{slug}-{count}"


def build(markdown_path: str, html_path: str, title: str, lang: str, page_size: str,
          margin: int, body_font: str, sans_font: str, mono_font: str,
          toc_levels: tuple[int, ...]) -> dict:
    with open(markdown_path, encoding="utf-8") as handle:
        source = handle.read()

    parser = MarkdownIt("commonmark", {"html": False, "linkify": False, "typographer": False})
    parser.enable("table")
    parser.enable("strikethrough")
    tokens = parser.parse(source)

    used_slugs: dict = {}
    toc_entries: list[tuple[int, str, str]] = []
    for index, token in enumerate(tokens):
        if token.type != "heading_open":
            continue
        level = int(token.tag[1])
        inline = tokens[index + 1]
        text = "".join(child.content for child in (inline.children or []) if child.type in ("text", "code_inline"))
        slug = slugify(text, used_slugs)
        token.attrSet("id", slug)
        if level in toc_levels:
            toc_entries.append((level, text, slug))

    rendered = parser.renderer.render(tokens, parser.options, {})

    toc_items = "\n".join(
        f'<li class="lvl-{level}"><a href="#{slug}">{html.escape(text)}</a></li>'
        for level, text, slug in toc_entries
    )
    toc = f'<nav class="toc"><h2 id="toc">Mục lục</h2><ol>{toc_items}</ol></nav>' if toc_items else ""

    # Promote the first h1 to a cover title and place the generated TOC right after it.
    match = re.search(r"<h1([^>]*)>(.*?)</h1>", rendered, flags=re.DOTALL)
    if match:
        cover = f'<h1{match.group(1)} class="book-title">{match.group(2)}</h1>'
        rendered = rendered[:match.start()] + cover + toc + rendered[match.end():]
    else:
        rendered = toc + rendered

    document = TEMPLATE.format(
        lang=lang,
        title=html.escape(title),
        page_size=page_size,
        margin=margin,
        body_font=body_font,
        sans_font=sans_font,
        mono_font=mono_font,
        body=rendered,
    )

    os.makedirs(os.path.dirname(html_path) or ".", exist_ok=True)
    with open(html_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(document)

    images = re.findall(r'<img[^>]+src="([^"]+)"', rendered)
    base = os.path.dirname(os.path.abspath(markdown_path))
    missing = sorted({src for src in images if not src.startswith(("http:", "https:", "data:"))
                      and not os.path.exists(os.path.join(base, src))})

    return {
        "markdown": markdown_path,
        "html": html_path,
        "characters": len(source),
        "headings": len(used_slugs),
        "tocEntries": len(toc_entries),
        "images": len(images),
        "missingImages": missing,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Render book Markdown to print-ready HTML.")
    parser.add_argument("--markdown", required=True)
    parser.add_argument("--html", required=True)
    parser.add_argument("--title", default="")
    parser.add_argument("--lang", default="vi")
    parser.add_argument("--page-size", default="A4")
    parser.add_argument("--margin", type=int, default=22)
    parser.add_argument("--body-font", default="'Noto Serif', 'Times New Roman', serif")
    parser.add_argument("--sans-font", default="'Noto Sans', 'Segoe UI', sans-serif")
    parser.add_argument("--mono-font", default="'Noto Sans Mono', Consolas, monospace")
    parser.add_argument("--toc-levels", default="2,3")
    args = parser.parse_args()

    levels = tuple(int(v) for v in args.toc_levels.split(",") if v.strip())
    summary = build(args.markdown, args.html, args.title or "Book", args.lang, args.page_size,
                    args.margin, args.body_font, args.sans_font, args.mono_font, levels)
    sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
