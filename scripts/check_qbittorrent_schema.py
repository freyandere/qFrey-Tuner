"""Read-only contract check against tagged official qBittorrent API controllers."""
from pathlib import Path
import re
import sys
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from optimizer.calculator import calculate_optimal_settings
from optimizer.models import ConnectionType, HardwareSettings, NetworkSettings, StorageType, TrackerType, UsageSettings
from optimizer.optimization_cycle import recommended_preferences

RELEASES = ('4.6.0', '4.6.7', '5.0.0', '5.1.0', '5.2.0')


def check_source(source: str, release: str) -> None:
    """Require every generated key to be readable and writable with its JSON type."""
    read, write = source.split('void AppController::setPreferencesAction()', 1)
    keys = re.findall(r'data\[u?"([a-z_]+)"(?:_s)?\]\s*=', read)
    current = dict.fromkeys(keys, 0)
    for key in ('limit_utp_rate', 'queueing_enabled', 'preallocate_all', 'anonymous_mode',
                'dht', 'pex', 'lsd', 'enable_coalesce_read_write', 'random_port'):
        if key in current:
            current[key] = False
    if 'current_network_interface' in current:
        current['current_network_interface'] = ''
    settings = calculate_optimal_settings(
        NetworkSettings(500, 100, ConnectionType.FIBER, True, 'wg0', True),
        HardwareSettings(StorageType.NVME, 16, 8), UsageSettings(TrackerType.PUBLIC))
    for libtorrent in ('1.2.19', '2.0.11'):
        proposed, omissions = recommended_preferences(settings, current, libtorrent)
        # Do not let the live-schema filter hide a removed or mistyped optional key.
        unsupported = [note for note in omissions if 'unsupported or incompatible' in note]
        if unsupported:
            raise ValueError(f'{release}: {unsupported}')
        for key, value in proposed.items():
            marker = f'hasKey(u"{key}"_s)'
            if key not in keys or marker not in write:
                raise ValueError(f'{release}: {key} is not readable/writable')
            block = write.split(marker, 1)[1].split('hasKey(', 1)[0]
            conversion = 'toBool' if type(value) is bool else 'toInt' if type(value) is int else 'toString'
            if f'.{conversion}(' not in block:
                raise ValueError(f'{release}: {key} does not accept {type(value).__name__}')
        print(f'qBittorrent {release}, libtorrent branch {libtorrent}: {len(proposed)} API keys/types checked')


def main() -> None:
    for release in RELEASES:
        url = f'https://raw.githubusercontent.com/qbittorrent/qBittorrent/release-{release}/src/webui/api/appcontroller.cpp'
        with urllib.request.urlopen(url, timeout=30) as response:
            check_source(response.read().decode('utf-8'), release)


if __name__ == '__main__':
    main()
