from pathlib import Path
import argparse
import hashlib
import json
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


def main() -> None:
    parser = argparse.ArgumentParser(description='Package the release DLL for a BepInEx 5 profile.')
    parser.add_argument('dll', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1]
    name = 'BepInEx/plugins/GardenHarvesterOwl/GardenHarvesterOwl.dll'
    payload = args.dll.read_bytes()
    if args.dll.name != 'GardenHarvesterOwl.dll' or payload[:2] != b'MZ':
        parser.error('Pass the compiled GardenHarvesterOwl.dll.')
    metadata = {
        'name': 'Gardener Owl',
        'author': 'Legios',
        'version': '0.2.0',
        'tested_game_build': 26671410,
        'loader': 'BepInEx 5.4.23.5 x64',
        'plugin': name,
        'plugin_sha256': hashlib.sha256(payload).hexdigest(),
    }
    files = {
        name: payload,
        'README.md': ((project / 'README.md').read_text().split('\n## Build from source\n', 1)[0].rstrip() + '\n').encode(),
        'release.json': (json.dumps(metadata, indent=2) + '\n').encode(),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(args.output, 'w') as archive:
        for path, content in files.items():
            entry = ZipInfo(path, (2026, 11, 17, 0, 0, 0))
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, content, compress_type=ZIP_DEFLATED)
    print(args.output)


if __name__ == '__main__':
    main()
