#!/usr/bin/env python3
"""从 PNG 素材生成桌面端应用图标 desktop/Translator/Assets/app.ico。

仅依赖标准库：自行解码 PNG（面积平均缩放）→ 圆角遮罩 → 逐档编码 PNG → 打包为标准 ICO。
不引入 Pillow 等第三方包，保证在任何装有 Python 的机器上都能复现图标。

支持 8/16 位、非隔行的灰度 / RGB / 调色板 / 灰度+Alpha / RGBA；16 位取高字节。
非正方形素材会先按中心裁成正方形，再做面积平均（大比例缩小时等价于理想低通，不会摩尔纹）。
四角默认裁为圆角（比例可调，传 0 恢复直角）；遮罩按每档目标尺寸独立计算，抗锯齿边缘干净。

用法：python packaging/make-icon.py [源图] [输出ico] [圆角比例0~0.5，默认0.2]
"""
import math
import struct
import sys
import zlib
from pathlib import Path

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)

DEFAULT_SOURCE = Path("desktop/Translator/Assets/app-icon.png")
DEFAULT_TARGET = Path("desktop/Translator/Assets/app.ico")

# 各颜色类型的通道数
_CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


def _chunks(blob):
    """按顺序产出 (类型, 数据)，跳过长度/CRC 校验以外的所有块。"""
    pos = 8  # 跳过 PNG 签名
    while pos + 8 <= len(blob):
        length = struct.unpack(">I", blob[pos:pos + 4])[0]
        tag = blob[pos + 4:pos + 8]
        yield tag, blob[pos + 8:pos + 8 + length]
        if tag == b"IEND":
            return
        pos += 12 + length


def _unfilter(raw, width, height, bpp):
    """还原逐行过滤器；bpp 为单像素字节数。"""
    stride = width * bpp
    out = bytearray(height * stride)
    prev = bytearray(stride)
    pos = 0
    for y in range(height):
        ftype = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        if ftype == 0:
            pass
        elif ftype == 1:
            for x in range(bpp, stride):
                line[x] = (line[x] + line[x - bpp]) & 0xFF
        elif ftype == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 0xFF
        elif ftype == 3:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 0xFF
        elif ftype == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                b = prev[x]
                c = prev[x - bpp] if x >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 0xFF
        else:
            raise ValueError("不支持的行过滤器类型：%d" % ftype)
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return out


