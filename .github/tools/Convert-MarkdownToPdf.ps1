[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MarkdownPath,

    [Parameter(Mandatory = $true)]
    [string]$PdfPath,

    [string]$Title,
    [string]$Language = 'vi',
    [string]$RepositoryRoot,
    [string]$PythonCommand = 'python',
    [string]$BrowserPath,
    [switch]$KeepHtml
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$config = Get-TranslationPipelineConfig -RepositoryRoot $RepositoryRoot
$pageSize = if ($config.rendering.pageSize) { $config.rendering.pageSize } else { 'A4' }
$margin = if ($config.rendering.marginMillimeters) { [int]$config.rendering.marginMillimeters } else { 22 }
$bodyFont = "'$($config.rendering.bodyFont)', 'Times New Roman', serif"
$sansFont = "'$($config.rendering.sansFont)', 'Segoe UI', sans-serif"
$monoFont = "'$($config.rendering.monoFont)', Consolas, monospace"

$resolvedMarkdown = if ([System.IO.Path]::IsPathRooted($MarkdownPath)) { $MarkdownPath } else { Join-Path $RepositoryRoot $MarkdownPath }
$resolvedPdf = if ([System.IO.Path]::IsPathRooted($PdfPath)) { $PdfPath } else { Join-Path $RepositoryRoot $PdfPath }
if (-not (Test-Path -LiteralPath $resolvedMarkdown -PathType Leaf)) {
    throw "Markdown does not exist: $resolvedMarkdown"
}

$python = Get-Command $PythonCommand -ErrorAction SilentlyContinue
if (-not $python) { throw "Python command '$PythonCommand' was not found." }

$missing = & $python.Source -c "import importlib.util as u; print('markdown_it' if u.find_spec('markdown_it') is None else '')"
if (-not [string]::IsNullOrWhiteSpace($missing)) {
    throw "Missing required Python package: markdown-it-py."
}

if ([string]::IsNullOrWhiteSpace($BrowserPath)) {
    $candidates = @(
        'C:\Program Files\Google\Chrome\Application\chrome.exe',
        'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe',
        'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
    )
    $BrowserPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($BrowserPath)) {
    throw 'No local Chromium browser found for headless PDF rendering. Pass -BrowserPath explicitly.'
}

if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = [System.IO.Path]::GetFileNameWithoutExtension($resolvedMarkdown)
}

$htmlPath = [System.IO.Path]::ChangeExtension($resolvedMarkdown, '.print.html')
$previousEncoding = $env:PYTHONIOENCODING
$env:PYTHONIOENCODING = 'utf-8'
try {
    $output = & $python.Source (Join-Path $PSScriptRoot 'book_markdown_to_html.py') `
        --markdown $resolvedMarkdown --html $htmlPath --title $Title --lang $Language `
        --page-size $pageSize --margin $margin `
        --body-font $bodyFont --sans-font $sansFont --mono-font $monoFont 2>&1
    $exitCode = $LASTEXITCODE
}
finally {
    $env:PYTHONIOENCODING = $previousEncoding
}
if ($exitCode -ne 0) {
    throw "HTML generation failed with exit code ${exitCode}:`n$($output | Out-String)"
}
$summary = ($output | Out-String) | ConvertFrom-Json

New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedPdf) -Force | Out-Null
if (Test-Path -LiteralPath $resolvedPdf) { Remove-Item -LiteralPath $resolvedPdf -Force }

$profileDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("book-render-" + [Guid]::NewGuid().ToString('N'))
$fileUri = ([Uri]$htmlPath).AbsoluteUri
$arguments = @(
    '--headless=new'
    '--disable-gpu'
    '--no-sandbox'
    '--no-first-run'
    '--no-pdf-header-footer'
    '--run-all-compositor-stages-before-draw'
    '--virtual-time-budget=120000'
    "--user-data-dir=$profileDirectory"
    "--print-to-pdf=$resolvedPdf"
    $fileUri
)

try {
    $process = Start-Process -FilePath $BrowserPath -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "Headless browser exited with code $($process.ExitCode)."
    }
}
finally {
    Remove-Item -LiteralPath $profileDirectory -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $KeepHtml) { Remove-Item -LiteralPath $htmlPath -Force -ErrorAction SilentlyContinue }
}

if (-not (Test-Path -LiteralPath $resolvedPdf -PathType Leaf)) {
    throw "Renderer did not produce a PDF: $resolvedPdf"
}

return [pscustomobject]@{
    markdown      = $summary.markdown
    pdf           = $resolvedPdf
    browser       = $BrowserPath
    pageSize      = $pageSize
    marginMm      = $margin
    headings      = $summary.headings
    tocEntries    = $summary.tocEntries
    images        = $summary.images
    missingImages = $summary.missingImages
    pdfBytes      = (Get-Item -LiteralPath $resolvedPdf).Length
}
