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
from collections import deque
from types import SimpleNamespace
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
        "_live_translator_worker", "_live_translator_api_host",
        "_live_translator_apply_provider_options", "_live_translator_config_number",
        "_live_translator_error_text", "_live_translator_fail_batch",
        "_live_translator_take_batch", "_live_translator_work_once", "_live_translator_split_batch",
        "_live_translator_toggle", "_live_translator_show_status", "_live_translator_process_batch",
        "_live_translator_finish_batch", "_LiveTranslatorContentError",
        "_live_translator_accepts_end_animation", "_live_translator_uses_anthropic",
        "_live_translator_anthropic_payload", "_live_translator_anthropic_text",
        "_live_translator_endpoint", "_live_translator_sanitize_error_detail",
        "_live_translator_is_content_block", "_live_translator_extract_api_error",
        "_live_translator_raise_http_error", "_live_translator_openai_text",
        "_live_translator_is_echo", "_live_translator_is_protected_name_text", "_live_translator_should_translate",
        "_LiveTranslatorRefusal", "_LiveTranslatorEcho", "_LiveTranslatorTemporaryError",
        "_live_translator_replace_text", "_live_translator_refresh", "_live_translator_lookup",
    }
    constants = {
        "_live_translator_thinking_hosts", "_live_translator_thinking_host_suffixes",
        "_live_translator_host_pattern", "_live_translator_english_pattern", "_live_translator_chinese_pattern",
        "_live_translator_name_punctuation_pattern", "_live_translator_text_tag_pattern",
        "_live_translator_anthropic_host", "_live_translator_anthropic_version",
        "_live_translator_translation_schema", "_live_translator_content_block_markers",
    }

    def wanted(node):
        if isinstance(node, (ast.FunctionDef, ast.ClassDef)):
            return node.name in names
        # 服务商规则常量是纯数据，与函数一同从真实源码中取出。
        return isinstance(node, ast.Assign) and any(
            isinstance(target, ast.Name) and target.id in constants for target in node.targets
        )

    # 仅执行实际源码中的纯函数，隔离顶层 Ren'Py 初始化和后台线程。
    selected = ast.Module(body=[node for node in tree.body if wanted(node)], type_ignores=[])
    scope = {
        "_live_translator_text_type": str,
        "_live_translator_re_module": re,
        "_live_translator_io_module": io,
        "_live_translator_os_module": os,
        "_live_translator_json_module": json,
        "_live_translator_log": lambda message: None,
        "_live_translator_plain_dict": dict,
        "_live_translator_plain_set": set,
        "_live_translator_protected_names": {"yoshinorinagira"},
        "_live_translator_skip_patterns": [],
    }
    exec(compile(selected, "zz_live_translator.rpy", "exec"), scope)
    return scope


