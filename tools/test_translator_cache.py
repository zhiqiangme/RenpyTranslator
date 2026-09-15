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
import threading
import time
import queue
from unittest.mock import Mock, patch


def load_functions():
    source = (Path(__file__).resolve().parents[1] / "game/zz_live_translator.rpy").read_text(encoding="utf-8")
    block = source.split("init 999 python:\n", 1)[1].split("\nscreen live_translator_hotkeys", 1)[0]
    tree = ast.parse(textwrap.dedent(block))
    names = {
        "_live_translator_to_text", "_live_translator_normalize_source_key",
        "_live_translator_index_normalized_source", "_live_translator_load_cache",
        "_live_translator_extract_json", "_live_translator_request_batch",
        "_live_translator_confirm_message", "_live_translator_enqueue",
        "_live_translator_worker",
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


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.scope = load_functions()
        self.scope.update({
            "_live_translator_config": {"enabled": True, "model": "mock", "batch_size": 1},
            "_live_translator_default_config": {"system_prompt": "mock"},
            "_live_translator_name_prompt_rule": "names",
            "_live_translator_request_count": 0,
            "_live_translator_api_key": lambda: "mock-key",
            "_live_translator_endpoint": lambda: "mock://no-network",
            "_live_translator_runtime_api_ready": lambda: True,
            "_live_translator_time_module": time,
            "_live_translator_queue_module": queue,
            "_live_translator_queue": queue.Queue(),
            "_live_translator_lock": threading.RLock(),
            "_live_translator_pending": set(),
            "_live_translator_retry_after": {},
            "_live_translator_should_translate": lambda text: True,
            "_live_translator_lookup": lambda text: "cached translation",
        })
        self.requests = Mock()
        self.module_patch = patch.dict("sys.modules", {"requests": self.requests})
        self.module_patch.start()
        self.addCleanup(self.module_patch.stop)

    def response(self, payload):
        self.requests.post.return_value.json.return_value = {
            "choices": [{"message": {"content": json.dumps(payload)}}]
        }

    def test_invalid_translations_are_rejected(self):
        for payload in ({"translations": [None]}, {"translations": [123]},
                        {"translations": [True]}, {"translations": [{}]},
                        {"translations": {"wrong": "value"}}, [],
                        {"translations": [""]}, {"translations": []}):
            with self.subTest(payload=payload):
                self.response(payload)
                with self.assertRaises(ValueError):
                    self.scope["_live_translator_request_batch"](["Hello"])

    def test_valid_translations_are_accepted(self):
        self.response({"translations": ["  你好  "]})
        self.assertEqual(self.scope["_live_translator_request_batch"](["Hello"]), ["你好"])

    def test_disabled_confirm_preserves_source_without_queueing(self):
        self.scope["_live_translator_config"]["enabled"] = False
        self.assertEqual(self.scope["_live_translator_confirm_message"]("Hello"), "Hello")
        self.scope["_live_translator_lookup"] = lambda text: None
        self.assertEqual(self.scope["_live_translator_confirm_message"]("Uncached"), "Uncached")
        self.scope["_live_translator_enqueue"]("Direct enqueue")
        self.assertTrue(self.scope["_live_translator_queue"].empty())
        self.assertFalse(self.scope["_live_translator_pending"])

    def test_disable_before_send_skips_network(self):
        # 在读取密钥时模拟 F9，覆盖组批完成到发送之间再次检查开关的分支。
        def key():
            self.scope["_live_translator_config"]["enabled"] = False
            return "mock-key"
        self.scope["_live_translator_api_key"] = key
        self.assertIsNone(self.scope["_live_translator_request_batch"](["Hello"]))
        self.requests.post.assert_not_called()

    def run_one_batch(self):
        class StopWorker(BaseException):
            pass
        actual_queue = self.scope["_live_translator_queue"]
        # 确定性结束无限工作循环，不创建线程或依赖等待时长。
        def get(**kwargs):
            if actual_queue.empty():
                raise StopWorker()
            return actual_queue.get_nowait()
        self.scope["_live_translator_queue"] = Mock(get=get)
        try:
            with self.assertRaises(StopWorker):
                self.scope["_live_translator_worker"]()
        finally:
            self.scope["_live_translator_queue"] = actual_queue

    def test_disabled_batch_releases_pending_and_can_be_requeued(self):
        self.scope["_live_translator_enqueue"]("Hello")
        self.scope["_live_translator_config"]["enabled"] = False
        self.run_one_batch()
        self.requests.post.assert_not_called()
        self.assertFalse(self.scope["_live_translator_pending"])
        self.scope["_live_translator_config"]["enabled"] = True
        self.scope["_live_translator_enqueue"]("Hello")
        self.response({"translations": ["你好"]})
        finish = Mock()
        self.scope["_live_translator_finish_batch"] = finish
        self.run_one_batch()
        finish.assert_called_once_with(["Hello"], ["你好"])

    def test_invalid_batch_never_reaches_cache_writer(self):
        self.scope["_live_translator_enqueue"]("Hello")
        self.response({"translations": [None]})
        finish, fail = Mock(), Mock()
        self.scope["_live_translator_finish_batch"] = finish
        self.scope["_live_translator_fail_batch"] = fail
        self.run_one_batch()
        finish.assert_not_called()
        fail.assert_called_once()


if __name__ == "__main__":
    unittest.main()
