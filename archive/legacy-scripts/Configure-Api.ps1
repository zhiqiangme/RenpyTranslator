# 配置脚本：设置 API Key（DPAPI 加密）与模型服务商
# 用法：在 PowerShell 中运行 .\Configure-Api.ps1
# 交互流程：选择模型服务商（默认 DeepSeek）→ 计费方式（官方API/订阅端点，无订阅不显示）→ 确认 API 地址 → 填写 API Key → 确认模型名
# 可选参数：提供 -GamePath/-Provider/-ApiKey/-BaseUrl/-Model 时跳过对应交互（便于自动化）
param(
    [string]$GamePath = "",
    [string]$Provider = "",
    [string]$ApiKey = "",
    [string]$BaseUrl = "",
    [string]$Model = ""
)

$ErrorActionPreference = "Stop"

# ---------- 0. 检查 PowerShell 7 (pwsh) ----------
# 本脚本的 API Key 加密（DPAPI）依赖 pwsh 7；Windows PowerShell 5 下该类型不可用。
# 未安装 pwsh 时先询问是否自动安装（winget），用户不同意则不安装。
# 否定词白名单：回车=安装，n/N/不/取消/停止 等=跳过。
$pwshDeclineWords = @("n", "no", "不", "不装", "不安装", "不同意", "不要",
    "取消", "停止", "退出", "跳过", "跳过安装", "stop", "exit", "quit",
    "cancel", "skip")
if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "未检测到 PowerShell 7（pwsh）。" -ForegroundColor Yellow
    Write-Host "本脚本的 API Key 加密功能需要 pwsh 7，Windows PowerShell 5 下无法使用。" -ForegroundColor Yellow
    $pwshChoice = Read-Host "按回车自动安装 pwsh；输入 n/不/取消 等跳过（不安装）"
    if ($pwshDeclineWords -notcontains $pwshChoice.Trim().ToLower()) {
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
        Write-Host "警告：未安装 pwsh 时，API Key 加密在 Windows PowerShell 5 下无法完成，操作可能中断。" -ForegroundColor Yellow
    }
}

$defaultGamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season"

