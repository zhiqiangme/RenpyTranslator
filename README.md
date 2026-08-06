# Ren'Py 实时翻译器

适用于 Windows 版 Ren'Py 游戏。当前目标游戏已确认使用 Ren'Py 7.8.4。

## 工作方式

- 使用 Ren'Py 的对话/菜单早期过滤器精确匹配带标签和变量的完整原文，
  普通 UI 文本继续通过 `config.replace_text` 处理。
- 后台批量调用 OpenAI 兼容的 `/chat/completions` 接口，不阻塞游戏。
- 优先读取由本项目人工维护的分卷译文，未命中时再使用运行时 API。
- 首次出现时先显示英文，译文返回后自动刷新；之后从本地缓存即时显示中文。
- 默认使用本机微软雅黑，避免原游戏字体缺少中文字形。

图片中烘焙进去的英文不属于 Ren'Py 文本，不能通过此模组翻译，需要额外 OCR 或替换图片。

## 安装

在 PowerShell 中运行：

```powershell
.\Install.ps1
```

默认安装到：

```text
D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season
```

若游戏路径不同：

```powershell
.\Install.ps1 -GamePath "D:\Games\YourRenPyGame"
```

安装后编辑：

```text
游戏目录\game\live_translator\config.json
```

至少填写：

```json
{
  "base_url": "https://你的服务地址/v1",
  "api_key": "你的 API Key",
  "model": "你的模型名称"
}
```

不要把包含 API Key 的配置文件发给别人。

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

两个安装脚本（`Install.ps1`、`Install-Interactive.ps1`）在安装完成后也会自动
检查一次更新：无异常且无更新时直接结束；检测到新版本时按回车立即更新，
输入「不更新」跳过。网络不可用或仓库暂未发布时静默跳过，不影响安装。

版本号取自 GitHub 发行版的标签（如 `v1.0.0`），本地记录在根目录
`version.txt`。更新完成后**需要重新运行一次安装脚本**，才会把新译文
合并进游戏目录的 `pretranslated.jsonl`。

## 公开译文范围

公开仓库会保留全部 JSONL 分卷，但成人敏感内容会按单条
`{"source":"...","translation":"..."}` 记录移除，不会把整个分卷文件删除。
其余大部分人工译文仍可直接使用。

遇到公开译文中缺失的句子时：

- 正确填写大模型 API 后，会在后台翻译并写入本地运行时缓存。
- 未填写有效 API（包括仍保留示例 Key）时，不会发送网络请求，游戏直接显示英文原文。

## 卸载

仅移除模组，保留配置和缓存：

```powershell
.\Uninstall.ps1
```

连同配置和缓存一起移除：

```powershell
.\Uninstall.ps1 -RemoveData
```
