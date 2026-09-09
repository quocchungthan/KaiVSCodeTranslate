<#
.SYNOPSIS
    Converts a scanned (image-only) technical-book PDF to structured Markdown offline.

.DESCRIPTION
    Runs entirely on the local machine:
      1. ocr_prepare.py stitches the extracted page tiles into full-height strips and
         cuts them into OCR-safe bands on blank pixel rows.
      2. Invoke-WindowsOcr.ps1 recognizes text with the OS Windows.Media.Ocr engine.
      3. ocr_to_markdown.py rebuilds headings, paragraphs, lists and figure crops from
         the OCR line geometry.

    Used when a PDF has no text layer and no Tesseract/Docling install is available.
    No source-book content leaves the machine.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$JobId,
    [string]$RepositoryRoot,
    [string]$PythonCommand = 'python',
    [string]$Title,
    [double]$OcrScale = 2.0,
    [switch]$SkipPrepare,
    [switch]$SkipOcr
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$jobDirectory = Join-Path $RepositoryRoot "_processing\jobs\$JobId"
$state = Read-TranslationJobState -Path (Join-Path $jobDirectory 'job.json')
if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = [System.IO.Path]::GetFileNameWithoutExtension($state.source.relativePath)
}

$python = Get-Command $PythonCommand -ErrorAction Stop
$sourceDirectory = Join-Path $jobDirectory 'source'
$tileDirectory = Join-Path $sourceDirectory 'assets'
$workDirectory = Join-Path $jobDirectory 'work'
$bandDirectory = Join-Path $workDirectory 'bands'
$manifestPath = Join-Path $workDirectory 'bands.manifest.json'
$ocrDirectory = Join-Path $workDirectory 'ocr'
$stagedAssets = Join-Path $workDirectory 'assets2'
$stagedMarkdown = Join-Path $workDirectory 'book.md'

if (-not (Test-Path -LiteralPath $tileDirectory)) {
    throw "Extracted page tiles were not found at $tileDirectory. Run Convert-PdfToMarkdown.ps1 first."
}

if (-not $SkipPrepare) {
    & $python.Source (Join-Path $PSScriptRoot 'ocr_prepare.py') `
        --assets $tileDirectory --work $bandDirectory --manifest $manifestPath --scale $OcrScale
    if ($LASTEXITCODE -ne 0) { throw 'Band preparation failed.' }
}

if (-not $SkipOcr) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-WindowsOcr.ps1') `
        -ManifestPath ([System.IO.Path]::GetFullPath($manifestPath)) `
        -OutputDirectory ([System.IO.Path]::GetFullPath($ocrDirectory))
    if ($LASTEXITCODE -ne 0) { throw 'Windows OCR pass failed.' }
}

if (Test-Path -LiteralPath $stagedAssets) { Remove-Item -LiteralPath $stagedAssets -Recurse -Force }
$summaryJson = & $python.Source (Join-Path $PSScriptRoot 'ocr_to_markdown.py') `
    --manifest $manifestPath --ocr $ocrDirectory --assets $stagedAssets --markdown $stagedMarkdown --title $Title
if ($LASTEXITCODE -ne 0) { throw 'Markdown reconstruction failed.' }
$summary = $summaryJson | ConvertFrom-Json

Get-ChildItem -LiteralPath $tileDirectory -Filter 'fig-p*' -File | Remove-Item -Force
Get-ChildItem -LiteralPath $stagedAssets -File | Move-Item -Destination $tileDirectory -Force
Move-Item -LiteralPath $stagedMarkdown -Destination (Join-Path $sourceDirectory 'book.md') -Force
Remove-Item -LiteralPath $stagedAssets -Recurse -Force

$logPath = Join-Path $jobDirectory 'logs\convert-ocr.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $logPath) -Force | Out-Null
$summaryJson | Set-Content -LiteralPath $logPath -Encoding UTF8

return $summary
