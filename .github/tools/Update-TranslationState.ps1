[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [Parameter(Mandatory = $true)]
    [ValidateSet('convert', 'chunk', 'translate', 'assemble', 'validate')]
    [string]$Stage,

    [Parameter(Mandatory = $true)]
    [ValidateSet('pending', 'running', 'completed', 'failed', 'blocked', 'skipped')]
    [string]$Status,

    [string]$Message,
    [string]$ErrorMessage,
    [string]$ErrorDetail,
    [string]$ArtifactName,
    [string]$ArtifactRelativePath,
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
$maximumStageAttempts = Get-TranslationStageAttemptLimit -Config $config

Invoke-WithTranslationJobLock -JobDirectory $jobDirectory -TimeoutSeconds $lockTimeoutSeconds -Action {
    $state = Read-TranslationJobState -Path $statePath
    if ($state.jobId -ne $JobId) {
        throw "Job ID does not match state file: $JobId"
    }

    $stageOrder = @('convert', 'chunk', 'translate', 'assemble', 'validate')
    $stageIndex = [array]::IndexOf($stageOrder, $Stage)
    if ($Status -in @('running', 'completed')) {
        for ($index = 0; $index -lt $stageIndex; $index++) {
            $dependency = $state.stages.($stageOrder[$index])
            if ($dependency.status -notin @('completed', 'skipped')) {
                throw "Cannot set $Stage to $Status before $($stageOrder[$index]) is complete."
            }
        }
    }

    $stageState = $state.stages.$Stage
    if ($Status -eq 'running' -and $stageState.status -eq 'failed' -and [int]$stageState.attempts -ge $maximumStageAttempts) {
        $now = [DateTime]::UtcNow.ToString('o')
        $reason = "Stage $Stage exhausted maximum attempts ($maximumStageAttempts)."
        $stageState.status = 'blocked'
        $stageState.message = $reason
        $state.status = 'blocked'
        $state.updatedUtc = $now
        Write-TranslationJsonAtomic -Path $statePath -Value $state
        throw $reason
    }
    $allowedTransitions = @{
        pending = @('pending', 'running', 'blocked', 'skipped')
        running = @('running', 'completed', 'failed', 'blocked')
        failed = @('failed', 'running', 'blocked')
        blocked = @('blocked', 'pending', 'running')
        completed = @('completed')
        skipped = @('skipped')
    }
    if ($Status -notin $allowedTransitions[$stageState.status]) {
        throw "Invalid stage transition for ${Stage}: $($stageState.status) -> $Status"
    }

    $now = [DateTime]::UtcNow.ToString('o')
    if ($Status -eq 'running' -and $stageState.status -ne 'running') {
        $stageState.attempts = [int]$stageState.attempts + 1
        $stageState.startedUtc = $now
        $stageState.completedUtc = $null
        $stageState.lastError = $null
    }
    if ($Status -in @('completed', 'skipped')) {
        $stageState.completedUtc = $now
        $stageState.lastError = $null
    }
    if ($Status -eq 'failed') {
        if ([string]::IsNullOrWhiteSpace($ErrorMessage)) {
            throw 'ErrorMessage is required when recording a failed stage.'
        }
        $stageState.lastError = [pscustomobject]@{
            occurredUtc = $now
            message = $ErrorMessage
            detail = $ErrorDetail
            attempt = $stageState.attempts
        }
    }

    $stageState.status = $Status
    $stageState.message = $Message
    $state.updatedUtc = $now
    $state.status = if ($Stage -eq 'validate' -and $Status -eq 'completed') {
        'completed'
    } elseif ($Status -eq 'failed') {
        'needs-retry'
    } elseif ($Status -eq 'blocked') {
        'blocked'
    } else {
        'in-progress'
    }

    if (-not [string]::IsNullOrWhiteSpace($ArtifactName)) {
        if ([string]::IsNullOrWhiteSpace($ArtifactRelativePath) -or
            [System.IO.Path]::IsPathRooted($ArtifactRelativePath) -or
            $ArtifactRelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
            throw 'ArtifactRelativePath must be a safe repository-relative path.'
        }
        $state.artifacts | Add-Member -NotePropertyName $ArtifactName -NotePropertyValue $ArtifactRelativePath.Replace('\', '/') -Force
    }

    if ($null -eq $state.PSObject.Properties['history']) {
        $state | Add-Member -NotePropertyName history -NotePropertyValue @()
    }
    $state.history = @($state.history) + [pscustomobject]@{
        occurredUtc = $now
        type = 'stage-transition'
        stage = $Stage
        status = $Status
        message = $Message
    }

    Write-TranslationJsonAtomic -Path $statePath -Value $state
    $state
}