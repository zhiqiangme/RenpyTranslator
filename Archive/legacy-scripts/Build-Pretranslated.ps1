param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot "translations"),
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$translationFiles = @(
    Get-ChildItem -LiteralPath $SourceDirectory -Filter "*.jsonl" -File |
        Sort-Object Name
)

if ($translationFiles.Count -eq 0) {
    throw "没有找到分卷译文：$SourceDirectory"
}

$mergedLines = [System.Collections.Generic.List[string]]::new()
$knownSources = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal
)

foreach ($translationFile in $translationFiles) {
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines($translationFile.FullName)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $record = $line | ConvertFrom-Json
        } catch {
            throw "JSON 格式错误：$($translationFile.FullName):$lineNumber"
        }

        if (
            [string]::IsNullOrWhiteSpace([string]$record.source) -or
            [string]::IsNullOrWhiteSpace([string]$record.translation)
        ) {
            throw "缺少原文或译文：$($translationFile.FullName):$lineNumber"
        }

        if (-not $knownSources.Add([string]$record.source)) {
            throw "发现重复原文：$($record.source)"
        }
        $mergedLines.Add($line)
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

# Ren'Py/Python 2.7 读取无 BOM 的 UTF-8 最稳定。
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
$mergedText = [string]::Join("`n", $mergedLines) + "`n"
[System.IO.File]::WriteAllText($OutputPath, $mergedText, $utf8WithoutBom)

Write-Host "已合并 $($translationFiles.Count) 个分卷，共 $($mergedLines.Count) 条：$OutputPath"
