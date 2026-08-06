# 更新脚本：检测并下载安装 renpy-live-translator 的最新发行版
# 用法：
#   .\Update.ps1            双击/手动运行：检测 -> 确认 -> 下载 -> 覆盖 -> 更新 version.txt
#   .\Update.ps1 -CheckOnly 只检测，单行输出状态并设置退出码（供安装脚本复用）
#   .\Update.ps1 -Force     跳过确认直接更新（安装脚本在用户确认后调用）
param(
    [switch]$CheckOnly,
    [switch]$Force,
    [string]$Repository = "zhiqiangme/renpy-live-translator",
    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"
# PowerShell 5.1 默认可能不启用 TLS 1.2，GitHub API 要求
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = [System.IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path)
}

$versionFile = Join-Path $ProjectRoot "version.txt"
$tempRoot = Join-Path $env:TEMP "renpy-live-translator-update"

# 覆盖时排除的本地私有内容（对应 .gitignore，避免破坏用户本地数据与 git 仓库）
$excludeTopDirs = @(
    ".git", ".codegraph", ".workbuddy", "work", "backups",
    "translations_bak", "translations - 副本", "__pycache__"
)
$excludeFileNames = @("config.json", "cache.jsonl")
$excludeExtensions = @(".pyc", ".rpyc", ".bak")

function Get-RemoteLatestTag {
    # 匿名 GitHub API 优先（发布后公网用户无需 gh）；失败时用 gh 降级（私库期作者可用）
    try {
        $release = Invoke-RestMethod -Uri (
            "https://api.github.com/repos/$Repository/releases/latest"
        ) -TimeoutSec 15
        if ($release -and $release.tag_name) {
            return [string]$release.tag_name
        }
    } catch {
        # 无 release 与无权限均为 404，统一视为"无可用发行版"
    }
    try {
        $ghPath = Get-Command gh -ErrorAction SilentlyContinue
        if ($ghPath) {
            $release = gh api "repos/$Repository/releases/latest" --jq ".tag_name" 2>$null
            # 外部命令失败时可能把 JSON 错误体输出到 stdout，用退出码与格式双重校验
            if (
                $LASTEXITCODE -eq 0 -and
                $release -and
                $release -notmatch '^\s*\{'
            ) {
                return [string]$release
            }
        }
    } catch {
        # 忽略，最终返回 $null
    }
    return $null
}

function Get-LocalVersion {
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        Write-Host "警告：本地缺少 version.txt，无法判断当前版本" -ForegroundColor Yellow
        return $null
    }
    $content = (Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8).Trim()
    if ([string]::IsNullOrWhiteSpace($content)) {
        Write-Host "警告：version.txt 内容为空" -ForegroundColor Yellow
        return $null
    }
    # 去 v 前缀（v1.0.0 -> 1.0.0）
    return $content -replace "^[vV]", ""
}

function ConvertTo-VersionParts {
    param([string]$Version)
    if ([string]::IsNullOrWhiteSpace($Version)) {
        return $null
    }
    # 支持 v1 / v1.2 / v1.2.3 / v1.2.3-pre / v1.2.3+build；pre-release 后缀剥离不参与比较
    if ($Version -notmatch "^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:[-+].+)?$") {
        return $null
    }
    $major = [int]$Matches[1]
    $minor = if ($Matches[2]) { [int]$Matches[2] } else { 0 }
    $patch = if ($Matches[3]) { [int]$Matches[3] } else { 0 }
    return @($major, $minor, $patch)
}

function Compare-SemanticVersion {
    # 返回 1（a>b）/0（相等）/-1（a<b）；任一无法解析返回 $null
    param([string]$Left, [string]$Right)
    $a = ConvertTo-VersionParts $Left
    $b = ConvertTo-VersionParts $Right
    if ($null -eq $a -or $null -eq $b) {
        return $null
    }
    for ($i = 0; $i -lt 3; $i++) {
        if ($a[$i] -gt $b[$i]) { return 1 }
        if ($a[$i] -lt $b[$i]) { return -1 }
    }
    return 0
}

