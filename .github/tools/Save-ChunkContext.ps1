[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [Parameter(Mandatory = $true)]
    [string]$ChunkId,

    [Parameter(Mandatory = $true)]
    [string]$VietnameseMarkdown,

    [Parameter(Mandatory = $true)]
    [string]$BackwardSummary,

    [Parameter(Mandatory = $true)]
    [string]$Handoff,

    [string]$TerminologyJson = '[]',
    [string]$FormatDecisionsJson = '[]',
    [string]$UnresolvedReferencesJson = '[]',
    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)

if ([System.IO.Path]::IsPathRooted($VietnameseMarkdown) -or
    $VietnameseMarkdown -match '(^|[\\/])\.\.([\\/]|$)') {
    throw 'VietnameseMarkdown must be a safe repository-relative path.'
}
$translatedPath = Join-Path $RepositoryRoot $VietnameseMarkdown
if (-not (Test-Path -LiteralPath $translatedPath -PathType Leaf)) {
    throw "Translated Markdown does not exist: $translatedPath"
}

$terminology = @($TerminologyJson | ConvertFrom-Json)
$formatDecisions = @($FormatDecisionsJson | ConvertFrom-Json)
$unresolvedReferences = @($UnresolvedReferencesJson | ConvertFrom-Json)
$jobDirectory = Join-Path $RepositoryRoot "_processing\jobs\$JobId"
$statePath = Join-Path $jobDirectory 'job.json'
$config = Get-TranslationPipelineConfig -RepositoryRoot $RepositoryRoot
$lockTimeoutSeconds = Get-TranslationLockTimeoutSeconds -Config $config

Invoke-WithTranslationJobLock -JobDirectory $jobDirectory -TimeoutSeconds $lockTimeoutSeconds -Action {
    $state = Read-TranslationJobState -Path $statePath
    $chunk = @($state.chunks | Where-Object { $_.id -eq $ChunkId })
    if ($chunk.Count -ne 1) {
        throw "Expected exactly one chunk named $ChunkId."
    }
    if ($chunk[0].status -eq 'completed') {
        Read-TranslationJson -Path (Join-Path $RepositoryRoot $chunk[0].context)
        return
    }
    if ($chunk[0].status -ne 'running') {
        throw "Chunk $ChunkId must be marked running before its context is saved."
    }

    for ($index = 0; $index -lt ([int]$chunk[0].sequence - 1); $index++) {
        if ($state.chunks[$index].status -ne 'completed') {
            throw "Chunk $ChunkId cannot complete before chunk $($state.chunks[$index].id)."
        }
    }

    $now = [DateTime]::UtcNow.ToString('o')
    $contextRelativePath = "_processing/jobs/$JobId/chunks/$ChunkId/context.json"
    $contextPath = Join-Path $RepositoryRoot $contextRelativePath
    $context = [ordered]@{
        schemaVersion = 1
        jobId = $JobId
        chunkId = $ChunkId
        sequence = [int]$chunk[0].sequence
        savedUtc = $now
        backwardSummary = $BackwardSummary
        terminologyDecisions = $terminology
        formatDecisions = $formatDecisions
        unresolvedReferences = $unresolvedReferences
        handoff = $Handoff
    }
    Write-TranslationJsonAtomic -Path $contextPath -Value $context

    $chunk[0].status = 'completed'
    $chunk[0].lastError = $null
    $chunk[0].vietnameseMarkdown = $VietnameseMarkdown.Replace('\', '/')
    $chunk[0].context = $contextRelativePath
    $state.updatedUtc = $now

    $remaining = @($state.chunks | Where-Object { $_.status -ne 'completed' })
    if ($remaining.Count -eq 0) {
        $state.stages.translate.status = 'completed'
        $state.stages.translate.completedUtc = $now
    }
    else {
        $state.stages.translate.status = 'running'
        if ($null -eq $state.stages.translate.startedUtc) {
            $state.stages.translate.startedUtc = $now
            $state.stages.translate.attempts = [int]$state.stages.translate.attempts + 1
        }
    }
    $state.status = 'in-progress'

    Write-TranslationJsonAtomic -Path $statePath -Value $state
    $context
}