[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$SkipDiscovery
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}
$config = Get-TranslationPipelineConfig -RepositoryRoot $RepositoryRoot
$maximumStageAttempts = Get-TranslationStageAttemptLimit -Config $config
$maximumChunkAttempts = Get-TranslationChunkAttemptLimit -Config $config

if (-not $SkipDiscovery) {
    & (Join-Path $PSScriptRoot 'Initialize-TranslationJobs.ps1') -RepositoryRoot $RepositoryRoot | Out-Null
}

$jobsRoot = Join-Path $RepositoryRoot '_processing\jobs'
if (-not (Test-Path -LiteralPath $jobsRoot -PathType Container)) {
    [pscustomobject]@{ action = 'idle'; reason = 'No jobs exist.' }
    return
}

$states = @(Get-ChildItem -LiteralPath $jobsRoot -Filter 'job.json' -File -Recurse |
    ForEach-Object { Read-TranslationJobState -Path $_.FullName } |
    Where-Object { $_.status -notin @('completed', 'superseded') } |
    Sort-Object { $_.source.relativePath }, createdUtc)

$blocked = @()
$stageOrder = @('convert', 'chunk', 'translate', 'assemble', 'validate')
foreach ($state in $states) {
    foreach ($stageName in $stageOrder) {
        $stage = $state.stages.$stageName
        if ($stage.status -in @('completed', 'skipped')) {
            continue
        }
        if ($stage.status -eq 'blocked') {
            $blocked += [pscustomobject]@{ jobId = $state.jobId; stage = $stageName; reason = $stage.message }
            break
        }
        if ($stage.status -eq 'failed' -and [int]$stage.attempts -ge $maximumStageAttempts) {
            $reason = "Stage $stageName exhausted maximum attempts ($maximumStageAttempts)."
            & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $RepositoryRoot -JobId $state.jobId -Stage $stageName -Status blocked -Message $reason | Out-Null
            $blocked += [pscustomobject]@{ jobId = $state.jobId; stage = $stageName; reason = $reason }
            break
        }
        if ($stageName -eq 'translate' -and $state.chunks.Count -gt 0) {
            $nextChunk = @($state.chunks |
                Where-Object { $_.status -in @('pending', 'failed') } |
                Sort-Object sequence |
                Select-Object -First 1)
            if ($nextChunk.Count -eq 1) {
                if ($nextChunk[0].status -eq 'failed' -and [int]$nextChunk[0].attempts -ge $maximumChunkAttempts) {
                    $reason = "Chunk $($nextChunk[0].id) exhausted maximum attempts ($maximumChunkAttempts)."
                    & (Join-Path $PSScriptRoot 'Update-TranslationChunkState.ps1') -RepositoryRoot $RepositoryRoot -JobId $state.jobId -ChunkId $nextChunk[0].id -Status blocked -ErrorMessage $reason | Out-Null
                    $blocked += [pscustomobject]@{ jobId = $state.jobId; stage = 'translate'; chunkId = $nextChunk[0].id; reason = $reason }
                    break
                }
                [pscustomobject]@{
                    action = 'translate-chunk'
                    jobId = $state.jobId
                    stage = 'translate'
                    chunkId = $nextChunk[0].id
                    sourceMarkdown = $nextChunk[0].sourceMarkdown
                    retry = $nextChunk[0].status -eq 'failed'
                }
                return
            }
        }

        [pscustomobject]@{
            action = if ($stage.status -eq 'failed') { 'retry-stage' } else { 'run-stage' }
            jobId = $state.jobId
            stage = $stageName
            status = $stage.status
            retry = $stage.status -eq 'failed'
        }
        return
    }
}

if ($blocked.Count -gt 0) {
    [pscustomobject]@{ action = 'blocked'; jobs = $blocked }
}
else {
    [pscustomobject]@{ action = 'idle'; reason = 'No actionable jobs remain.' }
}