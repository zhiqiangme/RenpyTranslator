param(
    [string]$GamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameDirectory = Join-Path $GamePath "game"
$sourceScript = Join-Path $projectRoot "game\zz_live_translator.rpy"
$sourceConfig = Join-Path $projectRoot "config.example.json"
$targetScript = Join-Path $gameDirectory "zz_live_translator.rpy"
$targetDataDirectory = Join-Path $gameDirectory "live_translator"
$targetConfig = Join-Path $targetDataDirectory "config.json"

if (-not (Test-Path -LiteralPath $gameDirectory -PathType Container)) {
    throw "未找到 Ren'Py game 目录：$gameDirectory"
}

if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) {
    throw "缺少模组脚本：$sourceScript"
}

if (Test-Path -LiteralPath $targetScript -PathType Leaf) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupPath = "$targetScript.$timestamp.bak"
    Copy-Item -LiteralPath $targetScript -Destination $backupPath
    Write-Host "已备份旧模组：$backupPath"
}

New-Item -ItemType Directory -Path $targetDataDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceScript -Destination $targetScript -Force

# 保留已有 API 配置与翻译缓存，升级时不会覆盖。
if (-not (Test-Path -LiteralPath $targetConfig -PathType Leaf)) {
    Copy-Item -LiteralPath $sourceConfig -Destination $targetConfig
}

Write-Host "安装完成：$targetScript"
Write-Host "请编辑配置：$targetConfig"
Write-Host "游戏内快捷键：F9 开关翻译，F10 查看状态。"