def load_png(path):
    """解码 PNG，返回按行拆分的 RGBA 列表（每行 bytes，长度 = 宽 × 4）。"""
    blob = Path(path).read_bytes()
    if blob[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG 文件：%s" % path)

    header = None
    palette = b""
    trns = b""
    idat = bytearray()
    for tag, data in _chunks(blob):
        if tag == b"IHDR":
            header = struct.unpack(">IIBBBBB", data)
        elif tag == b"PLTE":
            palette = data
        elif tag == b"tRNS":
            trns = data
        elif tag == b"IDAT":
            idat += data

    width, height, depth, color_type, _comp, _filt, interlace = header
    if interlace:
        raise ValueError("不支持隔行（Adam7）PNG，请另存为非隔行格式")
    if depth not in (8, 16):
        raise ValueError("仅支持 8/16 位色深，当前为 %d 位" % depth)
    if color_type not in _CHANNELS:
        raise ValueError("不支持的颜色类型：%d" % color_type)

    channels = _CHANNELS[color_type]
    bpp = channels * (depth // 8)
    raw = _unfilter(zlib.decompress(bytes(idat)), width, height, bpp)

    rows = []
    for y in range(height):
        base = y * width * bpp
        row = bytearray(width * 4)
        for x in range(width):
            i = base + x * bpp
            if depth == 16:  # 16 位取高字节，等价于线性缩放到 8 位
                sample = [raw[i + k * 2] for k in range(channels)]
            else:
                sample = raw[i:i + channels]
            if color_type == 0:
                r = g = b = sample[0]; a = 255
            elif color_type == 2:
                r, g, b = sample[0], sample[1], sample[2]; a = 255
            elif color_type == 3:
                idx = sample[0]
                r, g, b = palette[idx * 3], palette[idx * 3 + 1], palette[idx * 3 + 2]
                a = trns[idx] if idx < len(trns) else 255
            elif color_type == 4:
                r = g = b = sample[0]; a = sample[1]
            else:
                r, g, b, a = sample[0], sample[1], sample[2], sample[3]
            o = x * 4
            row[o] = r; row[o + 1] = g; row[o + 2] = b; row[o + 3] = a
        rows.append(bytes(row))
    return width, height, rows


def center_crop_square(width, height, rows):
    """非正方形素材先按中心裁成正方形。"""
    if width == height:
        return width, rows
    side = min(width, height)
    x0 = (width - side) // 2
    y0 = (height - side) // 2
    cropped = [row[x0 * 4:(x0 + side) * 4] for row in rows[y0:y0 + side]]
    return side, cropped


def box_downscale(rows, src_size, dst_size):
    """面积平均缩放：目标像素 = 对应源矩形内所有像素的均值。

    源缩小到 1/5 以下时，面积平均本身就是理想的低通滤波，不会产生摩尔纹。
    """
    out = bytearray(dst_size * dst_size * 4)
    scale = src_size / dst_size
    for ty in range(dst_size):
        y0 = int(ty * scale)
        y1 = min(max(y0 + 1, int((ty + 1) * scale)), src_size)
        band = rows[y0:y1]
        band_rows = len(band)
        for tx in range(dst_size):
            x0 = int(tx * scale)
            x1 = min(max(x0 + 1, int((tx + 1) * scale)), src_size)
            lo, hi = x0 * 4, x1 * 4
            sr = sg = sb = sa = 0
            for row in band:
                seg = row[lo:hi]
                sr += sum(seg[0::4])
                sg += sum(seg[1::4])
                sb += sum(seg[2::4])
                sa += sum(seg[3::4])
            count = band_rows * (x1 - x0)
            o = (ty * dst_size + tx) * 4
            out[o] = sr // count
            out[o + 1] = sg // count
            out[o + 2] = sb // count
            out[o + 3] = sa // count
    return bytes(out)


def apply_rounded(rgba, size, radius):
    """把方形图像四角裁为圆角：按圆角矩形的带符号距离做线性抗锯齿 alpha 遮罩。

    每档 ICO 尺寸独立计算（而非先遮罩再缩放），保证 16px 小图的圆角边缘同样干净；
    距离场在 1px 过渡带内线性映射到 alpha，等价于 2x~3x 超采样的观感且无整数倍限制。
    """
    out = bytearray(rgba)
    half = size / 2.0
    inner = half - radius  # 角圆心到中轴线的距离
    for y in range(size):
        qy = abs(y + 0.5 - half) - inner
        for x in range(size):
            qx = abs(x + 0.5 - half) - inner
            # 标准圆角矩形 SDF：角区为到圆心的距离减半径，边内外为直线距离。
            if qx > 0 and qy > 0:
                d = math.hypot(qx, qy) - radius
            else:
                d = min(max(qx, qy), 0.0)
            a = round((0.5 - d) * 255)
            if a < 255:
                o = (y * size + x) * 4 + 3
                out[o] = a if a > 0 else 0
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
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_SOURCE
    target = Path(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_TARGET
    corner_ratio = float(sys.argv[3]) if len(sys.argv) > 3 else 0.2
    if not 0.0 <= corner_ratio <= 0.5:
        raise SystemExit("圆角比例须在 0~0.5 之间（当前：%s）" % corner_ratio)
    if not source.is_file():
        raise SystemExit("找不到源图：%s" % source)

    width, height, rows = load_png(source)
    print("源图 %s：%d × %d" % (source.name, width, height))
    side, rows = center_crop_square(width, height, rows)
    if side != width:
        print("  非正方形，已按中心裁为 %d × %d" % (side, side))
    print("  圆角比例：%.0f%%" % (corner_ratio * 100) if corner_ratio else "  直角（未裁圆角）")

    images = []
    for size in ICO_SIZES:
        scaled = box_downscale(rows, side, size) if side != size else b"".join(rows)
        radius = int(round(size * corner_ratio))
        if radius >= 1:
            scaled = apply_rounded(scaled, size, radius)
        images.append((size, encode_png(size, scaled)))
        print("  已生成 %3d × %3d" % (size, size))

    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(encode_ico(images))
    print("已写入 %s（%d 字节，含 %d 种尺寸）" % (target, target.stat().st_size, len(ICO_SIZES)))


if __name__ == "__main__":
    main()
