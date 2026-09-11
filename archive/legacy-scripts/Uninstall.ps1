param(
    [string]$GamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season",
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$gameDirectory = Join-Path $GamePath "game"
$targetScript = Join-Path $gameDirectory "zz_live_translator.rpy"
$compiledScript = Join-Path $gameDirectory "zz_live_translator.rpyc"
$targetDataDirectory = Join-Path $gameDirectory "live_translator"

foreach ($targetFile in @($targetScript, $compiledScript)) {
    if (Test-Path -LiteralPath $targetFile -PathType Leaf) {
        Remove-Item -LiteralPath $targetFile -Force
        Write-Host "已移除：$targetFile"
    }
}

if ($RemoveData -and (Test-Path -LiteralPath $targetDataDirectory)) {
    $resolvedGameDirectory = (Resolve-Path -LiteralPath $gameDirectory).Path
    $resolvedDataDirectory = (Resolve-Path -LiteralPath $targetDataDirectory).Path
    $expectedDataDirectory = Join-Path $resolvedGameDirectory "live_translator"
    if ($resolvedDataDirectory -ne $expectedDataDirectory) {
        throw "数据目录校验失败，拒绝删除：$resolvedDataDirectory"
    }
    Remove-Item -LiteralPath $resolvedDataDirectory -Recurse -Force
    Write-Host "已移除配置与缓存：$resolvedDataDirectory"
} else {
    Write-Host "已保留 API 配置与翻译缓存。若要一并删除，请添加 -RemoveData。"
}
