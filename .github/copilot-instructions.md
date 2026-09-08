# Kai VS Code Translate

## Purpose

- This workspace converts technical-book PDFs from `_pdfs/` into Vietnamese Markdown and PDF under `_output/`.
- Treat `_processing/jobs/<job-id>/job.json` as authoritative pipeline state. Do not infer progress from file presence alone.
- Never modify source files in `_pdfs/`.

## Execution

- On Windows, use PowerShell-native commands only. Run deterministic job, state, chunk, context, assembly, and QC operations through `.github/tools/*.ps1`.
- Make writes atomic and reruns idempotent. Record failures before retrying and resume the earliest actionable stage or chunk.
- Preserve `_pdfs/`, `_processing/`, and `_output/` entries in `.gitignore`.

## Privacy And Tools

- Keep source-book content local. Do not upload PDFs, extracted text, images, or translations to external services unless the user explicitly authorizes that destination.
- Prefer already-installed open-source tools. Verify source, license, and pinned version from `.github/translation-pipeline.defaults.json` before adoption.
- Ask before any machine-wide install, network package download, destructive action, or external content transfer. Never place secrets in files, command arguments, state, or logs.
- Record web documentation consulted for a job with `.github/tools/Add-TranslationResearchSource.ps1`.

## Translation Quality

- Translate faithfully into natural technical Vietnamese. Never add claims or restore content absent from the source.
- Preserve headings, lists, tables, code, equations, captions, links, anchors, and image order as far as the source conversion allows.
- Process chunks sequentially and persist terminology, formatting decisions, summaries, unresolved references, and next-chunk handoff context after every completed chunk.