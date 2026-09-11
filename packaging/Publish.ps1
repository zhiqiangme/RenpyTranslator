param([string]$OutputDirectory = "", [string]$Version = "1.0.0", [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ($env:GITHUB_REF_NAME -like 'desktop-v*') { $Version = $env:GITHUB_REF_NAME.Substring(9) }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本必须为 major.minor.patch' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "dist" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
# 使用独立暂存目录，避免旧产物混入发行包。
$stage = Join-Path $output ("stage-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
dotnet publish (Join-Path $repo "desktop/Translator/Translator.csproj") -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $stage
if ($LASTEXITCODE -ne 0) { throw "管理器构建失败" }
$helper = Join-Path $output ("helper-" + [guid]::NewGuid().ToString("N"))
dotnet publish (Join-Path $repo "desktop/Updater/Updater.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $helper
if ($LASTEXITCODE -ne 0) { throw "更新器构建失败" }
Copy-Item -LiteralPath (Join-Path $helper "RenpyTranslator.Updater.exe") -Destination $stage
if (-not $SkipTests) {
    $test = Start-Process -FilePath (Join-Path $stage "RenpyTranslator.exe") -ArgumentList "--self-test" -PassThru -Wait -WindowStyle Hidden
    if ($test.ExitCode -ne 0) { throw "自检失败，请查看 LocalAppData/RenpyTranslator/self-test.log" }
    $updaterTest = Start-Process -FilePath (Join-Path $stage "RenpyTranslator.Updater.exe") -ArgumentList "--self-test" -PassThru -Wait -WindowStyle Hidden
    if ($updaterTest.ExitCode -ne 0) { throw "更新器自检失败，请查看 LocalAppData/RenpyTranslator/updater-test.log" }
}
$zip = Join-Path $output "RenpyTranslator-win-x64.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  RenpyTranslator-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
Write-Output "EXE: $stage\RenpyTranslator.exe"
Write-Output "Release: $zip"
