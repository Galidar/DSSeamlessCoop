#!/usr/bin/env python3
"""
Patch the Icon ID of a single ItemParam row in DS2 SOTFS PARAM format.

DS2 ItemParam row table starts at 0x30 (after header), each row is 24
bytes (id u32, _pad u32, dataOffset u32, _pad u32, nameOffset u32, _pad u32).
The data section pointed to by dataOffset begins with `s32 Icon ID`
(per Paramdex DS2S Defs/ITEM_PARAM.xml).

Usage:
    ds2_itemparam_set_icon.py <ItemParam.param> --id 62061000 --icon 62045000
    ds2_itemparam_set_icon.py <ItemParam.param> --id 62061000 --show
"""

import argparse
import struct
import sys
from pathlib import Path


def find_row(data: bytes, target_id: int) -> int | None:
    """Scan the row table for a matching id. Returns row offset or None."""
    row_count = struct.unpack_from("<H", data, 0x0A)[0]
    # DS2 SOTFS PARAM (format2D 0x07) row table starts at 0x40, stride 24
    # (id u32, _pad u32, dataOff u32, _pad u32, nameOff u32, _pad u32).
    row_table_start = 0x40
    row_size = 24
    for i in range(row_count):
        base = row_table_start + i * row_size
        if base + row_size > len(data):
            break
        row_id = struct.unpack_from("<I", data, base)[0]
        if row_id == target_id:
            return base
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--id", type=int, required=True)
    ap.add_argument("--icon", type=int, default=None,
                    help="New Icon ID (omit with --show to just inspect)")
    ap.add_argument("--show", action="store_true")
    ap.add_argument("--no-backup", dest="backup", action="store_false")
    ap.add_argument("--dry-run", action="store_true")
    ap.set_defaults(backup=True)
    args = ap.parse_args()

    path = Path(args.file)
    data = bytearray(path.read_bytes())
    row_off = find_row(data, args.id)
    if row_off is None:
        print(f"ID {args.id} NOT found in {args.file}", file=sys.stderr)
        return 1

    data_off = struct.unpack_from("<I", data, row_off + 8)[0]
    current_icon = struct.unpack_from("<i", data, data_off + 0x00)[0]
    print(f"# {args.file}")
    print(f"# Row {args.id} @ 0x{row_off:x}, data @ 0x{data_off:x}")
    print(f"# Current Icon ID: {current_icon}")

    if args.show:
        return 0
    if args.icon is None:
        print("--icon required (or use --show)", file=sys.stderr)
        return 2

    struct.pack_into("<i", data, data_off + 0x00, args.icon)
    print(f"# New Icon ID:     {args.icon}")

    if args.dry_run:
        print("(dry-run) Not writing.")
        return 0

    if args.backup:
        backup = path.with_suffix(path.suffix + ".bak")
        if not backup.exists():
            backup.write_bytes(bytes(path.read_bytes()))
            print(f"  Backup -> {backup}")
    path.write_bytes(bytes(data))
    print(f"  Wrote {len(data)} bytes -> {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
