"""Regenerate W00 golden fixtures using the checked-in Python implementation."""
from dataclasses import asdict, replace
from enum import Enum
import itertools
import json
from pathlib import Path
import tempfile

from optimizer import calculator
from optimizer.calculator import calculate_optimal_settings
from optimizer.models import (
    ConnectionType, EnvironmentProfile, HardwareSettings, NetworkSettings,
    StorageType, TrackerType, UsageSettings, UserRole,
)
from optimizer.optimization_cycle import OptimizationCycle, recommended_preferences
from optimizer.ramp_metrics import ramp_metrics, trace_point
from optimizer import test_workload
from optimizer.workload_catalog import KALI, REQUIRED_SECONDS, UBUNTU, workload_recommendation

ROOT = Path(__file__).parent
CACHE = ROOT.resolve().parents[2] / '.cache' / 'fixture-generation'


def jsonable(value):
    if isinstance(value, Enum):
        return value.name
    if isinstance(value, dict):
        return {key: jsonable(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [jsonable(item) for item in value]
    return value


def write_json(name, value):
    (ROOT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def calculator_matrix():
    rows = []
    for tracker, role, environment, storage in itertools.product(
        TrackerType, UserRole, EnvironmentProfile, StorageType
    ):
        network = NetworkSettings(100, 100, ConnectionType.FIBER, False)
        hardware = HardwareSettings(storage, 16, 8)
        usage = UsageSettings(tracker, role, environment)
        result = calculate_optimal_settings(network, hardware, usage)
        rows.append({"id": f"{tracker.name}-{role.name}-{environment.name}-{storage.name}",
                     "input": {"network": jsonable(asdict(network)),
                               "hardware": jsonable(asdict(hardware)),
                               "usage": jsonable(asdict(usage))},
                     "output": jsonable(asdict(result))})

    for speed in (49.999, 50, 99.999, 100, 299.999, 300, 499.999, 500):
        network = NetworkSettings(speed, speed, ConnectionType.CABLE_DSL, False)
        hardware = HardwareSettings(StorageType.SSD_SATA, 8, 4)
        usage = UsageSettings(TrackerType.PUBLIC, UserRole.LEECHER, EnvironmentProfile.SYSTEM)
        result = calculate_optimal_settings(network, hardware, usage)
        rows.append({"id": f"speed-boundary-{speed}",
                     "input": {"network": jsonable(asdict(network)),
                               "hardware": jsonable(asdict(hardware)),
                               "usage": jsonable(asdict(usage))},
                     "output": jsonable(asdict(result))})

    network = NetworkSettings(500, 100, ConnectionType.FIBER, True, "wg0", True)
    hardware = HardwareSettings(StorageType.NVME, 16, 8)
    usage = UsageSettings(TrackerType.PUBLIC, UserRole.LEECHER, EnvironmentProfile.SYSTEM)
    old_randint = calculator.random.randint
    calculator.random.randint = lambda low, high: 55000
    try:
        result = calculate_optimal_settings(network, hardware, usage)
    finally:
        calculator.random.randint = old_randint
    rows.append({"id": "fixed-random-port", "input": {"network": jsonable(asdict(network)),
                 "hardware": jsonable(asdict(hardware)), "usage": jsonable(asdict(usage))},
                 "output": jsonable(asdict(result))})
    return rows


def settings_payloads():
    network = NetworkSettings(500, 100, ConnectionType.FIBER, True, "wg0", True)
    hardware = HardwareSettings(StorageType.NVME, 16, 8)
    usage = UsageSettings(TrackerType.PUBLIC, UserRole.LEECHER, EnvironmentProfile.SYSTEM)
    old_randint = calculator.random.randint
    calculator.random.randint = lambda low, high: 55000
    try:
        settings = calculate_optimal_settings(network, hardware, usage)
    finally:
        calculator.random.randint = old_randint

    payloads = {}
    for libtorrent in ("1.2.19", "2.0.11"):
        # Synthetic live schema values with the exact types expected by the Python converter.
        current = {"up_limit": 0, "dl_limit": 0, "max_connec": 500,
                   "max_connec_per_torrent": 100, "max_uploads": 50,
                   "max_uploads_per_torrent": 10, "limit_utp_rate": True,
                   "queueing_enabled": True, "max_active_downloads": 5,
                   "max_active_uploads": 8, "max_active_torrents": 13,
                   "preallocate_all": True, "async_io_threads": 8,
                   "disk_io_read_mode": 0, "disk_io_write_mode": 2,
                   "bittorrent_protocol": 0, "send_buffer_watermark": 500,
                   "send_buffer_low_watermark": 16, "send_buffer_watermark_factor": 100,
                   "socket_backlog_size": 30, "connection_speed": 100,
                   "encryption": 0, "anonymous_mode": False, "dht": True,
                   "pex": True, "lsd": True, "current_network_interface": "",
                   "listen_port": 6881, "random_port": True}
        if libtorrent.startswith("1."):
            current.update(disk_cache=0, enable_coalesce_read_write=True)
        payload, omitted = recommended_preferences(settings, current, libtorrent)
        payloads[libtorrent] = {"input": {"libtorrent": libtorrent, "randomPort": 55000},
                                "payload": payload, "omitted": omitted}
    return payloads


class FixtureClient:
    host = "http://127.0.0.1:8080"
    version = "v5.1.0"
    api_version = "2.11.0"
    libtorrent = "2.0.11"


def legacy_cycle():
    client = FixtureClient()
    cycle = OptimizationCycle(client, ROOT)
    cycle.baseline = {
        "workload": ["a" * 40],
        "samples": [{"elapsed": 0.0, "download": 1048576, "upload": 262144, "dht_nodes": 12},
                    {"elapsed": 1.0, "download": 1258291, "upload": 262144, "dht_nodes": 12}],
        "preferences": {"max_connec": 500, "up_limit": 0, "listen_port": 6881, "random_port": False},
        "mean_download_mib_s": 1.1, "mean_upload_mib_s": 0.25,
        "download_stddev": 104857.5, "upload_stddev": 0.0,
        "warmup_seconds": 10, "interval_seconds": 1,
        "torrent_context": [{"hash": "a" * 40, "progress": 0.3, "state": "downloading",
                             "num_seeds": 8, "num_leechs": 3}],
        # Older cycles may not have ramp_trace/ramp_metrics.
    }
    cycle.plan = {"before": {"max_connec": 500}, "after": {"max_connec": 1000},
                  "inputs": {"speed": 500}, "omitted": [], "explanations": {}, "warnings": []}
    cycle.original = {"max_connec": 500}
    cycle.applied = None  # A legacy recovery case can have an original snapshot without applied values.
    cycle.verified = False
    cycle.failure = ""
    cycle.test_workload = {"host": client.host, "hash": "a" * 40,
                           "tag": "qfrey-test-" + "b" * 32, "bytes": UBUNTU.size_bytes,
                           "save_path": "<fixture-download-path>", "source": UBUNTU.url,
                           "workload_id": UBUNTU.key}
    CACHE.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=CACHE) as temp:
        cycle.path = Path(temp) / "cycle.json"
        cycle.persist()
        data = json.loads(cycle.path.read_text(encoding="utf-8"))
    data["created_or_updated_utc"] = "2026-10-05T00:00:00+00:00"
    return data


def ramp_fixture():
    trace = {"fresh_start": True, "scope": "test torrent", "samples": [
        trace_point([{"hash": "a" * 40, "dlspeed": speed * 1048576, "upspeed": 0,
                      "num_seeds": seeds, "num_leechs": peers,
                      "state": "downloading"}], second,
                    "measurement" if second >= 5 else "warmup", {"a" * 40})
        for second, speed, seeds, peers in [
            (0, 0, 0, 0), (1, 10, 0, 1), (2, 20, 3, 2), (3, 55, 3, 2),
            (4, 70, 3, 2), (5, 100, 3, 2), (6, 100, 3, 2), (7, 100, 3, 2),
            (8, 100, 3, 2), (9, 100, 3, 2), (10, 100, 3, 2)]]}
    return {"input": trace, "output": ramp_metrics(trace)}


def workload_record():
    # Exercise the legacy writer with synthetic metadata and an in-memory API client.
    name = test_workload.NAME
    raw = b"d4:infod6:lengthi123e4:name" + str(len(name)).encode() + b":" + name + b"ee"
    workload = replace(UBUNTU, size_bytes=123)
    synthetic_hash, _ = test_workload.metadata_info(raw, name)

    class AddClient:
        host = "http://127.0.0.1:8080"

        def __init__(self):
            self.items = []

        def torrents(self):
            return list(self.items)

        def request(self, method, endpoint, **kwargs):
            self.items.append({"hash": synthetic_hash, "tags": kwargs["data"]["tags"],
                               "name": name.decode(), "state": "downloading"})

    old_fetch = test_workload.fetch_metadata
    old_uuid = test_workload.uuid.uuid4
    test_workload.fetch_metadata = lambda *_: raw
    test_workload.uuid.uuid4 = lambda: type("FixtureUUID", (), {"hex": "b" * 32})()
    try:
        CACHE.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=CACHE) as temp:
            path = Path(temp) / "test-workload.json"
            test_workload.add_workload(AddClient(), "<fixture-download-path>", path, workload)
            return json.loads(path.read_text(encoding="utf-8"))
    finally:
        test_workload.fetch_metadata = old_fetch
        test_workload.uuid.uuid4 = old_uuid


def main():
    # These are sizing outputs from the existing pure catalog function; no metadata is fetched.
    workloads = {str(speed): {key: (value.key if key == "workload" and value else value)
                              for key, value in workload_recommendation(speed).items()}
                 for speed in (100, 400, 500, 1000, 2500)}
    workloads["requiredSeconds"] = REQUIRED_SECONDS
    workloads["officialImageBytes"] = {"ubuntu": UBUNTU.size_bytes, "kali": KALI.size_bytes}
    workloads["legacyRecord"] = workload_record()
    write_json("calculator-matrix.json", {"sourceSha": "398095c13f334a91cc351e7fa62d5ceaab92e7ef",
                                         "cases": calculator_matrix()})
    write_json("settings-payloads.json", settings_payloads())
    write_json("legacy-cycle.json", legacy_cycle())
    write_json("legacy-workload-and-ramp.json", {"workload": workloads, "ramp": ramp_fixture()})


if __name__ == "__main__":
    main()
