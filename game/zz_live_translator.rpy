# Ren'Py 7.8 / Python 2.7 实时翻译模组。
# 所有网络请求都在后台线程执行，避免阻塞游戏渲染。

init 999 python:
    # Ren'Py 的 Python 块共享游戏 store，必须使用独有别名避免被剧情变量覆盖。
    import io as _live_translator_io_module
    import json as _live_translator_json_module
    import os as _live_translator_os_module
    import re as _live_translator_re_module
    import threading as _live_translator_threading_module
    import time as _live_translator_time_module

    try:
        import Queue as _live_translator_queue_module
    except ImportError:
        import queue as _live_translator_queue_module

    try:
        _live_translator_text_type = unicode
    except NameError:
        _live_translator_text_type = str

    _live_translator_directory = _live_translator_os_module.path.join(
        renpy.config.gamedir, "live_translator"
    )
    _live_translator_config_path = _live_translator_os_module.path.join(
        _live_translator_directory, "config.json"
    )
    _live_translator_cache_path = _live_translator_os_module.path.join(
        _live_translator_directory, "cache.jsonl"
    )

    _live_translator_default_config = {
        "enabled": True,
        "base_url": "https://api.openai.com/v1",
        "api_key": "",
        "api_key_env": "REN_TRANSLATOR_API_KEY",
        "model": "your-model-name",
        "font": "C:/Windows/Fonts/msyh.ttc",
        "batch_size": 8,
        "batch_wait_ms": 180,
        "request_timeout_seconds": 60,
        "retry_cooldown_seconds": 30,
        "temperature": 0.1,
        "max_output_tokens": 2400,
        "pending_text": "",
        "system_prompt": (
            "你是视觉小说本地化译者。把输入数组中的英文逐条翻译成自然、简洁的"
            "简体中文，保持人物语气、称谓、情绪和原意，不审查、不解释。"
            "原样保留 Ren'Py 文本标签、方括号变量、printf 占位符、转义符和专有名词。"
            "只返回严格 JSON 对象，格式为 {\"translations\":[\"译文1\",\"译文2\"]}，"
            "数组长度和顺序必须与输入一致。"
        ),
        "skip_patterns": [
            "^https?://",
            "^[A-Za-z]:[\\\\/]",
            "^[A-Z0-9_+.-]{1,4}$"
        ]
    }

    def _live_translator_to_text(value):
        if isinstance(value, _live_translator_text_type):
            return value
        try:
            return value.decode("utf-8", "replace")
        except AttributeError:
            return _live_translator_text_type(value)

    def _live_translator_load_config():
        loaded = {}
        try:
            with _live_translator_io_module.open(
                _live_translator_config_path, "r", encoding="utf-8"
            ) as config_file:
                loaded = _live_translator_json_module.load(config_file)
        except Exception as error:
            print("LiveTranslator: config load failed: %s" % error)

        merged = dict(_live_translator_default_config)
        if isinstance(loaded, dict):
            merged.update(loaded)
        return merged

    _live_translator_config = _live_translator_load_config()
    _live_translator_cache = {}
    _live_translator_pending = set()
    _live_translator_retry_after = {}
    _live_translator_queue = _live_translator_queue_module.Queue()
    _live_translator_lock = _live_translator_threading_module.RLock()
    _live_translator_last_error = ""
    _live_translator_request_count = 0
    _live_translator_success_count = 0
    _live_translator_previous_replace_text = config.replace_text

    def _live_translator_load_cache():
        if not _live_translator_os_module.path.isfile(
            _live_translator_cache_path
        ):
            return

        try:
            with _live_translator_io_module.open(
                _live_translator_cache_path, "r", encoding="utf-8"
            ) as cache_file:
                for line in cache_file:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        record = _live_translator_json_module.loads(line)
                        source = _live_translator_to_text(record["source"])
                        translation = _live_translator_to_text(
                            record["translation"]
                        )
                        _live_translator_cache[source] = translation
                    except Exception:
                        # 单行损坏不影响其余缓存。
                        continue
        except Exception as error:
            print("LiveTranslator: cache load failed: %s" % error)

    def _live_translator_append_cache(source, translation):
        try:
            record = {
                "source": source,
                "translation": translation
            }
            serialized = _live_translator_json_module.dumps(
                record, ensure_ascii=False
            )
            with _live_translator_io_module.open(
                _live_translator_cache_path, "a", encoding="utf-8"
            ) as cache_file:
                cache_file.write(serialized)
                cache_file.write(u"\n")
        except Exception as error:
            print("LiveTranslator: cache write failed: %s" % error)

    _live_translator_load_cache()

    _live_translator_english_pattern = _live_translator_re_module.compile(
        r"[A-Za-z]"
    )
    _live_translator_skip_patterns = []
    for configured_pattern in _live_translator_config.get(
        "skip_patterns", []
    ):
        try:
            _live_translator_skip_patterns.append(
                _live_translator_re_module.compile(configured_pattern)
            )
        except Exception as error:
            print("LiveTranslator: invalid skip pattern: %s" % error)

    def _live_translator_should_translate(source):
        stripped = source.strip()
        if len(stripped) < 2:
            return False
        if not _live_translator_english_pattern.search(stripped):
            return False
        for skip_pattern in _live_translator_skip_patterns:
            if skip_pattern.search(stripped):
                return False
        return True

    def _live_translator_api_key():
        configured_key = _live_translator_config.get("api_key", "")
        if configured_key:
            return configured_key
        environment_name = _live_translator_config.get(
            "api_key_env", "REN_TRANSLATOR_API_KEY"
        )
        return _live_translator_os_module.environ.get(environment_name, "")

    def _live_translator_endpoint():
        base_url = _live_translator_config.get("base_url", "").rstrip("/")
        if base_url.endswith("/chat/completions"):
            return base_url
        return base_url + "/chat/completions"

    def _live_translator_extract_json(content):
        cleaned = _live_translator_to_text(content).strip()
        if cleaned.startswith(u"```"):
            first_newline = cleaned.find(u"\n")
            last_fence = cleaned.rfind(u"```")
            if first_newline >= 0 and last_fence > first_newline:
                cleaned = cleaned[first_newline + 1:last_fence].strip()

        object_start = cleaned.find(u"{")
        object_end = cleaned.rfind(u"}")
        if object_start >= 0 and object_end >= object_start:
            cleaned = cleaned[object_start:object_end + 1]
        return _live_translator_json_module.loads(cleaned)

    def _live_translator_request_batch(sources):
        global _live_translator_request_count

        try:
            import requests
        except ImportError:
            raise RuntimeError("游戏环境缺少 requests 模块")

        model = _live_translator_config.get("model", "")
        api_key = _live_translator_api_key()
        if not model or model == "your-model-name":
            raise RuntimeError("config.json 尚未填写 model")
        if not api_key:
            raise RuntimeError("config.json 尚未填写 api_key")

        user_content = _live_translator_json_module.dumps(
            {"texts": sources},
            ensure_ascii=False
        )
        payload = {
            "model": model,
            "messages": [
                {
                    "role": "system",
                    "content": _live_translator_config.get(
                        "system_prompt",
                        _live_translator_default_config["system_prompt"]
                    )
                },
                {
                    "role": "user",
                    "content": user_content
                }
            ],
            "temperature": float(
                _live_translator_config.get("temperature", 0.1)
            ),
            "max_tokens": int(
                _live_translator_config.get("max_output_tokens", 2400)
            )
        }
        headers = {
            "Authorization": "Bearer " + api_key,
            "Content-Type": "application/json"
        }
        timeout_seconds = float(
            _live_translator_config.get("request_timeout_seconds", 60)
        )

        _live_translator_request_count += 1
        response = requests.post(
            _live_translator_endpoint(),
            data=_live_translator_json_module.dumps(
                payload, ensure_ascii=False
            ).encode("utf-8"),
            headers=headers,
            timeout=timeout_seconds
        )
        response.raise_for_status()
        response_data = response.json()
        message_content = response_data["choices"][0]["message"]["content"]

        # 兼容部分接口返回内容块数组的形式。
        if isinstance(message_content, list):
            content_parts = []
            for content_block in message_content:
                if isinstance(content_block, dict):
                    content_parts.append(content_block.get("text", ""))
                else:
                    content_parts.append(_live_translator_to_text(content_block))
            message_content = u"".join(content_parts)

        translated_data = _live_translator_extract_json(message_content)
        translations = translated_data.get("translations")
        if not isinstance(translations, list):
            raise ValueError("API 返回缺少 translations 数组")
        if len(translations) != len(sources):
            raise ValueError("API 返回的译文数量与原文不一致")

        normalized = []
        for translation in translations:
            normalized_translation = _live_translator_to_text(
                translation
            ).strip()
            if not normalized_translation:
                raise ValueError("API 返回了空译文")
            normalized.append(normalized_translation)
        return normalized

    def _live_translator_refresh():
        if not renpy.is_init_phase():
            renpy.restart_interaction()

    def _live_translator_finish_batch(sources, translations):
        global _live_translator_success_count

        with _live_translator_lock:
            for source, translation in zip(sources, translations):
                _live_translator_cache[source] = translation
                _live_translator_pending.discard(source)
                _live_translator_retry_after.pop(source, None)
                _live_translator_append_cache(source, translation)
                _live_translator_success_count += 1

        if not renpy.is_init_phase():
            renpy.invoke_in_main_thread(_live_translator_refresh)

    def _live_translator_fail_batch(sources, error):
        global _live_translator_last_error

        cooldown = float(
            _live_translator_config.get("retry_cooldown_seconds", 30)
        )
        retry_time = _live_translator_time_module.time() + max(5.0, cooldown)
        with _live_translator_lock:
            for source in sources:
                _live_translator_pending.discard(source)
                _live_translator_retry_after[source] = retry_time
        _live_translator_last_error = _live_translator_to_text(error)
        print("LiveTranslator: request failed: %s" % error)

    def _live_translator_worker():
        while True:
            first_source = _live_translator_queue.get()
            batch = [first_source]
            batch_size = max(
                1, int(_live_translator_config.get("batch_size", 8))
            )
            wait_seconds = max(
                0.0,
                float(
                    _live_translator_config.get("batch_wait_ms", 180)
                ) / 1000.0
            )
            deadline = _live_translator_time_module.time() + wait_seconds

            while len(batch) < batch_size:
                remaining = deadline - _live_translator_time_module.time()
                if remaining <= 0:
                    break
                try:
                    batch.append(
                        _live_translator_queue.get(timeout=remaining)
                    )
                except _live_translator_queue_module.Empty:
                    break

            try:
                translations = _live_translator_request_batch(batch)
                _live_translator_finish_batch(batch, translations)
            except Exception as error:
                _live_translator_fail_batch(batch, error)

    def _live_translator_enqueue(source):
        now = _live_translator_time_module.time()
        with _live_translator_lock:
            retry_time = _live_translator_retry_after.get(source, 0)
            if source in _live_translator_pending or now < retry_time:
                return
            _live_translator_pending.add(source)
        _live_translator_queue.put(source)

    def _live_translator_replace_text(value):
        original_value = value
        if (
            _live_translator_previous_replace_text is not None
            and _live_translator_previous_replace_text
            is not _live_translator_replace_text
        ):
            try:
                original_value = _live_translator_previous_replace_text(value)
            except Exception as error:
                print("LiveTranslator: previous text hook failed: %s" % error)

        if not _live_translator_config.get("enabled", True):
            return original_value

        source = _live_translator_to_text(original_value)
        if not _live_translator_should_translate(source):
            return original_value

        with _live_translator_lock:
            cached_translation = _live_translator_cache.get(source)
        if cached_translation is not None:
            return cached_translation

        _live_translator_enqueue(source)
        pending_text = _live_translator_config.get("pending_text", "")
        if pending_text:
            return _live_translator_to_text(pending_text)
        return original_value

    def _live_translator_toggle():
        _live_translator_config["enabled"] = not _live_translator_config.get(
            "enabled", True
        )
        if _live_translator_config["enabled"]:
            renpy.notify(u"实时翻译：已开启")
        else:
            renpy.notify(u"实时翻译：已关闭")
        renpy.restart_interaction()

    def _live_translator_show_status():
        if not _live_translator_config.get("enabled", True):
            status_message = u"实时翻译已关闭"
        elif not _live_translator_api_key():
            status_message = u"实时翻译：请填写 API Key"
        elif (
            not _live_translator_config.get("model")
            or _live_translator_config.get("model") == "your-model-name"
        ):
            status_message = u"实时翻译：请填写模型名称"
        elif _live_translator_last_error:
            status_message = u"翻译错误：" + _live_translator_last_error[:120]
        else:
            status_message = (
                u"实时翻译正常：缓存 %d 条，请求 %d 次"
                % (
                    len(_live_translator_cache),
                    _live_translator_request_count
                )
            )
        renpy.notify(status_message)

    def _live_translator_apply_font():
        font_path = _live_translator_config.get("font", "")
        if (
            not font_path
            or not _live_translator_os_module.path.isfile(font_path)
        ):
            print("LiveTranslator: configured font not found: %s" % font_path)
            return

        # 同时覆盖常见 GUI 变量和样式，兼容默认及多数定制界面。
        try:
            gui.text_font = font_path
            gui.name_text_font = font_path
            gui.interface_text_font = font_path
        except Exception:
            pass

        common_style_names = [
            "default",
            "say_dialogue",
            "say_label",
            "input",
            "choice_button_text",
            "button_text",
            "label_text",
            "interface_text",
            "navigation_button_text",
            "game_menu_label_text",
            "pref_label_text",
            "pref_button_text",
            "help_button_text",
            "quick_button_text",
            "slot_button_text",
            "notify_text"
        ]
        for style_name in common_style_names:
            try:
                getattr(style, style_name).font = font_path
            except Exception:
                continue

    _live_translator_apply_font()
    config.replace_text = _live_translator_replace_text

    if "live_translator_hotkeys" not in config.overlay_screens:
        config.overlay_screens.append("live_translator_hotkeys")

    _live_translator_thread = _live_translator_threading_module.Thread(
        target=_live_translator_worker,
        name="RenPyLiveTranslator"
    )
    _live_translator_thread.daemon = True
    _live_translator_thread.start()


screen live_translator_hotkeys():
    key "K_F9" action Function(_live_translator_toggle)
    key "K_F10" action Function(_live_translator_show_status)
