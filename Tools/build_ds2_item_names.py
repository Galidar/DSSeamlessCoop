#!/usr/bin/env python3
"""
Build Source/BonfireService/Resources/ds2_item_names.json from the
Paramdex DS2S name files.

The output is a flat dict mapping `param_id (str) -> human_name (str)`,
used by Ds2ItemNames.cs in BonfireService to resolve weapon/armor/ring
IDs sent over the BNCD wire format back to readable names for the
Flutter "Connected Peers" panel.

Run once whenever Paramdex updates upstream; the JSON ships embedded
in BonfireService.exe.
"""

import json
import os
import re
import sys


PARAMDEX_DIR = os.path.normpath(
    os.path.join(os.path.dirname(__file__), '..', 'Temp', 'external_repos',
                 'Paramdex', 'DS2S', 'Names')
)
OUT_PATH = os.path.normpath(
    os.path.join(os.path.dirname(__file__), '..', 'Source', 'BonfireService',
                 'Resources', 'ds2_item_names.json')
)

# Order = priority. Earlier files win when an ID appears in multiple.
FILES = ['ItemParam.txt', 'WeaponParam.txt', 'ArmorParam.txt']

# Always-present overrides (Bonfire's custom orbs + the "Fists" sentinel).
OVERRIDES = {
    3400000: 'Fists',
    62030000: 'White Sign Soapstone',
    62061000: 'Saponita Desbloqueada',
    62061001: 'Crystal Eye Orb (Bonfire)',
}


def main() -> int:
    if not os.path.isdir(PARAMDEX_DIR):
        print(f'Paramdex dir missing: {PARAMDEX_DIR}', file=sys.stderr)
        return 1

    table: dict[int, str] = {}
    for fname in FILES:
        path = os.path.join(PARAMDEX_DIR, fname)
        if not os.path.exists(path):
            print(f'  skip (missing): {fname}', file=sys.stderr)
            continue
        with open(path, encoding='utf-8', errors='replace') as h:
            for line in h:
                m = re.match(r'^(\d+)\s+(.+?)\s*$', line.rstrip())
                if not m:
                    continue
                id_ = int(m.group(1))
                name = m.group(2).split('--')[0].strip()
                if not name or name == 'UNKNOWN':
                    continue
                if 'Empty' in name and id_ < 1000:
                    continue
                # Don't overwrite a good prior entry with a worse one.
                if id_ in table and not table[id_].startswith('NPC:'):
                    continue
                table[id_] = name

    table.update(OVERRIDES)

    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    sorted_table = {str(k): table[k] for k in sorted(table)}
    with open(OUT_PATH, 'w', encoding='utf-8') as h:
        json.dump(sorted_table, h, separators=(',', ':'), ensure_ascii=False)

    print(f'Wrote {OUT_PATH}')
    print(f'  entries: {len(table):,}')
    print(f'  size:    {os.path.getsize(OUT_PATH):,} bytes')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
