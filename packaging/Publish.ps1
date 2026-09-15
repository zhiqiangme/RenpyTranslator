param([string]$OutputDirectory = "", [string]$Version = "", [switch]$SkipTests, [switch]$SkipInstaller, [switch]$KeepStage)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
# 版本号唯一来源：packaging/version.txt。手动打包默认取它，CI 打 v* 标签（如 v26.9.11）时由标签覆盖。
# 该值经 -p:Version 注入程序集，因此管理器版本与内置资源版本必然一致，无需人工对齐。
$versionFile = Join-Path $PSScriptRoot "version.txt"
if (-not $Version) {
    if (-not (Test-Path -LiteralPath $versionFile)) { throw "缺少版本文件：$versionFile" }
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}
if ($env:GITHUB_REF_NAME -like 'v*') { $Version = $env:GITHUB_REF_NAME.Substring(1) }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本必须为 major.minor.patch（当前：$Version）" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "dist" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
# 使用独立暂存目录，避免旧产物混入发行包。目录统一在收尾的 finally 中回收。
$stage = Join-Path $output ("stage-" + [guid]::NewGuid().ToString("N"))
$helper = Join-Path $output ("helper-" + [guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    dotnet publish (Join-Path $repo "desktop/Translator/Translator.csproj") -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $stage
    if ($LASTEXITCODE -ne 0) { throw "管理器构建失败" }
    # 参数或标签覆盖版本时，仅同步发行暂存资源，不改仓库内的默认版本文件。
    [IO.File]::WriteAllText((Join-Path $stage "Resources/version.txt"), "$Version`n", [Text.UTF8Encoding]::new($false))
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
    Write-Output "Release: $zip"
    # 安装包：从同一份暂存目录编译单文件安装程序。ZIP 仍是应用内更新器的下载源，
    # 安装包只是首次安装的便捷入口，两者随同一次构建产出，保证内容一致。
    if (-not $SkipInstaller) {
        # ISCC 定位：优先 ISCC 环境变量（CI 装完 Inno Setup 后会写入），其次 PATH，最后常见安装目录。
        # 本机装在 Program Files，choco/CI 常装在 Program Files (x86)，两处都要覆盖。
        $iscc = $env:ISCC
        if (-not $iscc) { $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source }
        if (-not $iscc) {
            $iscc = @("C:\Program Files\Inno Setup 6\ISCC.exe", "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") |
                Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
        if (-not $iscc -or -not (Test-Path -LiteralPath $iscc)) { throw "未找到 Inno Setup ISCC.exe（可设 ISCC 环境变量指定路径，或加 -SkipInstaller 跳过安装包）" }
        Write-Output "ISCC: $iscc"
        & $iscc "/DVersion=$Version" "/DSourceDir=$stage" "/DOutputDir=$output" (Join-Path $PSScriptRoot "installer.iss") | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "安装包编译失败" }
        $setup = Join-Path $output "RenpyTranslator-Setup-win-x64.exe"
        $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$setup.sha256", "$setupHash  RenpyTranslator-Setup-win-x64.exe`n", [Text.UTF8Encoding]::new($false))
        Write-Output "Installer: $setup"
    }
}
finally {
    # 暂存目录只在本次运行内有意义：单次约 226MB(stage) + 71MB(helper)，
    # 不回收会逐次堆积（曾累积到 1.55GB）。这里直接删除而不过回收站 —— 内容是可由本脚本再生的
    # 构建产物，走回收站只是把堆积位置从 dist 换到回收站。需要留档时加 -KeepStage。
    if (-not $KeepStage) {
        foreach ($dir in @($stage, $helper)) {
            if ($dir -and (Test-Path -LiteralPath $dir)) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
if ($KeepStage) { Write-Output "EXE: $stage\RenpyTranslator.exe" }
else { Write-Output "暂存目录已回收（需要保留时加 -KeepStage）" }
