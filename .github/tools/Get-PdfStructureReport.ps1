[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Job')]
    [string]$JobId,

    [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
    [string]$PdfPath,

    [string]$DumpPages,
    [string]$RepositoryRoot,
    [string]$PythonCommand = 'python',
    [switch]$AsJson,
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

if ($PSCmdlet.ParameterSetName -eq 'Job') {
    $statePath = Join-Path $RepositoryRoot "_processing\jobs\$JobId\job.json"
    $state = Read-TranslationJobState -Path $statePath
    $PdfPath = Join-Path $RepositoryRoot ($state.source.relativePath -replace '/', '\')
}

if (-not (Test-Path -LiteralPath $PdfPath -PathType Leaf)) {
    throw "PDF does not exist: $PdfPath"
}

$python = Get-Command $PythonCommand -ErrorAction SilentlyContinue
if (-not $python) {
    throw "Python command '$PythonCommand' was not found."
}

$arguments = @((Join-Path $PSScriptRoot 'pdf_structure_report.py'), '--pdf', $PdfPath)
if (-not [string]::IsNullOrWhiteSpace($DumpPages)) { $arguments += @('--dump-pages', $DumpPages) }
if ($AsJson) { $arguments += '--json' }
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) { $arguments += @('--out', $OutputPath) }

$previousEncoding = $env:PYTHONIOENCODING
$env:PYTHONIOENCODING = 'utf-8'
try {
    & $python.Source @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    $env:PYTHONIOENCODING = $previousEncoding
}

if ($exitCode -ne 0) {
    throw "PDF structure report failed with exit code $exitCode."
}
