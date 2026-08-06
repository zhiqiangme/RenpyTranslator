param(
    [string]$GamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season",
    [string]$TranslationsPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetDirectoryName(
    $MyInvocation.MyCommand.Path
)
$gameDirectory = Join-Path $GamePath "game"
$sourceScript = Join-Path $projectRoot "game\zz_live_translator.rpy"
$sourceConfig = Join-Path $projectRoot "config.example.json"
$sourceTranslations = if ([string]::IsNullOrWhiteSpace($TranslationsPath)) {
    Join-Path $projectRoot "translations"
} else {
    [System.IO.Path]::GetFullPath($TranslationsPath)
}
$buildPretranslated = Join-Path $projectRoot "Build-Pretranslated.ps1"
$targetScript = Join-Path $gameDirectory "zz_live_translator.rpy"
$targetDataDirectory = Join-Path $gameDirectory "live_translator"
$targetConfig = Join-Path $targetDataDirectory "config.json"
$targetPretranslated = Join-Path $targetDataDirectory "pretranslated.jsonl"

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

# 分卷译文安装时合并，游戏仍只需读取一个文件。
if (Test-Path -LiteralPath $sourceTranslations -PathType Container) {
    & $buildPretranslated `
        -SourceDirectory $sourceTranslations `
        -OutputPath $targetPretranslated
}

Write-Host "安装完成：$targetScript"
Write-Host "请编辑配置：$targetConfig"
Write-Host "游戏内快捷键：F9 开关翻译，F10 查看状态。"

# ---------- 更新检测 ----------
$updateScript = Join-Path $projectRoot "Update.ps1"
if (Test-Path -LiteralPath $updateScript -PathType Leaf) {
    try {
        $updateState = & $updateScript -CheckOnly
        if ($updateState -like "UPDATE_AVAILABLE*" -or $updateState -like "UNKNOWN_LOCAL*") {
            Write-Host "检测到项目有可用更新（$updateState）" -ForegroundColor Cyan
            $updateAnswer = Read-Host "按回车立即更新；输入「不更新」跳过"
            if ($updateAnswer.Trim() -ne "不更新") { & $updateScript -Force }
        } elseif ($updateState -ne "NO_RELEASE") {
            Write-Host "模组已是最新版本（$updateState）"
        }
    } catch {
        Write-Host "警告：更新检测失败，不影响本次安装。$_" -ForegroundColor Yellow
    }
}
