# -*- coding: utf-8 -*-
"""
翻译 JSONL 查看器

适用格式：renpy-live-translator/translations 目录下的 jsonl 文件
每条记录为一行 JSON：{"source": 英文原文, "translation": 中文翻译}

功能：
  1. 输入/选择任意文件夹，自动列出其中的所有 .jsonl 文件（默认打开属于本项目的专属译文目录）
  2. 下拉选择文件，逐条展示「一行英文原文 + 一行中文翻译」
  3. 关键字过滤（不区分大小写，原文和翻译均参与匹配）
  4. Ctrl + 鼠标滚轮：放大/缩小正文字体（UI 界面字号不变）
  5. Ctrl + = / Ctrl + -：放大/缩小 UI 界面字号（正文字体不变）
  6. 行号跳转：输入记录序号回车/点跳转，定位到对应记录并高亮
  7. 会话记忆：记住上次打开的文件夹/文件/搜索词/滚动位置，重启自动恢复
  8. VSCode 打开：一键用 VSCode 编辑当前 jsonl 文件

运行方式：双击同目录下的「启动查看器.bat」，或直接 python translation_viewer.py
"""

import json
import os
import shutil
import subprocess

try:
    import tkinter as tk
    import tkinter.font as tkfont
    from tkinter import ttk, filedialog, scrolledtext
except ImportError:
    # 无 Tkinter 时给出可读提示而不是一堆堆栈
    import sys
    print("错误：当前 Python 未安装 Tkinter。")
    print("请使用 python.org 官方 Python 3（自带 Tkinter），或双击本目录下的「启动查看器.bat」。")
    sys.exit(1)

# ---------- 界面常量 ----------
APP_TITLE = "翻译 JSONL 查看器"
TEXT_FAMILY = "Microsoft YaHei UI"  # 正文（中英混排字体）
TEXT_SIZE = 10                      # 正文初始字号
TEXT_SIZE_MIN, TEXT_SIZE_MAX = 8, 32  # 正文字号可调范围
UI_SIZE_MIN, UI_SIZE_MAX = 8, 24      # UI 字号可调范围

COLOR_NUM = "#9e9e9e"   # 序号：灰色
COLOR_EN = "#1a5276"    # 英文原文：深蓝
COLOR_ZH = "#1e8449"    # 中文翻译：深绿
COLOR_BG = "#ffffff"    # 内容区背景

# 默认打开的游戏译文目录：脚本所在目录的上两级 translations/ 下的专属译文子目录
# 只扫描该目录本身，因此必须指到真正存放 jsonl 的那一层；译文换位置时改这里即可
DEFAULT_GAME_DIR = "camp-buddy-scoutmaster"

# 会话状态文件（记住上次打开的文件与滚动位置，不入库）
STATE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "viewer_state.json")


def _as_text(value) -> str:
    """把 JSON 字段统一成字符串：缺失/None → ""，非字符串类型（数字/布尔等）转成字符串"""
    if value is None:
        return ""
    return value if isinstance(value, str) else str(value)


