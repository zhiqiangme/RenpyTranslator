# Camp Buddy Scoutmaster Season 汉化方法与进度

更新日期：2026-08-02

## 当前结论

- 待翻译清单总数：15,770 条。
- 已完成人工翻译：14,844 条，约 94.1%。
- 剩余：926 条。
- 当前准确断点：吾郎个人场景 `goro_1.rpy` 尚未开始，共 78 条，下次从第 1 条继续。
- 所有译文保存在项目的 `translations` 目录，并按 Day、路线和分支拆分。
- 这一阶段的离线人工翻译没有调用 DeepSeek 或其他翻译 API。
- 最后一笔翻译提交：`translations：完成吾郎路线dayg10 GEPE分支`。

## 实现方式

### 1. 游戏内直接替换文本

这不是悬浮窗翻译。模组安装到游戏的 `game` 目录后，直接替换 Ren'Py
界面中的对话、菜单和普通 UI 文本：

- 模组源码：`game/zz_live_translator.rpy`
- 使用 Ren'Py 的早期文本过滤器精确处理对话和菜单。
- 普通 UI 文本通过 `config.replace_text` 处理。
- 本地已有译文时立即显示中文。
- 本地没有译文且启用了 API 时，后台请求 OpenAI 兼容接口，返回后刷新界面并写入缓存。
- 图片或视频中已经烘焙进去的英文无法用该方式翻译。

目标游戏已确认使用 Ren'Py 7.8.4。模组中的 Python 标准库均使用专属私有别名，
避免被游戏全局变量覆盖。之前出现过 `unicode object has no attribute time`，原因就是
游戏变量覆盖了名为 `time` 的模块引用，目前已经修复。

### 2. 提取游戏文本

原游戏剧情主要位于：

```text
D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season\game\scripts.rpa
```

处理过程：

1. 使用 `tools/extract_rpa.py` 从 RPA-3.0 封包提取 `.rpy` 剧情源码。
2. 使用 `tools/build_translation_queue.py` 扫描对话、旁白、菜单和 UI 文本。
3. 去掉空文本、资源文件名、网址和重复原文。
4. 生成 `work/translation_queue.jsonl`，每条记录保留原文、来源文件、行号、说话人、
   标签和最多三条上下文。

`work` 目录只用于提取结果和翻译队列，已被 Git 忽略。

### 3. 人工翻译与分卷

每条译文使用一行 JSONL：

```json
{"source":"English source text","translation":"中文译文"}
```

处理原则：

- `source` 必须与翻译队列中的原文逐字符一致。
- `{i}`、`{/i}`、`{fast}`、`{p=1.0}` 和方括号变量等 Ren'Py 标记必须原样保留。
- 原文中的异常字符，例如 `＊`、`＃`、`每`、`〞`，也不能擅自修正，否则游戏无法命中。
- 人名、营地名和语气在各 Day 之间保持一致。
- 人名、姓氏和昵称一律保留原文英文；中文仅保留“先生”“女士”等称谓。
- 成人内容按原意翻译，不使用会改变剧情含义的模糊表达。

分卷命名示例：

- `day01.jsonl`：公共剧情 Day 1。
- `day06_aiden.jsonl`：公共 Day 6 的艾登分支。
- `daya08.jsonl`：艾登路线 Day 8。
- `daya09_be.jsonl`：艾登路线 Day 9 的 BE 分支。
- `daya09_we.jsonl`：艾登路线 Day 9 的 WE 分支。
- 后续 `dayg*.jsonl`：吾郎路线对应 Day。

### 4. 每批校验

每批翻译后执行以下检查：

1. 每一行都能解析为 JSON。
2. `source` 必须存在于 `work/translation_queue.jsonl`。
3. 所有分卷之间不允许出现重复 `source`。
4. 原文和译文中的 Ren'Py 标签及方括号变量必须完全一致。
5. 执行 `git diff --check`，检查空白和格式问题。
6. 校验通过后单独提交 Git，不推送。

`Build-Pretranslated.ps1` 在合并时还会再次检查 JSON 格式、空字段和重复原文，
最终以无 BOM 的 UTF-8 写出，兼容游戏内置的 Python 2.7。

### 5. 合并并安装到游戏

项目内的分卷是权威译文来源。运行：

```powershell
.\Install.ps1
```

安装脚本会：

1. 备份游戏目录里已有的 `zz_live_translator.rpy`。
2. 复制最新模组脚本。
3. 合并 `translations/*.jsonl`。
4. 写入游戏目录的 `game/live_translator/pretranslated.jsonl`。
5. 保留现有 `config.json`、API Key 和 `cache.jsonl`，不会覆盖它们。

游戏加载顺序是先读取 `pretranslated.jsonl`，再读取运行时 `cache.jsonl`；因此缓存中
同一条原文的译文可以覆盖预翻译结果。

## 已完成分卷

