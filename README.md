# Ren'Py 实时翻译器

适用于 Windows 版 Ren'Py 游戏。当前目标游戏已确认使用 Ren'Py 7.8.4。

## 工作方式

- 使用 Ren'Py 的对话/菜单早期过滤器精确匹配带标签和变量的完整原文，
  普通 UI 文本继续通过 `config.replace_text` 处理。
- 后台批量调用 OpenAI 兼容的 `/chat/completions` 接口，不阻塞游戏。
- 优先读取由本项目人工维护的分卷译文，未命中时再使用运行时 API。
- 首次出现时先显示英文，译文返回后自动刷新；之后从本地缓存即时显示中文。
- 默认使用随模组内置的鸿蒙字体 HarmonyOS Sans SC，避免原游戏字体缺少中文字形。

图片中烘焙进去的英文不属于 Ren'Py 文本，不能通过此模组翻译，需要额外 OCR 或替换图片。

## 安装

在 PowerShell 中运行：

```powershell
.\Install.ps1
```

安装脚本会交互式询问游戏目录、显示字体，并可填写 API Key。默认安装到：

```text
D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season
```

若游戏路径不同，可跳过交互直接指定：

```powershell
.\Install.ps1 -GamePath "D:\Games\YourRenPyGame"
```

安装脚本自动完成：

1. 备份游戏目录里已有的 `zz_live_translator.rpy`。
2. 复制最新模组脚本。
3. 合并 `translations/*.jsonl` 到 `game/live_translator/pretranslated.jsonl`。
4. 写入字体配置；`config.json` 中已有配置（base_url/model/缓存）不会被覆盖。

## API Key 配置（DPAPI 加密）

**API Key 不会明文保存在配置文件中。** 安装脚本（`Install.ps1`）或独立配置脚本
（`Configure-Api.ps1`）会把 Key 用 Windows DPAPI 加密后写入
`config.json` 的 `api_key_encrypted` 字段（绑定当前 Windows 用户，仅本机可解密），
并清空旧的明文 `api_key` 字段。请勿手动向 `config.json` 填写明文 Key——模组不会读取它。

推荐使用独立配置脚本管理 API Key、服务商与模型：

```powershell
.\Configure-Api.ps1
```

交互流程：选择游戏目录 → 选择模型服务商 → 选择计费方式（官方 API / Token Plan 订阅，
仅支持订阅端点的服务商显示）→ 确认 API 地址（预设自动填入，可改）→ 填写 API Key（加密保存）
→ 确认模型名（预填该服务商性价比默认模型，可改）。

内置 10 家服务商预设（按序，默认模型为各家最新款性价比模型）：

| 服务商 | 默认模型 | 订阅端点 |
| --- | --- | --- |
| DeepSeek | deepseek-v4-flash | — |
| OpenAI | gpt-5.6-luna | — |
| 小米 MiMo | mimo-v2.5 | 有 |
| MiniMax | minimax-m3 | 有 |
| 腾讯混元 | hy3 | — |
| Google Gemini | gemini-3.6-flash | — |
| 阿里通义千问 | qwen-flash | 有 |
| 智谱 GLM | glm-4.7-flash | 有 |
| Kimi（月之暗面） | kimi-k2.6 | 有 |
| 字节豆包 | doubao-seed-2.0-lite | 有 |

另有「自定义」选项，可手动输入 API 地址与模型名（如使用中转站或自建服务）。

## 使用

- `F9`：临时开启或关闭翻译。
- `F10`：显示配置、缓存或最近一次请求错误。
- 项目分卷译文：`translations\interface.jsonl`、`translations\day01.jsonl` 等。
- 游戏合并译文：`game\live_translator\pretranslated.jsonl`。
- 运行时缓存：`game\live_translator\cache.jsonl`。

加载顺序是先读预翻译、再读运行时缓存，因此后者可以覆盖同一条预翻译，
便于人工修正。删除运行时缓存后，已存在于预翻译文件中的文本仍会直接显示中文。

`translations` 中的文件只包含原文和中文译文，不包含 API Key。执行安装脚本时
会自动校验重复项并合并到游戏目录；翻译清单和提取出的剧情源码只保存在已忽略的
`work` 目录中。

模组对部分动态文本（如存档位编号、星期、图片缺失报错）做了本地确定性翻译，
命中时不会调用 API。

## 更新

更新脚本会检测 GitHub 仓库 `zhiqiangme/renpy-live-translator` 的发行版，
有新版本时下载并覆盖项目文件（模组代码 + 译文），本地私有文件
（`config.json`、`cache.jsonl`、`work`、`backups`、`translations_bak`）不受影响。

双击运行：

```powershell
.\Update.ps1
```

或者只检测不更新（供脚本复用）：

```powershell
.\Update.ps1 -CheckOnly
```

`Install.ps1` 在安装完成后也会自动检查一次更新：无异常且无更新时直接结束；
检测到新版本时按回车立即更新，输入「不更新」跳过。网络不可用或仓库暂未发布时
静默跳过，不影响安装。

版本号取自 GitHub 发行版的标签（如 `v1.0.0`），本地记录在根目录
`version.txt`。更新完成后**需要重新运行一次安装脚本**，才会把新译文
合并进游戏目录的 `pretranslated.jsonl`。

## 公开译文范围

公开仓库保留全部 JSONL 分卷，其余大部分人工译文仍可直接使用。

遇到公开译文中缺失的句子时：

- 正确填写大模型 API 后，会在后台翻译并写入本地运行时缓存。
- 未填写有效 API（或 DPAPI 解密失败）时，不会发送网络请求，游戏直接显示英文原文。

## 卸载

仅移除模组，保留配置和缓存：

```powershell
.\Uninstall.ps1
```

连同配置和缓存一起移除：

```powershell
.\Uninstall.ps1 -RemoveData
```
