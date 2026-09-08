[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [string]$RepositoryRoot,
    [string]$PythonCommand = 'python',
    [string]$Title,
    [int]$ProfileSamplePages = 60,
    [switch]$SkipStateUpdate
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$jobDirectory = Join-Path $RepositoryRoot "_processing\jobs\$JobId"
$statePath = Join-Path $jobDirectory 'job.json'
$state = Read-TranslationJobState -Path $statePath

$pdfPath = Join-Path $RepositoryRoot ($state.source.relativePath -replace '/', '\')
if (-not (Test-Path -LiteralPath $pdfPath -PathType Leaf)) {
    throw "Source PDF does not exist: $pdfPath"
}

$python = Get-Command $PythonCommand -ErrorAction SilentlyContinue
if (-not $python) {
    throw "Python command '$PythonCommand' was not found. Install Python 3.10+ or pass -PythonCommand."
}

$missing = & $python.Source -c @'
import importlib.util as u
print(",".join(m for m in ("pdfplumber", "pypdf", "PIL") if u.find_spec(m) is None))
'@
if ($LASTEXITCODE -ne 0) { throw 'Failed to probe Python packages.' }
if (-not [string]::IsNullOrWhiteSpace($missing)) {
    throw "Missing required Python packages: $missing. Install them before running the convert stage."
}

$sourceDirectory = Join-Path $jobDirectory 'source'
$assetsDirectory = Join-Path $sourceDirectory 'assets'
$markdownPath = Join-Path $sourceDirectory 'book.md'
$logPath = Join-Path $jobDirectory 'logs\convert.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $logPath) -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = [System.IO.Path]::GetFileNameWithoutExtension($pdfPath)
}

if (-not $SkipStateUpdate) {
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -JobId $JobId -Stage convert -Status running `
        -Message 'Converting PDF with local pdfplumber/pypdf adapter' -RepositoryRoot $RepositoryRoot | Out-Null
}

$scriptPath = Join-Path $PSScriptRoot 'pdf_to_markdown.py'
$arguments = @(
    $scriptPath
    '--pdf', $pdfPath
    '--markdown', $markdownPath
    '--assets', $assetsDirectory
    '--asset-prefix', 'fig'
    '--title', $Title
    '--profile-sample-pages', $ProfileSamplePages
)

$previousEncoding = $env:PYTHONIOENCODING
$env:PYTHONIOENCODING = 'utf-8'
try {
    $output = & $python.Source @arguments 2>&1
    $exitCode = $LASTEXITCODE
}
finally {
    $env:PYTHONIOENCODING = $previousEncoding
}

$outputText = ($output | Out-String)
if ($exitCode -ne 0) {
    if (-not $SkipStateUpdate) {
        & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -JobId $JobId -Stage convert -Status failed `
            -ErrorMessage 'PDF to Markdown conversion failed.' -ErrorDetail $outputText -RepositoryRoot $RepositoryRoot | Out-Null
    }
    throw "Conversion failed with exit code ${exitCode}:`n$outputText"
}

Set-Content -LiteralPath $logPath -Value $outputText -Encoding UTF8
$summary = $outputText | ConvertFrom-Json

$relativeMarkdown = "_processing/jobs/$JobId/source/book.md"
if (-not $SkipStateUpdate) {
    $message = "Converted $($summary.pageCount) pages, $($summary.characters) chars, $($summary.imagesWritten) assets."
    if ($summary.warnings.Count -gt 0) {
        $message += " Warnings: $($summary.warnings -join '; ')"
    }
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -JobId $JobId -Stage convert -Status completed `
        -Message $message -ArtifactName 'sourceMarkdown' -ArtifactRelativePath $relativeMarkdown `
        -RepositoryRoot $RepositoryRoot | Out-Null
}

return $summary
