import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest

from optimizer.calculator import calculate_optimal_settings
from optimizer.models import NetworkSettings, HardwareSettings, UsageSettings, ConnectionType, StorageType, TrackerType
from optimizer.optimization_cycle import OptimizationCycle, recommended_preferences
from optimizer.qbittorrent_client import QBittorrentClient, ClientError


def settings():
    return calculate_optimal_settings(NetworkSettings(500, 100, ConnectionType.FIBER, False),
        HardwareSettings(StorageType.NVME, 16, 8), UsageSettings(TrackerType.PUBLIC))


class FakeClient:
    host = "http://127.0.0.1:8080"
    version = "v5.1.0"
    api_version = "2.11.0"
    libtorrent = "2.0.11"
    connected = True

    def __init__(self):
        self.prefs = {"max_connec": 500, "max_connec_per_torrent": 100, "up_limit": 0, "dl_limit": 0,
                      "dht": True, "pex": True, "lsd": True, "encryption": 0,
                      "anonymous_mode": False, "async_io_threads": 10, "current_network_interface": "",
                      "disk_io_read_mode": 0, "disk_io_write_mode": 2,
                      "web_ui_password": "must-not-be-saved"}
        self.reject = False
        self.download = 1024**2
        self.workload = "a" * 40

    def preferences(self):
        return dict(self.prefs)

    def set_preferences(self, values):
        if not self.reject:
            self.prefs.update(values)

    def torrents(self):
        return [{"hash": self.workload, "state": "downloading", "progress": .3, "num_seeds": 10}]

    def transfer(self):
        return {"dl_info_speed": self.download, "up_info_speed": 512 * 1024}

    def interfaces(self):
        return [{"name": "WireGuard", "value": "wg0"}]


@pytest.fixture
def cycle(tmp_path):
    return OptimizationCycle(FakeClient(), tmp_path)


def baseline(cycle):
    return cycle.measure("baseline", warmup=0, samples=30, interval=.001)


def test_complete_cycle_readback_comparison_rollback(cycle):
    baseline(cycle)
    plan = cycle.preview(settings(), {"speed": 100})
    assert plan["after"]["up_limit"] == 9765 * 1024
    cycle.apply({"speed": 100})
    assert cycle.verified
    cycle.client.download *= 2
    cycle.measure("optimized", warmup=0, samples=30, interval=.001)
    assert "observed improvement" in cycle.comparison()
    assert "must-not-be-saved" not in cycle.path.read_text()
    cycle.rollback()
    assert cycle.client.prefs["max_connec"] == 500
    assert not cycle.verified
    assert cycle.client.prefs['disk_io_read_mode'] == 0
    assert cycle.client.prefs['disk_io_write_mode'] == 2


def test_order_and_stale_input_guards(cycle):
    assert cycle.preview(settings(), {})["after"]
    with pytest.raises(ClientError, match="baseline"):
        cycle.apply({})
    with pytest.raises(ClientError, match="verify"):
        cycle.measure("optimized")
    baseline(cycle)
    cycle.preview(settings(), {"speed": 100})
    with pytest.raises(ClientError, match="Inputs changed"):
        cycle.apply({"speed": 500})
    assert cycle.original is None


def test_drift_blocks_apply(cycle):
    baseline(cycle)
    cycle.preview(settings(), {})
    cycle.client.prefs["max_connec"] = 999
    with pytest.raises(ClientError, match="changed since baseline"):
        cycle.apply({})
    assert cycle.original is None


def test_backup_precedes_write_and_rejected_readback_is_not_success(cycle, monkeypatch):
    baseline(cycle)
    cycle.preview(settings(), {})
    def reject(values):
        data = json.loads(cycle.path.read_text())
        assert data["original"]["max_connec"] == 500
        assert not data["verified"]
    cycle.client.set_preferences = reject
    monkeypatch.setattr("optimizer.optimization_cycle.time.sleep", lambda _: None)
    with pytest.raises(ClientError, match="Readback differs"):
        cycle.apply({})
    assert cycle.original and not cycle.verified


def test_api_failure_invalidates_run(cycle):
    def failed():
        raise ClientError("connection lost")
    cycle.client.transfer = failed
    with pytest.raises(ClientError, match="connection lost"):
        baseline(cycle)
    assert cycle.baseline is None


def test_workload_change_blocks_comparison(cycle):
    baseline(cycle)
    cycle.preview(settings(), {})
    cycle.apply({})
    cycle.client.workload = "b" * 40
    with pytest.raises(ClientError, match="differs from the baseline"):
        cycle.measure("optimized", warmup=0, samples=30, interval=.001)


