[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [Parameter(Mandatory = $true)]
    [string]$ChunkId,

    [ValidateRange(1, 20)]
    [int]$SummaryWindow = 3,

    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$statePath = Join-Path $RepositoryRoot "_processing\jobs\$JobId\job.json"
$state = Read-TranslationJobState -Path $statePath
$current = @($state.chunks | Where-Object { $_.id -eq $ChunkId })
if ($current.Count -ne 1) {
    throw "Expected exactly one chunk named $ChunkId."
}

$priorChunks = @($state.chunks |
    Where-Object { [int]$_.sequence -lt [int]$current[0].sequence -and $_.status -eq 'completed' } |
    Sort-Object sequence)
$recentChunks = @($priorChunks | Select-Object -Last $SummaryWindow)
$contexts = @()
foreach ($priorChunk in $recentChunks) {
    $contextPath = Join-Path $RepositoryRoot $priorChunk.context
    $contexts += Read-TranslationJson -Path $contextPath
}

$allContexts = @()
foreach ($priorChunk in $priorChunks) {
    $contextPath = Join-Path $RepositoryRoot $priorChunk.context
    $allContexts += Read-TranslationJson -Path $contextPath
}

[pscustomobject]@{
    jobId = $JobId
    chunkId = $ChunkId
    immediateHandoff = if ($contexts.Count -gt 0) { $contexts[-1].handoff } else { $null }
    recentSummaries = @($contexts | ForEach-Object {
        [pscustomobject]@{ chunkId = $_.chunkId; summary = $_.backwardSummary }
    })
    terminologyDecisions = @($allContexts | ForEach-Object { $_.terminologyDecisions })
    formatDecisions = @($allContexts | ForEach-Object { $_.formatDecisions })
    unresolvedReferences = @($allContexts | ForEach-Object { $_.unresolvedReferences })
} | ConvertTo-Json -Depth 100