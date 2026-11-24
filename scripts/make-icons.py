#!/usr/bin/env python3
"""Render docs/icons/icon-192.png and icon-512.png (same design as icon.svg). Stdlib only."""
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "icons")
BLUE, WHITE = (0x2C, 0x6B, 0x5A), (255, 255, 255)


def in_rrect(x, y, x0, y0, x1, y1, r):
    if not (x0 <= x <= x1 and y0 <= y <= y1):
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def in_tri(x, y, a, b, c):
    def s(p, q, r):
        return (p[0] - r[0]) * (q[1] - r[1]) - (q[0] - r[0]) * (p[1] - r[1])
    d1, d2, d3 = s((x, y), a, b), s((x, y), b, c), s((x, y), c, a)
    return not ((d1 < 0 or d2 < 0 or d3 < 0) and (d1 > 0 or d2 > 0 or d3 > 0))


def color_at(x, y):  # coordinates in the 512 design space; None = transparent
    if not in_rrect(x, y, 0, 0, 512, 512, 112):
        return None
    for cx in (196, 256, 316):
        if (x - cx) ** 2 + (y - 252) ** 2 <= 22 * 22:
            return BLUE
    if in_rrect(x, y, 96, 152, 416, 352, 40) or in_tri(x, y, (164, 340), (236, 340), (164, 408)):
        return WHITE
    return BLUE


def render(size, ss=3):
    rows = []
    k = 512 / size
    for py in range(size):
        row = bytearray([0])
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    c = color_at((px + (sx + .5) / ss) * k, (py + (sy + .5) / ss) * k)
                    if c:
                        acc[0] += c[0]; acc[1] += c[1]; acc[2] += c[2]; acc[3] += 1
            n = acc[3]
            row += bytes([acc[0] // n, acc[1] // n, acc[2] // n, n * 255 // (ss * ss)]) if n else b"\0\0\0\0"
        rows.append(bytes(row))
    raw = zlib.compress(b"".join(rows), 9)

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", raw) + chunk(b"IEND", b""))


for size in (192, 512):
    with open(os.path.join(OUT, f"icon-{size}.png"), "wb") as f:
        f.write(render(size))
    print("wrote", f"icon-{size}.png")
