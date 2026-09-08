---
name: convert-pdf-to-markdown
description: "Convert a local technical-book PDF to structure-preserving Markdown, extract assets, and apply OCR only where needed. Use for the convert stage of a translation job."
---

# Convert PDF To Markdown

1. Mark `convert` running with `Update-TranslationState.ps1`.
2. Resolve the source path from `job.json`; never alter or upload it.
3. Probe the pinned local Docling adapter first. Inspect its installed `--help` and version before forming the command. If absent, load `write-and-execute-tools` and record the stage blocked pending approval.
4. Write conversion output to `_processing/jobs/<job-id>/source/book.md` and binary assets to the adjacent `source/assets/` directory.
5. Use local OCR only for scanned/image-only pages, missing text regions, or clearly corrupt extraction. Preserve page order and record which pages were OCR-derived.
6. Normalize image references in `book.md` to `assets/<file>` using unique deterministic names. Keep links relative and verify every referenced local asset exists.
7. Compare page/section coverage, heading hierarchy, tables, code fences, equations, captions, footnotes, and reading order against the source. Never reconstruct unreadable content by guessing.
8. Mark the stage completed with artifact `sourceMarkdown`; on failure record the command-independent error and retry guidance.

Keep any raw extraction logs under the job directory and strip secrets or machine-specific credentials.