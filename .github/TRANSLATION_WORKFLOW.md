# Translation Workflow

Huong processes local PDFs through five durable stages:

`convert -> chunk -> translate -> assemble -> validate`

Start or resume with PowerShell:

```powershell
& .\.github\tools\Initialize-TranslationJobs.ps1
& .\.github\tools\Get-NextTranslationAction.ps1
```

State lives in `_processing/jobs/<job-id>/job.json`. Generated source Markdown and translated chunks stay under that job; final Markdown, assets, PDF, and `qc.json` go to `_output/<job-id>/`. These directories remain gitignored.

Pipeline behavior and pinned tool candidates are in `.github/translation-pipeline.defaults.json`. Increment `pipelineVersion` when a conversion, chunking, translation, or rendering rule changes enough to invalidate derived output.

Run the dependency-free foundation test with:

```powershell
& .\.github\tools\Test-TranslationPipeline.ps1
```

External prerequisites are intentionally not installed by this repository: local Docling/Tesseract for extraction and OCR, Pandoc/Typst for rendering, Noto Vietnamese-capable fonts, and a local PDF rasterizer for visual QC. Huong probes first and requests approval before downloads or installation.