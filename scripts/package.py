"""Build catalogue metadata from an already-built Release DLL."""
from pathlib import Path
import hashlib
import json
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'AnimeScheduleSync'
metadata = json.loads((PROJECT / 'meta.json').read_text())
version = metadata['version']
archive = ROOT / 'packages' / f'animeschedule-sync_{version}.zip'
archive.parent.mkdir(exist_ok=True)
dll = PROJECT / 'bin/Release/net10.0/Jellyfin.Plugin.AnimeScheduleSync.dll'
if not dll.exists():
    raise SystemExit('Run dotnet build AnimeScheduleSync -c Release first.')
# Fixed timestamps make repackaging the same build reproducible.
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
    for name, data in [('Jellyfin.Plugin.AnimeScheduleSync.dll', dll.read_bytes()), ('meta.json', (PROJECT / 'meta.json').read_bytes())]:
        info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, data)
entry = {
    'version': version,
    'changelog': 'Multi-user Personal / Server / None modes, permission controls, safe migration and isolated retries. Catalogue-ready packaging.',
    'targetAbi': metadata['targetAbi'],
    'sourceUrl': 'https://raw.githubusercontent.com/gohun04/jellyfin-plugin-animeschedule-sync/main/packages/' + archive.name,
    'checksum': hashlib.md5(archive.read_bytes()).hexdigest(),  # Jellyfin catalogue format requires MD5.
    'timestamp': metadata['timestamp'],
}
manifest_path = ROOT / 'manifest.json'
manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else [{
    'guid': metadata['guid'], 'name': metadata['name'], 'overview': metadata['overview'],
    'description': metadata['description'], 'owner': 'gohun04', 'category': 'Other', 'versions': [],
}]
package = next(x for x in manifest if x['guid'] == metadata['guid'])
package['versions'] = [entry] + [x for x in package['versions'] if x['version'] != version]
package['versions'].sort(key=lambda x: tuple(map(int, x['version'].split('.'))), reverse=True)
manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')
(ROOT / 'SHA256SUMS').write_text(''.join(hashlib.sha256(f.read_bytes()).hexdigest() + '  packages/' + f.name + '\n' for f in sorted(archive.parent.glob('*.zip'))))
print(archive.name)
