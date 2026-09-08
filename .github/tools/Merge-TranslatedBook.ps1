[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$statePath = Join-Path $RepositoryRoot "_processing\jobs\$JobId\job.json"
$state = Read-TranslationJobState -Path $statePath
$incomplete = @($state.chunks | Where-Object { $_.status -ne 'completed' })
if ($state.chunks.Count -eq 0 -or $incomplete.Count -gt 0) {
    throw 'All registered chunks must be complete before assembly.'
}
for ($index = 0; $index -lt $state.chunks.Count; $index++) {
    $sequence = 0
    if (-not [int]::TryParse([string]$state.chunks[$index].sequence, [ref]$sequence) -or $sequence -ne ($index + 1)) {
        throw 'Chunk sequence must be unique, contiguous, and ordered from 1 before assembly.'
    }
}

$parts = @()
foreach ($chunk in @($state.chunks)) {
    $path = Join-Path $RepositoryRoot $chunk.vietnameseMarkdown
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Translated chunk is missing: $path"
    }
    $parts += [System.IO.File]::ReadAllText($path).TrimEnd()
}

$outputDirectory = Join-Path $RepositoryRoot "_output\$JobId"
$markdownPath = Join-Path $outputDirectory 'book.vi.md'
Write-TranslationTextAtomic -Path $markdownPath -Value (($parts -join "`r`n`r`n") + "`r`n")

$sourceMarkdownPath = Join-Path $RepositoryRoot $state.artifacts.sourceMarkdown
$sourceAssets = Join-Path (Split-Path -Parent $sourceMarkdownPath) 'assets'
$outputAssets = Join-Path $outputDirectory 'assets'
if (Test-Path -LiteralPath $sourceAssets -PathType Container) {
    if (-not (Test-Path -LiteralPath $outputAssets -PathType Container)) {
        New-Item -ItemType Directory -Path $outputAssets -Force | Out-Null
    }
    Copy-Item -Path (Join-Path $sourceAssets '*') -Destination $outputAssets -Recurse -Force
}

[pscustomobject]@{
    jobId = $JobId
    markdown = ConvertTo-TranslationRelativePath -Root $RepositoryRoot -Path $markdownPath
    assets = if (Test-Path -LiteralPath $outputAssets) { ConvertTo-TranslationRelativePath -Root $RepositoryRoot -Path $outputAssets } else { $null }
}