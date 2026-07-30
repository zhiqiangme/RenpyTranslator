# Ren'Py 实时翻译器

适用于 Windows 版 Ren'Py 游戏。当前目标游戏已确认使用 Ren'Py 7.8.4。

## 工作方式

- 使用 Ren'Py 官方 `config.replace_text` 钩子捕获所有原生文本，包括对话、选项和 UI。
- 后台批量调用 OpenAI 兼容的 `/chat/completions` 接口，不阻塞游戏。
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
- 缓存文件：`game\live_translator\cache.jsonl`。

如需修改某条译文，可退出游戏后编辑或删除对应缓存行。删除整个缓存文件会重新翻译并重新产生费用。

## 卸载

仅移除模组，保留配置和缓存：

```powershell
.\Uninstall.ps1
```

连同配置和缓存一起移除：

```powershell
.\Uninstall.ps1 -RemoveData
```
