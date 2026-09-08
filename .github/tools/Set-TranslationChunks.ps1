[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$manifestDocument = Read-TranslationJson -Path $ManifestPath
$manifest = if ($manifestDocument -is [System.Array]) { $manifestDocument } else { @($manifestDocument) }
if ($manifest.Count -eq 0) {
    throw 'Chunk manifest must contain at least one chunk.'
}

$seenIds = @{}
$chunks = @()
for ($index = 0; $index -lt $manifest.Count; $index++) {
    $item = $manifest[$index]
    if ([string]::IsNullOrWhiteSpace($item.id) -or $seenIds.ContainsKey($item.id)) {
        throw "Chunk IDs must be non-empty and unique: $($item.id)"
    }
    if ([int]$item.sequence -ne ($index + 1)) {
        throw "Chunk sequence must be contiguous and ordered from 1. Invalid item: $($item.id)"
    }
    if ([string]::IsNullOrWhiteSpace($item.sourceMarkdown) -or
        [System.IO.Path]::IsPathRooted($item.sourceMarkdown) -or
        $item.sourceMarkdown -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Chunk sourceMarkdown must be repository-relative: $($item.id)"
    }

    $seenIds[$item.id] = $true
    $chunks += [pscustomobject]@{
        id = $item.id
        sequence = [int]$item.sequence
        title = $item.title
        sourceMarkdown = $item.sourceMarkdown.Replace('\', '/')
        sourceSha256 = $item.sourceSha256
        characterCount = [int]$item.characterCount
        status = 'pending'
        attempts = 0
        lastError = $null
        vietnameseMarkdown = $null
        context = $null
    }
}

$jobDirectory = Join-Path $RepositoryRoot "_processing\jobs\$JobId"
$statePath = Join-Path $jobDirectory 'job.json'
$config = Get-TranslationPipelineConfig -RepositoryRoot $RepositoryRoot
$lockTimeoutSeconds = Get-TranslationLockTimeoutSeconds -Config $config
Invoke-WithTranslationJobLock -JobDirectory $jobDirectory -TimeoutSeconds $lockTimeoutSeconds -Action {
    $state = Read-TranslationJobState -Path $statePath
    if ($state.stages.convert.status -ne 'completed') {
        throw 'Convert stage must be complete before registering chunks.'
    }
    if ($state.chunks.Count -gt 0) {
        throw 'Chunks are already registered. Invalidate the pipeline before replacing the manifest.'
    }

    $state.chunks = $chunks
    $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Write-TranslationJsonAtomic -Path $statePath -Value $state
    $state
}