# Ren'Py 汉化管理器

Windows x64 桌面程序：用图形界面安装、升级和卸载汉化，配置 OpenAI 兼容模型 API，管理译文缓存并更新软件。

## 运行

两种方式，任选其一。发行包包含 .NET 运行环境，使用者无需安装 Python、PowerShell 7 或 .NET SDK。

- **安装包（推荐）**：下载 `RenpyTranslator-Setup-win-x64.exe` 双击安装。安装到 `%LOCALAPPDATA%\Programs\RenpyTranslator`，无需管理员权限，可选开始菜单与桌面快捷方式。卸载不会删除 `%LOCALAPPDATA%\RenpyTranslator` 中的配置、缓存、译文与备份。
- **绿色版**：下载 `RenpyTranslator-win-x64.zip`，完整解压到当前用户可写的目录，双击 `RenpyTranslator.exe`。保留同目录的 `Resources` 和 `RenpyTranslator.Updater.exe`。

两种方式安装的程序相同，应用内“更新”功能均可用（更新器直接替换安装目录文件，因此安装位置必须当前用户可写）。安装后：

1. 在“游戏管理”中浏览游戏根目录，或选择历史目录后点击“读取 / 检查状态”。目录应包含 `game` 和 `renpy` 文件夹。
2. 选择资源：通用模式不导入游戏专属译文；内置专属译文仅适用于 **Camp Buddy Scoutmaster Season**。通用模式不会删除游戏已有的预译文。
3. 根据需要在“模型 API”填写地址、模型与密钥，然后点击“安装 / 升级 / 修复汉化”。只使用预译文时可以不填写密钥。
4. 安装完成后可关闭管理器。游戏中的实时翻译仍由 `game/zz_live_translator.rpy` 执行。F9 开关翻译，F10 查看状态；配置更改后需重新启动游戏。

管理器不会启动游戏。原模组已有的兼容性范围仍适用；尚未实测的 Ren'Py 版本不能保证兼容。图片内的英文不在文本翻译范围内。

## API 与配置

- 支持自定义 OpenAI 兼容 `/chat/completions` 接口，地址可填写到端点或其上一级。
- 服务商预设沿用旧版，模型名、端点与账户可用性需以服务商实际支持为准；订阅端点也可手动填写。
- API Key 留空表示保留，清除密钥需勾选对应选项。密钥通过 Windows DPAPI CurrentUser 加密，与旧 PowerShell 配置兼容，跨用户或跨机器需重新填写。
- “测试连接 / 翻译”发送一条 Hello 请求，可能产生少量费用；不会保存测试译文到游戏缓存。
- 高级设置支持批量大小、等待、超时、冷却、输出 token、温度、提示词、人名和跳过规则。恢复默认值仅修改编辑区，点击保存后才写入游戏。
- 每个游戏独立保存配置。未知配置字段会保留。地址要求 HTTPS，本机回环服务可用 HTTP。

## 安装、卸载与数据

安装前校验译文格式及重复原文，写入前备份被覆盖文件，失败后恢复原始字节。安装目录中的配置、运行时缓存和用户其他文件不会被软件自更新覆盖。

默认卸载只删除模组 `.rpy`、`.rpyc` 和桌面安装记录，保留配置、缓存、译文及字体。勾选“同时清理”后会删除 `game/live_translator` 内的数据文件，操作前仍会备份。原游戏文件、存档和其他目录不属于清理范围。

管理器数据位于 `%LOCALAPPDATA%/RenpyTranslator`：

| 位置 | 内容 |
| --- | --- |
| `games.json` | 已添加游戏目录 |
| `backups/<操作编号>/` | 修改前文件，`target.txt` 标明游戏根目录 |
| `updates/<操作编号>/previous/` | 软件更新前版本备份 |
| `tests/<编号>/` | 自检生成的模拟游戏目录，保留最近 4 份 |
| `self-test.log`、`updater-test.log` | 自动验证结果 |

