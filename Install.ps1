# 交互式安装脚本：输入游戏目录、选择字体、填写 DeepSeek API Key
# 用法：在 PowerShell 中运行 .\Install-Interactive.ps1
# 可选参数：提供 -GamePath/-FontChoice/-ApiKey 时跳过对应交互（便于自动化与批量）
param(
    [string]$GamePath = "",
    [string]$FontChoice = "",
    [string]$ApiKey = "",
    [string]$FontSourcePath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetDirectoryName(
    $MyInvocation.MyCommand.Path
)

$defaultGamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season"

# ---------- 0. 检查 PowerShell 7 (pwsh) ----------
# 本脚本的 API Key 加密（DPAPI）依赖 pwsh 7；Windows PowerShell 5 下该类型不可用。
# 未安装 pwsh 时先询问是否自动安装（winget），用户不同意则不安装。
if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "未检测到 PowerShell 7（pwsh）。" -ForegroundColor Yellow
    Write-Host "本脚本的 API Key 加密功能需要 pwsh 7，Windows PowerShell 5 下无法使用。" -ForegroundColor Yellow
    $pwshChoice = Read-Host "按回车自动安装 pwsh；输入 n 跳过（不安装）"
    if ($pwshChoice.Trim().ToLower() -ne "n") {
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            Write-Host "正在通过 winget 安装 PowerShell 7 ..."
            winget install --id Microsoft.PowerShell --source winget `
                --accept-package-agreements --accept-source-agreements
            Write-Host "pwsh 安装完成，请用 PowerShell 7 重新运行本脚本。" -ForegroundColor Cyan
            exit 0
        } else {
            Write-Host "未找到 winget，无法自动安装。请手动下载安装：" -ForegroundColor Yellow
            Write-Host "https://github.com/PowerShell/PowerShell/releases" -ForegroundColor Cyan
            Write-Host "安装完成后用 PowerShell 7 重新运行本脚本。"
            exit 1
        }
    } else {
        Write-Host "已跳过 pwsh 安装。" -ForegroundColor Yellow
        Write-Host "警告：未安装 pwsh 时，API Key 加密在 Windows PowerShell 5 下无法完成，安装可能中断。" -ForegroundColor Yellow
    }
}

# ---------- 1. 输入游戏目录 ----------
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    Write-Host "默认游戏目录：$defaultGamePath"
    $gamePathInput = Read-Host "请输入游戏目录（直接回车使用默认路径）"
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
    $apiKeyInput = Read-Host "请输入 DeepSeek API Key（直接回车：已有配置则保留原值，新安装则留空；将使用 DPAPI 加密保存）"
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
        # 未指定时优先使用项目内置字体目录
        if ([string]::IsNullOrWhiteSpace($FontSourcePath)) {
            $FontSourcePath = Join-Path $projectRoot "fonts\HarmonyOS_Sans_SC.ttf"
        }
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

# API Key：DPAPI 加密后写入 api_key_encrypted，config.json 不再保存明文。
# 输入非空才覆盖；空回车保留原值（新装 config 时留空）。
# 要求 pwsh 7 环境（脚本开头已检查并引导安装）；5.1 下 ProtectedData 不可用。
if (-not [string]::IsNullOrWhiteSpace($apiKeyInput)) {
    $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($apiKeyInput)
    $encrypted = [System.Security.Cryptography.ProtectedData]::Protect(
        $keyBytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    $config.api_key_encrypted = [Convert]::ToBase64String($encrypted)
    $config.api_key = ""
    Write-Host "API Key 已使用 DPAPI 加密保存（绑定当前 Windows 用户）"
} elseif ([string]::IsNullOrWhiteSpace($config.api_key_encrypted)) {
    # 无新输入且无加密密钥：若存在旧明文 key 则加密迁移，否则清空避免误发请求
    $oldKey = [string]$config.api_key
    if (-not [string]::IsNullOrWhiteSpace($oldKey) -and
        $oldKey -notmatch "这里填写" -and $oldKey -notmatch "不要在这里填") {
        $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($oldKey)
        $encrypted = [System.Security.Cryptography.ProtectedData]::Protect(
            $keyBytes,
            $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser
        )
        $config.api_key_encrypted = [Convert]::ToBase64String($encrypted)
        Write-Host "已将旧明文 API Key 加密迁移到 api_key_encrypted"
    } else {
        $config.api_key_encrypted = ""
    }
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
Write-Host "游戏内快捷键：" -NoNewline
Write-Host "F9 开关翻译，F10 查看状态。" -ForegroundColor Red
Write-Host "如需修改 " -NoNewline
Write-Host "API Key" -ForegroundColor Green -NoNewline
Write-Host " 或切换模型服务商，请运行 " -NoNewline
Write-Host "Configure-Api.ps1" -ForegroundColor Green -NoNewline
Write-Host "。"

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
