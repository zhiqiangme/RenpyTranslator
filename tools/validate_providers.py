#!/usr/bin/env python3
"""校验 providers.json 的结构、字段类型、日期格式与 HTTPS 地址合法性。"""

from __future__ import annotations

import datetime
import json
import re
import sys
from pathlib import Path
from urllib.parse import urlparse

SUPPORTED_SCHEMA_VERSION = 1
MAX_FILE_BYTES = 1024 * 1024
ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]*$")


def fail(message: str) -> None:
    print(f"ERROR: {message}", file=sys.stderr)
    sys.exit(1)


def validate_base_url(url: str, label: str) -> None:
    if not isinstance(url, str) or not url or url != url.strip():
        fail(f"{label}: base_url 必须是非空且不含首尾空白的字符串")
    if url.endswith("/"):
        fail(f"{label}: base_url 不应包含尾随斜杠: {url}")
    parsed = urlparse(url)
    if parsed.scheme != "https":
        fail(f"{label}: base_url 必须使用 https 协议: {url}")
    if not parsed.hostname:
        fail(f"{label}: base_url 缺少主机名: {url}")
    if parsed.username is not None or parsed.password is not None:
        fail(f"{label}: base_url 不能包含账号或密码: {url}")
    if parsed.query or parsed.fragment:
        fail(f"{label}: base_url 不能包含查询参数或片段: {url}")


def validate_catalog(path: Path) -> int:
    if not path.is_file():
        fail(f"找不到清单文件: {path}")
    raw = path.read_bytes()
    if len(raw) > MAX_FILE_BYTES:
        fail(f"清单文件超过大小限制 ({MAX_FILE_BYTES} 字节)")
    try:
        data = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        fail(f"JSON 解析失败: {exc}")

    if not isinstance(data, dict):
        fail("顶层必须是 JSON 对象")

    schema_version = data.get("schema_version")
    if type(schema_version) is not int or schema_version != SUPPORTED_SCHEMA_VERSION:
        fail(f"schema_version 必须为整数 {SUPPORTED_SCHEMA_VERSION}，当前为 {schema_version!r}")

    updated_at = data.get("updated_at")
    if not isinstance(updated_at, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", updated_at):
        fail(f"updated_at 必须为 YYYY-MM-DD 日期字符串，当前为 {updated_at!r}")
    try:
        datetime.date.fromisoformat(updated_at)
    except ValueError as exc:
        fail(f"updated_at 不是有效日期 ({updated_at}): {exc}")

    providers = data.get("providers")
    if not isinstance(providers, list) or len(providers) == 0:
        fail("providers 必须是非空数组")

    seen_ids: set[str] = set()
    seen_names: set[str] = set()
    seen_urls: set[str] = set()
    total_models = 0

    for idx, item in enumerate(providers):
        prefix = f"providers[{idx}]"
        if not isinstance(item, dict):
            fail(f"{prefix} 必须是 JSON 对象")

        pid = item.get("id")
        if not isinstance(pid, str) or not ID_PATTERN.fullmatch(pid):
            fail(f"{prefix}.id 必须匹配 {ID_PATTERN.pattern}: {pid!r}")
        if pid == "custom":
            fail(f"{prefix}.id 不能使用保留标识 'custom'")
        if pid in seen_ids:
            fail(f"{prefix}.id 重复: {pid}")
        seen_ids.add(pid)

        name = item.get("name")
        if not isinstance(name, str) or not name.strip() or name != name.strip():
            fail(f"{prefix} ({pid}).name 必须是非空且不含首尾空白的字符串")
        if name in seen_names:
            fail(f"{prefix} ({pid}).name 重复: {name}")
        seen_names.add(name)

        base_url = item.get("base_url")
        validate_base_url(base_url, f"{prefix} ({pid})")
        url_key = base_url.lower()
        if url_key in seen_urls:
            fail(f"{prefix} ({pid}).base_url 重复: {base_url}")
        seen_urls.add(url_key)

        default_model = item.get("default_model")
        if not isinstance(default_model, str) or not default_model.strip() or default_model != default_model.strip():
            fail(f"{prefix} ({pid}).default_model 必须是非空且不含首尾空白的字符串")

        models = item.get("models")
        if not isinstance(models, list) or len(models) == 0:
            fail(f"{prefix} ({pid}).models 必须是非空数组")

        seen_model_ids: set[str] = set()
        for midx, model in enumerate(models):
            mprefix = f"{prefix} ({pid}).models[{midx}]"
            if not isinstance(model, dict):
                fail(f"{mprefix} 必须是 JSON 对象")
            mid = model.get("id")
            if not isinstance(mid, str) or not mid.strip() or mid != mid.strip():
                fail(f"{mprefix}.id 必须是非空且不含首尾空白的字符串")
            if mid in seen_model_ids:
                fail(f"{mprefix}.id 重复: {mid}")
            seen_model_ids.add(mid)
            if "label" in model and model["label"] is not None:
                if not isinstance(model["label"], str):
                    fail(f"{mprefix}.label 必须是字符串")
            total_models += 1

        if default_model not in seen_model_ids:
            fail(f"{prefix} ({pid}).default_model ({default_model}) 必须包含在 models 列表中")

    print(
        f"OK: {path.name} 校验通过（schema v{schema_version}, updated_at={updated_at}, "
        f"{len(providers)} 个服务商, {total_models} 个推荐模型）"
    )
    return len(providers)


def main() -> None:
    repo_root = Path(__file__).resolve().parent.parent
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else repo_root / "providers.json"
    validate_catalog(target)


if __name__ == "__main__":
    main()
