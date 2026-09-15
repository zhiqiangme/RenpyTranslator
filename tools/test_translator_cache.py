"""独立验证模组缓存函数；不启动 Ren'Py，也不创建网络线程。"""

import ast
import io
import json
import os
from pathlib import Path
import re
import tempfile
import textwrap
import unittest


def load_functions():
    source = (Path(__file__).resolve().parents[1] / "game/zz_live_translator.rpy").read_text(encoding="utf-8")
    block = source.split("init 999 python:\n", 1)[1].split("\nscreen live_translator_hotkeys", 1)[0]
    tree = ast.parse(textwrap.dedent(block))
    names = {
        "_live_translator_to_text", "_live_translator_normalize_source_key",
        "_live_translator_index_normalized_source", "_live_translator_load_cache",
    }
    # 仅执行实际源码中的纯函数，隔离顶层 Ren'Py 初始化和后台线程。
    selected = ast.Module(body=[node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name in names], type_ignores=[])
    scope = {
        "_live_translator_text_type": str,
        "_live_translator_re_module": re,
        "_live_translator_io_module": io,
        "_live_translator_os_module": os,
        "_live_translator_json_module": json,
        "_live_translator_log": lambda message: None,
    }
    exec(compile(selected, "zz_live_translator.rpy", "exec"), scope)
    return scope


class CacheTests(unittest.TestCase):
    def setUp(self):
        self.functions = load_functions()

    def test_conflicts_keep_first_fallback_and_exact_variants(self):
        records = [
            {"source": "Hello!", "translation": "你好"},
            {"source": "Hello！", "translation": "您好"},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cache.jsonl"
            path.write_text("\n".join(json.dumps(record) for record in records), encoding="utf-8")
            exact, normalized = {}, {}
            count = self.functions["_live_translator_load_cache"](str(path), "test", exact, normalized)
        self.assertEqual(count, 2)
        self.assertEqual(exact, {"Hello!": "你好", "Hello！": "您好"})
        self.assertEqual(normalized["Hello!"], ("Hello!", "你好"))

    def test_same_source_updates_and_empty_source_is_ignored(self):
        index = self.functions["_live_translator_index_normalized_source"]
        cache = {}
        index("Hello!", "你好", cache)
        index("Hello!", "您好", cache)
        index(" \t", "空白", cache)
        self.assertEqual(cache, {"Hello!": ("Hello!", "您好")})


if __name__ == "__main__":
    unittest.main()
