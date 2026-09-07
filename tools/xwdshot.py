#!/usr/bin/env python3
"""
Turn an Xvfb -fbdir framebuffer into a PNG.

Xvfb writes that file in XWD format, not as raw pixels: a big-endian header,
then the window name, then a colour map, and only then the image. Reading from
byte zero and assuming raw BGRA shifts every row by the header's length — which
looks exactly like a rendering bug, because the picture wraps horizontally by a
constant. Two hours of chasing a UI "anchoring fault" that was this instead.

    python3 xwdshot.py <fbdir>/Xvfb_screen0 out.png
"""
import struct
import sys

from PIL import Image

FIELDS = ("header_size file_version pixmap_format pixmap_depth pixmap_width pixmap_height "
          "xoffset byte_order bitmap_unit bitmap_bit_order bitmap_pad bits_per_pixel "
          "bytes_per_line visual_class red_mask green_mask blue_mask bits_per_rgb "
          "colormap_entries ncolors window_width window_height window_x window_y "
          "window_bdrwidth").split()


def read(path):
    blob = open(path, "rb").read()
    values = struct.unpack(">25I", blob[:100])
    head = dict(zip(FIELDS, values))

    if head["file_version"] != 7:
        raise SystemExit(f"{path}: not an XWD v7 file (version {head['file_version']})")

    start = head["header_size"] + head["ncolors"] * 12
    width, height = head["pixmap_width"], head["pixmap_height"]
    stride, depth = head["bytes_per_line"], head["bits_per_pixel"]

    if depth != 32:
        raise SystemExit(f"{path}: expected 32bpp, got {depth}")

    rows = []
    for y in range(height):
        row = blob[start + y * stride: start + y * stride + width * 4]
        rows.append(row)

    # Masks say which byte is which; Xvfb on a little-endian host gives BGRA.
    raw = b"".join(rows)
    mode = "BGRA" if head["byte_order"] == 0 else "ARGB"
    return Image.frombytes("RGBA", (width, height), raw, "raw", mode).convert("RGB"), head


if __name__ == "__main__":
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    image, header = read(sys.argv[1])
    image.save(sys.argv[2])
    print(f"{image.width}x{image.height} from XWD "
          f"(header {header['header_size']}B + {header['ncolors']} colours, stride {header['bytes_per_line']})")
