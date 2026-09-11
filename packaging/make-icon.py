#!/usr/bin/env python3
"""生成桌面端应用图标 desktop/Translator/Assets/app.ico。

仅依赖标准库：手工绘制（超采样抗锯齿）→ 自行编码 PNG → 打包为标准 ICO。
图形为强调色圆角方块 + 白色「文」字，避免目标机器缺少字体时图标渲染出方块。
用法：python packaging/make-icon.py [输出路径]
"""
import struct
import sys
import zlib
from pathlib import Path

# 与 Themes/Tokens.xaml 中的 AccentBrush 保持一致
ACCENT = (0x2F, 0x7D, 0xE1)
INK = (0xFF, 0xFF, 0xFF)
CANVAS = 1.0
CORNER_RADIUS = 0.22

# 「文」字的笔画：(起点 x, 起点 y, 终点 x, 终点 y)，坐标按画布 0~1 归一化
STROKES = [
    (0.500, 0.190, 0.500, 0.245),  # 点
    (0.248, 0.335, 0.752, 0.335),  # 横
    (0.612, 0.432, 0.215, 0.845),  # 撇：自横下方偏右起笔，向左下
    (0.388, 0.432, 0.785, 0.845),  # 捺：自横下方偏左起笔，向右下，与撇在中段交叉
]
STROKE_HALF = 0.043  # 笔画半宽

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def _inside_rounded_square(u, v):
    r = CORNER_RADIUS
    cx = min(max(u, r), CANVAS - r)
    cy = min(max(v, r), CANVAS - r)
    if cx == u and cy == v:
        return True
    return (u - cx) ** 2 + (v - cy) ** 2 <= r * r


def _segment_distance(u, v, x0, y0, x1, y1):
    dx, dy = x1 - x0, y1 - y0
    length_sq = dx * dx + dy * dy
    if length_sq == 0:
        return ((u - x0) ** 2 + (v - y0) ** 2) ** 0.5
    t = max(0.0, min(1.0, ((u - x0) * dx + (v - y0) * dy) / length_sq))
    px, py = x0 + t * dx, y0 + t * dy
    return ((u - px) ** 2 + (v - py) ** 2) ** 0.5


def _inside_ink(u, v):
    for x0, y0, x1, y1 in STROKES:
        if _segment_distance(u, v, x0, y0, x1, y1) <= STROKE_HALF:
            return True
    return False


def render_rgba(size):
    """超采样渲染：每个像素取 ss×ss 个子样本，分别统计底色与墨色覆盖率。"""
    ss = 4 if size <= 64 else 2
    total = ss * ss
    base = [0] * (size * size)
    ink = [0] * (size * size)
    for sy in range(size * ss):
        v = (sy + 0.5) / (size * ss)
        row = (sy // ss) * size
        for sx in range(size * ss):
            u = (sx + 0.5) / (size * ss)
            if not _inside_rounded_square(u, v):
                continue
            cell = row + sx // ss
            base[cell] += 1
            if _inside_ink(u, v):
                ink[cell] += 1

    out = bytearray(size * size * 4)
    for i in range(size * size):
        if base[i] == 0:
            continue
        alpha = base[i] / total
        ratio = ink[i] / base[i]
        for c in range(3):
            out[i * 4 + c] = round(ACCENT[c] + (INK[c] - ACCENT[c]) * ratio)
        out[i * 4 + 3] = round(alpha * 255)
    return bytes(out)


def encode_png(size, rgba):
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)  # 每行过滤器固定为 None
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def encode_ico(images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, payload = b"", b""
    for size, blob in images:
        edge = 0 if size >= 256 else size  # 256 在 ICO 目录中以 0 表示
        entries += struct.pack("<BBBBHHII", edge, edge, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        payload += blob
    return header + entries + payload


def main():
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("desktop/Translator/Assets/app.ico")
    target.parent.mkdir(parents=True, exist_ok=True)
    images = []
    for size in ICO_SIZES:
        images.append((size, encode_png(size, render_rgba(size))))
    target.write_bytes(encode_ico(images))
    print(f"已生成 {target}（{target.stat().st_size} 字节，含 {len(ICO_SIZES)} 种尺寸）")


if __name__ == "__main__":
    main()