“数据与日志”页面可导出缓存、备份并清空缓存、打开备份目录和导出本次操作日志。恢复备份时先关闭游戏，按备份内的相对路径复制回 `target.txt` 对应游戏的 `game` 文件夹。备份保留最近 10 份，自检模拟目录保留最近 4 份，超出部分在下次操作或自检时自动清理。

## 更新

“更新”页面检测 `zhiqiangme/RenpyTranslator` 的最新 GitHub Release。可用桌面版本必须包含：

- `RenpyTranslator-win-x64.zip`
- `RenpyTranslator-win-x64.zip.sha256`
- 版本标签 `desktop-v26.9.11` 或兼容的三段数字版本标签

下载后校验 SHA-256，由独立更新器等待管理器退出、备份旧文件、替换发行文件并重启管理器；替换失败会尝试恢复旧文件。旧发行版的多余内置资源会移除，避免旧译文混入。校验文件用于检测下载损坏，不等同于代码签名。

软件与内置资源随同一个发行包分发，管理器版本和资源版本分别显示。更新后，在游戏管理页面对需要更新的游戏点击“安装 / 升级”，即可应用新模组和译文，无需运行脚本。

## 开发与发布

需要 Windows 和 .NET 10 SDK。开发构建：

```powershell
dotnet build desktop/Translator/Translator.csproj -c Release
```

应用图标由 `packaging/make-icon.py` 从 `desktop/Translator/Assets/app-icon.png` 生成 `Assets/app.ico`（纯标准库，自行解码 PNG，四角裁圆角并按 16~256 七档尺寸独立抗锯齿）。替换素材后重新运行该脚本即可，无需改动工程文件；第三个可选参数为圆角比例（0~0.5，默认 0.2，传 0 恢复直角）。

生成自包含发行包并执行隔离测试：

```powershell
./packaging/Publish.ps1
# 指定版本（省略时取 packaging/version.txt 的值）
./packaging/Publish.ps1 -Version 26.10.1
# 需要保留解包后的 EXE 时
./packaging/Publish.ps1 -KeepStage
# 只出 ZIP、不编译安装包时
./packaging/Publish.ps1 -SkipInstaller
```

输出在 `dist`，包括 ZIP、安装包 `RenpyTranslator-Setup-win-x64.exe` 与各自的 SHA-256 校验文件。安装包由 Inno Setup 6 编译（脚本 `packaging/installer.iss`，本机与 GitHub Actions 的 windows-latest 均已预装），安装到用户目录、无需管理员权限。构建用的暂存目录会在结束时自动回收，加 `-KeepStage` 可保留。测试仅操作 `%LOCALAPPDATA%/RenpyTranslator/tests` 下模拟游戏目录，API 测试使用本机模拟服务，不打开游戏、不调用真实模型。

版本号唯一来源是 `packaging/version.txt`：发布脚本默认读它，并通过 `-p:Version` 注入程序集，因此管理器版本与内置资源版本必然一致，不需要人工对齐。推送 `desktop-v*` 标签时由标签覆盖版本号，自动构建、测试并发布 Release。本地提交不会自动发布，需另行推送标签。

## 旧版归档

旧版 PowerShell 入口及中英文说明已原样移入 `archive/legacy-scripts`，包含归档哈希，不删除。该目录用于保留历史实现；旧脚本仍依赖原根目录布局，不应直接在归档目录运行。

`game`、`translations`、`fonts` 继续作为新版资源使用；`tools` 保留开发用途。完整翻译版本（含旧安装脚本）已归档到 `archive/translations_bak`；`backups` 保存历史快照与归档包；两者均被 `.gitignore` 排除，不进入版本控制。`dist` 只保留发行 ZIP 与解包目录，发布脚本每次运行结束会自动回收自己的暂存目录。

## 许可证

沿用项目 [LICENSE](LICENSE)。本项目是第三方翻译模组，与游戏开发商无关，请支持正版游戏。
