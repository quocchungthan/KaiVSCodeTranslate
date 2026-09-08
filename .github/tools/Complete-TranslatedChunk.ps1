[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [Parameter(Mandatory = $true)]
    [string]$ChunkId,

    [Parameter(Mandatory = $true)]
    [string]$ContextFile,

    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$contextPath = if ([System.IO.Path]::IsPathRooted($ContextFile)) { $ContextFile } else { Join-Path $RepositoryRoot $ContextFile }
$context = Read-TranslationJson -Path $contextPath

$translated = "_processing/jobs/$JobId/source/translated-$ChunkId.vi.md"
if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $translated) -PathType Leaf)) {
    throw "Translated chunk does not exist: $translated"
}

$toJson = {
    param($value)
    if ($null -eq $value) { return '[]' }
    return (ConvertTo-Json -InputObject @($value) -Depth 8 -Compress)
}

& (Join-Path $PSScriptRoot 'Save-ChunkContext.ps1') `
    -JobId $JobId `
    -ChunkId $ChunkId `
    -VietnameseMarkdown $translated `
    -BackwardSummary $context.backwardSummary `
    -Handoff $context.handoff `
    -TerminologyJson (& $toJson $context.terminologyDecisions) `
    -FormatDecisionsJson (& $toJson $context.formatDecisions) `
    -UnresolvedReferencesJson (& $toJson $context.unresolvedReferences) `
    -RepositoryRoot $RepositoryRoot | Out-Null

Remove-Item -LiteralPath $contextPath -Force
Write-Output "chunk $ChunkId completed"