| 文件 | 条数 | 状态 |
| --- | ---: | --- |
| `interface.jsonl` | 82 | 完成 |
| `day01.jsonl` | 377 | 完成 |
| `day02.jsonl` | 390 | 完成 |
| `day03.jsonl` | 536 | 完成 |
| `day04.jsonl` | 456 | 完成 |
| `day05.jsonl` | 584 | 完成 |
| `day06.jsonl` | 347 | 完成 |
| `day06_aiden.jsonl` | 111 | 完成 |
| `day06_goro.jsonl` | 60 | 完成 |
| `daya01.jsonl` | 212 | 完成 |
| `daya02.jsonl` | 297 | 完成 |
| `daya03.jsonl` | 866 | 完成 |
| `daya04.jsonl` | 1,171 | 完成 |
| `daya05.jsonl` | 476 | 完成 |
| `daya06.jsonl` | 447 | 完成 |
| `daya07.jsonl` | 926 | 完成 |
| `daya08.jsonl` | 641 | 完成 |
| `daya09.jsonl` | 386 | 主文件完成 |
| `daya09_we.jsonl` | 6 | WE 分支完成 |
| `daya09_be.jsonl` | 73 | 完成 |
| `daya09_gepe.jsonl` | 82 | GEPE 分支完成 |
| `daya10_we.jsonl` | 25 | WE 分支完成 |
| `daya10_be.jsonl` | 231 | BE 分支完成 |
| `daya10_gepe.jsonl` | 558 | GEPE 分支完成 |
| `aiden_1.jsonl` | 70 | 个人场景完成 |
| `aiden_2.jsonl` | 53 | 个人场景完成 |
| `aiden_3b.jsonl` | 55 | 个人场景完成 |
| `aiden_3t.jsonl` | 32 | 个人场景完成 |
| `aiden_4b.jsonl` | 46 | 个人场景完成 |
| `aiden_4t.jsonl` | 36 | 个人场景完成 |
| `aiden_5b.jsonl` | 55 | 个人场景完成 |
| `aiden_5t.jsonl` | 42 | 个人场景完成 |
| `aiden_6b.jsonl` | 65 | 个人场景完成 |
| `aiden_6t.jsonl` | 53 | 个人场景完成 |
| `aiden_7b.jsonl` | 49 | 个人场景完成 |
| `aiden_7t.jsonl` | 45 | 个人场景完成 |
| `aiden_8.jsonl` | 46 | 个人场景完成 |
| `aiden_crack.jsonl` | 98 | 多人分支完成 |
| `aiden_ma1.jsonl` | 57 | 个人场景完成 |
| `aiden_ma2.jsonl` | 42 | 个人场景完成 |
| `aiden_ma4.jsonl` | 65 | 个人场景完成 |
| `dayg1.jsonl` | 110 | 吾郎路线完成 |
| `dayg2.jsonl` | 218 | 吾郎路线完成 |
| `dayg3.jsonl` | 599 | 吾郎路线完成 |
| `dayg4.jsonl` | 736 | 吾郎路线完成 |
| `dayg5.jsonl` | 292 | 吾郎路线完成 |
| `dayg6.jsonl` | 182 | 吾郎路线完成 |
| `dayg7.jsonl` | 350 | 吾郎路线完成 |
| `dayg8.jsonl` | 810 | 吾郎路线完成 |
| `dayg9.jsonl` | 413 | 吾郎路线完成 |
| `dayg9_we.jsonl` | 23 | WE 分支完成 |
| `dayg9_be.jsonl` | 51 | BE 分支完成 |
| `dayg9_gepe.jsonl` | 31 | GEPE 分支完成 |
| `dayg10_we.jsonl` | 54 | WE 分支完成 |
| `dayg10_be.jsonl` | 193 | BE 分支完成 |
| `dayg10_gepe.jsonl` | 533 | GEPE 分支完成 |
| **合计** | **14,844** | **剩余 926 条** |

## 后续翻译顺序

建议继续保持按 Day 和路线推进：

1. 从吾郎个人场景 `goro_1.rpy` 共 78 条开始。
2. 继续处理剩余 `goro_*.rpy` 个人场景，共 830 条。
3. 最后处理 `yoshi_1.rpy`、零散 UI 和其他剩余文件，共 18 条。

剩余量最大的文件包括：

| 原剧情文件 | 剩余条数 |
| --- | ---: |
| `goro_crack.rpy` | 92 |
| `goro_6d.rpy` | 84 |
| `goro_1.rpy` | 78 |
| `goro_6s.rpy` | 69 |
| `goro_mg2.rpy` | 68 |

完整待翻译记录仍以 `work/translation_queue.jsonl` 为准。

## 当前游戏目录状态

截至 2026-08-02 的实测结果：

- 项目分卷：14,844 条。
- 游戏内 `pretranslated.jsonl`：8,551 条，修改时间为 2026-08-02 17:21:02，
  SHA-256 为 `B9B2891B0D9DF44D21EFFF4AC18465E661DDBE32C5A00476577AC9B76E4858FB`。
- 游戏内文件仍是上次安装的 8,551 条版本；项目中新增的 6,293 条目前只在项目中。
- 游戏内运行时缓存：18 条、2,743 字节。
- 项目备份：`backups/cache.jsonl`。
- 游戏缓存与项目备份 SHA-256 均为
  `CC3AF0FBCAD881BA63D60751ABC8B602BD6A2A6249E5024282F0EEA913BD84CD`。

后续新增译文后，重新运行一次 `Install.ps1` 即可更新游戏内文件；
该操作会保留已有 API 配置和运行时缓存。

## 注意事项

- 不要把空白或误生成的松散 `.rpy`/`.rpyc` 文件放进游戏 `game` 目录，它们可能覆盖
  `scripts.rpa` 中的正式剧情并导致 `could not find label 'start'`。
- `config.example.json` 是示例配置；真实 API Key 位于游戏目录的
  `game/live_translator/config.json`，不要提交或分享。
- 分卷译文不会上传给 AI。只有启用运行时 API 且遇到本地未命中的文本时，模组才会
  按配置中的提示词发送待翻译文本。
- 继续工作前先确认 Git 工作树干净，并在每批完成后提交，便于回滚和检查。
