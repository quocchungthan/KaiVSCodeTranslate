---
name: discover-initialize-book-jobs
description: "Discover pending PDFs and initialize stable SHA-256 translation jobs. Use at startup, after adding or changing a PDF, or when pipeline defaults change."
---

# Discover And Initialize Book Jobs

1. Run `.github/tools/Initialize-TranslationJobs.ps1` from the repository with PowerShell.
2. Review `created`, `unchanged`, and `invalidated` results. A changed source gets a new hash-based job and the prior same-path job becomes `superseded`.
3. Run `.github/tools/Get-NextTranslationAction.ps1` and route to the returned stage.

Do not rename, move, rewrite, or metadata-normalize files in `_pdfs/`. The SHA-256 digest plus a sanitized basename defines stable job identity. A pipeline-version change invalidates derived state while retaining the job history directory.