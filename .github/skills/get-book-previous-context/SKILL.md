---
name: get-book-previous-context
description: "Load bounded prior summaries, terminology, formatting decisions, unresolved references, and the immediate handoff before translating the next technical-book chunk."
---

# Get Book Previous Context

1. Run `.github/tools/Get-PreviousChunkContext.ps1 -JobId <id> -ChunkId <id>` immediately before reading the current source chunk.
2. Treat `immediateHandoff` as local continuity guidance, not source content.
3. Apply accumulated terminology and formatting decisions consistently unless the current source requires a documented correction.
4. Use recent summaries only to resolve backward references. Never translate text from summaries in place of the current source.
5. Carry unresolved references forward until the source resolves them; do not guess.

The default window is three summaries to bound context. Increase `-SummaryWindow` only for a specific cross-chapter dependency.
