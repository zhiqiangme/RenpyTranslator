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

    from collections import deque as _live_translator_deque
    from renpy.text.text import Text as _live_translator_text_displayable

    try:
        import __builtin__ as _live_translator_builtins_module
    except ImportError:
        import builtins as _live_translator_builtins_module

    # Ren'Py 把脚本里的 dict/set 及字面量替换成可回滚容器，修改时会写入回滚日志
    # 并复制整个容器；缓存会在后台线程频繁修改，因此一律使用 Python 原生容器。
    _live_translator_plain_dict = _live_translator_builtins_module.dict
    _live_translator_plain_set = _live_translator_builtins_module.set

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

    _live_translator_name_prompt_rule = (
        u"所有人物姓名必须保持英文原样，不得翻译、音译或改写。"
    )

    _live_translator_default_config = {
        "enabled": True,
        "base_url": "https://api.openai.com/v1",
        "api_key": "",
        "api_key_encrypted": "",
        "model": "your-model-name",
        "font": "C:/Windows/Fonts/msyh.ttc",
        "batch_size": 8,
        "batch_wait_ms": 180,
        "request_timeout_seconds": 60,
        "retry_cooldown_seconds": 30,
        "temperature": 0.1,
        "max_output_tokens": 2400,
        "thinking_enabled": False,
        "json_response_format": True,
        "pending_text": "",
        "system_prompt": (
            "你是视觉小说本地化译者。把输入数组中的英文逐条翻译成自然、简洁的"
            "简体中文，保持人物语气、称谓、情绪和原意，不审查、不解释。"
            "所有人物姓名必须保持英文原样，不得翻译、音译或改写。"
            "原样保留 Ren'Py 文本标签、方括号变量、printf 占位符、转义符和专有名词。"
            "只返回严格 JSON 对象，格式为 {\"translations\":[\"译文1\",\"译文2\"]}，"
            "数组长度和顺序必须与输入一致。"
        ),
        "protected_names": [
            "Aiden", "Andre", "Darius", "Emilia", "Emillia", "Goro", "Hiro",
            "Hunter", "Hyunjin", "Justin", "Keitaro", "Lloyd", "Naoto",
            "Natsumi", "Reimond", "Taiga", "Vera", "William", "Yoichi",
            "Yoshinori", "Yuri", "Yoshi", "Jin", "Dar", "Sirius",
            "Najjar", "Nagira", "Nomoru", "Yoshinori Nagira",
            "Yuri Nomoru"
        ],
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

        merged = _live_translator_plain_dict(_live_translator_default_config)
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
    _live_translator_pretranslated_cache = _live_translator_plain_dict()
    _live_translator_runtime_cache = _live_translator_plain_dict()
    _live_translator_pretranslated_normalized_cache = (
        _live_translator_plain_dict()
    )
    _live_translator_runtime_normalized_cache = _live_translator_plain_dict()
    _live_translator_pending = _live_translator_plain_set()
    _live_translator_retry_after = _live_translator_plain_dict()
    # 原文 -> (各类失败次数，当前原因，诊断)。网络超时不能耗掉单句原样返回的重试机会。
    _live_translator_failures = _live_translator_plain_dict()
    # 曾在多条批次中出错的原文改为单独请求，避免一条坏文本拖累整批。
    _live_translator_isolated = _live_translator_plain_set()
    _live_translator_queue = _live_translator_deque()
    _live_translator_lock = _live_translator_threading_module.Condition()
    # 运行状态放在原生字典中原地修改：重新赋值 store 变量会让 Ren'Py 把它写进存档并参与回滚。
    _live_translator_state = _live_translator_plain_dict(
        generation=0,
        request_count=0,
        success_count=0
    )
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
        # 脚本中的 \% 在不同文本阶段可能变成 % 或 %%。
        source_text = source_text.replace(u"\\%", u"%")
        source_text = source_text.replace(u"%%", u"%")
        # Ren'Py 运行时可能把标点换成全角形式，统一映射为半角以便兜底命中。
        source_text = source_text.replace(u"！", u"!")
        source_text = source_text.replace(u"？", u"?")
        source_text = source_text.replace(u"，", u",")
        source_text = source_text.replace(u"。", u".")
        source_text = source_text.replace(u"：", u":")
        source_text = source_text.replace(u"；", u";")
        source_text = source_text.replace(u"（", u"(")
        source_text = source_text.replace(u"）", u")")
        source_text = source_text.replace(u"～", u"~")
        source_text = source_text.replace(u"“", u'"')
        source_text = source_text.replace(u"”", u'"')
        source_text = source_text.replace(u"‘", u"'")
        source_text = source_text.replace(u"’", u"'")
        source_text = source_text.replace(u"\u3000", u" ")
        # 中文习惯用两个省略号，英文用一个；折叠成单个便于匹配。
        source_text = _live_translator_re_module.sub(
            u"…+", u"…", source_text
        )
        return _live_translator_re_module.sub(
            u"[ \\t\\r\\n]+", u" ", source_text
        ).strip()

    def _live_translator_index_normalized_source(
        source,
        translation,
        normalized_cache
    ):
        normalized_key = _live_translator_normalize_source_key(source)
        if not normalized_key:
            return

        indexed_record = normalized_cache.get(normalized_key)
        if indexed_record is None:
            normalized_cache[normalized_key] = (source, translation)
            return

        indexed_source, indexed_translation = indexed_record
        if indexed_source == source or indexed_translation == translation:
            normalized_cache[normalized_key] = (source, translation)
            return

        # 归一化后同一键但译文不同的冲突：保留第一条用于兜底，避免
        # 运行时空白/标点变体因无精确命中而白白调用 API。若运行时文本
        # 精确命中库中某条变体，仍走精确匹配，不受此兜底影响。
        pass

    def _live_translator_load_cache(
        cache_path,
        cache_name,
        target_cache,
        target_normalized_cache,
        runtime=False
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
                        if runtime and _live_translator_is_echo(source, translation):
                            continue
                        target_cache[source] = translation
                        _live_translator_index_normalized_source(
                            source,
                            translation,
                            target_normalized_cache
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
        r"^Save Slot(?:\s+([1-9]\d{0,2}))?\s*$"
    )
    _live_translator_weekday_pattern = _live_translator_re_module.compile(
        r"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)"
        r"(,)?\s*$"
    )
    _live_translator_weekday_translations = {
        "Monday": u"星期一",
        "Tuesday": u"星期二",
        "Wednesday": u"星期三",
        "Thursday": u"星期四",
        "Friday": u"星期五",
        "Saturday": u"星期六",
        "Sunday": u"星期日"
    }
    # 引擎找不到图片时的运行时错误提示，图名是动态的，需本地确定性翻译，
    # 避免每张缺失图都触发一次 API 调用；兼容直引号/弯引号及有无句号。
    _live_translator_image_not_found_pattern = (
        _live_translator_re_module.compile(
            u"^Image\\s+[\u0027\u2018\u2019]"
            u"([^\u0027\u2018\u2019]+)"
            u"[\u0027\u2018\u2019]\\s+not found\\.?\\s*$"
        )
    )
    _live_translator_text_tag_pattern = _live_translator_re_module.compile(
        u"\\{[^{}]*\\}"
    )
    _live_translator_name_punctuation_pattern = (
        _live_translator_re_module.compile(
            u"[\\s\\.,!?…，。！？、~:;：；'\"“”‘’()（）\\[\\]<>《》【】—–-]+"
        )
    )
    _live_translator_protected_names = set()
    for configured_name in _live_translator_config.get(
        "protected_names", []
    ):
        normalized_name = _live_translator_name_punctuation_pattern.sub(
            u"",
            _live_translator_to_text(configured_name)
        ).lower()
        if normalized_name:
            _live_translator_protected_names.add(normalized_name)
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

    def _live_translator_is_protected_name_text(source):
        # 纯人名及其标点属于显示文本，直接保留英文且不占用 API。
        without_tags = _live_translator_text_tag_pattern.sub(u"", source)
        normalized_name = _live_translator_name_punctuation_pattern.sub(
            u"", without_tags
        ).lower()
        return normalized_name in _live_translator_protected_names

    def _live_translator_local_save_slot_translation(source):
        # 存档位编号是动态元数据，本地确定性翻译，避免 999 条缓存和 API 调用。
        slot_match = _live_translator_save_slot_pattern.match(source)
        if slot_match is None:
            return None
        slot_number = slot_match.group(1)
        if slot_number:
            return u"存档位 %s" % slot_number
        return u"存档位"

    def _live_translator_local_weekday_translation(source):
        # 存档界面可能把星期单独传入；本地转换，完整日期仍保持原文。
        weekday_match = _live_translator_weekday_pattern.match(source)
        if weekday_match is None:
            return None
        translated_weekday = _live_translator_weekday_translations.get(
            weekday_match.group(1)
        )
        if translated_weekday is None:
            return None
        if weekday_match.group(2):
            return translated_weekday + u"，"
        return translated_weekday

    def _live_translator_local_image_not_found_translation(source):
        # 引擎报错 "Image 'xxx' not found." 时按动态图名本地直译，
        # 不查询翻译库也不调用 API。
        image_match = _live_translator_image_not_found_pattern.match(source)
        if image_match is None:
            return None
        return u"找不到图片 '%s'" % image_match.group(1)

    def _live_translator_should_translate(source):
        stripped = source.strip()
        if len(stripped) < 2:
            return False
        # 中文译文会保留英文人名，不能因此再次送入 API 反向翻译。
        if _live_translator_chinese_pattern.search(stripped):
            return False
        if _live_translator_is_protected_name_text(stripped):
            return False
        if not _live_translator_english_pattern.search(stripped):
            return False
        for skip_pattern in _live_translator_skip_patterns:
            if skip_pattern.search(stripped):
                return False
        return True

    def _live_translator_is_echo(source, translation):
        if source.strip() != translation.strip() or not _live_translator_should_translate(source):
            return False
        # 标签、插值和 printf 占位符不参与判断。只拦截有普通小写词的英文句子，
        # 让姓名、全大写缩写和短标签仍可原样返回。
        visible = _live_translator_re_module.sub(
            r"\{[^{}]*\}|\[[^\]]*\]|%(?:\([^)]+\))?[-+#0-9.*]*[a-zA-Z%]", " ", source
        ).strip()
        if _live_translator_is_protected_name_text(visible):
            return False
        words = _live_translator_re_module.findall(r"[A-Za-z]+(?:'[A-Za-z]+)?", visible)
        if len(words) < 2 or not any(word.islower() for word in words):
            return False
        if words[0][0].isupper() and words[-1][0].isupper() and all(
            word[0].isupper() or word in ("de", "del", "der", "van", "von", "of", "the", "and")
            for word in words
        ):
            return False
        if visible.rstrip(u"\"'”’)").endswith((u".", u"?", u"!", u"…", u"。", u"？", u"！")):
            return True
        return any(word.lower() in ("i", "we", "you", "they", "the", "this", "that", "please")
                   for word in words)

    # 人工预译文优先。旧缓存只在读入时过滤，已有的有效译文和磁盘文件均可保留。
    _live_translator_pretranslated_count = _live_translator_load_cache(
        _live_translator_pretranslated_path, "pretranslated cache",
        _live_translator_pretranslated_cache, _live_translator_pretranslated_normalized_cache
    )
    _live_translator_runtime_cache_count = _live_translator_load_cache(
        _live_translator_cache_path, "runtime cache",
        _live_translator_runtime_cache, _live_translator_runtime_normalized_cache, runtime=True
    )
    _live_translator_log("LiveTranslator: loaded pretranslated=%d runtime_cache=%d" % (
        _live_translator_pretranslated_count, _live_translator_runtime_cache_count
    ))

    def _live_translator_is_save_metadata(source):
        stripped = source.strip()
        if _live_translator_save_date_pattern.match(stripped):
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

    def _live_translator_decrypt_api_key(encrypted_text):
        # 使用 Windows DPAPI 解密安装脚本写入的 API Key，
        # 密钥绑定当前 Windows 用户，仅本机本用户可解密。
        try:
            import ctypes as _ctypes_module
            from ctypes import wintypes as _wintypes_module
            import base64 as _base64_module

            class _LiveTranslatorBlob(_ctypes_module.Structure):
                _fields_ = [
                    ("cbData", _wintypes_module.DWORD),
                    ("pbData", _ctypes_module.c_void_p)
                ]

            encrypted_bytes = _base64_module.b64decode(
                encrypted_text.encode("ascii")
            )
            buffer = _ctypes_module.create_string_buffer(
                encrypted_bytes, len(encrypted_bytes)
            )
            blob_in = _LiveTranslatorBlob(
                len(encrypted_bytes),
                _ctypes_module.cast(
                    buffer, _ctypes_module.c_void_p
                )
            )
            blob_out = _LiveTranslatorBlob(0, None)
            crypt32 = _ctypes_module.windll.crypt32
            # 显式声明签名，保证 64 位指针按引用正确传递。
            _CryptUnprotectData = crypt32.CryptUnprotectData
            _CryptUnprotectData.argtypes = [
                _ctypes_module.POINTER(_LiveTranslatorBlob),
                _ctypes_module.c_void_p,
                _ctypes_module.c_void_p,
                _ctypes_module.c_void_p,
                _ctypes_module.c_void_p,
                _wintypes_module.DWORD,
                _ctypes_module.POINTER(_LiveTranslatorBlob)
            ]
            _CryptUnprotectData.restype = _wintypes_module.BOOL
            if not _CryptUnprotectData(
                _ctypes_module.byref(blob_in),
                None,
                None,
                None,
                None,
                0,
                _ctypes_module.byref(blob_out)
            ):
                return u""
            try:
                data_size = int(blob_out.cbData)
                decrypted_bytes = _ctypes_module.string_at(
                    blob_out.pbData, data_size
                )
                return decrypted_bytes.decode("utf-8", "replace")
            finally:
                if blob_out.pbData:
                    # 显式声明参数类型，避免 64 位指针被当作 int 截断。
                    _LocalFree = _ctypes_module.windll.kernel32.LocalFree
                    _LocalFree.argtypes = [_wintypes_module.HLOCAL]
                    _LocalFree.restype = _wintypes_module.HLOCAL
                    _LocalFree(blob_out.pbData)
        except Exception:
            return u""

    def _live_translator_resolve_api_key():
        # 优先读取 DPAPI 加密的密钥；明文 api_key 字段仅作兼容回退。
        encrypted_key = _live_translator_to_text(
            _live_translator_config.get("api_key_encrypted", "")
        ).strip()
        if encrypted_key:
            decrypted_key = _live_translator_decrypt_api_key(encrypted_key)
            if decrypted_key:
                return decrypted_key
            _live_translator_log(
                u"LiveTranslator: DPAPI 解密 API Key 失败，"
                u"请在汉化管理器中重新填写密钥"
            )
            return u""
        configured_key = _live_translator_to_text(
            _live_translator_config.get("api_key", "")
        ).strip()
        if configured_key and u"这里填写" not in configured_key:
            return configured_key
        return u""

    # 只缓存在原生字典中，绝不写入 store 变量，避免明文密钥进入存档。
    _live_translator_api_key_cache = _live_translator_plain_dict()

    def _live_translator_api_key():
        # 主线程渲染每条未命中文本都会检查密钥；配置只在启动时读取，解密一次即可复用，
        # 避免反复调用 DPAPI 拖慢渲染，解密失败时也只记录一次日志。
        if "value" not in _live_translator_api_key_cache:
            _live_translator_api_key_cache["value"] = (
                _live_translator_resolve_api_key()
            )
        return _live_translator_api_key_cache["value"]

    def _live_translator_runtime_api_ready():
        model = _live_translator_to_text(
            _live_translator_config.get("model", "")
        ).strip()
        return bool(
            _live_translator_api_key()
            and model
            and model != "your-model-name"
        )

    # Claude 官方地址走 Anthropic Messages API，其余地址按 OpenAI 兼容接口处理。
    _live_translator_anthropic_host = "api.anthropic.com"
    _live_translator_anthropic_version = "2023-06-01"
    # Claude 结构化输出的 JSON Schema，保证返回体一定是 {"translations": [...]}。
    _live_translator_translation_schema = {
        "type": "object",
        "properties": {
            "translations": {"type": "array", "items": {"type": "string"}}
        },
        "required": ["translations"],
        "additionalProperties": False
    }

    def _live_translator_uses_anthropic():
        return _live_translator_api_host() == _live_translator_anthropic_host

    def _live_translator_endpoint():
        base_url = _live_translator_config.get("base_url", "").rstrip("/")
        suffix = (
            "/messages" if _live_translator_uses_anthropic()
            else "/chat/completions"
        )
        if base_url.endswith(suffix):
            return base_url
        return base_url + suffix

    def _live_translator_anthropic_payload(
        model, system_prompt, user_content, thinking_enabled
    ):
        # 与管理器 Api.AnthropicPayload 保持同一规则。
        # 当前 Claude 模型拒绝非默认 temperature，因此不发送；思考深度改用 effort 控制：
        # Opus 5.5、Sonnet 5.5 不接受关闭思考，low 是所有当前模型都接受的最省方式。
        payload = _live_translator_plain_dict({
            "model": model,
            "max_tokens": int(
                _live_translator_config.get("max_output_tokens", 2400)
            ),
            "system": system_prompt,
            "messages": [{"role": "user", "content": user_content}]
        })
        output_config = _live_translator_plain_dict()
        if not thinking_enabled:
            output_config["effort"] = "low"
        if _live_translator_config.get("json_response_format", True):
            output_config["format"] = {
                "type": "json_schema",
                "schema": _live_translator_translation_schema
            }
        if output_config:
            payload["output_config"] = output_config
        return payload

    def _live_translator_anthropic_text(response_data):
        # 单句明确拒绝后停止；回复可能以 thinking 块开头，只取 text 块。
        if response_data.get("stop_reason") == "refusal":
            raise _LiveTranslatorRefusal(u"Claude 拒绝翻译这批文本")
        return u"".join(
            _live_translator_to_text(block.get("text", u""))
            for block in response_data.get("content") or []
            if hasattr(block, "get") and block.get("type") == "text"
        )

    # 已确认接受 thinking 字段的服务商主机；OpenAI、Gemini 等严格接口
    # 遇到未知参数会直接返回 HTTP 400，因此不能无条件发送。
    _live_translator_thinking_hosts = (
        "api.deepseek.com",
        "open.bigmodel.cn"
    )
    _live_translator_thinking_host_suffixes = (".volces.com",)
    _live_translator_host_pattern = _live_translator_re_module.compile(
        r"^[A-Za-z][A-Za-z0-9+.-]*://(?:[^/@]*@)?([^/:?#]+)"
    )

    def _live_translator_api_host():
        host_match = _live_translator_host_pattern.match(
            _live_translator_to_text(
                _live_translator_config.get("base_url", "")
            ).strip()
        )
        if host_match is None:
            return u""
        return host_match.group(1).lower()

    def _live_translator_apply_provider_options(payload, host, thinking_enabled):
        # 与管理器 Api.ApplyProviderOptions 保持同一规则。
        if host == "api.openai.com":
            # OpenAI 推理模型（GPT-5/6、o 系列等）拒绝已弃用的 max_tokens、非默认
            # temperature 与扩展字段 thinking；省略 temperature 与 thinking 并改用
            # max_completion_tokens 可兼容全部 OpenAI 新旧模型。
            if "max_tokens" in payload:
                payload["max_completion_tokens"] = payload.pop("max_tokens")
            payload.pop("temperature", None)
            return payload
        supports_thinking = host in _live_translator_thinking_hosts or any(
            host.endswith(suffix)
            for suffix in _live_translator_thinking_host_suffixes
        )
        # 已知服务商显式关闭思考以降低延迟；其他接口仅在用户主动开启时发送。
        # 文档：https://api-docs.deepseek.com/zh-cn/guides/thinking_mode
        if supports_thinking or thinking_enabled:
            payload["thinking"] = {
                "type": "enabled" if thinking_enabled else "disabled"
            }
        return payload

    # 服务商内容风控常见特征词（小写比对）：用于将 HTTP 400/422 的内容拦截归类为
    # _LiveTranslatorContentError，从而触发自动拆批隔离并避免单条敏感文本拖累整批。
    _live_translator_content_block_markers = (
        u"content exists risk",
        u"content_policy_violation",
        u"content policy",
        u"content_filter",
        u"datainspectionfailed",
        u"inappropriate content",
        u"sensitive content",
        u"敏感内容",
        u"内容安全",
        u"违规内容",
        u"审核未通过"
    )

    def _live_translator_sanitize_error_detail(text, api_key=u""):
        cleaned = _live_translator_re_module.sub(
            u"[\\r\\n\\t ]+", u" ", _live_translator_to_text(text)
        ).strip()
        if api_key:
            key_text = _live_translator_to_text(api_key).strip()
            if key_text and key_text in cleaned:
                cleaned = cleaned.replace(key_text, u"***")
        if len(cleaned) > 200:
            cleaned = cleaned[:200].rstrip() + u"…"
        return cleaned

    def _live_translator_is_content_block(text):
        lowered = _live_translator_to_text(text).lower()
        for marker in _live_translator_content_block_markers:
            if marker in lowered:
                return True
        return False

    def _live_translator_extract_api_error(response_text, api_key=u""):
        # 仅解析结构化 JSON 错误字段，避免把 HTML 错误页或长响应正文直接展示给用户。
        raw = _live_translator_to_text(response_text or u"").strip()
        if not raw:
            return u"", False
        try:
            parsed = _live_translator_json_module.loads(raw)
        except Exception:
            return u"", False
        if not hasattr(parsed, "get"):
            return u"", False

        parts = []
        message = u""
        error_field = parsed.get("error")
        if hasattr(error_field, "get"):
            for key in ("code", "type", "message"):
                val = error_field.get(key)
                if isinstance(val, (_live_translator_text_type, str)):
                    val_text = _live_translator_to_text(val).strip()
                    if val_text:
                        parts.append(val_text)
                        if key == "message":
                            message = val_text
        elif isinstance(error_field, (_live_translator_text_type, str)):
            message = _live_translator_to_text(error_field).strip()
            if message:
                parts.append(message)

        if not message:
            for key in ("message", "detail"):
                val = parsed.get(key)
                if isinstance(val, (_live_translator_text_type, str)):
                    val_text = _live_translator_to_text(val).strip()
                    if val_text:
                        message = val_text
                        parts.append(val_text)
                        break

        combined = u" ".join(parts)
        detail = _live_translator_sanitize_error_detail(
            message or combined, api_key
        )
        return detail, _live_translator_is_content_block(combined)

    def _live_translator_raise_http_error(response, status_code, api_key=u""):
        response_text = u""
        try:
            raw_text = getattr(response, "text", u"")
            if isinstance(raw_text, (_live_translator_text_type, str)):
                response_text = raw_text
        except Exception:
            response_text = u""
        detail, is_content_blocked = _live_translator_extract_api_error(
            response_text, api_key
        )
        # 仅在 400/422 上归类为内容风控，避免把 401/403/404/429/5xx 误判为文本内容问题。
        if status_code in (400, 422) and is_content_blocked:
            if detail:
                raise _LiveTranslatorRefusal(
                    u"触发服务商内容审查（HTTP %d：%s）"
                    % (status_code, detail)
                )
            raise _LiveTranslatorRefusal(
                u"触发服务商内容审查（HTTP %d）" % status_code
            )
        if status_code in (408, 429) or 500 <= status_code < 600:
            message = u"HTTP %d" % status_code
            if detail:
                message += u"：%s" % detail
            error = _LiveTranslatorTemporaryError(message)
            # 服务商给出等待秒数时，采用它和本地冷却时间中的较大值。
            value = getattr(response, "headers", {}).get("Retry-After", "")
            if isinstance(value, (_live_translator_text_type, str)) and value.strip().isdigit():
                error.retry_seconds = int(value)
            raise error
        if detail:
            raise RuntimeError(u"HTTP %d：%s" % (status_code, detail))
        response.raise_for_status()
        raise RuntimeError(u"HTTP %d" % status_code)

    def _live_translator_openai_text(response_data):
        choices = (
            response_data.get("choices")
            if hasattr(response_data, "get")
            else None
        )
        if not choices or not hasattr(choices, "__getitem__"):
            raise _LiveTranslatorContentError(u"API 返回缺少 choices 结果")
        choice = choices[0]
        if not hasattr(choice, "get"):
            raise _LiveTranslatorContentError(u"API 返回缺少 choices 结果")
        # 命中内容审查或显式拒答时优先给出明确原因，按内容错误计次以便拆批隔离。
        if choice.get("finish_reason") == "content_filter":
            raise _LiveTranslatorRefusal(
                u"触发服务商内容审查（content_filter）"
            )
        message = choice.get("message")
        if not hasattr(message, "get"):
            raise _LiveTranslatorContentError(u"API 返回缺少 message 内容")
        refusal = message.get("refusal")
        if isinstance(refusal, (_live_translator_text_type, str)):
            refusal_text = _live_translator_sanitize_error_detail(refusal)
            if refusal_text:
                raise _LiveTranslatorRefusal(
                    u"模型拒绝翻译这批文本：%s" % refusal_text[:80]
                )
        message_content = message.get("content")
        # 兼容部分 OpenAI 兼容接口返回内容块数组的形式。
        if (
            not isinstance(message_content, _live_translator_text_type)
            and hasattr(message_content, "append")
        ):
            content_parts = []
            for content_block in message_content:
                if hasattr(content_block, "get"):
                    content_parts.append(content_block.get("text", u""))
                else:
                    content_parts.append(_live_translator_to_text(content_block))
            message_content = u"".join(content_parts)
        if message_content is None:
            raise _LiveTranslatorContentError(u"API 返回了空内容")
        text = _live_translator_to_text(message_content)
        if not text.strip():
            raise _LiveTranslatorContentError(u"API 返回了空内容")
        return text

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

    class _LiveTranslatorContentError(ValueError):
        kind = "invalid"
        max_attempts = 3

    class _LiveTranslatorRefusal(_LiveTranslatorContentError):
        kind = "refused"
        max_attempts = 1

    class _LiveTranslatorEcho(_LiveTranslatorContentError):
        kind = "echo"
        max_attempts = 2

    class _LiveTranslatorTemporaryError(RuntimeError):
        kind = "temporary"
        max_attempts = 3

    def _live_translator_request_batch(sources, generation=None):
        if not _live_translator_config.get("enabled", True):
            return None

        try:
            import requests
        except ImportError:
            raise RuntimeError(u"游戏环境缺少 requests 模块")

        model = _live_translator_config.get("model", "")
        api_key = _live_translator_api_key()
        if not model or model == "your-model-name":
            raise RuntimeError(u"config.json 尚未填写 model")
        if not api_key:
            raise RuntimeError(u"config.json 尚未填写 api_key")

        user_content = _live_translator_json_module.dumps(
            {"texts": sources},
            ensure_ascii=False
        )
        system_prompt = _live_translator_to_text(
            _live_translator_config.get(
                "system_prompt",
                _live_translator_default_config["system_prompt"]
            )
        ).strip()
        # 即使用户沿用旧配置，也强制附加人名规则；这不会新增 API 请求。
        if _live_translator_name_prompt_rule not in system_prompt:
            system_prompt = (
                system_prompt + u" " + _live_translator_name_prompt_rule
            ).strip()

        thinking_enabled = bool(
            _live_translator_config.get("thinking_enabled", False)
        )
        anthropic = _live_translator_uses_anthropic()
        if anthropic:
            payload = _live_translator_anthropic_payload(
                model, system_prompt, user_content, thinking_enabled
            )
            headers = {
                "x-api-key": api_key,
                "anthropic-version": _live_translator_anthropic_version,
                "Content-Type": "application/json"
            }
        else:
            payload = _live_translator_plain_dict({
                "model": model,
                "messages": [
                    {
                        "role": "system",
                        "content": system_prompt
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
            })
            if _live_translator_config.get("json_response_format", True):
                payload["response_format"] = {"type": "json_object"}
            _live_translator_apply_provider_options(
                payload,
                _live_translator_api_host(),
                thinking_enabled
            )
            headers = {
                "Authorization": "Bearer " + api_key,
                "Content-Type": "application/json"
            }
        timeout_seconds = float(
            _live_translator_config.get("request_timeout_seconds", 60)
        )

        # 组批期间可能按 F9 关闭；发送前再次检查，已发出的请求不强行中断。
        with _live_translator_lock:
            if (not _live_translator_config.get("enabled", True)
                    or (generation is not None and generation != _live_translator_state["generation"])):
                return None
            _live_translator_state["request_count"] += 1
        try:
            response = requests.post(
                _live_translator_endpoint(),
                data=_live_translator_json_module.dumps(payload, ensure_ascii=False).encode("utf-8"),
                headers=headers, timeout=timeout_seconds
            )
        except (requests.exceptions.Timeout, requests.exceptions.ConnectionError) as error:
            raise _LiveTranslatorTemporaryError(_live_translator_error_text(error))
        status_code = getattr(response, "status_code", None)
        if (
            isinstance(status_code, int)
            and not isinstance(status_code, bool)
            and status_code >= 400
        ):
            _live_translator_raise_http_error(response, status_code, api_key)
        response.raise_for_status()
        response_data = response.json()
        if anthropic:
            message_content = _live_translator_anthropic_text(response_data)
        else:
            message_content = _live_translator_openai_text(response_data)

        try:
            translated_data = _live_translator_extract_json(message_content)
        except ValueError:
            raise _LiveTranslatorContentError(u"API 返回的内容不是有效 JSON")
        # JSON 解码的数组必须是列表；不能把对象键或非字符串元素当作译文缓存。
        # 从 JSON 解码器取得容器类型，避免 Ren'Py 对脚本 dict/list 类型的替换。
        object_type = type(_live_translator_json_module.loads("{}"))
        array_type = type(_live_translator_json_module.loads("[]"))
        translations = translated_data.get("translations") if isinstance(translated_data, object_type) else None
        if not isinstance(translations, array_type):
            raise _LiveTranslatorContentError(u"API 返回缺少 translations 数组")
        if len(translations) != len(sources):
            raise _LiveTranslatorContentError(u"API 返回的译文数量与原文不一致")

        normalized = []
        for translation in translations:
            if not isinstance(translation, _live_translator_text_type):
                raise _LiveTranslatorContentError(u"API 返回的译文必须是字符串")
            normalized_translation = _live_translator_to_text(
                translation
            ).strip()
            if not normalized_translation:
                raise _LiveTranslatorContentError(u"API 返回了空译文")
            normalized.append(normalized_translation)
        return normalized

    def _live_translator_refresh(sources=None):
        if renpy.is_init_phase():
            return

        def invalidate(widget):
            if not isinstance(widget, _live_translator_text_displayable):
                return
            if sources is not None:
                parts = (part for part in (widget.text or ()) if isinstance(part, (_live_translator_text_type, str)))
                if not any(source in part for part in parts for source in sources):
                    return
            # Ren'Py 会复用 Text 和已分词的排版结果。仅重启交互不会再次调用
            # config.replace_text，需要让受影响的可见控件在主线程重新分词。
            widget.dirty = True
            renpy.redraw(widget, 0)

        for layer in config.layers:
            for tag in renpy.get_showing_tags(layer=layer, sort=True):
                root = renpy.get_screen(tag, layer=layer)
                if root is not None:
                    root.visit_all(invalidate)
        renpy.restart_interaction()

    def _live_translator_finish_batch(sources, translations):
        with _live_translator_lock:
            for source, translation in zip(sources, translations):
                if _live_translator_is_echo(source, translation):
                    # translations 已按位置校验，其他成功句可以直接交付。
                    _live_translator_isolated.add(source)
                    _live_translator_fail_batch([source], _LiveTranslatorEcho(u"模型返回原文"))
                    continue
                _live_translator_runtime_cache[source] = translation
                _live_translator_index_normalized_source(
                    source,
                    translation,
                    _live_translator_runtime_normalized_cache
                )
                _live_translator_pending.discard(source)
                _live_translator_retry_after.pop(source, None)
                _live_translator_failures.pop(source, None)
                _live_translator_isolated.discard(source)
                _live_translator_append_cache(source, translation)
                _live_translator_state["success_count"] += 1

    def _live_translator_config_number(key, default, convert):
        # 配置可能被手工改成错误类型；回退默认值，不能让后台线程因此退出。
        try:
            return convert(_live_translator_config.get(key, default))
        except Exception:
            return default

    def _live_translator_error_text(error):
        # Python 2 中含中文字节串的异常直接转 unicode 会抛 UnicodeDecodeError，
        # 失败处理本身不能再抛异常，因此逐级退回到 str/repr。
        for describe in (
            lambda: _live_translator_text_type(error),
            lambda: _live_translator_to_text(str(error)),
            lambda: _live_translator_to_text(repr(error))
        ):
            try:
                return describe()
            except Exception:
                continue
        return u"未知错误"

    def _live_translator_fail_batch(sources, error):
        message = _live_translator_error_text(error)
        kind = getattr(error, "kind", "error")
        limit = getattr(error, "max_attempts", 1)
        split = isinstance(error, _LiveTranslatorContentError) and len(sources) > 1
        cooldown = max(5.0, _live_translator_config_number("retry_cooldown_seconds", 30.0, float))
        deadline = _live_translator_time_module.time() + max(cooldown, getattr(error, "retry_seconds", 0))
        with _live_translator_lock:
            for source in sources:
                previous = _live_translator_failures.get(source)
                attempts = previous[0] if previous else _live_translator_plain_dict()
                if split:
                    # 整批被拒时还不能判断是哪句触发；保留 pending，立即逐句验证。
                    _live_translator_isolated.add(source)
                    _live_translator_failures[source] = (attempts, "split", message)
                    _live_translator_queue.append(source)
                    continue
                attempts[kind] = attempts.get(kind, 0) + 1
                _live_translator_pending.discard(source)
                _live_translator_failures[source] = (attempts, kind, message)
                if attempts[kind] < limit:
                    _live_translator_retry_after[source] = deadline
                else:
                    _live_translator_isolated.discard(source)
        _live_translator_log(u"LiveTranslator: request failed: %s" % message)

    def _live_translator_take_batch():
        # 新文本入队或最近一次冷却到期都会唤醒同一个工作线程；没有任务时一直阻塞。
        with _live_translator_lock:
            while True:
                now = _live_translator_time_module.time()
                for source, due in list(_live_translator_retry_after.items()):
                    if due <= now:
                        del _live_translator_retry_after[source]
                        _live_translator_pending.add(source)
                        _live_translator_queue.append(source)
                if _live_translator_queue:
                    break
                timeout = min(_live_translator_retry_after.values()) - now if _live_translator_retry_after else None
                _live_translator_lock.wait(timeout)

            generation = _live_translator_state["generation"]
            size = max(1, _live_translator_config_number("batch_size", 8, int))
            wait_ms = _live_translator_config_number("batch_wait_ms", 180.0, float)
            deadline = now + max(0.0, wait_ms / 1000.0)
            while len(_live_translator_queue) < size:
                remaining = deadline - _live_translator_time_module.time()
                if remaining <= 0:
                    break
                _live_translator_lock.wait(remaining)
                if generation != _live_translator_state["generation"]:
                    return generation, []
            return generation, [
                _live_translator_queue.popleft() for _ in range(min(size, len(_live_translator_queue)))
            ]

    def _live_translator_work_once():
        generation, batch = _live_translator_take_batch()
        try:
            for group in _live_translator_split_batch(batch):
                _live_translator_process_batch(group, generation)
        except Exception as error:
            # 失败处理本身出错时也要释放任务；已经拆批重新排队的原文仍属于 pending。
            with _live_translator_lock:
                if generation == _live_translator_state["generation"]:
                    for source in batch:
                        if source in _live_translator_pending and source not in _live_translator_queue:
                            _live_translator_pending.discard(source)
                            _live_translator_failures.pop(source, None)
                            _live_translator_isolated.discard(source)
            _live_translator_log(u"LiveTranslator: batch error: %s" % _live_translator_error_text(error))

    def _live_translator_worker():
        while True:
            try:
                _live_translator_work_once()
            except Exception as error:
                _live_translator_log(u"LiveTranslator: worker error: %s" % _live_translator_error_text(error))

    def _live_translator_split_batch(batch):
        # 曾在多条批次中出错的原文单独请求，其余仍合并成一批。
        with _live_translator_lock:
            isolated = [
                source for source in batch
                if source in _live_translator_isolated
            ]
        groups = [[source] for source in isolated]
        remaining = [source for source in batch if source not in isolated]
        if remaining:
            groups.append(remaining)
        return groups

    def _live_translator_process_batch(batch, generation):
        try:
            translations = _live_translator_request_batch(batch, generation)
        except Exception as error:
            with _live_translator_lock:
                if generation != _live_translator_state["generation"]:
                    return
                _live_translator_fail_batch(batch, error)
                if all(source in _live_translator_pending or source in _live_translator_retry_after for source in batch):
                    return
        else:
            with _live_translator_lock:
                if generation != _live_translator_state["generation"]:
                    return
                if translations is None:
                    _live_translator_pending.difference_update(batch)
                    return
                _live_translator_finish_batch(batch, translations)
        if not renpy.is_init_phase():
            renpy.invoke_in_main_thread(_live_translator_refresh, batch)

    def _live_translator_enqueue(source):
        with _live_translator_lock:
            if not _live_translator_config.get("enabled", True) or not _live_translator_runtime_api_ready():
                return False
            if source in _live_translator_pending or source in _live_translator_retry_after:
                return True
            if source in _live_translator_failures or source in _live_translator_runtime_cache:
                return False
            _live_translator_pending.add(source)
            _live_translator_queue.append(source)
            _live_translator_lock.notify()
            return True

    def _live_translator_lookup(source):
        # 预制译文优先，确保旧 API 缓存不能覆盖已校对文案。
        with _live_translator_lock:
            cached_translation = _live_translator_pretranslated_cache.get(
                source
            )
            if cached_translation is not None:
                return cached_translation

            # Ren'Py 可能折叠空格或移除引号转义，使用规范化键安全兜底。
            normalized_key = _live_translator_normalize_source_key(source)
            normalized_record = (
                _live_translator_pretranslated_normalized_cache.get(
                    normalized_key
                )
            )
            if normalized_record is not None:
                return normalized_record[1]

            cached_translation = _live_translator_runtime_cache.get(source)
            if cached_translation is not None:
                return cached_translation

            normalized_record = (
                _live_translator_runtime_normalized_cache.get(normalized_key)
            )
            if normalized_record is not None:
                return normalized_record[1]
        return None

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

    def _live_translator_font_available(font_path):
        # 绝对路径走真实文件检查；相对路径视为 Ren'Py 虚拟文件系统路径，
        # 以游戏 game 目录为基准拼接后检查真实文件是否存在。
        if not font_path:
            return False
        if _live_translator_os_module.path.isabs(font_path):
            return _live_translator_os_module.path.isfile(font_path)
        candidate = _live_translator_os_module.path.join(
            renpy.config.gamedir, font_path
        )
        return _live_translator_os_module.path.isfile(candidate)

    def _live_translator_font_path(fallback):
        font_path = _live_translator_config.get("font", "")
        if font_path and _live_translator_font_available(font_path):
            return font_path
        return fallback

    def _live_translator_confirm_message(message):
        # Ren'Py 的内置确认文本可能先逐句翻译，再用换行拼成完整消息。
        message_text = _live_translator_to_text(message)
        if not _live_translator_config.get("enabled", True):
            return message_text
        translated_parts = []
        for part in _live_translator_re_module.split(u"(\n)", message_text):
            if not part or part == u"\n":
                translated_parts.append(part)
                continue

            if not _live_translator_should_translate(part):
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
        source = _live_translator_to_text(original_value)
        local_weekday = _live_translator_local_weekday_translation(source)
        if local_weekday is not None:
            return local_weekday
        local_save_slot = _live_translator_local_save_slot_translation(
            source
        )
        if local_save_slot is not None:
            return local_save_slot
        local_image_not_found = (
            _live_translator_local_image_not_found_translation(source)
        )
        if local_image_not_found is not None:
            return local_image_not_found
        if not _live_translator_should_translate(source):
            return original_value
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
        local_weekday = _live_translator_local_weekday_translation(source)
        if local_weekday is not None:
            return local_weekday
        local_save_slot = _live_translator_local_save_slot_translation(
            source
        )
        if local_save_slot is not None:
            return local_save_slot
        local_image_not_found = (
            _live_translator_local_image_not_found_translation(source)
        )
        if local_image_not_found is not None:
            return local_image_not_found
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

        waiting = _live_translator_enqueue(source)
        pending_text = _live_translator_config.get("pending_text", "")
        if waiting and pending_text:
            return _live_translator_to_text(pending_text)
        return original_value

    def _live_translator_toggle():
        with _live_translator_lock:
            enabled = not _live_translator_config.get("enabled", True)
            _live_translator_config["enabled"] = enabled
            if not enabled:
                # F9 取消已排队的重试。代号让开关前已发出的响应不能覆盖新任务。
                _live_translator_state["generation"] += 1
                _live_translator_queue.clear()
                _live_translator_pending.clear()
                _live_translator_retry_after.clear()
                _live_translator_isolated.clear()
                _live_translator_failures.clear()
                _live_translator_lock.notify()
        renpy.notify(u"实时翻译：已开启" if enabled else u"实时翻译：已关闭")
        _live_translator_refresh()

    def _live_translator_show_status():
        if not _live_translator_config.get("enabled", True):
            status_message = u"实时翻译已关闭"
        elif not _live_translator_api_key():
            status_message = u"实时翻译：请填写 API Key"
        elif not _live_translator_runtime_api_ready():
            status_message = u"实时翻译：请填写模型名称"
        else:
            with _live_translator_lock:
                status_message = u"实时翻译：预翻译 %d 条，缓存 %d 条，请求 %d 次" % (
                    _live_translator_pretranslated_count,
                    _live_translator_runtime_cache_count + _live_translator_state["success_count"],
                    _live_translator_state["request_count"]
                )
                counts = {u"等待重试": len(_live_translator_retry_after), u"处理中": len(_live_translator_pending)}
                detail = u""
                for source, (_, kind, message) in _live_translator_failures.items():
                    if not detail and kind in ("temporary", "invalid", "error"):
                        detail = message
                    if source in _live_translator_pending or source in _live_translator_retry_after:
                        continue
                    label = {"refused": u"服务商拒绝", "echo": u"模型返回原文"}.get(kind, u"其他失败")
                    counts[label] = counts.get(label, 0) + 1
                for label in (u"处理中", u"等待重试", u"服务商拒绝", u"模型返回原文", u"其他失败"):
                    if counts.get(label):
                        status_message += u"，%s %d 条" % (label, counts[label])
                if detail:
                    status_message += u"（%s）" % detail[:120]
        renpy.notify(status_message)

    def _live_translator_apply_font():
        font_path = _live_translator_config.get("font", "")
        if (
            not font_path
            or not _live_translator_font_available(font_path)
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

    def _live_translator_accepts_end_animation(function):
        import inspect
        try:
            get_spec = inspect.getfullargspec
        except AttributeError:
            get_spec = inspect.getargspec
        spec = get_spec(function)
        # 下标 2 在两种实现中都是 **kwargs 参数名。
        return "end_animation" in spec[0] or spec[2] is not None

    def _live_translator_patch_readback_adjustment():
        # 通用模组对所有游戏生效：只在游戏自定义的 change 不兼容新参数、必然报错时
        # 才替换；无该类、非 Adjustment 子类或已兼容的游戏保持原样。
        readback_adjustment_class = globals().get("NewAdj")
        if readback_adjustment_class is None:
            return
        try:
            if not issubclass(
                readback_adjustment_class,
                renpy.display.behavior.Adjustment
            ):
                return
        except TypeError:
            return
        try:
            if _live_translator_accepts_end_animation(
                getattr(readback_adjustment_class, "change")
            ):
                return
        except Exception:
            # 无法检查签名时沿用原补丁行为，保证 Camp Buddy 聊天记录不会因此崩溃。
            pass
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
