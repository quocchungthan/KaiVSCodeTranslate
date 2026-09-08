---
name: translate-book-chunk
description: "Translate one ordered technical-book Markdown chunk into Vietnamese with terminology continuity and durable handoff context. Use for each translate-chunk action."
---

# Translate Book Chunk

1. Mark the selected chunk running with `Update-TranslationChunkState.ps1`.
2. Load `get-book-previous-context`, then read the selected source Markdown.
3. Translate all prose faithfully into natural technical Vietnamese. Keep heading levels, list nesting, tables, block structure, anchors, link destinations, code fences, code, equations, identifiers, numbering, captions, and image placement.
4. Use `assets/...` links in translated chunks so they remain correct in the assembled output. Translate link labels and captions, not URLs or filenames.
5. Never silently omit or invent content. Put unreadable or unresolved source references into context; use a clear source-faithful marker only when necessary.
6. Write `_processing/jobs/<job-id>/source/translated-<chunk-id>.vi.md`.
7. Call `Save-ChunkContext.ps1` with a backward-looking summary, terminology decisions, format decisions, unresolved references, and a concise handoff for the next chunk.
8. On failure call `Update-TranslationChunkState.ps1 -Status failed -ErrorMessage ...`. Retry from the unchanged source chunk and previous durable context.

Read one source chunk at a time. Do not use future chunks to embellish current content.