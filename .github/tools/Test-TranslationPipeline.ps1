[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "KaiVSCodeTranslate-$([guid]::NewGuid().ToString('N'))"

function Assert-TranslationTest {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-TranslationThrows {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [Parameter(Mandatory = $true)]
        [string]$MessagePattern,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    try {
        & $Action
    }
    catch {
        Assert-TranslationTest ($_.Exception.Message -match $MessagePattern) $Message
        return
    }
    throw "Assertion failed: $Message"
}

try {
    New-Item -ItemType Directory -Path (Join-Path $testRoot '_pdfs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot '.github') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot '.github\translation-pipeline.defaults.json') -Destination (Join-Path $testRoot '.github\translation-pipeline.defaults.json')
    $testConfigPath = Join-Path $testRoot '.github\translation-pipeline.defaults.json'
    $testConfig = Get-Content -LiteralPath $testConfigPath -Raw | ConvertFrom-Json
    $testConfig.chunking.minimumCharacters = 20
    $testConfig.chunking.targetCharacters = 100
    $testConfig.chunking.maximumCharacters = 140
    $testConfig.retries.maximumStageAttempts = 1
    $testConfig.retries.maximumChunkAttempts = 1
    $testConfig | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $testConfigPath -Encoding UTF8

    $sourcePath = Join-Path $testRoot '_pdfs\Sample Book.pdf'
    [System.IO.File]::WriteAllBytes($sourcePath, [byte[]](1..32))
    $initial = @(& (Join-Path $PSScriptRoot 'Initialize-TranslationJobs.ps1') -RepositoryRoot $testRoot)
    Assert-TranslationTest ($initial.Count -eq 1 -and $initial[0].action -eq 'created') 'A source PDF creates one job.'
    $jobId = $initial[0].jobId

    $repeat = @(& (Join-Path $PSScriptRoot 'Initialize-TranslationJobs.ps1') -RepositoryRoot $testRoot)
    Assert-TranslationTest ($repeat[0].action -eq 'unchanged') 'Repeated discovery is idempotent.'

    . (Join-Path $PSScriptRoot 'TranslationPipeline.Common.ps1')
    $missingSchemaPath = Join-Path $testRoot 'missing-schema.json'
    [pscustomobject]@{ jobId = 'missing-schema' } | ConvertTo-Json | Set-Content -LiteralPath $missingSchemaPath -Encoding UTF8
    Assert-TranslationThrows { Read-TranslationJobState -Path $missingSchemaPath | Out-Null } 'schemaVersion is missing' 'Missing job state schemaVersion is rejected clearly.'
    $unsupportedSchemaPath = Join-Path $testRoot 'unsupported-schema.json'
    [pscustomobject]@{ schemaVersion = 2; jobId = 'unsupported-schema' } | ConvertTo-Json | Set-Content -LiteralPath $unsupportedSchemaPath -Encoding UTF8
    Assert-TranslationThrows { Read-TranslationJobState -Path $unsupportedSchemaPath | Out-Null } 'Unsupported job state schemaVersion' 'Unsupported job state schemaVersion is rejected clearly.'

    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage convert -Status running | Out-Null
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage convert -Status failed -ErrorMessage 'Expected retry test failure.' | Out-Null
    $stageBlocked = & (Join-Path $PSScriptRoot 'Get-NextTranslationAction.ps1') -RepositoryRoot $testRoot -SkipDiscovery
    Assert-TranslationTest ($stageBlocked.action -eq 'blocked' -and $stageBlocked.jobs[0].reason -match 'exhausted maximum attempts \(1\)') 'A failed stage is blocked after its configured maximum attempts.'
    $blockedStageState = Get-Content -LiteralPath (Join-Path $testRoot "_processing\jobs\$jobId\job.json") -Raw | ConvertFrom-Json
    Assert-TranslationTest ($blockedStageState.stages.convert.status -eq 'blocked') 'Stage retry exhaustion is persisted in job state.'

    $testConfig = Get-Content -LiteralPath $testConfigPath -Raw | ConvertFrom-Json
    $testConfig.pipelineVersion = 'retry-test-recovery'
    $testConfig | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $testConfigPath -Encoding UTF8
    $retryReset = @(& (Join-Path $PSScriptRoot 'Initialize-TranslationJobs.ps1') -RepositoryRoot $testRoot)
    Assert-TranslationTest ($retryReset[0].action -eq 'invalidated') 'Pipeline invalidation resets the stage retry test fixture.'

    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage convert -Status running | Out-Null
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage convert -Status completed -ArtifactName sourceMarkdown -ArtifactRelativePath "_processing/jobs/$jobId/source/book.md" | Out-Null

    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage chunk -Status running | Out-Null
    $sourceRoot = Join-Path $testRoot "_processing\jobs\$jobId\source"
    $assetsRoot = Join-Path $sourceRoot 'assets'
    New-Item -ItemType Directory -Path $assetsRoot -Force | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $assetsRoot 'figure.png'), [byte[]](1..8))
    $sourceMarkdownPath = Join-Path $sourceRoot 'book.md'
    $sourceBook = "# One`r`n`r`nFirst section contains enough technical prose to form its own semantic unit.`r`n`r`n![Figure](assets/figure.png)`r`n`r`n# Two`r`n`r`nSecond section contains enough technical prose to form another semantic unit.`r`n"
    [System.IO.File]::WriteAllText($sourceMarkdownPath, $sourceBook)
    $split = & (Join-Path $PSScriptRoot 'Split-MarkdownIntoChunks.ps1') -RepositoryRoot $testRoot -JobId $jobId
    Assert-TranslationTest ($split.chunkCount -eq 2) 'Semantic headings produce two bounded chunks.'
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage chunk -Status completed | Out-Null

    $next = & (Join-Path $PSScriptRoot 'Get-NextTranslationAction.ps1') -RepositoryRoot $testRoot -SkipDiscovery
    Assert-TranslationTest ($next.action -eq 'translate-chunk' -and $next.chunkId -eq '0001') 'The first pending chunk is selected.'

    & (Join-Path $PSScriptRoot 'Update-TranslationChunkState.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0001' -Status running | Out-Null
    & (Join-Path $PSScriptRoot 'Update-TranslationChunkState.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0001' -Status failed -ErrorMessage 'Expected chunk retry test failure.' | Out-Null
    $chunkBlocked = & (Join-Path $PSScriptRoot 'Get-NextTranslationAction.ps1') -RepositoryRoot $testRoot -SkipDiscovery
    Assert-TranslationTest ($chunkBlocked.action -eq 'blocked' -and $chunkBlocked.jobs[0].chunkId -eq '0001') 'A failed chunk is blocked after its configured maximum attempts.'
    $blockedChunkState = Get-Content -LiteralPath (Join-Path $testRoot "_processing\jobs\$jobId\job.json") -Raw | ConvertFrom-Json
    Assert-TranslationTest ($blockedChunkState.chunks[0].status -eq 'blocked' -and $blockedChunkState.stages.translate.message -match 'exhausted maximum attempts') 'Chunk retry exhaustion and its reason are persisted in job state.'
    $testConfig = Get-Content -LiteralPath $testConfigPath -Raw | ConvertFrom-Json
    $testConfig.retries.maximumChunkAttempts = 3
    $testConfig | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $testConfigPath -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Update-TranslationChunkState.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0001' -Status pending | Out-Null
    & (Join-Path $PSScriptRoot 'Update-TranslationChunkState.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0001' -Status running | Out-Null
    $firstTranslation = "_processing/jobs/$jobId/source/translated-0001.vi.md"
    [System.IO.File]::WriteAllText((Join-Path $testRoot $firstTranslation), "# Một`r`n`r`nNội dung kỹ thuật.`r`n`r`n![Hình](assets/figure.png)`r`n")
    & (Join-Path $PSScriptRoot 'Save-ChunkContext.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0001' -VietnameseMarkdown $firstTranslation -BackwardSummary 'First summary' -Handoff 'Continue term A.' -TerminologyJson '[{"source":"A","target":"A"}]' | Out-Null
    $firstState = Get-Content -LiteralPath (Join-Path $testRoot "_processing\jobs\$jobId\job.json") -Raw | ConvertFrom-Json
    $firstUpdatedUtc = $firstState.updatedUtc
    & (Join-Path $PSScriptRoot 'Save-ChunkContext.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0001' -VietnameseMarkdown $firstTranslation -BackwardSummary 'Ignored' -Handoff 'Ignored' | Out-Null
    $idempotentState = Get-Content -LiteralPath (Join-Path $testRoot "_processing\jobs\$jobId\job.json") -Raw | ConvertFrom-Json
    Assert-TranslationTest ($idempotentState.updatedUtc -eq $firstUpdatedUtc) 'Saving a completed chunk is idempotent.'

    $prior = & (Join-Path $PSScriptRoot 'Get-PreviousChunkContext.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0002' | ConvertFrom-Json
    Assert-TranslationTest ($prior.immediateHandoff -eq 'Continue term A.') 'The next chunk receives the prior handoff.'

    & (Join-Path $PSScriptRoot 'Update-TranslationChunkState.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0002' -Status running | Out-Null
    $secondTranslation = "_processing/jobs/$jobId/source/translated-0002.vi.md"
    [System.IO.File]::WriteAllText((Join-Path $testRoot $secondTranslation), "# Hai`r`n`r`nNội dung kỹ thuật tiếp theo.`r`n")
    & (Join-Path $PSScriptRoot 'Save-ChunkContext.ps1') -RepositoryRoot $testRoot -JobId $jobId -ChunkId '0002' -VietnameseMarkdown $secondTranslation -BackwardSummary 'Second summary' -Handoff 'Done.' | Out-Null
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage assemble -Status running | Out-Null
    $statePath = Join-Path $testRoot "_processing\jobs\$jobId\job.json"
    $sequenceState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $sequenceState.chunks[1].sequence = 1
    $sequenceState | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Assert-TranslationThrows { & (Join-Path $PSScriptRoot 'Merge-TranslatedBook.ps1') -RepositoryRoot $testRoot -JobId $jobId | Out-Null } 'unique, contiguous, and ordered' 'Duplicate chunk sequences are rejected.'
    $sequenceState.chunks[0].sequence = 1
    $sequenceState.chunks[1].sequence = 3
    $sequenceState | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Assert-TranslationThrows { & (Join-Path $PSScriptRoot 'Merge-TranslatedBook.ps1') -RepositoryRoot $testRoot -JobId $jobId | Out-Null } 'unique, contiguous, and ordered' 'Missing or non-contiguous chunk sequences are rejected.'
    $sequenceState.chunks[0].sequence = 2
    $sequenceState.chunks[1].sequence = 1
    $sequenceState | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Assert-TranslationThrows { & (Join-Path $PSScriptRoot 'Merge-TranslatedBook.ps1') -RepositoryRoot $testRoot -JobId $jobId | Out-Null } 'unique, contiguous, and ordered' 'Out-of-order chunk sequences are rejected.'
    $sequenceState.chunks[0].sequence = 1
    $sequenceState.chunks[1].sequence = 2
    $sequenceState | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $statePath -Encoding UTF8
    $merged = & (Join-Path $PSScriptRoot 'Merge-TranslatedBook.ps1') -RepositoryRoot $testRoot -JobId $jobId
    Assert-TranslationTest ((Test-Path -LiteralPath (Join-Path $testRoot "_output\$jobId\assets\figure.png"))) 'Assembly copies referenced assets.'
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage assemble -Status completed -ArtifactName translatedMarkdown -ArtifactRelativePath $merged.markdown | Out-Null
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage validate -Status running | Out-Null
    $assembledPath = Join-Path $testRoot $merged.markdown
    $assembledContent = [System.IO.File]::ReadAllText($assembledPath)
    [System.IO.File]::WriteAllText($assembledPath, ($assembledContent -replace '(?m)^# Hai\r?\n', ''))
    $testConfig = Get-Content -LiteralPath $testConfigPath -Raw | ConvertFrom-Json
    $testConfig.quality.maximumHeadingCountDriftPercent = 60
    $testConfig | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $testConfigPath -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Test-TranslatedBook.ps1') -RepositoryRoot $testRoot -JobId $jobId | Out-Null
    $warningQc = Get-Content -LiteralPath (Join-Path $testRoot "_output\$jobId\qc.json") -Raw | ConvertFrom-Json
    Assert-TranslationTest ($warningQc.passed -and $warningQc.warnings.Count -gt 0) 'Heading drift within the configured threshold warns without failing.'
    $testConfig.quality.maximumHeadingCountDriftPercent = 10
    $testConfig | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $testConfigPath -Encoding UTF8
    $powerShell = (Get-Process -Id $PID).Path
    $qcProcess = Start-Process -FilePath $powerShell -ArgumentList @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-TranslatedBook.ps1'), '-RepositoryRoot', $testRoot, '-JobId', $jobId) -Wait -PassThru -NoNewWindow
    $failedQc = Get-Content -LiteralPath (Join-Path $testRoot "_output\$jobId\qc.json") -Raw | ConvertFrom-Json
    Assert-TranslationTest ($qcProcess.ExitCode -eq 1 -and -not $failedQc.passed -and $failedQc.failures.Count -gt 0) 'Heading drift above the configured threshold fails structural QC.'
    [System.IO.File]::WriteAllText($assembledPath, $assembledContent)
    $qc = & (Join-Path $PSScriptRoot 'Test-TranslatedBook.ps1') -RepositoryRoot $testRoot -JobId $jobId
    Assert-TranslationTest ($qc.passed) 'Assembled Markdown passes structural and link QC.'
    $sources = @(& (Join-Path $PSScriptRoot 'Add-TranslationResearchSource.ps1') -RepositoryRoot $testRoot -JobId $jobId -Url 'https://example.com/layout-docs' -Title 'Layout docs' -Purpose 'Render configuration')
    Assert-TranslationTest ($sources.Count -eq 1) 'Research provenance is recorded once.'
    & (Join-Path $PSScriptRoot 'Update-TranslationState.ps1') -RepositoryRoot $testRoot -JobId $jobId -Stage validate -Status completed | Out-Null
    $idle = & (Join-Path $PSScriptRoot 'Get-NextTranslationAction.ps1') -RepositoryRoot $testRoot -SkipDiscovery
    Assert-TranslationTest ($idle.action -eq 'idle') 'A validated job is no longer actionable.'

    $configPath = $testConfigPath
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $config.pipelineVersion = 'test-invalidation'
    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $configPath -Encoding UTF8
    $invalidated = @(& (Join-Path $PSScriptRoot 'Initialize-TranslationJobs.ps1') -RepositoryRoot $testRoot)
    Assert-TranslationTest ($invalidated[0].action -eq 'invalidated') 'A pipeline version change invalidates derived state.'

    [System.IO.File]::WriteAllBytes($sourcePath, [byte[]](33..64))
    $replacement = @(& (Join-Path $PSScriptRoot 'Initialize-TranslationJobs.ps1') -RepositoryRoot $testRoot)
    $oldState = Get-Content -LiteralPath (Join-Path $testRoot "_processing\jobs\$jobId\job.json") -Raw | ConvertFrom-Json
    Assert-TranslationTest ($replacement[0].jobId -ne $jobId -and $oldState.status -eq 'superseded') 'A source change creates a new job and supersedes the prior job.'

    'Translation pipeline smoke test passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}