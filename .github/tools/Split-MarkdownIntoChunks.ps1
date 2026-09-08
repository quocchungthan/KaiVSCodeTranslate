[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [string]$SourceMarkdown,
    [string]$RepositoryRoot,
    [string]$ConfigPath
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $RepositoryRoot '.github\translation-pipeline.defaults.json'
}

$statePath = Join-Path $RepositoryRoot "_processing\jobs\$JobId\job.json"
$state = Read-TranslationJobState -Path $statePath
$config = Read-TranslationJson -Path $ConfigPath
if ([string]::IsNullOrWhiteSpace($SourceMarkdown)) {
    $SourceMarkdown = $state.artifacts.sourceMarkdown
}
if ([string]::IsNullOrWhiteSpace($SourceMarkdown) -or [System.IO.Path]::IsPathRooted($SourceMarkdown)) {
    throw 'SourceMarkdown must be a repository-relative path.'
}

$sourcePath = Join-Path $RepositoryRoot $SourceMarkdown
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Source Markdown does not exist: $sourcePath"
}

$targetSize = [int]$config.chunking.targetCharacters
$maximumSize = [int]$config.chunking.maximumCharacters
$minimumSize = [int]$config.chunking.minimumCharacters
if ($minimumSize -le 0 -or $targetSize -lt $minimumSize -or $maximumSize -lt $targetSize) {
    throw 'Chunk limits must satisfy 0 < minimum <= target <= maximum.'
}

$lines = [System.IO.File]::ReadAllLines($sourcePath)
$sections = New-Object System.Collections.Generic.List[object]
$currentLines = New-Object System.Collections.Generic.List[string]
$insideFence = $false
foreach ($line in $lines) {
    $isFence = $line -match '^\s*(```|~~~)'
    $isBoundary = -not $insideFence -and $line -match '^#{1,3}\s+'
    if ($isBoundary -and $currentLines.Count -gt 0) {
        $sections.Add(($currentLines -join "`r`n"))
        $currentLines = New-Object System.Collections.Generic.List[string]
    }
    $currentLines.Add($line)
    if ($isFence) {
        $insideFence = -not $insideFence
    }
}
if ($currentLines.Count -gt 0) {
    $sections.Add(($currentLines -join "`r`n"))
}

$units = New-Object System.Collections.Generic.List[string]
foreach ($section in $sections) {
    if ($section.Length -le $maximumSize) {
        $units.Add($section)
        continue
    }

    $blocks = [regex]::Split($section, '(?:\r?\n){2,}')
    $piece = ''
    foreach ($block in $blocks) {
        $candidate = if ($piece.Length -eq 0) { $block } else { "$piece`r`n`r`n$block" }
        if ($candidate.Length -le $maximumSize) {
            $piece = $candidate
            continue
        }
        if ($piece.Length -gt 0) {
            $units.Add($piece)
            $piece = ''
        }
        if ($block.Length -le $maximumSize -or $block -match '^\s*(```|~~~)') {
            $units.Add($block)
            continue
        }
        for ($offset = 0; $offset -lt $block.Length; $offset += $maximumSize) {
            $length = [Math]::Min($maximumSize, $block.Length - $offset)
            $units.Add($block.Substring($offset, $length))
        }
    }
    if ($piece.Length -gt 0) {
        $units.Add($piece)
    }
}

$chunkTexts = New-Object System.Collections.Generic.List[string]
$currentChunk = ''
foreach ($unit in $units) {
    $candidate = if ($currentChunk.Length -eq 0) { $unit } else { "$currentChunk`r`n`r`n$unit" }
    if ($currentChunk.Length -eq 0 -or $candidate.Length -le $targetSize -or
        ($currentChunk.Length -lt $minimumSize -and $candidate.Length -le $maximumSize)) {
        $currentChunk = $candidate
    }
    else {
        $chunkTexts.Add($currentChunk)
        $currentChunk = $unit
    }
}
if ($currentChunk.Length -gt 0) {
    $chunkTexts.Add($currentChunk)
}
if ($chunkTexts.Count -eq 0) {
    throw 'Source Markdown produced no chunks.'
}

$sourceDirectory = Split-Path -Parent $sourcePath
$manifest = @()
for ($index = 0; $index -lt $chunkTexts.Count; $index++) {
    $sequence = $index + 1
    $id = $sequence.ToString('0000')
    $chunkPath = Join-Path $sourceDirectory "chunk-$id.md"
    $text = $chunkTexts[$index].TrimEnd() + "`r`n"
    Write-TranslationTextAtomic -Path $chunkPath -Value $text
    $heading = @($text -split '\r?\n' | Where-Object { $_ -match '^#{1,6}\s+' } | Select-Object -First 1)
    $manifest += [ordered]@{
        id = $id
        sequence = $sequence
        title = if ($heading.Count -eq 1) { $heading[0] -replace '^#{1,6}\s+', '' } else { "Chunk $id" }
        sourceMarkdown = ConvertTo-TranslationRelativePath -Root $RepositoryRoot -Path $chunkPath
        sourceSha256 = (Get-FileHash -LiteralPath $chunkPath -Algorithm SHA256).Hash.ToLowerInvariant()
        characterCount = $text.Length
        oversized = $text.Length -gt $maximumSize
    }
}

$manifestPath = Join-Path $sourceDirectory 'chunks.manifest.json'
Write-TranslationJsonAtomic -Path $manifestPath -Value $manifest
& (Join-Path $PSScriptRoot 'Set-TranslationChunks.ps1') -RepositoryRoot $RepositoryRoot -JobId $JobId -ManifestPath $manifestPath | Out-Null
[pscustomobject]@{
    jobId = $JobId
    chunkCount = $manifest.Count
    manifest = ConvertTo-TranslationRelativePath -Root $RepositoryRoot -Path $manifestPath
    oversizedChunks = @($manifest | Where-Object { $_.oversized }).Count
}