class CacheTests(unittest.TestCase):
    def setUp(self):
        self.functions = load_functions()

    def test_only_runtime_cache_ignores_unchanged_sentences(self):
        sentence = "Please open the window."
        records = [
            (sentence, sentence), ("Stay with me!", "陪我！"), ("Stay with me!", "Stay with me!"),
            ("Yoshinori Nagira", "Yoshinori Nagira"), ("U.S.A.", "U.S.A."),
            ("Ludwig van Beethoven.", "Ludwig van Beethoven."),
            ("{b}[first_name] %(last_name)s{/b}", "{b}[first_name] %(last_name)s{/b}"),
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cache.jsonl"
            path.write_text("\n".join(json.dumps({"source": s, "translation": t}) for s, t in records), encoding="utf-8")
            exact, normalized = {}, {}
            load = self.functions["_live_translator_load_cache"]
            self.assertEqual(load(str(path), "runtime", exact, normalized, runtime=True), 5)
            self.assertNotIn(sentence, exact)
            self.assertNotIn(sentence, normalized)
            self.assertEqual(exact["Stay with me!"], "陪我！")
            for source, target in records[3:]:
                self.assertEqual(exact[source], target)
            load(str(path), "pretranslated", exact, normalized)
            self.assertEqual(exact[sentence], sentence)

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


class ReadbackPatchTests(unittest.TestCase):
    def test_signature_check_only_flags_incompatible_change(self):
        accepts = load_functions()["_live_translator_accepts_end_animation"]
        def legacy(self, value):
            pass
        def modern(self, value, end_animation=True):
            pass
        def flexible(self, value, **kwargs):
            pass
        self.assertFalse(accepts(legacy))
        self.assertTrue(accepts(modern))
        self.assertTrue(accepts(flexible))


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.scope = load_functions()
        self.scope.update({
            "_live_translator_config": {"enabled": True, "model": "mock", "batch_size": 1},
            "_live_translator_default_config": {"system_prompt": "mock"},
            "_live_translator_name_prompt_rule": "names",
            "_live_translator_state": {"generation": 0, "request_count": 0, "success_count": 0},
            "_live_translator_failures": {},
            "_live_translator_isolated": set(),
            "_live_translator_pretranslated_count": 0,
            "_live_translator_runtime_cache_count": 0,
            "_live_translator_pretranslated_cache": {},
            "_live_translator_pretranslated_normalized_cache": {},
            "_live_translator_runtime_cache": {},
            "_live_translator_runtime_normalized_cache": {},
            "_live_translator_index_normalized_source": lambda source, translation, cache: None,
            "_live_translator_append_cache": Mock(),
            "renpy": Mock(is_init_phase=lambda: True),
            "_live_translator_api_key": lambda: "mock-key",
            "_live_translator_endpoint": lambda: "mock://no-network",
            "_live_translator_runtime_api_ready": lambda: True,
            "_live_translator_time_module": time,
            "_live_translator_queue": deque(),
            "_live_translator_lock": threading.Condition(),
            "_live_translator_pending": set(),
            "_live_translator_retry_after": {},
            "_live_translator_previous_replace_text": None,
            "_live_translator_local_weekday_translation": lambda source: None,
            "_live_translator_local_save_slot_translation": lambda source: None,
            "_live_translator_local_image_not_found_translation": lambda source: None,
            "_live_translator_is_save_metadata": lambda source: False,
        })
        self.requests = Mock()
        self.requests.exceptions.Timeout = TimeoutError
        self.requests.exceptions.ConnectionError = ConnectionError
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

    def sent_payload(self):
        return json.loads(self.requests.post.call_args.kwargs["data"].decode("utf-8"))

    def test_strict_provider_payload_omits_thinking(self):
        self.scope["_live_translator_config"]["base_url"] = "https://api.openai.com/v1"
        self.response({"translations": ["你好"]})
        self.scope["_live_translator_request_batch"](["Hello"])
        payload = self.sent_payload()
        self.assertNotIn("thinking", payload)
        self.assertNotIn("temperature", payload)
        self.assertNotIn("max_tokens", payload)
        self.assertEqual(payload["max_completion_tokens"], 2400)

    def test_openai_ignores_thinking_enabled_and_omits_temperature(self):
        self.scope["_live_translator_config"].update(
            {"base_url": "https://api.openai.com/v1", "thinking_enabled": True}
        )
        self.response({"translations": ["你好"]})
        self.scope["_live_translator_request_batch"](["Hello"])
        payload = self.sent_payload()
        self.assertNotIn("thinking", payload)
        self.assertNotIn("temperature", payload)
        self.assertNotIn("max_tokens", payload)
        self.assertEqual(payload["max_completion_tokens"], 2400)

    def test_known_provider_payload_disables_thinking(self):
        self.scope["_live_translator_config"]["base_url"] = "https://API.deepseek.com"
        self.response({"translations": ["你好"]})
        self.scope["_live_translator_request_batch"](["Hello"])
        payload = self.sent_payload()
        self.assertEqual(payload["thinking"], {"type": "disabled"})
        self.assertIn("temperature", payload)

    def test_provider_options_match_manager_rules(self):
        apply = self.scope["_live_translator_apply_provider_options"]
        self.assertNotIn("thinking", apply({"max_tokens": 1, "temperature": 0.1}, "generativelanguage.googleapis.com", False))
        self.assertIn("thinking", apply({"max_tokens": 1, "temperature": 0.1}, "ark.cn-beijing.volces.com", False))
        self.assertEqual(apply({"max_tokens": 1, "temperature": 0.1}, "127.0.0.1", True)["thinking"], {"type": "enabled"})
        openai_enabled = apply({"max_tokens": 1, "temperature": 0.1}, "api.openai.com", True)
        self.assertEqual(openai_enabled, {"max_completion_tokens": 1})

    def http_error_response(self, status_code, body):
        resp = Mock()
        resp.status_code = status_code
        resp.text = body
        resp.raise_for_status.side_effect = RuntimeError("HTTP %d" % status_code)
        self.requests.post.return_value = resp

    def test_http_400_content_risk_is_content_error_and_isolates_batch(self):
        self.scope["_live_translator_config"]["batch_size"] = 2
        self.http_error_response(
            400,
            json.dumps({"error": {"message": "Content Exists Risk", "type": "invalid_request_error"}})
        )
        self.scope["_live_translator_enqueue"]("Normal line")
        self.scope["_live_translator_enqueue"]("Blocked line")
        self.run_one_batch()
        self.assertEqual(self.scope["_live_translator_isolated"], {"Normal line", "Blocked line"})
        for attempts, kind, message in self.scope["_live_translator_failures"].values():
            self.assertEqual((attempts, kind), ({}, "split"))
            self.assertIn("内容审查", message)
            self.assertIn("Content Exists Risk", message)

    def test_http_400_parameter_error_preserves_detail_without_auto_retry(self):
        self.http_error_response(400, json.dumps({"error": {"message": "Unsupported value: temperature"}}))
        self.scope["_live_translator_enqueue"]("Hello")
        self.run_one_batch()
        self.assertFalse(self.scope["_live_translator_retry_after"])
        self.assertIn("HTTP 400", self.status())
        self.assertIn("temperature", self.status())

    def test_http_error_detail_redacts_api_key(self):
        self.http_error_response(
            401,
            json.dumps({"error": {"message": "Incorrect API key provided: mock-key"}})
        )
        self.scope["_live_translator_enqueue"]("Hello")
        self.run_one_batch()
        last_error = self.status()
        self.assertIn("***", last_error)
        self.assertNotIn("mock-key", last_error)

    def test_openai_content_filter_refusal_and_blank_content_are_content_errors(self):
        content_error = self.scope["_LiveTranslatorContentError"]
        self.requests.post.return_value.json.return_value = {
            "choices": [{"finish_reason": "content_filter", "message": {"content": ""}}]
        }
        with self.assertRaises(content_error) as ctx_filter:
            self.scope["_live_translator_request_batch"](["Hello"])
        self.assertIn("内容审查", str(ctx_filter.exception))

        self.requests.post.return_value.json.return_value = {
            "choices": [{"finish_reason": "stop", "message": {"content": None, "refusal": "Policy refusal"}}]
        }
        with self.assertRaises(content_error) as ctx_refusal:
            self.scope["_live_translator_request_batch"](["Hello"])
        self.assertIn("拒绝", str(ctx_refusal.exception))

        self.requests.post.return_value.json.return_value = {
            "choices": [{"finish_reason": "stop", "message": {"content": "   "}}]
        }
        with self.assertRaises(content_error):
            self.scope["_live_translator_request_batch"](["Hello"])

    def use_claude(self):
        self.scope["_live_translator_config"].update(
            {"base_url": "https://api.anthropic.com/v1", "model": "claude-haiku-5-5"}
        )
        # setUp 用桩替换了地址函数；Claude 用例需要真实的地址拼接规则。
        self.scope["_live_translator_endpoint"] = load_functions()["_live_translator_endpoint"]
        self.scope["_live_translator_endpoint"].__globals__.update(self.scope)

    def claude_response(self, content, stop_reason="end_turn"):
        self.requests.post.return_value.json.return_value = {
            "type": "message", "role": "assistant", "content": content, "stop_reason": stop_reason,
        }

    def test_claude_request_uses_messages_api(self):
        self.use_claude()
        self.claude_response([
            {"type": "thinking", "thinking": "", "signature": "sig"},
            {"type": "text", "text": json.dumps({"translations": ["你好"]})},
        ])
        self.assertEqual(self.scope["_live_translator_request_batch"](["Hello"]), ["你好"])
        call = self.requests.post.call_args
        self.assertEqual(call.args[0], "https://api.anthropic.com/v1/messages")
        self.assertEqual(call.kwargs["headers"]["x-api-key"], "mock-key")
        self.assertEqual(call.kwargs["headers"]["anthropic-version"], "2023-06-01")
        self.assertNotIn("Authorization", call.kwargs["headers"])
        payload = self.sent_payload()
        self.assertEqual(payload["model"], "claude-haiku-5-5")
        self.assertEqual(payload["system"], "mock names")
        self.assertEqual(payload["messages"], [{"role": "user", "content": '{"texts": ["Hello"]}'}])
        self.assertEqual(payload["max_tokens"], 2400)
        # 当前 Claude 模型拒绝非默认采样参数与 thinking 关闭写法，均不得发送。
        for forbidden in ("temperature", "thinking", "response_format"):
            self.assertNotIn(forbidden, payload)
        self.assertEqual(payload["output_config"]["effort"], "low")
        schema = payload["output_config"]["format"]["schema"]
        self.assertEqual(payload["output_config"]["format"]["type"], "json_schema")
        self.assertFalse(schema["additionalProperties"])
        self.assertEqual(schema["required"], ["translations"])

    def test_claude_thinking_and_format_switches(self):
        self.use_claude()
        self.scope["_live_translator_config"].update({"thinking_enabled": True, "json_response_format": False})
        self.claude_response([{"type": "text", "text": '{"translations":["你好"]}'}])
        self.scope["_live_translator_request_batch"](["Hello"])
        self.assertNotIn("output_config", self.sent_payload())

    def test_claude_endpoint_accepts_full_messages_url(self):
        self.use_claude()
        self.scope["_live_translator_config"]["base_url"] = "https://api.anthropic.com/v1/messages/"
        self.assertEqual(self.scope["_live_translator_endpoint"](), "https://api.anthropic.com/v1/messages")

    def test_claude_refusal_is_content_error(self):
        self.use_claude()
        self.claude_response([], stop_reason="refusal")
        with self.assertRaises(self.scope["_LiveTranslatorContentError"]):
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
        self.assertFalse(self.scope["_live_translator_queue"])
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
        self.scope["_live_translator_work_once"]()

    def status(self):
        self.scope["_live_translator_show_status"]()
        return self.scope["renpy"].notify.call_args.args[0]

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

    def test_invalid_config_numbers_do_not_stop_worker(self):
        self.scope["_live_translator_config"].update(
            {"batch_size": "abc", "batch_wait_ms": None, "retry_cooldown_seconds": "x"}
        )
        self.scope["_live_translator_enqueue"]("Hello")
        self.response({"translations": [None]})
        self.run_one_batch()
        self.assertFalse(self.scope["_live_translator_pending"])
        self.assertGreater(self.scope["_live_translator_retry_after"]["Hello"], time.time())
        self.assertIn("API", self.scope["_live_translator_failures"]["Hello"][2])

    def test_failure_handler_error_still_releases_pending(self):
        self.scope["_live_translator_enqueue"]("Hello")
        self.requests.post.side_effect = RuntimeError("network down")
        self.scope["_live_translator_fail_batch"] = Mock(side_effect=UnicodeDecodeError("ascii", b"\xe6", 0, 1, "bad"))
        self.run_one_batch()
        self.assertFalse(self.scope["_live_translator_pending"])
        # 线程仍可继续处理后续批次。
        self.requests.post.side_effect = None
        self.response({"translations": ["你好"]})
        finish = Mock()
        self.scope["_live_translator_finish_batch"] = finish
        self.scope["_live_translator_enqueue"]("Hello")
        self.run_one_batch()
        finish.assert_called_once_with(["Hello"], ["你好"])

    def test_error_text_survives_unprintable_exception(self):
        class Unprintable(Exception):
            def __str__(self):
                raise UnicodeDecodeError("ascii", b"\xe6", 0, 1, "bad")
        self.assertIn("Unprintable", self.scope["_live_translator_error_text"](Unprintable()))
        self.assertEqual(self.scope["_live_translator_error_text"](RuntimeError("中文")), "中文")

    def simulate_cooldown(self):
        # 等待时推进虚拟时钟，由真实调度器自行取出到期任务。
        clock = Mock(return_value=1000.0)
        self.scope["_live_translator_time_module"] = Mock(time=clock)
        def advance(seconds):
            self.assertIsNotNone(seconds, "没有待处理任务，工作线程将永久等待")
            clock.return_value += seconds
        self.scope["_live_translator_lock"].wait = Mock(side_effect=advance)

    def test_repeated_invalid_content_is_abandoned(self):
        self.simulate_cooldown()
        self.response({"translations": []})
        self.scope["_live_translator_enqueue"]("Bad")
        for _ in range(3):
            self.run_one_batch()
        self.assertFalse(self.scope["_live_translator_enqueue"]("Bad"))
        self.assertIn("其他失败 1 条", self.status())
        self.assertEqual(self.requests.post.call_count, 3)

    def test_timeout_and_rate_limit_retry_with_cooldown_and_stop(self):
        self.simulate_cooldown()
        self.http_error_response(429, json.dumps({"error": "rate limited"}))
        self.requests.post.return_value.headers = {"Retry-After": "45"}
        self.requests.post.side_effect = [TimeoutError("timeout"), self.requests.post.return_value, self.requests.post.return_value]
        self.scope["_live_translator_enqueue"]("Hello")
        self.run_one_batch()
        self.assertIn("等待重试 1 条", self.status())
        self.run_one_batch()
        self.run_one_batch()
        self.assertEqual(self.scope["_live_translator_time_module"].time(), 1075)
        self.assertFalse(self.scope["_live_translator_enqueue"]("Hello"))
        self.assertIn("HTTP 429", self.status())
        self.assertNotIn("服务商拒绝", self.status())

    def test_invalid_multi_batch_isolates_sources(self):
        self.scope["_live_translator_config"]["batch_size"] = 2
        self.response({"translations": ["只有一条"]})
        self.scope["_live_translator_enqueue"]("A")
        self.scope["_live_translator_enqueue"]("B")
        self.run_one_batch()
        self.assertEqual(self.scope["_live_translator_isolated"], {"A", "B"})
        split = self.scope["_live_translator_split_batch"]
        self.assertEqual(split(["A", "C", "B", "D"]), [["A"], ["B"], ["C", "D"]])

    def test_success_clears_error_and_failure_state(self):
        self.scope["_live_translator_failures"]["Hello"] = ({"invalid": 2}, "invalid", "old error")
        self.scope["_live_translator_isolated"].add("Hello")
        self.scope["_live_translator_finish_batch"](["Hello"], ["你好"])
        self.assertNotIn("Hello", self.scope["_live_translator_failures"])
        self.assertNotIn("Hello", self.scope["_live_translator_isolated"])
        self.assertEqual(self.scope["_live_translator_runtime_cache"]["Hello"], "你好")

    def test_static_screen_recovers_without_user_input_and_retains_refusal(self):
        self.scope["_live_translator_config"].update(batch_size=2, pending_text="…")
        render = self.scope["_live_translator_replace_text"]
        blocked, good = "This line was refused.", "Where is the station?"
        self.assertEqual([render(blocked), render(good)], ["…", "…"])
        self.http_error_response(400, json.dumps({"error": "Content Exists Risk"}))
        refused = self.requests.post.return_value
        translated = Mock(status_code=200)
        translated.json.return_value = {"choices": [{"message": {"content": json.dumps({"translations": ["车站在哪里？"]})}}]}
        self.requests.post.side_effect = [refused, refused, translated]
        self.scope["renpy"].is_init_phase = lambda: False
        self.run_one_batch()
        self.run_one_batch()
        batches = [json.loads(json.loads(call.kwargs["data"])["messages"][-1]["content"])["texts"]
                   for call in self.requests.post.call_args_list]
        self.assertEqual(batches, [[blocked, good], [blocked], [good]])
        self.assertEqual([render(blocked), render(good)], [blocked, "车站在哪里？"])
        self.assertIn("服务商拒绝 1 条", self.status())
        self.assertNotIn("等待重试", self.status())
        self.assertFalse(self.scope["_live_translator_queue"])
        self.scope["renpy"].invoke_in_main_thread.assert_called_with(self.scope["_live_translator_refresh"], [good])

    def test_refresh_invalidates_only_widgets_containing_changed_text(self):
        class TextWidget:
            def __init__(self, text):
                self.text = [text]
                self.dirty = False

        changed = TextWidget("{i}Where is the station?{/i}")
        other = TextWidget("Settings")
        screen = Mock()
        def visit(callback):
            for widget in (screen, changed, other):
                callback(widget)
        screen.visit_all.side_effect = visit
        api = self.scope["renpy"]
        api.is_init_phase = lambda: False
        api.get_showing_tags.return_value = ["fixture"]
        api.get_screen.return_value = screen
        self.scope.update(config=SimpleNamespace(layers=["screens"]), _live_translator_text_displayable=TextWidget)
        self.scope["_live_translator_refresh"](["Where is the station?"])
        self.assertTrue(changed.dirty)
        self.assertFalse(other.dirty)
        api.redraw.assert_called_once_with(changed, 0)
        api.restart_interaction.assert_called_once()

    def test_echo_retries_only_echoed_line_once_after_a_timeout(self):
        self.simulate_cooldown()
        self.scope["_live_translator_config"]["batch_size"] = 2
        sentence, good = "Please open the window.", "Good morning!"
        for source in (sentence, good):
            self.scope["_live_translator_enqueue"](source)
        self.requests.post.side_effect = TimeoutError("timeout")
        self.run_one_batch()
        self.requests.post.side_effect = None
        self.response({"translations": [sentence, "早安！"]})
        self.run_one_batch()
        self.assertEqual(self.scope["_live_translator_runtime_cache"], {good: "早安！"})
        self.response({"translations": [sentence]})
        self.run_one_batch()
        self.assertEqual(json.loads(self.sent_payload()["messages"][-1]["content"])["texts"], [sentence])
        self.assertFalse(self.scope["_live_translator_enqueue"](sentence))
        self.assertIn("模型返回原文 1 条", self.status())
        self.scope["_live_translator_append_cache"].assert_called_once_with(good, "早安！")

    def test_f9_cancels_cooldown_and_discards_inflight_reply_after_reenable(self):
        self.simulate_cooldown()
        enqueue = self.scope["_live_translator_enqueue"]
        toggle = self.scope["_live_translator_toggle"]
        enqueue("Old")
        self.requests.post.side_effect = TimeoutError("timeout")
        self.run_one_batch()
        enqueue("Hello")
        self.response({"translations": ["旧回复"]})
        def toggle_during_request(*args, **kwargs):
            toggle()
            toggle()
            enqueue("Hello")
            return self.requests.post.return_value
        self.requests.post.side_effect = toggle_during_request
        self.run_one_batch()
        self.assertFalse(self.scope["_live_translator_retry_after"])
        self.assertFalse(self.scope["_live_translator_runtime_cache"])
        self.assertEqual(self.scope["_live_translator_pending"], {"Hello"})
        self.requests.post.side_effect = None
        self.response({"translations": ["新回复"]})
        self.run_one_batch()
        self.assertEqual(self.scope["_live_translator_runtime_cache"], {"Hello": "新回复"})

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
