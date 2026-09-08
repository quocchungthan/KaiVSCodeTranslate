---
name: assemble-render-book
description: "Assemble completed Vietnamese chunks and assets, then render a polished Vietnamese technical-book PDF with pinned local open-source tools and fonts."
---

# Assemble And Render Book

1. Mark `assemble` running.
2. Run `.github/tools/Merge-TranslatedBook.ps1 -JobId <id>`. It orders chunks from state, writes `_output/<job-id>/book.vi.md`, and copies extracted assets.
3. Verify all relative links resolve before rendering.
4. Probe pinned Pandoc, Typst, and Noto fonts. If unavailable, use `write-and-execute-tools`; do not install without approval.
5. Render locally with A4 paper, configured margins, Noto Serif body, Noto Sans headings, Noto Sans Mono code, table of contents, numbered sections, syntax highlighting, wrapped tables/code, captions, and retained image aspect ratios. Inspect installed command help before invoking version-specific flags.
6. Write `_output/<job-id>/book.vi.pdf`. Do not overwrite the source or place generated artifacts in `_pdfs/`.
7. Mark `assemble` completed with `translatedMarkdown` and `translatedPdf` artifact paths.

If a renderer cannot preserve a construct, record the limitation and choose the least destructive representation; never drop the content silently.