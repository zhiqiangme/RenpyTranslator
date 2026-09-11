; Ren'Py 汉化管理器单文件安装包脚本。
; 由 packaging/Publish.ps1 调用 ISCC 编译；Version / SourceDir / OutputDir 均以 /D 开关注入，
; 版本号唯一来源保持为 packaging/version.txt。
; 注意：本文件必须保存为 UTF-8（含 BOM）。官方 Inno Setup 不附带简体中文语言文件，
; 本机与 CI 的默认语言也不一致，故在此用 [Messages] 覆盖向导主要文案，保证各处构建产物一致。

#define AppName "Ren'Py 汉化管理器"

[Setup]
; AppId 保持固定，升级安装与卸载信息都挂在同一个注册表项下。
AppId={{8F7A6B5C-4D3E-4F2A-9B1C-2D3E4F5A6B7C}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
VersionInfoVersion={#Version}
AppPublisher=zhiqiangme
AppUpdatesURL=https://github.com/zhiqiangme/RenpyTranslator
; 应用内更新器以普通权限直接替换安装目录下的文件（见 desktop/Translator/Updates.cs），
; 因此必须安装到用户可写目录（等同 VS Code 用户版安装模式），全程无需管理员权限。
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\RenpyTranslator
; 对应管理器单实例互斥体 Local\RenpyTranslator.Desktop：安装/卸载前要求先退出程序。
AppMutex=RenpyTranslator.Desktop
SetupIconFile=..\desktop\Translator\Assets\app.ico
OutputDir={#OutputDir}
OutputBaseFilename=RenpyTranslator-Setup-win-x64
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\RenpyTranslator.exe
; 发行包为 win-x64，仅允许 64 位 Windows 安装。
ArchitecturesAllowed=x64
WizardStyle=modern
DisableProgramGroupPage=yes
; 两个自包含 .NET 单文件包含大量重复的运行时字节，solid + lzma2/max 去重后体积收益明显。
Compression=lzma2/max
SolidCompression=yes

[Messages]
; 文案取自 Inno Setup 官方简体中文翻译，仅覆盖用户会看到的页面。
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=这将在你的计算机上安装 [name/ver]。%n%n建议你先关闭所有其他应用程序，然后再继续。
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
WizardSelectDir=选择目标位置
SelectDirDesc=[name] 应该安装在哪里？
SelectDirLabel3=安装程序会将 [name] 安装到以下文件夹中。
SelectDirBrowseLabel=若要继续，请单击“下一步”。如果要选择其他文件夹，请单击“浏览”。
DiskSpaceMBLabel=至少需要 [mb] MB 的可用磁盘空间。
WizardSelectTasks=选择“其他任务”
SelectTasksDesc=应执行哪些附加任务？
SelectTasksLabel2=选择你希望安装程序在安装 [name] 时执行的其他任务，然后单击下一步。
WizardReady=准备安装
ReadyLabel1=安装程序现在已准备好开始在计算机上安装 [name]。
ReadyLabel2a=单击“安装”继续安装，如果要查看或更改任何设置，请单击“返回”。
ReadyLabel2b=单击“安装”继续安装。
WizardInstalling=安装
InstallingLabel=安装程序在计算机上安装 [name] 时，请稍候。
FinishedHeadingLabel=结束 [name] 安装向导
FinishedLabelNoIcons=安装程序在计算机上已完成 [name] 的安装。
ExitSetupMessage=安装未完成。如果现在退出，则不会安装该程序。%n%n你可以在其他时间再次运行安装程序以完成安装。%n%n退出安装程序？
ConfirmUninstall=是否确实要完全删除 %1 及其所有组件？
UninstallStatusLabel=请稍候，直到从计算机中删除 %1。

[Tasks]
; 桌面快捷方式默认勾选，其余快捷方式（开始菜单）始终创建。
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加快捷方式:"

[Files]
; 打包 Publish.ps1 暂存目录的全部发行文件：主程序、独立更新器与内置资源。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\RenpyTranslator.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\RenpyTranslator.exe"; Tasks: desktopicon

[Run]
; “完成”页的运行复选框；静默安装（/VERYSILENT）时自动跳过。
Filename: "{app}\RenpyTranslator.exe"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent
