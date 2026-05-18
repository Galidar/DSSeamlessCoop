#!/usr/bin/env python3
"""
DS2 SOTFS FMG (Frpg2 Message) reader/writer.

Format spec extracted from SoulsFormats/Formats/FMG.cs (FMGVersion.DarkSouls1,
non-wide variant used by DS1 + DS2).

Usage:
    ds2_fmg_edit.py dump <file.fmg>
    ds2_fmg_edit.py dump <file.fmg> --id 62061000
    ds2_fmg_edit.py set <file.fmg> --id 62061000 --text "Saponita Desbloqueada"
"""

import argparse
import struct
import sys
from pathlib import Path


def read_fmg(data: bytes) -> tuple[list[tuple[int, str | None]], dict]:
    """Parse a DS1/DS2 FMG (non-wide). Returns (entries, header_info)."""
    assert data[0] == 0, f"Expected 0 at offset 0, got {data[0]}"
    big_endian = data[1] != 0
    version = data[2]
    assert data[3] == 0, f"Expected 0 at offset 3, got {data[3]}"
    assert version == 1, f"Expected DS1/DS2 version (1), got {version}"
    assert not big_endian, "BigEndian FMG not implemented (DemonsSouls only)"

    file_size = struct.unpack_from("<i", data, 4)[0]
    assert data[8] == 1, f"Expected 1 at offset 8, got {data[8]}"
    # 0x09 = 0 for DS1/DS2, 0xFF for DeS
    group_count = struct.unpack_from("<i", data, 0x0C)[0]
    string_count = struct.unpack_from("<i", data, 0x10)[0]
    string_offsets_offset = struct.unpack_from("<i", data, 0x14)[0]
    # 0x18 = padding int32 = 0

    entries: list[tuple[int, str | None]] = []
    # Groups start at 0x1C, each 12 bytes
    for g in range(group_count):
        base = 0x1C + g * 12
        offset_index = struct.unpack_from("<i", data, base)[0]
        first_id = struct.unpack_from("<i", data, base + 4)[0]
        last_id = struct.unpack_from("<i", data, base + 8)[0]

        for j in range(last_id - first_id + 1):
            string_offsets_pos = string_offsets_offset + (offset_index + j) * 4
            string_offset = struct.unpack_from(
                "<i", data, string_offsets_pos
            )[0]
            entry_id = first_id + j
            if string_offset == 0:
                entries.append((entry_id, None))
            else:
                # UTF-16 LE null-terminated
                end = string_offset
                while end + 1 < len(data) and not (
                    data[end] == 0 and data[end + 1] == 0
                ):
                    end += 2
                text = data[string_offset:end].decode("utf-16-le")
                entries.append((entry_id, text))

    return entries, {
        "file_size": file_size,
        "group_count": group_count,
        "string_count": string_count,
        "string_offsets_offset": string_offsets_offset,
        "version": version,
        "big_endian": big_endian,
    }


