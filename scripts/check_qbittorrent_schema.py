"""Read-only contract check against tagged official qBittorrent API controllers."""
from pathlib import Path
import argparse
import json
import re
import sys
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from optimizer.calculator import calculate_optimal_settings
from optimizer.models import ConnectionType, HardwareSettings, NetworkSettings, StorageType, TrackerType, UsageSettings
from optimizer.optimization_cycle import recommended_preferences

RELEASES = ('4.6.0', '4.6.7', '5.0.0', '5.1.0', '5.2.0')
LIBTORRENT_BRANCHES = ('1.2.19', '2.0.11')


def load_payloads(path: Path) -> dict:
    """Read both actual mapper exports; never substitute a missing branch."""
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f'Duplicate payload key: {key}')
            result[key] = value
        return result

    data = json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique_object)
    if not isinstance(data, dict) or set(data) != set(LIBTORRENT_BRANCHES):
        raise ValueError('Payload export requires exactly libtorrent 1.2.19 and 2.0.11')
    result = {}
    for branch in LIBTORRENT_BRANCHES:
        entry = data[branch]
        if not isinstance(entry, dict) or not isinstance(entry.get('payload'), dict) or not entry['payload']:
            raise ValueError(f'{branch}: a nonempty payload object is required')
        result[branch] = entry['payload']
        for key, value in result[branch].items():
            if type(value) not in (bool, int, str) or (type(value) is int and not -2147483648 <= value <= 2147483647):
                raise ValueError(f'{branch}: {key} has an invalid API value/type')
    return result


def check_source(source: str, release: str, payloads: dict | None = None) -> None:
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
    for libtorrent in LIBTORRENT_BRANCHES:
        proposed, omissions = recommended_preferences(settings, current, libtorrent)
        # Do not let the live-schema filter hide a removed or mistyped optional key.
        unsupported = [note for note in omissions if 'unsupported or incompatible' in note]
        if unsupported:
            raise ValueError(f'{release}: {unsupported}')
        if payloads is not None:
            actual = payloads[libtorrent]
            if actual.keys() != proposed.keys():
                raise ValueError(f'{release}, {libtorrent}: incomplete or unexpected payload keys; '
                                 f'missing={sorted(proposed.keys() - actual.keys())}, '
                                 f'extra={sorted(actual.keys() - proposed.keys())}')
            for key, value in actual.items():
                if type(value) is not type(proposed[key]):
                    raise ValueError(f'{release}, {libtorrent}: {key} has incompatible payload type')
            proposed = actual
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--payloads', type=Path, help='UTF-8 JSON of actual C# mapper payloads for both engine branches')
    args = parser.parse_args()
    payloads = load_payloads(args.payloads) if args.payloads else None
    for release in RELEASES:
        url = f'https://raw.githubusercontent.com/qbittorrent/qBittorrent/release-{release}/src/webui/api/appcontroller.cpp'
        with urllib.request.urlopen(url, timeout=30) as response:
            check_source(response.read().decode('utf-8'), release, payloads)


if __name__ == '__main__':
    main()
