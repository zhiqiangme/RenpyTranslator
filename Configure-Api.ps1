# 配置脚本：设置 API Key（DPAPI 加密）与模型服务商
# 用法：在 PowerShell 中运行 .\Configure-Api.ps1
# 交互流程：选择模型服务商（默认 DeepSeek）→ 确认 API 地址（预设自动填入）→ 填写 API Key
# 可选参数：提供 -GamePath/-Provider/-ApiKey/-BaseUrl 时跳过对应交互（便于自动化）
param(
    [string]$GamePath = "",
    [string]$Provider = "",
    [string]$ApiKey = "",
    [string]$BaseUrl = ""
)

$ErrorActionPreference = "Stop"

$defaultGamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season"

# 内置服务商预设：名称 / base_url / 默认模型
$providers = @(
    @{ Name = "DeepSeek"; BaseUrl = "https://api.deepseek.com"; DefaultModel = "deepseek-v4-flash" },
    @{ Name = "OpenAI"; BaseUrl = "https://api.openai.com/v1"; DefaultModel = "gpt-4o-mini" },
    @{ Name = "OpenRouter"; BaseUrl = "https://openrouter.ai/api/v1"; DefaultModel = "deepseek/deepseek-chat" },
    @{ Name = "硅基流动 SiliconFlow"; BaseUrl = "https://api.siliconflow.cn/v1"; DefaultModel = "deepseek-ai/DeepSeek-V3" },
    @{ Name = "自定义（手动输入）"; BaseUrl = ""; DefaultModel = "" }
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
    $Model = [string]$selectedProvider.DefaultModel

    # ---------- 3. 确认 API 地址（预设自动填入，回车默认，可手动修改） ----------
    Write-Host ""
    Write-Host "服务商：$($selectedProvider.Name)    模型：$Model"
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

    # ---------- 4. 输入 API Key ----------
    Write-Host ""
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        $apiKeyInput = Read-Host "请输入 API Key（直接回车：保留现有 Key）"
        $apiKeyInput = $apiKeyInput.Trim()
    } else {
        $apiKeyInput = $ApiKey.Trim()
    }

    # ---------- 写回配置：DPAPI 加密 API Key ----------
    # ProtectedData 类型在 Windows PowerShell 5.1 默认可用，无需 Add-Type。
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
