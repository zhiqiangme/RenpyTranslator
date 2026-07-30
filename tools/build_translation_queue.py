#!/usr/bin/env python3
"""从 Ren'Py 源码提取可直接写入预翻译缓存的文本。"""

from __future__ import annotations

import argparse
import ast
import hashlib
import json
import re
from pathlib import Path


CHARACTER_PATTERN = re.compile(
    r'^\s*\$\s+([A-Za-z_]\w*)\s*=\s*Character\(\s*(u?[\'"].*?[\'"])'
)
QUOTED_PATTERN = r'(?P<quoted>u?"(?:\\.|[^"\\])*")'
DIALOGUE_PATTERN = re.compile(
    r"^\s*(?P<prefix>[A-Za-z_]\w*(?:\s+[A-Za-z_]\w*)*)\s+"
    + QUOTED_PATTERN
    + r"\s*(?:with\s+[A-Za-z_][\w.]*)?\s*(?:#.*)?$"
)
NARRATION_PATTERN = re.compile(
    r"^\s*" + QUOTED_PATTERN + r"\s*(?:with\s+[A-Za-z_][\w.]*)?\s*(?:#.*)?$"
)
MENU_PATTERN = re.compile(
    r"^\s*" + QUOTED_PATTERN + r"\s*:\s*(?:#.*)?$"
)
SCREEN_TEXT_PATTERN = re.compile(
    r'^\s*(?:text|textbutton|label)\s+(?P<quoted>u?"(?:\\.|[^"\\])*")'
)
TRANSLATION_CALL_PATTERN = re.compile(
    r'(?<!\w)_(?:p)?\(\s*(?P<quoted>u?"(?:\\.|[^"\\])*")\s*\)'
)
LABEL_PATTERN = re.compile(r"^\s*label\s+([A-Za-z_]\w*)\s*:")
ASCII_LETTER_PATTERN = re.compile(r"[A-Za-z]")
FILE_LIKE_PATTERN = re.compile(
    r"(?i)\.(?:png|jpe?g|webp|gif|mp[34]|ogg|wav|rpyc?|rpa|ttf|otf)$"
)


def decode_literal(quoted: str) -> str:
    """按 Python 字符串字面量解码，保留 Ren'Py 标签和变量。"""
    value = ast.literal_eval(quoted)
    return value if isinstance(value, str) else str(value)


def should_keep(source: str) -> bool:
    stripped = source.strip()
    if not stripped or not ASCII_LETTER_PATTERN.search(stripped):
        return False
    if stripped.startswith(("http://", "https://")):
        return False
    if FILE_LIKE_PATTERN.search(stripped):
        return False
    if len(stripped) <= 4 and re.fullmatch(r"[A-Z0-9_+.-]+", stripped):
        return False
    return True


def load_existing_sources(paths: list[Path]) -> set[str]:
    sources: set[str] = set()
    for path in paths:
        if not path.is_file():
            continue
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            try:
                record = json.loads(line)
                sources.add(str(record["source"]))
            except (ValueError, KeyError, TypeError):
                continue
    return sources


def collect_characters(files: list[Path]) -> dict[str, str]:
    characters: dict[str, str] = {}
    for path in files:
        for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
            match = CHARACTER_PATTERN.match(line)
            if not match:
                continue
            try:
                characters[match.group(1)] = decode_literal(match.group(2))
            except (SyntaxError, ValueError):
                continue
    return characters


def file_priority(path: Path) -> tuple[int, str]:
    """界面文本优先，其次按主线和路线文件名排序。"""
    name = path.name.lower()
    ui_files = {
        "options.rpy",
        "gui.rpy",
        "screens.rpy",
        "mainmenu.rpy",
        "gamemenu.rpy",
        "quickmenu.rpy",
        "save.rpy",
        "load.rpy",
        "about.rpy",
    }
    if name in ui_files:
        return (0, name)
    if name == "script.rpy":
        return (1, name)
    if re.fullmatch(r"day[1-6]\.rpy", name):
        return (2, name)
    if name.startswith("day"):
        return (3, name)
    return (4, name)


