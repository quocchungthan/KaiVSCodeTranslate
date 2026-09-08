[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [Parameter(Mandatory = $true)]
    [uri]$Url,

    [Parameter(Mandatory = $true)]
    [string]$Title,

    [Parameter(Mandatory = $true)]
    [string]$Purpose,

    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ($Url.Scheme -ne 'https' -or -not [string]::IsNullOrWhiteSpace($Url.UserInfo)) {
    throw 'Research URLs must use HTTPS and must not contain credentials.'
}
if ($Url.Query -match '(?i)(token|key|secret|password)=') {
    throw 'Research URL query appears to contain a secret.'
}
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}

$jobDirectory = Join-Path $RepositoryRoot "_processing\jobs\$JobId"
$statePath = Join-Path $jobDirectory 'job.json'
$config = Get-TranslationPipelineConfig -RepositoryRoot $RepositoryRoot
$lockTimeoutSeconds = Get-TranslationLockTimeoutSeconds -Config $config
Invoke-WithTranslationJobLock -JobDirectory $jobDirectory -TimeoutSeconds $lockTimeoutSeconds -Action {
    $state = Read-TranslationJobState -Path $statePath
    $normalizedUrl = $Url.AbsoluteUri
    $existing = @($state.researchSources | Where-Object { $_.url -eq $normalizedUrl })
    if ($existing.Count -eq 0) {
        $state.researchSources = @($state.researchSources) + [pscustomobject]@{
            url = $normalizedUrl
            title = $Title
            purpose = $Purpose
            accessedUtc = [DateTime]::UtcNow.ToString('o')
        }
        $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
        Write-TranslationJsonAtomic -Path $statePath -Value $state
    }
    $state.researchSources
}