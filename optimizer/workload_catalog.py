"""Official single-file workloads sized against the ideal line rate."""
from dataclasses import dataclass
import math

WARMUP_SECONDS = 10
SAMPLE_SECONDS = 60
SAFETY_SECONDS = 20
REQUIRED_SECONDS = WARMUP_SECONDS + SAMPLE_SECONDS + SAFETY_SECONDS


@dataclass(frozen=True)
class Workload:
    key: str
    label: str
    name: bytes
    url: str
    size_bytes: int
    info_hash: str = ""


UBUNTU = Workload('ubuntu', 'Ubuntu 22.04.5', b'ubuntu-22.04.5-desktop-amd64.iso',
    'https://releases.ubuntu.com/22.04.5/ubuntu-22.04.5-desktop-amd64.iso.torrent', 4762707968)
KALI = Workload('kali', 'Kali Linux Everything 2026.2', b'kali-linux-2026.2-installer-everything-amd64.iso',
    'https://cdimage.kali.org/kali-2026.2/kali-linux-2026.2-installer-everything-amd64.iso.torrent', 14522228736, '1f7482c9dad2653af474a806a93e6c266e1a1b3c')
WORKLOADS = {item.key: item for item in (UBUNTU, KALI)}


def record_workload(record):
    key = record.get('workload_id')
    if key is None:
        key = next((item.key for item in WORKLOADS.values() if item.url == record.get('source')), 'ubuntu')
    if key not in WORKLOADS:
        raise ValueError('Unknown official test image. Start a new test from Speed test.')
    return WORKLOADS[key]


def workload_recommendation(download_mbps):
    speed = float(download_mbps)
    if not math.isfinite(speed) or speed <= 0:
        raise ValueError('Enter a positive finite download speed or run Speedtest first.')
    bytes_per_second = speed * 1_000_000 / 8
    required_bytes = math.ceil(bytes_per_second * REQUIRED_SECONDS)
    selected = next((item for item in WORKLOADS.values() if item.size_bytes >= required_bytes), None)
    return {'workload': selected, 'required_bytes': required_bytes, 'speed_mbps': speed,
            'required_seconds': REQUIRED_SECONDS,
            'ubuntu_seconds': UBUNTU.size_bytes / bytes_per_second,
            'kali_seconds': KALI.size_bytes / bytes_per_second}
