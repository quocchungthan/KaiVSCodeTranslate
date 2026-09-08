---
name: chunk-technical-book
description: "Split converted technical Markdown into ordered semantic chapter/section chunks with bounded fallback sizes. Use after PDF conversion and before translation."
---

# Chunk Technical Book

1. Mark `chunk` running.
2. Run `.github/tools/Split-MarkdownIntoChunks.ps1 -JobId <id>`.
3. The script prefers level 1-3 heading boundaries, respects fenced code while detecting headings, packs toward configured target size, and falls back to paragraph/character boundaries for oversized prose.
4. Review any reported oversized chunk. An indivisible oversized code fence is permitted only when splitting it would damage source fidelity.
5. Verify chunk order, first/last boundaries, total heading coverage, and that no content disappeared or duplicated.
6. Mark `chunk` completed. The registered manifest and `job.json` are the resume boundary.

Do not manually replace a registered manifest. Change `pipelineVersion` when chunking behavior changes so derived state is invalidated safely.