# 内置服务商预设：名称 / base_url / 默认模型 / 订阅端点（无则不填）/ 订阅端点默认模型
# 默认模型为该厂商最新款性价比模型；订阅端点与官方按量 API 分开的厂商才填 PlanBaseUrl
$providers = @(
    @{ Name = "DeepSeek"; BaseUrl = "https://api.deepseek.com"; DefaultModel = "deepseek-v4-flash"; PlanBaseUrl = ""; PlanModel = "" },
    @{ Name = "OpenAI"; BaseUrl = "https://api.openai.com/v1"; DefaultModel = "gpt-5.6-luna"; PlanBaseUrl = ""; PlanModel = "" },
    @{ Name = "小米 MiMo"; BaseUrl = "https://api.xiaomimimo.com/v1"; DefaultModel = "mimo-v2.5"; PlanBaseUrl = "https://token-plan-cn.xiaomimimo.com/v1"; PlanModel = "mimo-v2.5" },
    @{ Name = "MiniMax"; BaseUrl = "https://api.minimax.chat/v1"; DefaultModel = "minimax-m3"; PlanBaseUrl = "https://api.minimaxi.com/v1"; PlanModel = "MiniMax-M3" },
    @{ Name = "腾讯混元"; BaseUrl = "https://api.hunyuan.cloud.tencent.com/v1"; DefaultModel = "hy3"; PlanBaseUrl = ""; PlanModel = "" },
    @{ Name = "Google Gemini"; BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai"; DefaultModel = "gemini-3.6-flash"; PlanBaseUrl = ""; PlanModel = "" },
    @{ Name = "阿里通义千问"; BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1"; DefaultModel = "qwen-flash"; PlanBaseUrl = "https://coding.dashscope.aliyuncs.com/v1"; PlanModel = "qwen3.5-plus" },
    @{ Name = "智谱 GLM"; BaseUrl = "https://open.bigmodel.cn/api/paas/v4"; DefaultModel = "glm-4.7-flash"; PlanBaseUrl = "https://open.bigmodel.cn/api/coding/paas/v4"; PlanModel = "glm-4.7" },
    @{ Name = "Kimi（月之暗面）"; BaseUrl = "https://api.moonshot.cn/v1"; DefaultModel = "kimi-k2.6"; PlanBaseUrl = "https://api.kimi.com/coding/v1"; PlanModel = "kimi-for-coding" },
    @{ Name = "字节豆包"; BaseUrl = "https://ark.cn-beijing.volces.com/api/v3"; DefaultModel = "doubao-seed-2.0-lite"; PlanBaseUrl = "https://ark.cn-beijing.volces.com/api/coding/v3"; PlanModel = "doubao-seed-2.0-lite" },
    @{ Name = "自定义（手动输入）"; BaseUrl = ""; DefaultModel = ""; PlanBaseUrl = ""; PlanModel = "" }
)

try {
    # ---------- 1. 输入游戏目录 ----------
    if ([string]::IsNullOrWhiteSpace($GamePath)) {
        Write-Host ""
        Write-Host "默认游戏目录：$defaultGamePath"
        $gamePathInput = Read-Host "请输入游戏目录（直接回车使用默认路径）"
        if ([string]::IsNullOrWhiteSpace($gamePathInput)) {
            $GamePath = $defaultGamePath
        } else {
            $GamePath = $gamePathInput.Trim().Trim('"').Trim("'")
        }
    } else {
        $GamePath = $GamePath.Trim().Trim('"').Trim("'")
    }

    $targetConfig = Join-Path $GamePath "game\live_translator\config.json"
    if (-not (Test-Path -LiteralPath $targetConfig -PathType Leaf)) {
        throw "未找到配置文件：$targetConfig`n请先运行 Install.ps1 安装翻译插件。"
    }

    $config = Get-Content -LiteralPath $targetConfig -Raw -Encoding UTF8 | ConvertFrom-Json

    # ---------- 2. 选择模型服务商（默认 DeepSeek，直接回车） ----------
    if ([string]::IsNullOrWhiteSpace($Provider)) {
        Write-Host ""
        Write-Host "请选择模型服务商："
        for ($i = 0; $i -lt $providers.Count; $i++) {
            Write-Host "  $($i + 1)) $($providers[$i].Name)（$($providers[$i].DefaultModel)）"
        }
        $providerChoice = Read-Host "请输入序号（直接回车默认 1：DeepSeek）"
        if ([string]::IsNullOrWhiteSpace($providerChoice)) {
            $providerIndex = 0
        } else {
            $providerIndex = [int]$providerChoice.Trim() - 1
            if ($providerIndex -lt 0 -or $providerIndex -ge $providers.Count) {
                throw "无效的服务商序号：$providerChoice"
            }
        }
    } else {
        $providerIndex = [int]$Provider - 1
        if ($providerIndex -lt 0 -or $providerIndex -ge $providers.Count) {
            throw "无效的 Provider 参数：$Provider"
        }
    }

    $selectedProvider = $providers[$providerIndex]
    $baseUrl = [string]$selectedProvider.BaseUrl
    # 服务商预设的默认模型，作为后续确认步骤的预填值
    $defaultModel = [string]$selectedProvider.DefaultModel

    # ---------- 3. 计费方式选择（仅当该服务商有独立订阅端点时显示） ----------
    $planBaseUrl = [string]$selectedProvider.PlanBaseUrl
    if (-not [string]::IsNullOrWhiteSpace($planBaseUrl) -and
        [string]::IsNullOrWhiteSpace($BaseUrl) -and
        [string]::IsNullOrWhiteSpace($Model)) {
        Write-Host ""
        Write-Host "服务商：$($selectedProvider.Name)"
        Write-Host "  1) 官方按量 API：$baseUrl"
        Write-Host "  2) Token/Coding Plan 订阅：$planBaseUrl"
        $planChoice = Read-Host "请选择计费方式（直接回车默认 1：官方按量 API）"
        if ($planChoice.Trim() -eq "2") {
            $baseUrl = $planBaseUrl
            $planModel = [string]$selectedProvider.PlanModel
            if (-not [string]::IsNullOrWhiteSpace($planModel)) {
                $defaultModel = $planModel
            }
            Write-Host "已选择订阅端点，默认模型：$defaultModel"
        }
    }

    # ---------- 4. 确认 API 地址（预设自动填入，回车默认，可手动修改） ----------
    Write-Host ""
    Write-Host "服务商：$($selectedProvider.Name)"
    if (-not [string]::IsNullOrWhiteSpace($BaseUrl)) {
        $baseUrl = $BaseUrl.Trim()
    } elseif ([string]::IsNullOrWhiteSpace($baseUrl)) {
        $baseUrlInput = Read-Host "请输入 API 地址（如 https://api.example.com/v1）"
        if ([string]::IsNullOrWhiteSpace($baseUrlInput)) {
            throw "API 地址不能为空"
        }
        $baseUrl = $baseUrlInput.Trim()
    } else {
        $baseUrlInput = Read-Host "API 地址（直接回车使用预设：$baseUrl）"
        if (-not [string]::IsNullOrWhiteSpace($baseUrlInput)) {
            $baseUrl = $baseUrlInput.Trim()
        }
    }

    # ---------- 5. 输入 API Key ----------
    Write-Host ""
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        $apiKeyInput = Read-Host "请输入 API Key（直接回车：保留现有 Key）"
        $apiKeyInput = $apiKeyInput.Trim()
    } else {
        $apiKeyInput = $ApiKey.Trim()
    }

    # ---------- 6. 确认模型名（预填服务商默认模型，回车直接用，可手改） ----------
    Write-Host ""
    if ([string]::IsNullOrWhiteSpace($Model)) {
        $modelInput = Read-Host "模型名（直接回车使用默认：$defaultModel）"
        if ([string]::IsNullOrWhiteSpace($modelInput)) {
            if ([string]::IsNullOrWhiteSpace($defaultModel)) {
                throw "模型名不能为空，请重新运行并手动输入模型名"
            }
            $Model = $defaultModel
        } else {
            $Model = $modelInput.Trim()
        }
    } else {
        $Model = $Model.Trim()
    }

    # ---------- 写回配置：DPAPI 加密 API Key ----------
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
    }

    $config.base_url = $baseUrl
    $config.model = $Model

    # 无 BOM UTF-8 写回，兼容游戏内置 Python 2.7
    $jsonText = $config | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        $targetConfig,
        $jsonText,
        (New-Object System.Text.UTF8Encoding($false))
    )

    Write-Host ""
    Write-Host "配置完成：$targetConfig"
    Write-Host "服务商：$($selectedProvider.Name)（$baseUrl）"
    Write-Host "模型：$Model"
    Write-Host "游戏内快捷键：" -NoNewline
    Write-Host "F9 开关翻译，F10 查看状态。" -ForegroundColor Red
} catch {
    # 任何错误立即停止并提示，不继续写配置。
    Write-Host ""
    Write-Host "配置失败，已停止：" -ForegroundColor Yellow
    Write-Host "$_" -ForegroundColor Red
    # 停留 3 秒让用户看清错误信息再关闭窗口。
    Start-Sleep -Seconds 3
    exit 1
}

# 运行成功也停留 3 秒，避免窗口一闪而过。
Start-Sleep -Seconds 3