def write_fmg(entries: list[tuple[int, str | None]]) -> bytes:
    """
    Serialize back to DS1/DS2 FMG format. Mirrors SoulsFormats's Write method.
    """
    # Sort entries by ID
    entries = sorted(entries, key=lambda e: e[0])

    # Build groups (contiguous-ID runs)
    groups: list[tuple[int, int, int]] = []  # (offset_index, first_id, last_id)
    i = 0
    while i < len(entries):
        first_id = entries[i][0]
        # Walk until non-contiguous
        run_start = i
        while (
            i + 1 < len(entries)
            and entries[i + 1][0] == entries[i][0] + 1
        ):
            i += 1
        last_id = entries[i][0]
        groups.append((run_start, first_id, last_id))
        i += 1

    out = bytearray()
    # Header
    out += b"\x00\x00\x01\x00"  # byte 0, BigEndian=0, Version=1 (DS1/DS2), 0
    out += b"\x00\x00\x00\x00"  # FileSize placeholder
    out += b"\x01\x00\x00\x00"  # byte 1, byte 0, byte 0, byte 0
    out += struct.pack("<i", len(groups))  # GroupCount
    out += struct.pack("<i", len(entries))  # StringCount
    out += b"\x00\x00\x00\x00"  # StringOffsetsOffset placeholder
    out += b"\x00\x00\x00\x00"  # padding

    # Groups
    for offset_index, first_id, last_id in groups:
        out += struct.pack("<i", offset_index)
        out += struct.pack("<i", first_id)
        out += struct.pack("<i", last_id)

    # StringOffsets table location
    string_offsets_offset = len(out)
    # Reserve N int32 slots
    string_offsets_pos = string_offsets_offset
    out += b"\x00\x00\x00\x00" * len(entries)

    # String data
    string_offsets = []
    for idx, (entry_id, text) in enumerate(entries):
        if text is None:
            string_offsets.append(0)
        else:
            string_offsets.append(len(out))
            out += text.encode("utf-16-le") + b"\x00\x00"

    # Fill in StringOffsets
    for idx, off in enumerate(string_offsets):
        struct.pack_into(
            "<i", out, string_offsets_pos + idx * 4, off
        )

    # Fill StringOffsetsOffset
    struct.pack_into("<i", out, 0x14, string_offsets_offset)

    # Fill FileSize
    struct.pack_into("<i", out, 0x04, len(out))

    return bytes(out)


def cmd_dump(args):
    data = Path(args.file).read_bytes()
    entries, info = read_fmg(data)
    print(
        f"# {args.file}  "
        f"version={info['version']} entries={len(entries)} "
        f"file_size={info['file_size']}"
    )
    target = args.id
    shown = 0
    for entry_id, text in entries:
        if target is not None:
            if entry_id == target:
                print(f"{entry_id}\t{text!r}")
                shown = 1
                break
        else:
            limit = args.limit or 30
            if shown >= limit:
                break
            text_preview = (text or "")[:80]
            print(f"{entry_id}\t{text_preview!r}")
            shown += 1
    if target is not None and shown == 0:
        print(f"ID {target} NOT FOUND in {args.file}")


def cmd_set(args):
    path = Path(args.file)
    data = path.read_bytes()
    entries, info = read_fmg(data)

    # Find or insert
    target = args.id
    existing_idx = None
    for i, (eid, _) in enumerate(entries):
        if eid == target:
            existing_idx = i
            break

    if existing_idx is not None:
        old = entries[existing_idx][1]
        entries[existing_idx] = (target, args.text)
        print(f"Replaced ID {target}: {old!r} -> {args.text!r}")
    else:
        entries.append((target, args.text))
        print(f"Inserted ID {target}: {args.text!r}")

    new_data = write_fmg(entries)

    if not args.dry_run:
        if args.backup:
            backup = path.with_suffix(path.suffix + ".bak")
            if not backup.exists():
                backup.write_bytes(data)
                print(f"  Backup -> {backup}")
        path.write_bytes(new_data)
        print(f"  Wrote {len(new_data)} bytes -> {path}")
    else:
        print(f"  (dry-run) Would write {len(new_data)} bytes")


def cmd_find(args):
    """Find which IDs in a file contain a substring."""
    data = Path(args.file).read_bytes()
    entries, info = read_fmg(data)
    target = args.text.lower()
    for entry_id, text in entries:
        if text and target in text.lower():
            print(f"{entry_id}\t{text!r}")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)

    p_dump = sub.add_parser("dump")
    p_dump.add_argument("file")
    p_dump.add_argument("--id", type=int, default=None)
    p_dump.add_argument("--limit", type=int, default=None)
    p_dump.set_defaults(func=cmd_dump)

    p_set = sub.add_parser("set")
    p_set.add_argument("file")
    p_set.add_argument("--id", type=int, required=True)
    p_set.add_argument("--text", required=True)
    p_set.add_argument("--no-backup", dest="backup", action="store_false")
    p_set.add_argument("--dry-run", action="store_true")
    p_set.set_defaults(func=cmd_set, backup=True)

    p_find = sub.add_parser("find")
    p_find.add_argument("file")
    p_find.add_argument("text")
    p_find.set_defaults(func=cmd_find)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