def test_unsupported_required_key_and_libtorrent2_exclusions(cycle):
    target = settings()
    prefs = cycle.client.preferences()
    prefs.update(disk_cache=512, enable_os_cache=True, enable_coalesce_read_write=False)
    requested, omissions = recommended_preferences(target, prefs, "2.0.11")
    assert "disk_cache" not in requested
    assert any("libtorrent 2" in note for note in omissions)
    del prefs["encryption"]
    with pytest.raises(ClientError, match="encryption"):
        recommended_preferences(target, prefs, "2.0.11")


@pytest.mark.parametrize('libtorrent', ['1.2.19', '2.0.11'])
@pytest.mark.parametrize('cache_enabled', [False, True])
def test_official_disk_modes_units_and_enum_contract(cycle, libtorrent, cache_enabled):
    from optimizer.models import ProtocolMode, EncryptionMode
    target = settings()
    target.enable_os_cache = cache_enabled
    target.listening_port = 'Random (55000)'
    prefs = cycle.client.preferences()
    prefs.update(disk_cache=512, enable_coalesce_read_write=False,
                 send_buffer_watermark=500, send_buffer_low_watermark=10,
                 send_buffer_watermark_factor=50, bittorrent_protocol=0,
                 listen_port=6881, random_port=False)
    for protocol, encryption, number in zip(
            [ProtocolMode.UTP_TCP, ProtocolMode.TCP_ONLY, ProtocolMode.UTP_ONLY],
            [EncryptionMode.PREFER, EncryptionMode.REQUIRE, EncryptionMode.DISABLED], [0, 1, 2]):
        target.protocol_mode, target.encryption_mode = protocol, encryption
        requested, _ = recommended_preferences(target, prefs, libtorrent)
        assert requested['disk_io_read_mode'] == requested['disk_io_write_mode'] == int(cache_enabled)
        assert type(requested['disk_io_read_mode']) is int
        assert 'enable_os_cache' not in requested
        assert requested['up_limit'] == 9_999_360  # API bytes/s; stored speed is 9765 KiB/s.
        assert requested['send_buffer_watermark'] == 500  # API KiB, not bytes.
        assert requested['send_buffer_low_watermark'] == 16
        assert requested['bittorrent_protocol'] == requested['encryption'] == number
        assert requested['listen_port'] == 55000 and requested['random_port'] is False
        assert ('disk_cache' in requested) == libtorrent.startswith('1.')
        assert ('enable_coalesce_read_write' in requested) == libtorrent.startswith('1.')
        assert requested.keys() <= cycle._safe_preferences(requested).keys()
    from ui.tabs.results_tab import display
    assert display('disk_io_write_mode', 2) == 'Write-through'
    assert display('disk_io_read_mode', 0) == 'OS cache disabled'


def test_nonexistent_vpn_interface_blocks_preview(cycle):
    baseline(cycle)
    target = settings()
    target.network_interface = "missing"
    with pytest.raises(ClientError, match="does not exist"):
        cycle.preview(target, {})


def test_vpn_human_name_resolves_to_target_identifier(cycle):
    baseline(cycle)
    target = settings()
    target.network_interface = "WireGuard"
    plan = cycle.preview(target, {})
    assert plan["after"]["current_network_interface"] == "wg0"


def test_restore_after_application_restart(cycle, tmp_path):
    baseline(cycle)
    cycle.preview(settings(), {})
    cycle.apply({})
    fresh = OptimizationCycle(cycle.client, tmp_path)
    fresh.restore_backup(cycle.path)
    assert cycle.client.prefs["max_connec"] == 500


def test_external_changes_block_rollback(cycle):
    baseline(cycle)
    cycle.preview(settings(), {})
    cycle.apply({})
    cycle.client.prefs["max_connec"] = 777
    with pytest.raises(ClientError, match="outside this cycle"):
        cycle.rollback()
    assert cycle.original


def test_failed_restore_keeps_backup_but_clears_verified_status(cycle, monkeypatch):
    baseline(cycle)
    cycle.preview(settings(), {})
    cycle.apply({})
    def partial_restore(values):
        key = next(iter(values))
        cycle.client.prefs[key] = values[key]
        raise ClientError('lost response')
    monkeypatch.setattr(cycle.client, 'set_preferences', partial_restore)
    with pytest.raises(ClientError, match='lost response'):
        cycle.rollback()
    assert cycle.original
    assert not cycle.verified
    saved = json.loads(cycle.path.read_text())
    assert saved['original'] == cycle.original
    assert saved['verified'] is False


def test_backup_write_failure_never_applies_settings(cycle, monkeypatch):
    baseline(cycle)
    cycle.preview(settings(), {})
    original = dict(cycle.client.prefs)
    def disk_full():
        raise OSError('disk full')
    monkeypatch.setattr(cycle, 'persist', disk_full)
    with pytest.raises(OSError, match='disk full'):
        cycle.apply({})
    assert cycle.client.prefs == original
    assert cycle.original is None and cycle.applied is None
    assert not cycle.verified


