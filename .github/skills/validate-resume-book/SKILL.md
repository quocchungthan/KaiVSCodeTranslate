---
name: validate-resume-book
description: "Validate translated Markdown/PDF, inspect representative pages, classify failures, and safely resume interrupted or failed PDF translation jobs."
---

# Validate And Resume Book

## Validate

1. Mark `validate` running.
2. Run `.github/tools/Test-TranslatedBook.ps1 -JobId <id> -Pdf <repository-relative-pdf>`.
3. Treat missing files, broken relative links, unbalanced fences, unresolved omission markers, empty PDF, or incomplete chunks as failures.
4. Review warnings for heading-count drift and missing Vietnamese characters against the source; resolve or document each one.
5. With an already-installed local PDF rasterizer, render and visually inspect at least the first, middle, and last pages plus dense tables/code/equations. Check clipping, blank pages, fonts/diacritics, captions, image order, headers, footers, and page numbering.
6. Confirm text can be extracted from representative PDF pages and that the table of contents and internal links work when supported.
7. Mark `validate` completed only when automated and visual gates pass.

## Resume

1. Run `Initialize-TranslationJobs.ps1`, then `Get-NextTranslationAction.ps1`.
2. Trust completed durable units. Retry only the returned failed/pending stage or chunk.
3. If source hash or pipeline version changed, use the newly initialized state; never mix stale chunks with a new pipeline.
4. If blocked, record the exact prerequisite and proposed safe action. Continue another actionable job before stopping.