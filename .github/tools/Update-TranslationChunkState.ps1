[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [Parameter(Mandatory = $true)]
    [string]$ChunkId,

    [Parameter(Mandatory = $true)]
    [ValidateSet('pending', 'running', 'failed', 'blocked')]
    [string]$Status,

    [string]$ErrorMessage,
    [string]$ErrorDetail,
    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$jobDirectory = Join-Path $RepositoryRoot "_processing\jobs\$JobId"
$statePath = Join-Path $jobDirectory 'job.json'
$config = Get-TranslationPipelineConfig -RepositoryRoot $RepositoryRoot
$lockTimeoutSeconds = Get-TranslationLockTimeoutSeconds -Config $config
$maximumChunkAttempts = Get-TranslationChunkAttemptLimit -Config $config
Invoke-WithTranslationJobLock -JobDirectory $jobDirectory -TimeoutSeconds $lockTimeoutSeconds -Action {
    $state = Read-TranslationJobState -Path $statePath
    if ($state.stages.chunk.status -ne 'completed') {
        throw 'Chunking stage must be complete before translating a chunk.'
    }

    $chunk = @($state.chunks | Where-Object { $_.id -eq $ChunkId })
    if ($chunk.Count -ne 1) {
        throw "Expected exactly one chunk named $ChunkId."
    }
    if ($Status -eq 'running' -and $chunk[0].status -eq 'failed' -and [int]$chunk[0].attempts -ge $maximumChunkAttempts) {
        $now = [DateTime]::UtcNow.ToString('o')
        $reason = "Chunk $ChunkId exhausted maximum attempts ($maximumChunkAttempts)."
        $chunk[0].status = 'blocked'
        $chunk[0].lastError = [pscustomobject]@{ occurredUtc = $now; message = $reason; detail = $null; attempt = $chunk[0].attempts }
        $state.stages.translate.status = 'blocked'
        $state.stages.translate.message = $reason
        $state.status = 'blocked'
        $state.updatedUtc = $now
        Write-TranslationJsonAtomic -Path $statePath -Value $state
        throw $reason
    }

    $allowedTransitions = @{
        pending = @('pending', 'running', 'blocked')
        running = @('running', 'failed', 'blocked')
        failed = @('failed', 'running', 'blocked')
        blocked = @('blocked', 'pending', 'running')
    }
    if ($chunk[0].status -eq 'completed') {
        throw "Completed chunk $ChunkId cannot be reopened without pipeline invalidation."
    }
    if ($Status -notin $allowedTransitions[$chunk[0].status]) {
        throw "Invalid chunk transition for ${ChunkId}: $($chunk[0].status) -> $Status"
    }

    $now = [DateTime]::UtcNow.ToString('o')
    if ($Status -eq 'running' -and $chunk[0].status -ne 'running') {
        $chunk[0].attempts = [int]$chunk[0].attempts + 1
        $chunk[0].lastError = $null
    }
    if ($Status -eq 'failed') {
        if ([string]::IsNullOrWhiteSpace($ErrorMessage)) {
            throw 'ErrorMessage is required when recording a failed chunk.'
        }
        $chunk[0].lastError = [pscustomobject]@{
            occurredUtc = $now
            message = $ErrorMessage
            detail = $ErrorDetail
            attempt = $chunk[0].attempts
        }
    }
    if ($Status -eq 'blocked' -and -not [string]::IsNullOrWhiteSpace($ErrorMessage)) {
        $chunk[0].lastError = [pscustomobject]@{
            occurredUtc = $now
            message = $ErrorMessage
            detail = $ErrorDetail
            attempt = $chunk[0].attempts
        }
    }

    $chunk[0].status = $Status
    $state.stages.translate.status = if ($Status -eq 'blocked') { 'blocked' } else { 'running' }
    $state.stages.translate.message = if ($Status -eq 'blocked') { $ErrorMessage } else { $null }
    if ($null -eq $state.stages.translate.startedUtc) {
        $state.stages.translate.startedUtc = $now
        $state.stages.translate.attempts = [int]$state.stages.translate.attempts + 1
    }
    $state.status = if ($Status -eq 'blocked') { 'blocked' } elseif ($Status -eq 'failed') { 'needs-retry' } else { 'in-progress' }
    $state.updatedUtc = $now

    Write-TranslationJsonAtomic -Path $statePath -Value $state
    $chunk[0]
}