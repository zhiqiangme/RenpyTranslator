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
    _live_translator_pretranslated_path = (
        _live_translator_os_module.path.join(
            _live_translator_directory, "pretranslated.jsonl"
        )
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
        "json_response_format": True,
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

    def _live_translator_log(message):
        # 某些命令行启动方式没有可刷新的控制台句柄，日志失败不能中断游戏。
        try:
            print(message)
        except Exception:
            pass

    def _live_translator_load_config():
        loaded = {}
        try:
            with _live_translator_io_module.open(
                _live_translator_config_path, "r", encoding="utf-8"
            ) as config_file:
                loaded = _live_translator_json_module.load(config_file)
        except Exception as error:
            _live_translator_log(
                "LiveTranslator: config load failed: %s" % error
            )

        merged = dict(_live_translator_default_config)
        # Ren'Py 会替换脚本环境中的 dict 类型，不能用 isinstance 判断。
        if hasattr(loaded, "items"):
            merged.update(loaded)
        else:
            _live_translator_log(
                "LiveTranslator: config root must be a JSON object"
            )
        return merged

    _live_translator_config = _live_translator_load_config()
    _live_translator_log(
        "LiveTranslator: loaded config path=%s model=%s key_present=%s"
        % (
            _live_translator_config_path,
            _live_translator_config.get("model", ""),
            bool(_live_translator_config.get("api_key", ""))
        )
    )
    _live_translator_pretranslated_cache = {}
    _live_translator_runtime_cache = {}
    _live_translator_pretranslated_normalized_cache = {}
    _live_translator_runtime_normalized_cache = {}
    _live_translator_pretranslated_normalized_conflicts = set()
    _live_translator_runtime_normalized_conflicts = set()
    _live_translator_pending = set()
    _live_translator_retry_after = {}
    _live_translator_queue = _live_translator_queue_module.Queue()
    _live_translator_lock = _live_translator_threading_module.RLock()
    _live_translator_last_error = ""
    _live_translator_request_count = 0
    _live_translator_success_count = 0
    _live_translator_previous_replace_text = config.replace_text
    _live_translator_previous_say_menu_text_filter = (
        config.say_menu_text_filter
    )

    def _live_translator_normalize_source_key(source):
        # 仅规范化查找键，不修改实际显示文本或发送给 API 的原文。
        source_text = _live_translator_to_text(source)
        # Ren'Py 会移除弯引号前用于脚本解析的反斜杠。
        source_text = source_text.replace(u"\\“", u"“")
        source_text = source_text.replace(u"\\”", u"”")
        source_text = source_text.replace(u"\\‘", u"‘")
        source_text = source_text.replace(u"\\’", u"’")
        return _live_translator_re_module.sub(
            u"[ \\t\\r\\n]+", u" ", source_text
        ).strip()

    def _live_translator_index_normalized_source(
        source,
        translation,
        normalized_cache,
        normalized_conflicts
    ):
        normalized_key = _live_translator_normalize_source_key(source)
        if not normalized_key or normalized_key in normalized_conflicts:
            return

        indexed_record = normalized_cache.get(normalized_key)
        if indexed_record is None:
            normalized_cache[normalized_key] = (source, translation)
            return

        indexed_source, indexed_translation = indexed_record
        if indexed_source == source or indexed_translation == translation:
            normalized_cache[normalized_key] = (source, translation)
            return

        # 仅空格不同却对应不同译文时禁止兜底，避免误匹配到另一句。
        normalized_cache.pop(normalized_key, None)
        normalized_conflicts.add(normalized_key)

    def _live_translator_load_cache(
        cache_path,
        cache_name,
        target_cache,
        target_normalized_cache,
        target_normalized_conflicts
    ):
        if not _live_translator_os_module.path.isfile(cache_path):
            return 0

        loaded_count = 0
        try:
            with _live_translator_io_module.open(
                cache_path, "r", encoding="utf-8"
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
                        target_cache[source] = translation
                        _live_translator_index_normalized_source(
                            source,
                            translation,
                            target_normalized_cache,
                            target_normalized_conflicts
                        )
                        loaded_count += 1
                    except Exception:
                        # 单行损坏不影响其余缓存。
                        continue
        except Exception as error:
            _live_translator_log(
                "LiveTranslator: %s load failed: %s"
                % (cache_name, error)
            )
        return loaded_count

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
            _live_translator_log(
                "LiveTranslator: cache write failed: %s" % error
            )

    # 先加载离线预翻译，再加载运行时缓存；后者可覆盖预翻译，便于人工修正。
    _live_translator_pretranslated_count = _live_translator_load_cache(
        _live_translator_pretranslated_path,
        "pretranslated cache",
        _live_translator_pretranslated_cache,
        _live_translator_pretranslated_normalized_cache,
        _live_translator_pretranslated_normalized_conflicts
    )
    _live_translator_runtime_cache_count = _live_translator_load_cache(
        _live_translator_cache_path,
        "runtime cache",
        _live_translator_runtime_cache,
        _live_translator_runtime_normalized_cache,
        _live_translator_runtime_normalized_conflicts
    )
    _live_translator_log(
        "LiveTranslator: loaded pretranslated=%d runtime_cache=%d"
        % (
            _live_translator_pretranslated_count,
            _live_translator_runtime_cache_count
        )
    )

    _live_translator_english_pattern = _live_translator_re_module.compile(
        r"[A-Za-z]"
    )
    _live_translator_chinese_pattern = _live_translator_re_module.compile(
        u"[\u3400-\u9fff]"
    )
    _live_translator_save_date_pattern = _live_translator_re_module.compile(
        r"^(?:(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),"
        r"\s+)?(?:January|February|March|April|May|June|July|August|"
        r"September|October|November|December)\s+\d{1,2}\s+\d{4},?"
        r"\s+\d{1,2}:\d{2}$"
    )
    _live_translator_save_slot_pattern = _live_translator_re_module.compile(
        r"^Save Slot(?:\s+\d+)?\s*$"
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
            _live_translator_log(
                "LiveTranslator: invalid skip pattern: %s" % error
            )

    def _live_translator_should_translate(source):
        stripped = source.strip()
        if len(stripped) < 2:
            return False
        # 中文译文会保留英文人名，不能因此再次送入 API 反向翻译。
        if _live_translator_chinese_pattern.search(stripped):
            return False
        if not _live_translator_english_pattern.search(stripped):
            return False
        for skip_pattern in _live_translator_skip_patterns:
            if skip_pattern.search(stripped):
                return False
        return True

    def _live_translator_is_save_metadata(source):
        stripped = source.strip()
        if _live_translator_save_date_pattern.match(stripped):
            return True
        if _live_translator_save_slot_pattern.match(source):
            return True

        # 已保存的自定义名称只用于辨识存档，不属于待翻译文案。
        try:
            for save_name in getattr(persistent, "save_name", []):
                if (
                    save_name
                    and stripped
                    == _live_translator_to_text(save_name).strip()
                ):
                    return True
        except Exception:
            pass

        # 重命名时 Input 会把光标两侧文本片段送入 replace_text。
        try:
            if (
                renpy.get_screen("save_name") is not None
                or renpy.get_screen("load_name") is not None
            ):
                return True
        except Exception:
            pass
        return False

    def _live_translator_api_key():
        configured_key = _live_translator_to_text(
            _live_translator_config.get("api_key", "")
        ).strip()
        # 示例占位文本不应触发无效请求；仍允许环境变量提供真实密钥。
        if configured_key and u"这里填写" not in configured_key:
            return configured_key
        environment_name = _live_translator_config.get(
            "api_key_env", "REN_TRANSLATOR_API_KEY"
        )
        return _live_translator_to_text(
            _live_translator_os_module.environ.get(environment_name, "")
        ).strip()

    def _live_translator_runtime_api_ready():
        model = _live_translator_to_text(
            _live_translator_config.get("model", "")
        ).strip()
        return bool(
            _live_translator_api_key()
            and model
            and model != "your-model-name"
        )

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
        if _live_translator_config.get("json_response_format", True):
            payload["response_format"] = {"type": "json_object"}

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
        if (
            not isinstance(message_content, _live_translator_text_type)
            and hasattr(message_content, "append")
        ):
            content_parts = []
            for content_block in message_content:
                if hasattr(content_block, "get"):
                    content_parts.append(content_block.get("text", ""))
                else:
                    content_parts.append(_live_translator_to_text(content_block))
            message_content = u"".join(content_parts)

        translated_data = _live_translator_extract_json(message_content)
        translations = translated_data.get("translations")
        if (
            translations is None
            or isinstance(translations, _live_translator_text_type)
            or not hasattr(translations, "__iter__")
        ):
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
                _live_translator_runtime_cache[source] = translation
                _live_translator_index_normalized_source(
                    source,
                    translation,
                    _live_translator_runtime_normalized_cache,
                    _live_translator_runtime_normalized_conflicts
                )
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
        _live_translator_log("LiveTranslator: request failed: %s" % error)

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
        # 没有有效 API 配置时保留英文原文，也不创建失败重试任务。
        if not _live_translator_runtime_api_ready():
            return
        now = _live_translator_time_module.time()
        with _live_translator_lock:
            retry_time = _live_translator_retry_after.get(source, 0)
            if source in _live_translator_pending or now < retry_time:
                return
            _live_translator_pending.add(source)
        _live_translator_queue.put(source)

    def _live_translator_lookup(source):
        # 精确匹配优先；同一级别中运行时缓存优先，便于用户覆盖预翻译。
        with _live_translator_lock:
            cached_translation = _live_translator_runtime_cache.get(source)
            if cached_translation is None:
                cached_translation = (
                    _live_translator_pretranslated_cache.get(source)
                )
            if cached_translation is not None:
                return cached_translation

            # Ren'Py 可能折叠空格或移除引号转义，使用规范化键安全兜底。
            normalized_key = _live_translator_normalize_source_key(source)
            normalized_record = (
                _live_translator_runtime_normalized_cache.get(normalized_key)
            )
            if normalized_record is None:
                normalized_record = (
                    _live_translator_pretranslated_normalized_cache.get(
                        normalized_key
                    )
                )
            if normalized_record is not None:
                cached_translation = normalized_record[1]
        return cached_translation

    def _live_translator_lookup_pretranslated(source):
        # 存档界面只允许固定文案命中预制库，不读取动态名称的 API 缓存。
        with _live_translator_lock:
            cached_translation = (
                _live_translator_pretranslated_cache.get(source)
            )
            if cached_translation is not None:
                return cached_translation

            normalized_key = _live_translator_normalize_source_key(source)
            normalized_record = (
                _live_translator_pretranslated_normalized_cache.get(
                    normalized_key
                )
            )
            if normalized_record is not None:
                return normalized_record[1]
        return None

    def _live_translator_font_path(fallback):
        font_path = _live_translator_config.get("font", "")
        if font_path and _live_translator_os_module.path.isfile(font_path):
            return font_path
        return fallback

    def _live_translator_confirm_message(message):
        # Ren'Py 的内置确认文本可能先逐句翻译，再用换行拼成完整消息。
        message_text = _live_translator_to_text(message)
        translated_parts = []
        for part in _live_translator_re_module.split(u"(\n)", message_text):
            if not part or part == u"\n":
                translated_parts.append(part)
                continue

            cached_translation = _live_translator_lookup(part)
            if cached_translation is not None:
                translated_parts.append(cached_translation)
                continue

            translated_parts.append(part)
            if _live_translator_should_translate(part):
                _live_translator_enqueue(part)

        return u"".join(translated_parts)

    def _live_translator_replace_say_menu_text(value):
        # 此过滤器在文本标签和 [变量] 展开前运行，保证整句精确命中。
        original_value = value
        if (
            _live_translator_previous_say_menu_text_filter is not None
            and _live_translator_previous_say_menu_text_filter
            is not _live_translator_replace_say_menu_text
        ):
            try:
                original_value = (
                    _live_translator_previous_say_menu_text_filter(value)
                )
            except Exception as error:
                _live_translator_log(
                    "LiveTranslator: previous say/menu filter failed: %s"
                    % error
                )
        if not _live_translator_config.get("enabled", True):
            return original_value
        source = _live_translator_to_text(value)
        cached_translation = _live_translator_lookup(source)
        if cached_translation is not None:
            return cached_translation
        return original_value

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
                _live_translator_log(
                    "LiveTranslator: previous text hook failed: %s" % error
                )

        if not _live_translator_config.get("enabled", True):
            return original_value

        source = _live_translator_to_text(original_value)
        if not _live_translator_should_translate(source):
            return original_value

        if _live_translator_is_save_metadata(source):
            pretranslated_text = (
                _live_translator_lookup_pretranslated(source)
            )
            if pretranslated_text is not None:
                return pretranslated_text
            return original_value

        cached_translation = _live_translator_lookup(source)
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
                u"实时翻译正常：预翻译 %d 条，缓存 %d 条，请求 %d 次"
                % (
                    _live_translator_pretranslated_count,
                    _live_translator_runtime_cache_count
                    + _live_translator_success_count,
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
            _live_translator_log(
                "LiveTranslator: configured font not found: %s" % font_path
            )
            return

        # 同时覆盖常见 GUI 变量和样式，兼容默认及多数定制界面。
        try:
            gui.text_font = font_path
            gui.name_text_font = font_path
            gui.interface_text_font = font_path
        except Exception:
            pass

        # 字体替换在字形加载阶段生效，可覆盖屏幕中硬编码的英文字体，
        # 不会像动态文本标签那样污染对话、存档名和截图正文。
        replacement_fonts = [
            "Fonts/EnterCommand.ttf",
            "Fonts/Overspray.ttf",
            "fonts/TT2020StyleE-Regular.ttf"
        ]
        for source_font in replacement_fonts:
            for bold in (False, True):
                for italics in (False, True):
                    config.font_replacement_map[
                        (source_font, bold, italics)
                    ] = (font_path, bold, italics)

        common_style_names = [
            "default",
            "say_dialogue",
            "say_label",
            "input",
            "choice_button_text",
            # Camp Buddy 的四块木牌选项使用独立样式和 Overspray 字体。
            "choice_button_text1",
            "choice_button_text2",
            "choice_button_text3",
            "choice_button_text4",
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
            "notify_text",
            # Camp Buddy 的确认框正文使用独立字体样式。
            "text_confirm",
            # Camp Buddy 的聊天记录使用独立样式，默认 Halogen 字体不含中文字形。
            "readback_text",
            "readback_button_text"
        ]
        for style_name in common_style_names:
            try:
                getattr(style, style_name).font = font_path
            except Exception:
                continue

    def _live_translator_readback_adjustment_change(
        self, value, *args, **kwargs
    ):
        # 游戏旧版聊天记录没有接收 Ren'Py 7.8 惯性滚动新增的 end_animation 参数。
        return renpy.display.behavior.Adjustment.change(
            self, value, *args, **kwargs
        )

    def _live_translator_patch_readback_adjustment():
        readback_adjustment_class = globals().get("NewAdj")
        if readback_adjustment_class is None:
            return
        readback_adjustment_class.change = (
            _live_translator_readback_adjustment_change
        )
        _live_translator_log(
            "LiveTranslator: patched readback adjustment for Ren'Py 7.8"
        )

    _live_translator_apply_font()
    _live_translator_patch_readback_adjustment()
    config.say_menu_text_filter = _live_translator_replace_say_menu_text
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


# 游戏原确认屏幕将正文绑定到不含中文字形的 TT2020 字体。
# 在模组文件中重定义同名屏幕，并为正文直接指定已配置的中文字体。
screen confirm(message, yes_action, no_action):
    modal True
    zorder 1002
    style_prefix "confirm"

    add "images/interface/dark_overlay.png"
    add "images/interface/confirm/confirm_box.png" xalign 0.5 yalign 0.5

    text _live_translator_confirm_message(message):
        style "text_confirm"
        font _live_translator_font_path("fonts/TT2020StyleE-Regular.ttf")
        xalign 0.5
        yalign 0.5
        xmaximum 800
        yoffset -65

    hbox:
        xalign 0.5
        yalign 0.5
        yoffset 180
        spacing 150

        imagebutton:
            xmaximum 295
            ymaximum 121
            activate_sound "Audio/Buttons/button_accept.ogg"
            idle "images/interface/confirm/confirm_button_yes_idle.png"
            hover "images/interface/confirm/confirm_button_yes_hover.png"
            action [Function(sendCommandToActiveToy, {"command":"Stop"}), yes_action]

        imagebutton:
            xmaximum 295
            ymaximum 121
            activate_sound "Audio/Buttons/button_back.ogg"
            idle "images/interface/confirm/confirm_button_no_idle.png"
            hover "images/interface/confirm/confirm_button_no_hover.png"
            action no_action

    key "game_menu" action no_action
