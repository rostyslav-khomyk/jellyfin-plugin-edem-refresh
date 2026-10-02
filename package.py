"""Package a built plugin and its Jellyfin catalog manifest (stdlib only)."""
import hashlib
import json
import zipfile
from datetime import datetime, timezone
from pathlib import Path

root = Path(__file__).resolve().parent
output = root / 'artifacts'
output.mkdir(exist_ok=True)
version = '0.1.0.0'
archive = output / f'Jellyfin.Plugin.EdemRefresh_{version}.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
    dll = root / 'bin/Release/net10.0/Jellyfin.Plugin.EdemRefresh.dll'
    assert dll.stat().st_size > 0
    z.write(dll, dll.name)
manifest = [{
    'guid': '0b61a5e2-d37d-4e6a-9f83-fc7818e3d82a',
    'name': 'Edem Playlist & EPG Refresh',
    'overview': 'Daily matched M3U and cleaned XMLTV updates',
    'description': 'Processes original provider sources, preserves streams and groups, matches EPG IDs, cleans programmes and queues guide refresh.',
    'owner': 'rostyslav-khomyk', 'category': 'Live TV',
    'versions': [{'version': version, 'targetAbi': '12.1.0.0',
                  'changelog': 'Initial release for Jellyfin 12.1 (.NET 10).',
                  'sourceUrl': f'https://github.com/rostyslav-khomyk/jellyfin-plugin-edem-refresh/releases/download/v{version}/{archive.name}',
                  'checksum': hashlib.md5(archive.read_bytes()).hexdigest().upper(),
                  'timestamp': datetime.now(timezone.utc).isoformat()}]
}]
(output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(archive)
