"""Verify catalogue metadata and installation ZIP structure without network access."""
from pathlib import Path
import hashlib
import json
import zipfile
ROOT = Path(__file__).resolve().parents[1]
for package in json.loads((ROOT / 'manifest.json').read_text()):
    for version in package['versions']:
        archive = ROOT / 'packages' / version['sourceUrl'].rsplit('/', 1)[-1]
        assert hashlib.md5(archive.read_bytes()).hexdigest() == version['checksum'].lower()
        with zipfile.ZipFile(archive) as z:
            assert z.testzip() is None
            assert set(z.namelist()) == {'Jellyfin.Plugin.AnimeScheduleSync.dll', 'meta.json'}
            metadata = json.loads(z.read('meta.json'))
            assert metadata['guid'] == package['guid']
            assert metadata['version'] == version['version']
            assert metadata['targetAbi'] == version['targetAbi']
        print('Valid:', package['name'], version['version'])