def test_real_http_api_login_schema_apply_readback(tmp_path):
    state = FakeClient()
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass
        def reply(self, value, json_response=False):
            self.send_response(200)
            self.end_headers()
            self.wfile.write((json.dumps(value) if json_response else value).encode())
        def do_GET(self):
            if self.headers.get("Cookie") != "SID=valid":
                self.send_error(403)
                return
            endpoint = self.path.rsplit("/", 1)[-1]
            if endpoint == "version": self.reply(state.version)
            elif endpoint == "webapiVersion": self.reply(state.api_version)
            elif endpoint == "buildInfo": self.reply({"libtorrent": state.libtorrent}, True)
            elif endpoint == "preferences": self.reply(state.preferences(), True)
            else:
                self.send_error(404)
        def do_POST(self):
            from urllib.parse import parse_qs
            data = parse_qs(self.rfile.read(int(self.headers.get("Content-Length", 0))).decode())
            if self.path.endswith("auth/login"):
                if data.get("password") == ["correct"]:
                    self.send_response(200)
                    self.send_header("Set-Cookie", "SID=valid; Path=/")
                    self.end_headers()
                    self.wfile.write(b"Ok.")
                else:
                    self.reply("Fails.")
            elif self.path.endswith("setPreferences"):
                state.set_preferences(json.loads(data["json"][0]))
                self.reply("")
            else:
                self.send_error(404)
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    client = QBittorrentClient(f"http://127.0.0.1:{server.server_port}")
    try:
        with pytest.raises(ClientError, match="Login failed"):
            client.connect("admin", "bad")
        assert not client.connected
        client.connect("admin", "correct")
        assert client.connected
        client.set_preferences({"max_connec": 1000})
        assert client.preferences()["max_connec"] == 1000
        state.version = "v6.0.0"
        with pytest.raises(ClientError, match="Unsupported"):
            client.connect("admin", "correct")
        assert not client.connected
    finally:
        client.disconnect()
        server.shutdown()
        server.server_close()


def test_idle_session_rejected_before_sampling(cycle):
    cycle.client.transfer = lambda: {'dl_info_speed': 0, 'up_info_speed': 0}
    with pytest.raises(ClientError, match='No active download or upload traffic'):
        baseline(cycle)
    assert cycle.baseline is None


def test_completed_download_rejected_before_after_sampling(cycle):
    baseline(cycle)
    cycle.preview(settings(), {})
    cycle.apply({})
    cycle.client.torrents = lambda: [{'hash': cycle.baseline['workload'][0], 'state': 'uploading', 'progress': 1}]
    with pytest.raises(ClientError, match='now complete or stopped'):
        cycle.measure('optimized', warmup=0, samples=30, interval=.001)
    assert cycle.optimized is None and cycle.verified


def test_download_finishing_mid_test_is_not_a_result(cycle):
    count = 0
    original = cycle.client.torrents
    def torrents():
        nonlocal count
        count += 1
        values = original()
        if count > 4:
            values[0].update(progress=1, state='uploading')
        return values
    cycle.client.torrents = torrents
    with pytest.raises(ClientError, match='completed during'):
        baseline(cycle)
    assert cycle.baseline is None


def test_unrelated_preferences_do_not_invalidate_speed_test(cycle):
    original = cycle.client.preferences
    counter = 0
    def prefs():
        nonlocal counter
        counter += 1
        return dict(original(), unrelated_ui_runtime_counter=counter)
    cycle.client.preferences = prefs
    assert baseline(cycle)


def test_measurement_persists_curve_and_peer_counts(cycle):
    original = cycle.client.torrents
    def torrents():
        values = original()
        values[0].update(dlspeed=2*1024**2, upspeed=100, num_seeds=4, num_leechs=2)
        return values
    cycle.client.torrents = torrents
    result = baseline(cycle)
    assert len(result['ramp_trace']['samples']) == 30
    assert result['ramp_trace']['fresh_start'] is False
    assert result['ramp_metrics']['reference_download_mib_s'] == 2
    assert result['ramp_trace']['samples'][0]['peers'] == 6
    assert 'ramp_trace' in json.loads(cycle.path.read_text())['baseline']


def test_startup_clock_is_not_persisted_as_reusable_launch_time(cycle):
    cycle.test_workload = {'hash': 'a', '_startup_origin': 1234, '_startup_points': []}
    cycle.persist()
    saved = json.loads(cycle.path.read_text())
    assert saved['test_workload'] == {'hash': 'a'}
