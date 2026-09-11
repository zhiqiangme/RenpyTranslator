# 桌面版验证记录

日期：2026-09-11；Windows x64；.NET SDK 10.0.301；管理器版本 1.0.0。

- 管理器与独立更新器 Release 构建成功，无编译错误或警告。
- 自包含发行包构建后执行 15 项管理器检查及 4 项更新器检查，全部通过。
- 覆盖 DPAPI 加解密、安装和资源哈希、通用模式隔离专属译文、保留缓存、注入故障后的原始字节恢复、完整译文合并、升级保留密钥、卸载保留用户数据、显式清理、重复译文拒绝及 ZIP 路径穿越拒绝。
- 本机模拟 HTTP 服务验证兼容接口请求、成功翻译响应、HTTP 401 及错误译文数量。不使用真实 API Key，不调用外部模型。
- 更新器在独立模拟发行目录验证替换程序、删除过期资源、保留无关文件，以及更新失败后恢复被修改和删除的文件。
- 五个 WPF 页面已渲染检查；API 页面服务商与配置值一致。高级参数页面使用滚动容器。
- 归档的五个脚本和两份旧 README 逐文件 SHA-256 一致。

遵循用户要求：没有启动游戏，也没有修改实际游戏安装。上述游戏目录均为 `%LOCALAPPDATA%/RenpyTranslator/tests` 下的模拟目录。游戏内显示效果、特定 Ren'Py 版本兼容性、真实模型 API 和线上 GitHub Release 更新尚未实测。

复现：运行 `packaging/Publish.ps1`。测试日志为 `%LOCALAPPDATA%/RenpyTranslator/self-test.log` 与 `updater-test.log`；测试目录和备份保留用于排查。

## 界面改版验证（2026-09-11）

范围：桌面管理器界面重做，零新增依赖，未引入任何第三方 UI 库。

改动：

- 新增 `Themes/Tokens.xaml`（颜色 / 圆角 / 字号令牌）、`Themes/Controls.xaml`（按钮、输入框、密码框、复选框、下拉框、滚动条、进度条、TabControl 八类控件的 ControlTemplate，均含常态 / 悬停 / 按下 / 禁用 / 键盘焦点五态）、`Themes/Icons.xaml`（线性矢量图标）。
- `MainWindow.xaml` 的顶部 TabControl 经模板重塑为左侧导航栏，页面由裸堆叠改为卡片分组；「高级设置」页控件由代码动态生成改为 XAML 声明，`MainWindow.xaml.cs` 只保留「配置键 → 控件」映射。
- 新增 `packaging/make-icon.py`（仅标准库）生成 `desktop/Translator/Assets/app.ico`，同一文件用于窗口图标与可执行文件图标。
- 修复缺陷：`--snapshot` 原先既不退出进程也不产出图片；现挂在 `ContentRendered` 上，逐页等待渲染管线完成，并写出 `snapshot.log` 记录每页结果。同时新增 `--snapshot <页索引>` 与 `--game <目录>`，可在载入真实游戏数据的状态下出图。
- 新增全局未处理异常处理：写入 `%LOCALAPPDATA%/RenpyTranslator/error.log` 并弹出提示，不再静默退出。

验证结果：

- `dotnet build -c Debug`：0 错误 0 警告。
- `packaging/Publish.ps1`：管理器与更新器 Release 单文件发布成功，两项自检全部通过。
- `--self-test`：15 项检查全部 PASS，运行于模拟目录 `%LOCALAPPDATA%/RenpyTranslator/tests`。
- 五个页面在「已安装 · 资源 26.8.11 · 文件完整」的真实数据状态下逐页渲染核对，截图见 `temp/ui-after/`（`temp/` 不入库）。
- 单文件发行包内嵌图标 7 种尺寸（16 ~ 256）逐一校验存在。

界面核对使用模拟目录 `%LOCALAPPDATA%/RenpyTranslator/ui-check`；未启动游戏，未修改实际游戏安装。

未做：游戏内实际显示效果、深色主题、`dist/` 发行包重新生成（仍为旧界面产物）。
