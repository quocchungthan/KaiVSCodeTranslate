[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$githubRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$failures = @()

$agentPath = Join-Path $githubRoot 'agents\Huong.agent.md'
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf)) {
    $failures += 'Missing Huong agent.'
}
else {
    $agent = Get-Content -LiteralPath $agentPath -Raw -Encoding UTF8
    if ($agent -notmatch '(?ms)^---\r?\n.*?^description:\s*.+?\r?\n.*?^---\r?$') {
        $failures += 'Huong agent has invalid frontmatter or no description.'
    }
}

$skillFiles = Get-ChildItem -LiteralPath (Join-Path $githubRoot 'skills') -Filter 'SKILL.md' -File -Recurse
foreach ($skillFile in $skillFiles) {
    $folderName = Split-Path -Leaf (Split-Path -Parent $skillFile.FullName)
    $content = Get-Content -LiteralPath $skillFile.FullName -Raw -Encoding UTF8
    if ($content -notmatch '(?s)^---\r?\n(.*?)\r?\n---\r?\n') {
        $failures += "Invalid frontmatter delimiters: $($skillFile.FullName)"
        continue
    }
    $frontmatter = $Matches[1]
    $frontmatterLines = @($frontmatter -split '\r?\n')
    $nameLine = @($frontmatterLines | Where-Object { $_ -like 'name:*' } | Select-Object -First 1)
    $descriptionLine = @($frontmatterLines | Where-Object { $_ -like 'description:*' } | Select-Object -First 1)
    $declaredName = if ($nameLine.Count -eq 1) { $nameLine[0].Substring(5).Trim(' ', '"', "'") } else { '' }
    if ($declaredName -ne $folderName) {
        $failures += "Skill name does not match folder: $folderName"
    }
    if ($descriptionLine.Count -ne 1 -or [string]::IsNullOrWhiteSpace($descriptionLine[0].Substring(12))) {
        $failures += "Skill has no description: $folderName"
    }
}

$configPath = Join-Path $githubRoot 'translation-pipeline.defaults.json'
try {
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($config.pipelineVersion)) {
        $failures += 'Pipeline config has no version.'
    }
    if ([int]$config.schemaVersion -ne 1) {
        $failures += 'Pipeline config has an unsupported schema version.'
    }
    if ([int]$config.retries.maximumStageAttempts -ne 3 -or
        [int]$config.retries.maximumChunkAttempts -ne 3) {
        $failures += 'Default pipeline retry limits must both be 3.'
    }
    if ([int]$config.state.lockTimeoutSeconds -ne 30) {
        $failures += 'Default pipeline state lock timeout must be 30 seconds.'
    }
    if ([double]$config.quality.maximumHeadingCountDriftPercent -ne 10) {
        $failures += 'Default pipeline heading-count drift threshold must be 10 percent.'
    }
    if ([int]$config.chunking.minimumCharacters -gt [int]$config.chunking.targetCharacters -or
        [int]$config.chunking.targetCharacters -gt [int]$config.chunking.maximumCharacters) {
        $failures += 'Pipeline chunk sizes are inconsistent.'
    }
}
catch {
    $failures += "Pipeline config is invalid JSON: $($_.Exception.Message)"
}

Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File | ForEach-Object {
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    foreach ($parseError in $parseErrors) {
        $failures += "$($_.Name): $($parseError.Message)"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

"Validated 1 agent, $($skillFiles.Count) skills, pipeline defaults, and PowerShell syntax."