function Test-UpdateAvailable {
    # 返回状态字符串（供调用方输出）：
    #   NO_RELEASE | UNKNOWN_LOCAL <remote> | UP_TO_DATE <local> | UPDATE_AVAILABLE <local> <remote>
    $remote = Get-RemoteLatestTag
    if ([string]::IsNullOrWhiteSpace($remote)) {
        return "NO_RELEASE"
    }
    $remoteVersion = $remote -replace "^[vV]", ""
    $local = Get-LocalVersion
    if ($null -eq $local) {
        return "UNKNOWN_LOCAL $remoteVersion"
    }
    $comparison = Compare-SemanticVersion -Left $local -Right $remoteVersion
    if ($null -eq $comparison) {
        Write-Host "警告：无法解析版本号（本地=$local 远端=$remoteVersion），保守视为无更新" -ForegroundColor Yellow
        return "UP_TO_DATE $local"
    }
    if ($comparison -lt 0) {
        return "UPDATE_AVAILABLE $local $remoteVersion"
    }
    return "UP_TO_DATE $local"
}

function Test-ZipPathSafe {
    # 二次校验：拒绝绝对路径与 ".." 片段（zip-slip 双保险）
    param([string]$RelativePath)
    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        return $false
    }
    $parts = $RelativePath.Split([char[]]@("/", "\"), [System.StringSplitOptions]::RemoveEmptyEntries)
    foreach ($part in $parts) {
        if ($part -eq "..") {
            return $false
        }
    }
    return $true
}

function Should-Exclude {
    # 判断相对路径（release 顶层目录内）是否命中排除清单
    param([string]$RelativePath)
    $parts = $RelativePath.Split([char[]]@("/", "\"), [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -eq 0) {
        return $true
    }
    $first = $parts[0]
    if ($excludeTopDirs -contains $first) {
        return $true
    }
    $name = $parts[$parts.Count - 1]
    if ($excludeFileNames -contains $name) {
        return $true
    }
    $ext = [System.IO.Path]::GetExtension($name)
    if (-not [string]::IsNullOrWhiteSpace($ext) -and $excludeExtensions -contains $ext.ToLowerInvariant()) {
        return $true
    }
    return $false
}

function Invoke-DownloadAndExtract {
    param([string]$Tag)
    $zipUrl = "https://github.com/$Repository/archive/refs/tags/$Tag.zip"
    $tempDir = Join-Path $tempRoot "$Tag-$([guid]::NewGuid().ToString("N"))"
    $zipPath = Join-Path $tempDir "source.zip"
    $extractPath = Join-Path $tempDir "extracted"
    $null = New-Item -ItemType Directory -Path $tempDir -Force
    try {
        Write-Host "正在下载 $Tag ..."
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 120
        if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
            throw "下载失败：未生成文件"
        }
        $zipInfo = Get-Item -LiteralPath $zipPath
        if ($zipInfo.Length -eq 0) {
            throw "下载失败：文件为空（可能返回了错误页）"
        }
        Write-Host "正在解压..."
        # 用 PowerShell 原生 Expand-Archive（PS 5.1 内置，不依赖 .NET 运行时调用）
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractPath -Force
        # 二次校验：解压后所有文件必须位于 extractPath 内，兜底 zip-slip
        $extractFull = [System.IO.Path]::GetFullPath($extractPath)
        foreach ($file in Get-ChildItem -LiteralPath $extractPath -Recurse -File) {
            if (-not $file.FullName.StartsWith($extractFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "发行包包含不安全路径：$($file.FullName)"
            }
        }
        # 定位顶层目录（不硬编码仓库名-标签）
        $topDirs = @(Get-ChildItem -LiteralPath $extractPath -Directory | Select-Object -First 1)
        if ($topDirs.Count -eq 0) {
            throw "发行包中没有顶层目录"
        }
        return @{
            TopDirectory = $topDirs[0].FullName
            TempDir = $tempDir
        }
    } catch {
        if (Test-Path -LiteralPath $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force
        }
        throw
    }
}

function Copy-ReleaseFiles {
    # 备份后覆盖；只增不改删；单文件失败警告继续
    param([string]$TopDirectory)
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupDir = Join-Path $ProjectRoot "backups\update_$timestamp"
    # 用引用类型计数器，跨 ForEach-Object 子作用域共享
    $stats = [System.Collections.ArrayList]@(0, 0)   # [0]=成功数 [1]=失败数
    Get-ChildItem -LiteralPath $TopDirectory -Recurse -File | ForEach-Object {
        $file = $_
        $relativePath = $file.FullName.Substring($TopDirectory.Length).TrimStart("/", "\")
        if (Should-Exclude -RelativePath $relativePath) {
            return
        }
        $targetPath = Join-Path $ProjectRoot $relativePath
        $backupPath = Join-Path $backupDir $relativePath
        $targetDir = Split-Path -Parent $targetPath
        $backupDirForFile = Split-Path -Parent $backupPath
        try {
            if ($targetDir -and -not (Test-Path -LiteralPath $targetDir -PathType Container)) {
                $null = New-Item -ItemType Directory -Path $targetDir -Force
            }
            if ($backupDirForFile -and -not (Test-Path -LiteralPath $backupDirForFile -PathType Container)) {
                $null = New-Item -ItemType Directory -Path $backupDirForFile -Force
            }
            if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                Copy-Item -LiteralPath $targetPath -Destination $backupPath -Force
            }
            Copy-Item -LiteralPath $file.FullName -Destination $targetPath -Force
            $stats[0]++
        } catch {
            Write-Host "警告：无法覆盖 $targetPath ：$_" -ForegroundColor Yellow
            $stats[1]++
        }
    }
    Write-Host "已更新 $($stats[0]) 个文件"
    if ($stats[1] -gt 0) {
        Write-Host "有 $($stats[1]) 个文件更新失败，请检查上述警告" -ForegroundColor Yellow
    }
}

function Write-VersionFile {
    param([string]$Tag)
    $cleanVersion = $Tag -replace "^[vV]", ""
    # 无 BOM UTF-8，内容无 v 前缀
    [System.IO.File]::WriteAllText(
        $versionFile,
        $cleanVersion,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

# ---------- 主流程 ----------
$exitCode = 0
try {
    $state = Test-UpdateAvailable
    Write-Output $state
    $exitCode = if ($state -like "UPDATE_AVAILABLE*" -or $state -like "UNKNOWN_LOCAL*") { 2 } else { 0 }

    if (-not $CheckOnly) {
        if ($state -like "UPDATE_AVAILABLE*" -or $state -like "UNKNOWN_LOCAL*") {
            # 提取远端版本：行格式 "UPDATE_AVAILABLE <local> <remote>" / "UNKNOWN_LOCAL <remote>"
            $parts = $state.Split(" ")
            $remoteVersion = $parts[$parts.Count - 1]
            if (-not $Force) {
                Write-Host ""
                Write-Host "检测到可用更新：$remoteVersion" -ForegroundColor Cyan
                $answer = Read-Host "按回车立即更新；输入其他内容跳过"
                if ($answer.Trim() -ne "") {
                    Write-Host "已跳过更新。"
                    exit $exitCode
                }
            }
            $result = Invoke-DownloadAndExtract -Tag $remoteVersion
            try {
                Copy-ReleaseFiles -TopDirectory $result.TopDirectory
                Write-VersionFile -Tag $remoteVersion
                Write-Host ""
                Write-Host "更新完成，当前版本：$remoteVersion" -ForegroundColor Green
                Write-Host "提示：重新运行 Install.ps1 即可把新译文装进游戏目录。"
            } finally {
                Remove-Item -LiteralPath $result.TempDir -Recurse -Force
            }
        } else {
            if ($state -eq "NO_RELEASE") {
                Write-Host "暂无可用发行版（仓库无 release 或无法访问）。"
            } else {
                Write-Host "模组已是最新版本（$state）。"
            }
        }
    }
} catch {
    Write-Host "更新过程发生错误：$_" -ForegroundColor Red
    $exitCode = 1
}

exit $exitCode
