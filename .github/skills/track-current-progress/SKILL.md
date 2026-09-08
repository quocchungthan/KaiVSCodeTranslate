---
name: track-current-progress
description: "Inspect and update durable PDF translation job, stage, chunk, retry, blocker, and artifact progress. Use when deciding what Huong should do next or reporting resumable status."
---

# Track Current Progress

1. Run `.github/tools/Get-NextTranslationAction.ps1`. It initializes discovery unless `-SkipDiscovery` is explicitly supplied.
2. Read only the selected `_processing/jobs/<job-id>/job.json` for details.
3. Before stage work, call `Update-TranslationState.ps1 -Status running`; before chunk work, call `Update-TranslationChunkState.ps1 -Status running`.
4. Record stage completion and its repository-relative artifact path with `Update-TranslationState.ps1`.
5. On exceptions, record `failed` with a concise message and useful non-secret detail. Use `blocked` for missing approval, credentials, binaries, fonts, or unreadable input.
6. Query the next action after every durable completion. Do not skip incomplete predecessors or reopen completed work without pipeline invalidation.

`job.json` is authoritative. Files without corresponding state are incomplete artifacts, not proof of progress.