class TranslationViewer:
    """主窗口：文件夹选择 + 文件选择 + 内容浏览 + 关键字过滤"""

    def __init__(self, root: tk.Tk):
        self.root = root
        root.title(APP_TITLE)
        root.geometry("920x640")
        root.minsize(680, 480)

        self.records = []        # 当前文件全部记录 [{source, translation}, ...]
        self.current_file = ""   # 当前文件名
        self._skipped = 0        # 上次加载时跳过的无效行数（供状态栏提示）
        self._closing = False    # 是否已进入关闭流程（避免退出时重复保存）
        self._search_after = None  # 搜索防抖定时器句柄

        # 字号状态：正文与 UI 各自独立可调
        self.text_size = TEXT_SIZE                     # 当前正文字号
        self.ui_font = self._get_default_ui_font()     # 当前 UI 字体（取系统默认）
        self._ui_style = ttk.Style()

        # 跳转与提示状态
        self._record_pos = {}      # 记录序号(1起) -> 该记录在 Text 中的起始行号
        self._record_end = {}      # 记录序号(1起) -> 该记录的结束行号（下一条起始行）
        self._last_shown = 0       # 最近一次显示的条数（供状态栏恢复）
        self._hl_after = None      # 跳转高亮清除定时器
        self._status_after = None  # 状态栏临时提示恢复定时器

        self._build_top_bar()    # 顶部：文件夹输入 + 文件下拉
        self._build_search_bar() # 搜索框
        self._build_body()       # 内容区
        self._build_status_bar() # 底部状态栏
        self._apply_ui_font()    # 统一应用 UI 字体样式
        self._bind_shortcuts()   # Ctrl+滚轮 / Ctrl+= / Ctrl+- 快捷键
        root.protocol("WM_DELETE_WINDOW", self._on_close)  # 退出前保存会话状态

        # 会话恢复：优先上次保存的 文件夹+文件+搜索词+滚动位置
        state = self._load_state()
        script_dir = os.path.dirname(os.path.abspath(__file__))
        # 默认目录指向专属译文子目录（jsonl 实际所在层），而非 translations 根目录
        default_dir = os.path.normpath(
            os.path.join(script_dir, "..", "..", "translations", DEFAULT_GAME_DIR))
        saved_dir = (state or {}).get("dir", "")
        if saved_dir and os.path.isdir(saved_dir):
            self.dir_var.set(saved_dir)
            self._scan_files(prefer_file=(state or {}).get("file", ""))
            # 恢复搜索词与过滤视图
            keyword = (state or {}).get("keyword", "")
            if keyword:
                self.search_var.set(keyword)
                self._refresh_view()
            # 布局就绪后立即恢复滚动位置（同步执行，无需等待定时器）
            scroll = (state or {}).get("scroll", 0)
            if scroll:
                self.root.update_idletasks()
                self.text.yview_moveto(scroll)
        elif os.path.isdir(default_dir):
            self.dir_var.set(default_dir)
            self._scan_files()

    # ---------- 界面搭建 ----------
    def _build_top_bar(self):
        """顶部栏：文件夹路径 + 浏览按钮 + jsonl 文件下拉框"""
        bar = ttk.Frame(self.root, padding=(8, 8, 8, 4))
        bar.pack(fill="x")

        ttk.Label(bar, text="文件夹:").grid(row=0, column=0, sticky="w")
        self.dir_var = tk.StringVar()
        dir_entry = ttk.Entry(bar, textvariable=self.dir_var)
        dir_entry.grid(row=0, column=1, sticky="ew", padx=(4, 4))
        ttk.Button(bar, text="浏览...", command=self._browse_folder).grid(row=0, column=2)

        ttk.Label(bar, text="文件:").grid(row=1, column=0, sticky="w", pady=(6, 0))
        self.file_var = tk.StringVar()
        self.file_box = ttk.Combobox(bar, textvariable=self.file_var, state="readonly")
        self.file_box.grid(row=1, column=1, sticky="ew", padx=(4, 4), pady=(6, 0))
        self.file_box.bind("<<ComboboxSelected>>", self._on_file_selected)
        # 下一文件按钮：位于文件下拉框右侧（浏览按钮下方、清除按钮上方）
        self.next_btn = ttk.Button(bar, text="下一文件", command=self._next_file)
        self.next_btn.grid(row=1, column=2, sticky="e", padx=(4, 0), pady=(6, 0))
        # VSCode 打开按钮：用 VSCode 编辑当前 jsonl 文件
        self.vscode_btn = ttk.Button(bar, text="VSCode打开", command=self._open_in_vscode)
        self.vscode_btn.grid(row=1, column=3, sticky="e", padx=(4, 0), pady=(6, 0))

        # 让第二列（路径输入/文件下拉）随窗口宽度伸缩
        bar.columnconfigure(1, weight=1)

    def _build_search_bar(self):
        """搜索框：关键字实时过滤；右侧为行号跳转"""
        bar = ttk.Frame(self.root, padding=(8, 4, 8, 4))
        bar.pack(fill="x")

        ttk.Label(bar, text="搜索:").pack(side="left")
        self.search_var = tk.StringVar()
        search_entry = ttk.Entry(bar, textvariable=self.search_var)
        search_entry.pack(side="left", fill="x", expand=True, padx=(4, 4))
        search_entry.bind("<KeyRelease>", self._on_search_key)
        # 鼠标右键粘贴/剪切不会触发 KeyRelease；这两个事件在类绑定之前触发，
        # 由 200ms 防抖兜底，回调执行时文本已更新完毕
        search_entry.bind("<<Paste>>", self._on_search_key)
        search_entry.bind("<<Cut>>", self._on_search_key)
        ttk.Button(bar, text="清除", command=self._clear_search).pack(side="left")

        # 行号跳转：输入显示序号 [NNNN] 回车或点跳转，定位到对应记录
        ttk.Label(bar, text="行号:").pack(side="left", padx=(16, 2))
        self.jump_var = tk.StringVar()
        jump_entry = ttk.Entry(bar, textvariable=self.jump_var, width=6)
        jump_entry.pack(side="left")
        jump_entry.bind("<Return>", self._jump_to)
        ttk.Button(bar, text="跳转", command=self._jump_to).pack(side="left", padx=(4, 0))

    def _build_body(self):
        """内容区：只读文本，每条记录显示 原文一行 + 翻译一行"""
        body = ttk.Frame(self.root)
        body.pack(fill="both", expand=True, padx=8, pady=(4, 4))

        self.text = scrolledtext.ScrolledText(
            body, wrap="none", font=(TEXT_FAMILY, self.text_size),
            bg=COLOR_BG, state="disabled", cursor="arrow",
        )
        # 长行不折行（wrap="none"），必须配横向滚动条，否则超出窗口的部分看不到
        self.text.grid(row=0, column=0, sticky="nsew")
        xbar = ttk.Scrollbar(body, orient="horizontal", command=self.text.xview)
        xbar.grid(row=1, column=0, sticky="ew")
        self.text.configure(xscrollcommand=xbar.set)
        body.rowconfigure(0, weight=1)
        body.columnconfigure(0, weight=1)

        # 定义五种文本样式：序号 / 英文原文 / 中文翻译 / 空状态提示 / 跳转高亮
        self.text.tag_configure("num", foreground=COLOR_NUM)
        self.text.tag_configure("en", foreground=COLOR_EN)
        self.text.tag_configure("zh", foreground=COLOR_ZH)
        self.text.tag_configure("empty", foreground="#808080")
        self.text.tag_configure("hl", background="#fff3cd")  # 跳转目标浅黄高亮

    def _build_status_bar(self):
        """底部状态栏：条数统计（字体跟随 UI 样式统一变化）"""
        self.status_var = tk.StringVar(value="未加载文件")
        bar = ttk.Label(self.root, textvariable=self.status_var,
                        anchor="w", padding=(8, 3))
        bar.pack(fill="x", side="bottom")

    # ---------- 字号控制 ----------
    def _get_default_ui_font(self) -> tuple:
        """获取系统默认 UI 字体的 (字体名, 字号)，保证初始外观与系统一致"""
        default = tkfont.nametofont("TkDefaultFont")
        return (default.actual("family"), abs(default.actual("size")))

    def _apply_ui_font(self):
        """将当前 UI 字号应用到所有 ttk 控件及下拉列表（不影响正文字体）"""
        self._ui_style.configure(".", font=self.ui_font)
        self._ui_style.configure("TLabel", font=self.ui_font)
        self._ui_style.configure("TButton", font=self.ui_font)
        self._ui_style.configure("TEntry", font=self.ui_font)
        self._ui_style.configure("TCombobox", font=self.ui_font)
        # Combobox 下拉列表是独立 Listbox，需单独设置字体
        self.root.option_add("*TCombobox*Listbox.font", self.ui_font)

    def _bind_shortcuts(self):
        """绑定字号快捷键：Ctrl+滚轮控制正文，Ctrl+=/- 控制 UI"""
        self.text.bind("<Control-MouseWheel>", self._on_ctrl_wheel)
        self.root.bind("<Control-equal>", self._ui_font_bigger)   # Ctrl + =
        self.root.bind("<Control-plus>", self._ui_font_bigger)    # Ctrl + Shift + = / 小键盘 +
        self.root.bind("<Control-minus>", self._ui_font_smaller)  # Ctrl + -

    def _on_ctrl_wheel(self, event):
        """Ctrl+滚轮：放大/缩小正文字号（UI 界面字号不变）"""
        steps = max(1, abs(event.delta) // 120)  # 高分屏一次滚动可能超过 120
        if event.delta > 0:
            self.text_size = min(TEXT_SIZE_MAX, self.text_size + steps)
        else:
            self.text_size = max(TEXT_SIZE_MIN, self.text_size - steps)
        self.text.configure(font=(TEXT_FAMILY, self.text_size))
        return "break"  # 拦截默认滚动行为

    def _ui_font_bigger(self, _event=None):
        """Ctrl + =：放大 UI 字号（正文字体不变）"""
        self.ui_font = (self.ui_font[0], min(UI_SIZE_MAX, self.ui_font[1] + 1))
        self._apply_ui_font()
        return "break"

    def _ui_font_smaller(self, _event=None):
        """Ctrl + -：缩小 UI 字号（正文字体不变）"""
        self.ui_font = (self.ui_font[0], max(UI_SIZE_MIN, self.ui_font[1] - 1))
        self._apply_ui_font()
        return "break"

    # ---------- 事件处理 ----------
    def _browse_folder(self):
        """弹出文件夹选择对话框，选择后自动扫描并加载"""
        folder = filedialog.askdirectory(title="选择包含 jsonl 翻译文件的文件夹")
        if folder:
            self.dir_var.set(folder)
            self._scan_files()

    def _scan_files(self, prefer_file: str = ""):
        """扫描当前文件夹下的所有 .jsonl 文件；prefer_file 存在则加载它，否则加载第一个"""
        folder = self.dir_var.get().strip()
        files = []
        if os.path.isdir(folder):
            files = sorted(f for f in os.listdir(folder) if f.lower().endswith(".jsonl"))
        self.file_box["values"] = files
        self.file_var.set("")
        # 无文件时禁用"下一文件"/"VSCode打开"按钮
        state = "!disabled" if files else "disabled"
        self.next_btn.state([state])
        self.vscode_btn.state([state])
        if files:
            if prefer_file in files:
                self.file_box.current(files.index(prefer_file))
                self._load_file(prefer_file)
            else:
                self.file_box.current(0)
                self._load_file(files[0])
        else:
            # 目录不存在或没有 jsonl：彻底重置，避免状态栏残留上一个文件的信息
            self.current_file = ""
            self._clear_content()
            self._update_status()

    def _on_file_selected(self, _event=None):
        """下拉框切换文件"""
        name = self.file_var.get()
        if name:
            self._load_file(name)

    def _next_file(self):
        """切换到下拉列表中的下一个 jsonl 文件（末尾循环回第一个）"""
        files = list(self.file_box["values"])
        if not files:
            return
        current = self.file_var.get()
        try:
            idx = files.index(current)
        except ValueError:
            idx = -1
        nxt_idx = (idx + 1) % len(files)
        self.file_box.current(nxt_idx)
        self._load_file(files[nxt_idx])

    def _on_search_key(self, _event=None):
        """搜索框输入：200ms 防抖后刷新列表，避免大文件卡顿"""
        if self._search_after:
            self.root.after_cancel(self._search_after)
        self._search_after = self.root.after(200, self._refresh_view)

    def _clear_search(self):
        """清空搜索框并恢复完整列表"""
        self.search_var.set("")
        self._refresh_view()

    # ---------- 数据加载与渲染 ----------
    def _load_file(self, name: str):
        """读取 jsonl 文件（UTF-8，兼容带 BOM），解析失败或格式不对的行跳过并计数"""
        folder = self.dir_var.get().strip()
        path = os.path.join(folder, name)
        self.current_file = name
        self.records = []
        skipped = 0
        try:
            # utf-8-sig：自动剥离 BOM，否则部分编辑器保存的文件首行会解析失败
            with open(path, "r", encoding="utf-8-sig") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        item = json.loads(line)
                    except json.JSONDecodeError:
                        skipped += 1
                        continue
                    if not isinstance(item, dict):
                        skipped += 1  # 合法 JSON 但不是对象（数组/字符串等），无法当记录用
                        continue
                    self.records.append({
                        "source": _as_text(item.get("source")),
                        "translation": _as_text(item.get("translation")),
                    })
        except (OSError, UnicodeDecodeError) as e:
            # UnicodeDecodeError 属于 ValueError 而非 OSError：非 UTF-8（如编辑器存成 GBK）
            # 的文件必须在这里一并捕获，否则异常会冒泡到 Tk 回调，界面只是"没反应"
            self._clear_content()
            self.text.configure(state="normal")
            if isinstance(e, UnicodeDecodeError):
                self.text.insert("end", f"读取失败：{name} 不是 UTF-8 编码\n", ("en",))
                self.text.insert("end", "请用编辑器另存为 UTF-8 后重试。\n", ("empty",))
            else:
                self.text.insert("end", f"读取文件失败：{e}\n", ("en",))
            self.text.configure(state="disabled")
            self._update_status()
            return
        self._skipped = skipped
        if skipped:
            print(f"[警告] {name} 有 {skipped} 行解析失败，已跳过")
        self._refresh_view()

    def _refresh_view(self):
        """按搜索关键字过滤并重绘内容区"""
        keyword = self.search_var.get().strip().lower()
        shown = 0
        self._record_pos = {}  # 内容重建，行号映射一并重建
        self._record_end = {}
        self.text.configure(state="normal")
        self.text.delete("1.0", "end")

        for i, rec in enumerate(self.records):
            # 关键字匹配：原文或翻译包含即命中（不区分大小写）
            if keyword and keyword not in rec["source"].lower() \
                    and keyword not in rec["translation"].lower():
                continue
            shown += 1
            self._append_record(i + 1, rec)

        # 空状态提示：避免直接显示空白页
        if not self.records:
            self.text.insert("end", "该文件为空，无任何翻译记录\n", ("empty",))
        elif shown == 0:
            self.text.insert("end", f"未找到包含“{keyword}”的内容\n", ("empty",))

        self.text.configure(state="disabled")
        self._last_shown = shown
        self._update_status(shown)

    def _append_record(self, index: int, rec: dict):
        """向内容区追加一条记录：序号 + EN 行 + ZH 行 + 空行，并记录其起止行号"""
        # 起始行号 = 插入前 end-1c 的行号（空文本时为 1）
        self._record_pos[index] = int(self.text.index("end-1c").split(".")[0])

        text = self.text
        text.insert("end", f"[{index:04d}] ", ("num",))
        text.insert("end", f"{rec['source']}\n", ("en",))
        text.insert("end", "       ", ("num",))          # 与 EN 行对齐的缩进
        text.insert("end", f"{rec['translation']}\n", ("zh",))
        text.insert("end", "\n", ("num",))

        # 结束行号 = 插入后 end-1c 的行号（即下一条的起始行）。
        # 原文/译文本身可能含换行符，记录实际占几行不固定，跳转高亮必须用它而不是固定 3 行
        self._record_end[index] = int(self.text.index("end-1c").split(".")[0])

    def _clear_content(self):
        """清空内容区与记录（条数统计一并归零）"""
        self.records = []
        self._last_shown = 0
        self._skipped = 0
        self.text.configure(state="normal")
        self.text.delete("1.0", "end")
        self.text.configure(state="disabled")

    def _update_status(self, shown: int | None = None):
        """更新底部状态栏：文件总条数与当前显示条数（缺省用上次显示数）"""
        if shown is None:
            shown = self._last_shown
        total = len(self.records)
        if not self.current_file:
            self.status_var.set("未加载文件")
            return
        msg = f"{self.current_file} ｜ 共 {total} 条"
        if shown < total:
            msg += f"，当前显示 {shown} 条"
        if self._skipped:
            msg += f"（已跳过 {self._skipped} 行无效数据）"
        self.status_var.set(msg)

    # ---------- 行号跳转 ----------
    def _jump_to(self, _event=None):
        """跳转到指定记录序号（对应显示序号 [NNNN]），并短暂高亮"""
        try:
            n = int(self.jump_var.get().strip())
        except ValueError:
            return  # 非法输入直接忽略
        pos = self._record_pos.get(n)
        if pos is None:
            # 区分两种找不到：序号越界 vs 序号存在但被当前搜索条件过滤掉了
            if 1 <= n <= len(self.records):
                self._flash_status(f"记录 {n} 被当前搜索条件过滤，未显示")
            else:
                self._flash_status(f"记录 {n} 不存在（共 {len(self.records)} 条）")
            return
        start = f"{pos}.0"
        self.text.see(start)
        # 高亮范围取该记录实际占用的行（含内嵌换行的记录会超过 3 行），末行 +1 是为了
        # 让"下一条的起始行"也纳入，否则最后一行译文会落在高亮之外；1.5 秒后自动清除
        end = f"{self._record_end.get(n, pos + 3)}.0"
        self.text.tag_remove("hl", "1.0", "end")
        self.text.tag_add("hl", start, end)
        if self._hl_after:
            self.root.after_cancel(self._hl_after)
        self._hl_after = self.root.after(1500,
                                         lambda: self.text.tag_remove("hl", "1.0", "end"))

    def _flash_status(self, msg: str):
        """状态栏临时提示，2 秒后恢复条数统计"""
        self.status_var.set(msg)
        if self._status_after:
            self.root.after_cancel(self._status_after)
        self._status_after = self.root.after(2000, self._update_status)

    # ---------- 会话状态保存/恢复 ----------
    def _load_state(self) -> dict:
        """读取上次会话状态（文件/搜索词/滚动位置），无状态或损坏时返回空字典"""
        try:
            with open(STATE_FILE, "r", encoding="utf-8") as f:
                state = json.load(f)
        except (OSError, json.JSONDecodeError):
            return {}
        # 状态文件是合法 JSON 但不是对象（被手工改成数组/字符串等）时按无状态处理
        return state if isinstance(state, dict) else {}

    def _save_state(self):
        """保存当前会话状态：文件夹、文件名、搜索词、纵向滚动比例"""
        state = {
            "dir": self.dir_var.get().strip(),
            "file": self.current_file,
            "keyword": self.search_var.get(),
            "scroll": float(self.text.yview()[0]) if self.current_file else 0.0,
        }
        try:
            with open(STATE_FILE, "w", encoding="utf-8") as f:
                json.dump(state, f, ensure_ascii=False, indent=2)
        except OSError:
            pass  # 保存失败不影响退出

    def _on_close(self):
        """窗口关闭前保存会话状态（解释器退出时会再次触发，需幂等）"""
        if self._closing:
            return  # 二次进入时控件已销毁，再取滚动位置会抛异常并把 scroll 写成 0
        self._closing = True
        self._save_state()
        self.root.destroy()

    # ---------- VSCode 打开 ----------
    @staticmethod
    def _find_code() -> str | None:
        """定位 VSCode 可执行文件：优先 PATH 中的 code，其次常见安装路径"""
        p = shutil.which("code")
        if p:
            return p
        for cand in (
            os.path.expandvars(r"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe"),
            r"C:\Program Files\Microsoft VS Code\Code.exe",
            os.path.expandvars(r"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd"),
            r"C:\Program Files\Microsoft VS Code\bin\code.cmd",
        ):
            if os.path.exists(cand):
                return cand
        return None

    def _open_in_vscode(self):
        """用 VSCode 打开当前 jsonl 文件（不阻塞本程序）"""
        if not self.current_file:
            return
        path = os.path.join(self.dir_var.get().strip(), self.current_file)
        code = self._find_code()
        if not code:
            self._flash_status("未找到 VSCode，请安装或将 VSCode 加入 PATH")
            return
        try:
            if code.lower().endswith((".cmd", ".bat")):
                subprocess.Popen(f'"{code}" "{path}"', shell=True)
            else:
                subprocess.Popen([code, path])
            self._flash_status(f"已用 VSCode 打开 {self.current_file}")
        except OSError:
            self._flash_status("打开 VSCode 失败")


if __name__ == "__main__":
    root = tk.Tk()
    app = TranslationViewer(root)
    root.mainloop()
