# -*- coding: utf-8 -*-
"""提取 RPA-3.0 封包，支持通过参数指定游戏自定义 XOR 密钥。"""

from __future__ import print_function

import argparse
import os
import pickle
import zlib


def parse_arguments():
    parser = argparse.ArgumentParser(description="提取 Ren'Py RPA-3.0 封包")
    parser.add_argument("archive", help="RPA 封包路径")
    parser.add_argument("output", help="输出目录")
    parser.add_argument(
        "--xor-key",
        type=int,
        default=None,
        help="自定义索引 XOR 密钥；省略时使用 RPA 头部密钥",
    )
    parser.add_argument(
        "--suffix",
        action="append",
        default=[],
        help="仅提取指定后缀，可重复，例如 --suffix .rpy",
    )
    return parser.parse_args()


def load_index(archive_file):
    header = archive_file.read(40)
    if not header.startswith(b"RPA-3.0 "):
        raise RuntimeError("仅支持 RPA-3.0，实际头部为 %r" % header[:16])

    index_offset = int(header[8:24], 16)
    header_key = int(header[25:33], 16)
    archive_file.seek(index_offset)
    compressed_index = archive_file.read()
    index = pickle.loads(zlib.decompress(compressed_index))
    return index, header_key


def safe_target_path(output_directory, archive_name):
    normalized_name = archive_name.replace("\\", "/").lstrip("/")
    normalized_name = os.path.normpath(normalized_name)
    if normalized_name == ".." or normalized_name.startswith("../"):
        raise RuntimeError("封包条目包含越界路径：%r" % archive_name)

    output_root = os.path.abspath(output_directory)
    target_path = os.path.abspath(
        os.path.join(output_root, normalized_name)
    )
    if not (
        target_path == output_root
        or target_path.startswith(output_root + os.sep)
    ):
        raise RuntimeError("封包条目越过输出目录：%r" % archive_name)
    return target_path


def extract_archive(archive_path, output_directory, xor_key, suffixes):
    extracted_count = 0
    extracted_bytes = 0
    with open(archive_path, "rb") as archive_file:
        index, header_key = load_index(archive_file)
        effective_key = header_key if xor_key is None else xor_key

        for archive_name in sorted(index):
            if suffixes and not any(
                archive_name.lower().endswith(suffix.lower())
                for suffix in suffixes
            ):
                continue

            target_path = safe_target_path(
                output_directory, archive_name
            )
            target_directory = os.path.dirname(target_path)
            if not os.path.isdir(target_directory):
                os.makedirs(target_directory)

            file_size = 0
            with open(target_path, "wb") as output_file:
                for segment in index[archive_name]:
                    offset = segment[0] ^ effective_key
                    length = segment[1] ^ effective_key
                    prefix = segment[2] if len(segment) >= 3 else b""
                    if prefix:
                        output_file.write(prefix)
                        file_size += len(prefix)
                    archive_file.seek(offset)
                    content = archive_file.read(length)
                    if len(content) != length:
                        raise RuntimeError(
                            "条目读取不完整：%s" % archive_name
                        )
                    output_file.write(content)
                    file_size += len(content)

            extracted_count += 1
            extracted_bytes += file_size

    print(
        "提取完成：%d 个文件，共 %d 字节，输出到 %s"
        % (extracted_count, extracted_bytes, output_directory)
    )


def main():
    arguments = parse_arguments()
    extract_archive(
        arguments.archive,
        arguments.output,
        arguments.xor_key,
        arguments.suffix,
    )


if __name__ == "__main__":
    main()
