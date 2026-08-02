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

## 公开译文范围

公开仓库会保留全部 JSONL 分卷，但成人敏感内容会按单条
`{"source":"...","translation":"..."}` 记录移除，不会把整个分卷文件删除。
其余大部分人工译文仍可直接使用。

遇到公开译文中缺失的句子时：

- 正确填写大模型 API 后，会在后台翻译并写入本地运行时缓存。
- 未填写有效 API（包括仍保留示例 Key）时，不会发送网络请求，游戏直接显示英文原文。

本地私用的 `translations_bak` 译文已被 Git 忽略，不应提交或上传。需要安装
这套完整私用译文时，运行 `translations_bak\Install.ps1`；该脚本只会从
`translations_bak` 合并译文，不会读取公开的 `translations`。

## 卸载

仅移除模组，保留配置和缓存：

```powershell
.\Uninstall.ps1
```

连同配置和缓存一起移除：

```powershell
.\Uninstall.ps1 -RemoveData
```
