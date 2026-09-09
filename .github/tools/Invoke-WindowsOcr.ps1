<#
.SYNOPSIS
    Runs the locally installed Windows.Media.Ocr engine over prepared page bands.

.DESCRIPTION
    Emits one JSON file per page containing recognized lines with strip-space bounding
    boxes. No image data leaves the machine; the OS OCR engine is used offline.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Ocr.OcrEngine, Windows.Media, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]

$asTaskMethod = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

function Wait-WinRt {
    param($Operation, [type]$ResultType)
    $asTaskMethod.MakeGenericMethod($ResultType).Invoke($null, @($Operation)).GetAwaiter().GetResult()
}

$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($null -eq $engine) {
    throw 'No Windows OCR engine is available for the current user profile languages.'
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$scale = [double]$manifest.scale
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$totalLines = 0
foreach ($page in $manifest.pages) {
    $lines = New-Object System.Collections.Generic.List[object]
    foreach ($band in $page.bands) {
        $bandPath = [System.IO.Path]::GetFullPath($band.path)
        $file = Wait-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync($bandPath)) ([Windows.Storage.StorageFile])
        $stream = Wait-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        try {
            $decoder = Wait-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Wait-WinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            $result = Wait-WinRt ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
            foreach ($line in $result.Lines) {
                $words = @($line.Words)
                if ($words.Count -eq 0) { continue }
                $left = [double]::MaxValue; $right = [double]::MinValue
                $top = [double]::MaxValue; $bottom = [double]::MinValue
                $wordList = New-Object System.Collections.Generic.List[object]
                foreach ($word in $words) {
                    $rect = $word.BoundingRect
                    if ($rect.Left -lt $left) { $left = $rect.Left }
                    if ($rect.Right -gt $right) { $right = $rect.Right }
                    if ($rect.Top -lt $top) { $top = $rect.Top }
                    if ($rect.Bottom -gt $bottom) { $bottom = $rect.Bottom }
                    $wordList.Add([ordered]@{
                        text   = $word.Text
                        left   = [math]::Round($rect.Left / $scale, 2)
                        right  = [math]::Round($rect.Right / $scale, 2)
                        top    = [math]::Round($band.top + ($rect.Top / $scale), 2)
                        bottom = [math]::Round($band.top + ($rect.Bottom / $scale), 2)
                    })
                }
                $lines.Add([ordered]@{
                    text   = $line.Text
                    left   = [math]::Round($left / $scale, 2)
                    right  = [math]::Round($right / $scale, 2)
                    top    = [math]::Round($band.top + ($top / $scale), 2)
                    bottom = [math]::Round($band.top + ($bottom / $scale), 2)
                    words  = $wordList.ToArray()
                })
            }
            $bitmap.Dispose()
        }
        finally {
            $stream.Dispose()
        }
    }

    $sorted = @($lines | Sort-Object { $_.top }, { $_.left })
    $totalLines += $sorted.Count
    $outputPath = Join-Path $OutputDirectory ("page{0:d3}.ocr.json" -f [int]$page.page)
    [ordered]@{
        page   = [int]$page.page
        width  = [int]$page.width
        height = [int]$page.height
        lines  = $sorted
    } | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $outputPath -Encoding UTF8
}

[pscustomobject]@{ pages = $manifest.pages.Count; lines = $totalLines }