def add_record(
    records: list[dict[str, object]],
    seen: set[str],
    existing: set[str],
    source: str,
    kind: str,
    path: Path,
    line_number: int,
    label: str,
    speaker: str,
    speaker_name: str,
    context: list[str],
) -> None:
    if source in seen or source in existing or not should_keep(source):
        return
    seen.add(source)
    digest = hashlib.sha1(source.encode("utf-8")).hexdigest()[:12]
    records.append(
        {
            "id": digest,
            "source": source,
            "kind": kind,
            "speaker": speaker,
            "speaker_name": speaker_name,
            "file": path.name,
            "line": line_number,
            "label": label,
            "context": context[-3:],
        }
    )


def build_records(
    source_dir: Path, existing_sources: set[str]
) -> tuple[list[dict[str, object]], dict[str, str]]:
    files = sorted(source_dir.glob("*.rpy"), key=file_priority)
    characters = collect_characters(files)
    records: list[dict[str, object]] = []
    seen: set[str] = set()

    for path in files:
        label = ""
        recent_dialogue: list[str] = []
        lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
        for line_number, line in enumerate(lines, 1):
            label_match = LABEL_PATTERN.match(line)
            if label_match:
                label = label_match.group(1)

            extracted: list[tuple[str, str, str]] = []
            dialogue_match = DIALOGUE_PATTERN.match(line)
            if dialogue_match:
                prefix_tokens = dialogue_match.group("prefix").split()
                speaker = prefix_tokens[0]
                if speaker in characters or speaker in {"extend"}:
                    extracted.append(
                        ("dialogue", speaker, dialogue_match.group("quoted"))
                    )
            else:
                narration_match = NARRATION_PATTERN.match(line)
                menu_match = MENU_PATTERN.match(line)
                screen_match = SCREEN_TEXT_PATTERN.match(line)
                if menu_match:
                    extracted.append(("menu", "", menu_match.group("quoted")))
                elif narration_match:
                    extracted.append(
                        ("narration", "", narration_match.group("quoted"))
                    )
                elif screen_match:
                    extracted.append(("ui", "", screen_match.group("quoted")))

            # _("...") 与 _p("...") 是 Ren'Py 显式可翻译文本。
            for call_match in TRANSLATION_CALL_PATTERN.finditer(line):
                extracted.append(("ui", "", call_match.group("quoted")))

            for kind, speaker, quoted in extracted:
                try:
                    source = decode_literal(quoted)
                except (SyntaxError, ValueError):
                    continue
                add_record(
                    records,
                    seen,
                    existing_sources,
                    source,
                    kind,
                    path,
                    line_number,
                    label,
                    speaker,
                    characters.get(speaker, ""),
                    list(recent_dialogue),
                )
                if kind in {"dialogue", "narration"}:
                    recent_dialogue.append(source)
                    recent_dialogue = recent_dialogue[-3:]

    return records, characters


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_dir", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--existing",
        type=Path,
        action="append",
        default=[],
        help="已翻译 JSONL，可重复指定",
    )
    arguments = parser.parse_args()

    existing_sources = load_existing_sources(arguments.existing)
    records, characters = build_records(arguments.source_dir, existing_sources)
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    with arguments.output.open("w", encoding="utf-8", newline="\n") as output_file:
        for record in records:
            output_file.write(json.dumps(record, ensure_ascii=False) + "\n")

    kind_counts: dict[str, int] = {}
    for record in records:
        kind = str(record["kind"])
        kind_counts[kind] = kind_counts.get(kind, 0) + 1
    print(
        json.dumps(
            {
                "files": len(list(arguments.source_dir.glob("*.rpy"))),
                "characters": len(characters),
                "records": len(records),
                "kinds": kind_counts,
                "output": str(arguments.output),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
