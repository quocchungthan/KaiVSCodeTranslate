[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ConfigPath,
    [switch]$WhatIf
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $RepositoryRoot '.github\translation-pipeline.defaults.json'
}

$config = Read-TranslationJson -Path $ConfigPath
if ([int]$config.schemaVersion -ne 1) {
    throw "Unsupported pipeline schemaVersion '$($config.schemaVersion)'. Supported versions: 1."
}
$lockTimeoutSeconds = Get-TranslationLockTimeoutSeconds -Config $config
$pdfRoot = Join-Path $RepositoryRoot '_pdfs'
$jobsRoot = Join-Path $RepositoryRoot '_processing\jobs'

if (-not (Test-Path -LiteralPath $pdfRoot -PathType Container)) {
    throw "PDF inbox does not exist: $pdfRoot"
}

$results = @()
$pdfFiles = Get-ChildItem -LiteralPath $pdfRoot -Filter '*.pdf' -File -Recurse | Sort-Object FullName

foreach ($pdfFile in $pdfFiles) {
    $sha256 = (Get-FileHash -LiteralPath $pdfFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $slug = [System.IO.Path]::GetFileNameWithoutExtension($pdfFile.Name).ToLowerInvariant()
    $slug = [regex]::Replace($slug, '[^a-z0-9]+', '-')
    $slug = $slug.Trim('-')
    if ([string]::IsNullOrWhiteSpace($slug)) {
        $slug = 'book'
    }
    if ($slug.Length -gt 48) {
        $slug = $slug.Substring(0, 48).TrimEnd('-')
    }

    $jobId = "$slug-$($sha256.Substring(0, 16))"
    $jobDirectory = Join-Path $jobsRoot $jobId
    $statePath = Join-Path $jobDirectory 'job.json'
    $sourceRelativePath = ConvertTo-TranslationRelativePath -Root $RepositoryRoot -Path $pdfFile.FullName
    $action = 'created'
    $existingState = $null

    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        $existingState = Read-TranslationJobState -Path $statePath
        if ($existingState.source.sha256 -ne $sha256) {
            throw "Job identity collision for $jobId. Existing source hash differs."
        }
        if ($existingState.pipelineVersion -eq $config.pipelineVersion) {
            $action = 'unchanged'
            $results += [pscustomobject]@{ jobId = $jobId; action = $action; source = $sourceRelativePath }
            continue
        }
        $action = 'invalidated'
    }

    $now = [DateTime]::UtcNow.ToString('o')
    $state = [ordered]@{
        schemaVersion = $config.schemaVersion
        pipelineVersion = $config.pipelineVersion
        jobId = $jobId
        status = 'pending'
        source = [ordered]@{
            relativePath = $sourceRelativePath
            sha256 = $sha256
            sizeBytes = $pdfFile.Length
            lastWriteUtc = $pdfFile.LastWriteTimeUtc.ToString('o')
        }
        createdUtc = if ($null -ne $existingState) { $existingState.createdUtc } else { $now }
        updatedUtc = $now
        invalidation = if ($action -eq 'invalidated') {
            [ordered]@{
                reason = 'pipeline-version-changed'
                previousPipelineVersion = $existingState.pipelineVersion
                invalidatedUtc = $now
            }
        } else { $null }
        stages = New-TranslationStages
        chunks = @()
        artifacts = [ordered]@{}
        researchSources = @()
    }

    if (-not $WhatIf) {
        Invoke-WithTranslationJobLock -JobDirectory $jobDirectory -TimeoutSeconds $lockTimeoutSeconds -Action {
            Write-TranslationJsonAtomic -Path $statePath -Value $state
        }
    }

    if (Test-Path -LiteralPath $jobsRoot -PathType Container) {
        $otherStates = Get-ChildItem -LiteralPath $jobsRoot -Filter 'job.json' -File -Recurse |
            Where-Object { $_.FullName -ne $statePath }
        foreach ($otherStateFile in $otherStates) {
            $otherState = Read-TranslationJobState -Path $otherStateFile.FullName
            if ($otherState.source.relativePath -eq $sourceRelativePath -and
                $otherState.source.sha256 -ne $sha256 -and
                $otherState.status -ne 'superseded') {
                $otherState.status = 'superseded'
                $otherState.updatedUtc = $now
                $otherState.invalidation = [pscustomobject]@{
                    reason = 'source-hash-changed'
                    replacementJobId = $jobId
                    invalidatedUtc = $now
                }
                if (-not $WhatIf) {
                    $otherJobDirectory = Split-Path -Parent $otherStateFile.FullName
                    Invoke-WithTranslationJobLock -JobDirectory $otherJobDirectory -TimeoutSeconds $lockTimeoutSeconds -Action {
                        Write-TranslationJsonAtomic -Path $otherStateFile.FullName -Value $otherState
                    }
                }
            }
        }
    }

    $results += [pscustomobject]@{ jobId = $jobId; action = $action; source = $sourceRelativePath }
}

$results