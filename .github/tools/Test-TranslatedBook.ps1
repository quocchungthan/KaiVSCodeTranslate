[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,

    [string]$Markdown,
    [string]$Pdf,
    [string]$RepositoryRoot
)

. (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-TranslationRepositoryRoot
}
$state = Read-TranslationJobState -Path (Join-Path $RepositoryRoot "_processing\jobs\$JobId\job.json")
$config = Get-TranslationPipelineConfig -RepositoryRoot $RepositoryRoot
$maximumHeadingDriftPercent = Get-TranslationMaximumHeadingDriftPercent -Config $config
if ([string]::IsNullOrWhiteSpace($Markdown)) {
    $Markdown = "_output/$JobId/book.vi.md"
}

$markdownPath = Join-Path $RepositoryRoot $Markdown
$failures = @()
$warnings = @()
if (-not (Test-Path -LiteralPath $markdownPath -PathType Leaf)) {
    $failures += "Missing assembled Markdown: $Markdown"
}
else {
    $content = [System.IO.File]::ReadAllText($markdownPath)
    $sourceHeadings = 0
    if ($null -ne $state.artifacts.PSObject.Properties['sourceMarkdown']) {
        $sourceContent = [System.IO.File]::ReadAllText((Join-Path $RepositoryRoot $state.artifacts.sourceMarkdown))
        $sourceHeadings = @($sourceContent -split '\r?\n' | Where-Object { $_ -match '^#{1,6}\s+' }).Count
    }
    $translatedHeadings = @($content -split '\r?\n' | Where-Object { $_ -match '^#{1,6}\s+' }).Count
    if ($sourceHeadings -gt 0 -and $translatedHeadings -ne $sourceHeadings) {
        $headingDriftPercent = [Math]::Abs($translatedHeadings - $sourceHeadings) * 100.0 / $sourceHeadings
        $message = "Heading count differs: source=$sourceHeadings translated=$translatedHeadings drift=$([Math]::Round($headingDriftPercent, 2))% threshold=$maximumHeadingDriftPercent%"
        if ($headingDriftPercent -gt $maximumHeadingDriftPercent) {
            $failures += $message
        }
        else {
            $warnings += $message
        }
    }
    if ((@([regex]::Matches($content, '(?m)^\s*(```|~~~)')).Count % 2) -ne 0) {
        $failures += 'Unbalanced fenced code block markers.'
    }
    if ($content -notmatch '[\u0102-\u01B0\u1EA0-\u1EF9]') {
        $warnings += 'No Vietnamese-specific characters were detected.'
    }
    if ($content -match '(?i)\b(TODO_TRANSLATE|TRANSLATION_MISSING|UNCLEAR_SOURCE)\b') {
        $failures += 'Unresolved translation marker remains in the book.'
    }

    $links = [regex]::Matches($content, '!?(?:\[[^\]]*\])\(([^)]+)\)')
    foreach ($link in $links) {
        $target = $link.Groups[1].Value.Trim().Split(' ')[0].Trim('<', '>')
        if ($target -match '^(?:https?:|mailto:|#|data:)') {
            continue
        }
        $decodedTarget = [System.Uri]::UnescapeDataString(($target -split '#')[0])
        $linkedPath = Join-Path (Split-Path -Parent $markdownPath) $decodedTarget
        if (-not (Test-Path -LiteralPath $linkedPath)) {
            $failures += "Broken relative link: $target"
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($Pdf)) {
    $pdfPath = Join-Path $RepositoryRoot $Pdf
    if (-not (Test-Path -LiteralPath $pdfPath -PathType Leaf) -or (Get-Item -LiteralPath $pdfPath).Length -eq 0) {
        $failures += "Rendered PDF is missing or empty: $Pdf"
    }
}

$report = [ordered]@{
    schemaVersion = 1
    jobId = $JobId
    checkedUtc = [DateTime]::UtcNow.ToString('o')
    passed = $failures.Count -eq 0
    failures = $failures
    warnings = $warnings
}
$reportPath = Join-Path $RepositoryRoot "_output\$JobId\qc.json"
Write-TranslationJsonAtomic -Path $reportPath -Value $report
$report
if ($failures.Count -gt 0) {
    exit 1
}