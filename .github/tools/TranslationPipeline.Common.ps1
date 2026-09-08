Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TranslationRepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
}

function Read-TranslationJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "JSON file does not exist: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Read-TranslationJobState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $state = Read-TranslationJson -Path $Path
    if ($null -eq $state.PSObject.Properties['schemaVersion']) {
        throw "Job state schemaVersion is missing: $Path"
    }

    $schemaVersion = 0
    if (-not [int]::TryParse([string]$state.schemaVersion, [ref]$schemaVersion) -or $schemaVersion -ne 1) {
        throw "Unsupported job state schemaVersion '$($state.schemaVersion)' in: $Path. Supported versions: 1."
    }

    return $state
}

function Get-TranslationPipelineConfig {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [string]$ConfigPath
    )

    if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
        $ConfigPath = Join-Path $RepositoryRoot '.github\translation-pipeline.defaults.json'
    }
    return Read-TranslationJson -Path $ConfigPath
}

function Get-TranslationStageAttemptLimit {
    param([Parameter(Mandatory = $true)][object]$Config)

    $value = if ($null -ne $Config.PSObject.Properties['retries'] -and
        $null -ne $Config.retries.PSObject.Properties['maximumStageAttempts']) {
        [int]$Config.retries.maximumStageAttempts
    } else { 3 }
    if ($value -lt 1) { throw 'maximumStageAttempts must be at least 1.' }
    return $value
}

function Get-TranslationChunkAttemptLimit {
    param([Parameter(Mandatory = $true)][object]$Config)

    $value = if ($null -ne $Config.PSObject.Properties['retries'] -and
        $null -ne $Config.retries.PSObject.Properties['maximumChunkAttempts']) {
        [int]$Config.retries.maximumChunkAttempts
    } else { 3 }
    if ($value -lt 1) { throw 'maximumChunkAttempts must be at least 1.' }
    return $value
}

function Get-TranslationLockTimeoutSeconds {
    param([Parameter(Mandatory = $true)][object]$Config)

    $value = if ($null -ne $Config.PSObject.Properties['state'] -and
        $null -ne $Config.state.PSObject.Properties['lockTimeoutSeconds']) {
        [int]$Config.state.lockTimeoutSeconds
    } else { 30 }
    if ($value -lt 1) { throw 'lockTimeoutSeconds must be at least 1.' }
    return $value
}

function Get-TranslationMaximumHeadingDriftPercent {
    param([Parameter(Mandatory = $true)][object]$Config)

    $value = if ($null -ne $Config.PSObject.Properties['quality'] -and
        $null -ne $Config.quality.PSObject.Properties['maximumHeadingCountDriftPercent']) {
        [double]$Config.quality.maximumHeadingCountDriftPercent
    } else { 10.0 }
    if ($value -lt 0 -or $value -gt 100) { throw 'maximumHeadingCountDriftPercent must be between 0 and 100.' }
    return $value
}

function Write-TranslationJsonAtomic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $temporaryPath = "$Path.$PID.$([guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$Path.$PID.$([guid]::NewGuid().ToString('N')).bak"
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $json = $Value | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($temporaryPath, "$json`r`n", $encoding)

    try {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    }
}

function Invoke-WithTranslationJobLock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$JobDirectory,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [int]$TimeoutSeconds = 30
    )

    if (-not (Test-Path -LiteralPath $JobDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $JobDirectory -Force | Out-Null
    }

    $lockPath = Join-Path $JobDirectory '.state.lock'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lockStream = $null

    while ($null -eq $lockStream) {
        try {
            $lockStream = [System.IO.File]::Open(
                $lockPath,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None
            )
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out waiting for job lock: $lockPath"
            }
            Start-Sleep -Milliseconds 100
        }
    }

    try {
        return & $Action
    }
    finally {
        $lockStream.Dispose()
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
    }
}

function New-TranslationStageState {
    return [ordered]@{
        status = 'pending'
        attempts = 0
        startedUtc = $null
        completedUtc = $null
        lastError = $null
        message = $null
    }
}

function New-TranslationStages {
    return [ordered]@{
        convert = New-TranslationStageState
        chunk = New-TranslationStageState
        translate = New-TranslationStageState
        assemble = New-TranslationStageState
        validate = New-TranslationStageState
    }
}

function ConvertTo-TranslationRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside repository root: $fullPath"
    }

    return $fullPath.Substring($rootPath.Length).Replace('\', '/')
}

function Write-TranslationTextAtomic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $temporaryPath = "$Path.$PID.$([guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$Path.$PID.$([guid]::NewGuid().ToString('N')).bak"
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temporaryPath, $Value, $encoding)

    try {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
    }
}