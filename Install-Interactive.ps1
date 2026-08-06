# 交互式安装脚本：输入游戏目录、选择字体、填写 DeepSeek API Key
# 用法：在 PowerShell 中运行 .\Install-Interactive.ps1
# 可选参数：提供 -GamePath/-FontChoice/-ApiKey 时跳过对应交互（便于自动化与批量）
param(
    [string]$GamePath = "",
    [string]$FontChoice = "",
    [string]$ApiKey = "",
    [string]$FontSourcePath = "D:\Users\Desktop\HarmonyOS Sans\HarmonyOS_Sans_SC.ttf"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetDirectoryName(
    $MyInvocation.MyCommand.Path
)

$defaultGamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season"

# ---------- 1. 输入游戏目录 ----------
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $gamePathInput = Read-Host "请输入游戏目录（直接回车使用默认路径）`
$defaultGamePath"
    if ([string]::IsNullOrWhiteSpace($gamePathInput)) {
        $GamePath = $defaultGamePath
    } else {
        # 去掉用户可能误输入的引号
        $GamePath = $gamePathInput.Trim().Trim('"').Trim("'")
    }
} else {
    $GamePath = $GamePath.Trim().Trim('"').Trim("'")
}

# ---------- 2. 选择字体 ----------
if ([string]::IsNullOrWhiteSpace($FontChoice)) {
    Write-Host ""
    Write-Host "请选择翻译显示字体："
    Write-Host "  1) 鸿蒙字体 HarmonyOS Sans SC（推荐，随模组内置，无需系统安装）"
    Write-Host "  2) 宋体 SimSun（使用系统字体 C:/Windows/Fonts/simsun.ttc）"
    Write-Host "  3) 微软雅黑 Microsoft YaHei（使用系统字体 C:/Windows/Fonts/msyh.ttc）"
    $fontChoice = Read-Host "请输入 1/2/3（直接回车默认 1）"
    if ([string]::IsNullOrWhiteSpace($fontChoice)) {
        $fontChoice = "1"
    }
    $fontChoice = $fontChoice.Trim()
} else {
    $fontChoice = $FontChoice.Trim()
}

# ---------- 3. 输入 DeepSeek API Key ----------
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Host ""
    $apiKeyInput = Read-Host "请输入 DeepSeek API Key（直接回车：已有配置则保留原值，新安装则留空）"
    $apiKeyInput = $apiKeyInput.Trim()
} else {
    $apiKeyInput = $ApiKey.Trim()
}

# ---------- 开始安装 ----------
$gameDirectory = Join-Path $GamePath "game"
$sourceScript = Join-Path $projectRoot "game\zz_live_translator.rpy"
$sourceConfig = Join-Path $projectRoot "config.example.json"
$sourceTranslations = Join-Path $projectRoot "translations"
$buildPretranslated = Join-Path $projectRoot "Build-Pretranslated.ps1"
$targetScript = Join-Path $gameDirectory "zz_live_translator.rpy"
$targetDataDirectory = Join-Path $gameDirectory "live_translator"
$targetConfig = Join-Path $targetDataDirectory "config.json"
$targetPretranslated = Join-Path $targetDataDirectory "pretranslated.jsonl"
$targetFontDirectory = Join-Path $targetDataDirectory "fonts"
$targetFont = Join-Path $targetFontDirectory "HarmonyOS_Sans_SC.ttf"

if (-not (Test-Path -LiteralPath $gameDirectory -PathType Container)) {
    throw "未找到 Ren'Py game 目录：$gameDirectory"
}

if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) {
    throw "缺少模组脚本：$sourceScript"
}

# 备份旧模组
if (Test-Path -LiteralPath $targetScript -PathType Leaf) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupPath = "$targetScript.$timestamp.bak"
    Copy-Item -LiteralPath $targetScript -Destination $backupPath
    Write-Host "已备份旧模组：$backupPath"
}

New-Item -ItemType Directory -Path $targetDataDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceScript -Destination $targetScript -Force

# 首次安装时复制示例配置；已有配置则保留（base_url/model/旧 key 等不覆盖）
if (-not (Test-Path -LiteralPath $targetConfig -PathType Leaf)) {
    Copy-Item -LiteralPath $sourceConfig -Destination $targetConfig
}

# ---------- 修改 config.json：font 与 api_key ----------
$config = Get-Content -LiteralPath $targetConfig -Raw -Encoding UTF8 | ConvertFrom-Json

# 字体：鸿蒙复制到游戏目录并写相对路径；宋体/微软雅黑引用系统字体
switch ($fontChoice) {
    "1" {
        if (-not (Test-Path -LiteralPath $FontSourcePath -PathType Leaf)) {
            throw "未找到鸿蒙字体源文件：$FontSourcePath`n请用 -FontSourcePath 参数指定 HarmonyOS_Sans_SC.ttf 的实际位置"
        }
        New-Item -ItemType Directory -Path $targetFontDirectory -Force | Out-Null
        Copy-Item -LiteralPath $FontSourcePath -Destination $targetFont -Force
        $config.font = "live_translator/fonts/HarmonyOS_Sans_SC.ttf"
        Write-Host "已安装鸿蒙字体：$targetFont"
    }
    "2" {
        $config.font = "C:/Windows/Fonts/simsun.ttc"
        if (-not (Test-Path -LiteralPath "C:/Windows/Fonts/simsun.ttc" -PathType Leaf)) {
            Write-Host "警告：系统未找到宋体 C:/Windows/Fonts/simsun.ttc，游戏内中文可能显示异常"
        }
    }
    "3" {
        $config.font = "C:/Windows/Fonts/msyh.ttc"
        if (-not (Test-Path -LiteralPath "C:/Windows/Fonts/msyh.ttc" -PathType Leaf)) {
            Write-Host "警告：系统未找到微软雅黑 C:/Windows/Fonts/msyh.ttc，游戏内中文可能显示异常"
        }
    }
    default {
        throw "无效的字体选择：$fontChoice（请输入 1/2/3）"
    }
}

# API Key：输入非空才覆盖；空回车保留原值（新装 config 时为示例占位，一并清空）
if (-not [string]::IsNullOrWhiteSpace($apiKeyInput)) {
    $config.api_key = $apiKeyInput
} elseif ([string]::IsNullOrWhiteSpace($config.api_key)) {
    # 新安装且用户不填：确保是空字符串而非示例占位文本，避免误发请求
    $config.api_key = ""
}

# 无 BOM UTF-8 写回，兼容游戏内置 Python 2.7
$jsonText = $config | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText(
    $targetConfig,
    $jsonText,
    (New-Object System.Text.UTF8Encoding($false))
)
Write-Host "已更新配置：$targetConfig"

# 分卷译文安装时合并，游戏仍只需读取一个文件
if (Test-Path -LiteralPath $sourceTranslations -PathType Container) {
    & $buildPretranslated `
        -SourceDirectory $sourceTranslations `
        -OutputPath $targetPretranslated
}

Write-Host ""
Write-Host "安装完成：$targetScript"
Write-Host "配置文件：$targetConfig"
Write-Host "游戏内快捷键：F9 开关翻译，F10 查看状态。"
