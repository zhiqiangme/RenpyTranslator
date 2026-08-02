param(
    [string]$GamePath = "D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season"
)

$ErrorActionPreference = "Stop"
$translationsDirectory = $PSScriptRoot
$projectRoot = [System.IO.Path]::GetDirectoryName($translationsDirectory)
$projectInstaller = Join-Path $projectRoot "Install.ps1"

if (-not (Test-Path -LiteralPath $projectInstaller -PathType Leaf)) {
    throw "缺少主安装脚本：$projectInstaller"
}

# 复用主安装逻辑，但强制从本目录合并完整私用译文。
& $projectInstaller `
    -GamePath $GamePath `
    -TranslationsPath $translationsDirectory
