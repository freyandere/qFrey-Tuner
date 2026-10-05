"""GUI smoke checks; no real qBittorrent requests or lifecycle changes."""
from pathlib import Path
import pytest
from optimizer.config_manager import ConfigManager
from ui.main_window import MainWindow


@pytest.fixture(scope="module")
def window(tmp_path_factory):
    tmp_path = tmp_path_factory.mktemp("ui-workflow")
    monkeypatch = pytest.MonkeyPatch()
    monkeypatch.setattr(ConfigManager, "app_directory", staticmethod(lambda: tmp_path))
    from optimizer.process_manager import ProcessManager
    monkeypatch.setattr(ProcessManager, "discover", lambda: [])
    window = MainWindow(ConfigManager())
    window.withdraw()
    yield window
    window._close()
    monkeypatch.undo()


def test_missing_target_locks_navigation_and_actions(window):
    assert window.tab_view.get() == "Connect"
    assert all(button.cget("state") == "disabled" for button in window.tab_view._segmented_button._buttons_dict.values())
    window.tab_view.set("Network")
    window._gate_navigation()
    assert window.tab_view.get() == "Connect"
    assert window.results_tab.apply_btn.cget("state") == "disabled"
    assert window.results_tab.calc_btn.cget("state") == "disabled"
    assert window.benchmark_tab.base_btn.cget("state") == "disabled"


def test_version_read_and_preserved_environment(window):
    import tomllib
    source = Path(__file__).resolve().parents[1] / 'pyproject.toml'
    assert window._get_version() == tomllib.loads(source.read_text(encoding='utf-8'))['project']['version']
    assert window.usage_tab.get_settings().environment == window.config_manager.profile


def test_endpoint_edits_require_revalidation(window):
    window.client.connected = True
    window.host_entry.delete(0, "end")
    window.host_entry.insert(0, "http://127.0.0.1:9090")
    window._controls()
    assert not window._target_ready()
    assert window.benchmark_tab.base_btn.cget("state") == "disabled"
    window.client.connected = False
    window.host_entry.delete(0, "end")
    window.host_entry.insert(0, window.client.host)


def test_measured_speeds_not_rounded_to_slider_steps(window):
    tab = window.network_tab
    tab._on_test_finished(237.4, 17.3, "test")
    assert tab.get_settings().download_speed_mbps == 237.4
    assert tab.get_settings().upload_speed_mbps == 17.3


def test_guided_confirmation_and_stable_controls(window, monkeypatch):
    from types import SimpleNamespace
    from unittest.mock import Mock
    window.client.connected = True
    window.cycle = SimpleNamespace(baseline=None, plan=None, original=None, verified=False)
    window.tab_view.set("Network")
    preview = Mock()
    monkeypatch.setattr(window, "preview", preview)
    for expected in ("Hardware", "Your goals", "Speed test", "Review changes"):
        window.guide_next()
        assert window.tab_view.get() == expected
    assert window.reviewed == {"Network", "Hardware", "Your goals"}
    preview.assert_called_once()
    assert window.results_tab.calc_btn.cget("state") == "normal"
    assert window.results_tab.apply_btn.cget("state") == "disabled"
    configure = Mock(wraps=window.tab_view.configure)
    monkeypatch.setattr(window.tab_view, "configure", configure)
    for _ in range(10):
        window._controls()
    configure.assert_not_called()
    window.client.connected = False
    window.cycle = None
    window.reviewed.clear()
    window._controls()


def test_hardware_button_updates_storage(window, monkeypatch):
    tab = window.hardware_tab
    monkeypatch.setattr(tab.detector, "get_main_disk_type", lambda path: "NVMe")
    monkeypatch.setattr(tab.detector, "get_total_ram_gb", lambda: 32)
    monkeypatch.setattr(tab.detector, "get_cpu_info", lambda: {"physical_cores": 8, "is_hybrid": False})
    tab._on_autodetect()
    assert tab.storage_var.get() == "NVMe"


def test_api_key_paste_remains_masked(window, monkeypatch):
    monkeypatch.setattr(window, "clipboard_get", lambda: "  test-key  \n")
    assert window.paste_credential(window.api_key_entry) == "break"
    assert window.api_key_entry.get() == "test-key"
    assert window.api_key_entry.cget("show") == "*"
    window.api_key_entry.delete(0, "end")


def test_rapid_page_switch_keeps_latest_page_visible(window):
    # Reproduce connect: a queued Connect cleanup runs after Network is selected.
    window.tab_view.set("Connect")
    window.tab_view.set("Network")
    window.tab_view._grid_forget_all_tabs(exclude_name="Connect")
    assert window.tab_view.tab("Network").winfo_manager() == "grid"
    assert window.tab_view.tab("Connect").winfo_manager() == ""
    window.tab_view.set("Connect")


def test_result_chart_and_plain_language_settings(window):
    from types import SimpleNamespace
    from ui.tabs.results_tab import display
    assert display("up_limit", 1048576) == "1.00 MiB/s"
    assert display("dl_limit", 0) == "Unlimited"
    window.outcome_tab.show_cycle(SimpleNamespace(
        baseline={"mean_download_mib_s": 50, "mean_upload_mib_s": 0},
        optimized={"mean_download_mib_s": 55, "mean_upload_mib_s": 0},
        original={"up_limit": 0}, path="test-backup.json", comparison=lambda: "Observed difference"))
    assert len(window.outcome_tab.chart.find_all()) == 14


def test_every_page_has_visible_content_after_queued_navigation(window):
    window.deiconify()
    window.update()
    pages = ('Connect', 'Network', 'Hardware', 'Your goals', 'Speed test', 'Review changes', 'Results')
    for name in pages:
        window.tab_view.set(name)
        # Exercise the exact stale callback that used to blank the next page.
        window.tab_view._grid_forget_all_tabs(exclude_name='Connect')
        window.update_idletasks()
        frame = window.tab_view.tab(name)
        assert frame.winfo_ismapped(), name
        assert frame.winfo_width() > 800, name
        assert frame.winfo_height() > 400, name
        assert frame.winfo_children(), name
    window.tab_view.set('Connect')
    window.withdraw()


def test_recommendations_show_cards_without_overwriting_plan(window):
    from types import SimpleNamespace
    window.cycle = SimpleNamespace(baseline=True)
    window.results_tab.show_plan({
        'after': {'up_limit': 1048576, 'max_connec': 500},
        'before': {'up_limit': 0, 'max_connec': 200},
        'warnings': [], 'omitted': ['Existing network binding is preserved.']})
    assert '2 proposed changes' in window.results_tab.summary.cget('text')
    assert len(window.results_tab.changes.winfo_children()) == 3
    window.results_tab.set_report('Changes applied; second speed test is running.')
    assert len(window.results_tab.changes.winfo_children()) == 3
    window.cycle = None


def test_connection_explanation_tracks_selection(window):
    from optimizer.models import ConnectionType
    window.network_tab.conn_type_var.set(ConnectionType.CABLE_DSL.value)
    assert 'telephone line' in window.network_tab.conn_detail_text.cget('text')
    window.network_tab.conn_type_var.set(ConnectionType.FIBER.value)
    assert 'recommends TCP' in window.network_tab.conn_detail_text.cget('text')


def test_managed_after_measurement_prepares_fresh_workload_first(window, monkeypatch):
    from types import SimpleNamespace
    from unittest.mock import Mock
    order = []
    record = {'hash': 'a', 'save_path': 'E:/test'}
    cycle = SimpleNamespace(test_workload=record, persist=lambda: order.append('save'),
                            measure=lambda *args, **kwargs: order.append('measure'))
    monkeypatch.setattr('optimizer.test_workload.restart_workload', lambda *args: order.append('restart') or record)
    monkeypatch.setattr(window, '_require_target', lambda: None)
    jobs = []
    monkeypatch.setattr(window, '_job', lambda operation, *args, **kwargs: jobs.append(operation))
    window.cycle = cycle
    try:
        window.measure('optimized', restart_test=True)
        jobs[0]()
        assert order == ['restart', 'save', 'measure']
    finally:
        window.cycle = None


def test_speedtest_result_recommends_larger_image_and_labels_source(window):
    window.network_tab._on_test_finished(1000, 100, 'test')
    window._controls()
    assert window.benchmark_tab.recommendation['workload'].key == 'kali'
    assert '14.52 GB' in window.benchmark_tab.choice_title.cget('text')
    assert 'Measured by Speedtest' in window.benchmark_tab.choice_detail.cget('text')
    window.network_tab._on_dl_slider(1)
    window._controls()
    assert window.benchmark_tab.recommendation['workload'].key == 'ubuntu'
    assert 'Current download speed input' in window.benchmark_tab.choice_detail.cget('text')


def test_too_fast_for_catalog_disables_automatic_download(window, monkeypatch):
    monkeypatch.setattr(window, "_target_ready", lambda: True)
    window.network_tab._on_test_finished(2500, 100, 'test')
    window._controls()
    assert window.benchmark_tab.recommendation['workload'] is None
    assert window.benchmark_tab.test_btn.cget('state') == 'disabled'
    assert 'Both built-in images are too small' in window.benchmark_tab.choice_title.cget('text')
    window.network_tab._on_dl_slider(1)
    window._controls()



def test_larger_download_requires_explicit_confirmation(window, monkeypatch):
    from unittest.mock import Mock
    monkeypatch.setattr(window, '_target_ready', lambda: True)
    consent = Mock(return_value=False)
    monkeypatch.setattr('ui.main_window.messagebox.askyesno', consent)
    job = Mock()
    monkeypatch.setattr(window, '_job', job)
    window.network_tab._on_test_finished(1000, 100, 'test')
    window.benchmark_tab.path_entry.delete(0, 'end')
    window.benchmark_tab.path_entry.insert(0, 'E:/test')
    window.add_test_workload()
    assert 'Kali Linux Everything' in consent.call_args.args[1]
    assert '14.52 GB' in consent.call_args.args[1]
    job.assert_not_called()
    window.network_tab._on_dl_slider(1)


def test_test_download_buttons_fit_at_multiple_scales(window, monkeypatch):
    import customtkinter as ctk
    monkeypatch.setattr(window, '_target_ready', lambda: True)
    window.deiconify()
    try:
        for scale in (1, 1.25, 1.5, 2):
            ctk.set_widget_scaling(scale)
            window.tab_view.set('Speed test')
            window.update()
            for button in (window.benchmark_tab.stop_test_btn, window.benchmark_tab.delete_test_btn):
                assert button.winfo_ismapped()
                assert button.winfo_width() >= button._text_label.winfo_reqwidth() + 20 * scale
    finally:
        ctk.set_widget_scaling(1)
        window.tab_view.set('Connect')
        window.withdraw()


def test_result_ramp_curves_render_without_requiring_old_result_fields(window):
    from types import SimpleNamespace
    from optimizer.ramp_metrics import ramp_metrics
    data = {'mean_download_mib_s': 5, 'mean_upload_mib_s': 0,
            'ramp_trace': {'fresh_start': True, 'samples': [
                {'elapsed': i, 'download': i*1024**2, 'upload': 0, 'seeds': i, 'peers': i+2, 'phase': 'measurement'}
                for i in range(10)]}}
    data['ramp_metrics'] = ramp_metrics(data['ramp_trace'])
    cycle = SimpleNamespace(baseline=data, optimized=data, original={'a': 1}, path='test.json', comparison=lambda: 'Comparison')
    window.outcome_tab.show_cycle(cycle)
    assert 'First observed traffic' in window.outcome_tab.ramp_summary.cget('text')
    assert len(window.outcome_tab.speed_curve.find_all()) > 10
    assert len(window.outcome_tab.peer_curve.find_all()) > 10
    assert 'Before' in window.outcome_tab.ramp_summary.cget('text')
    assert 'After' in window.outcome_tab.ramp_summary.cget('text')


@pytest.mark.parametrize('network_first', [True, False])
def test_network_and_torrent_jobs_cannot_overlap(window, monkeypatch, network_first):
    from unittest.mock import Mock
    workers = []
    monkeypatch.setattr('ui.main_window.threading.Thread', lambda **kwargs: workers.append(kwargs['target']) or Mock())
    monkeypatch.setattr('ui.main_window.NetworkTester.run_full_test', lambda: (300, 30, 'test'))
    try:
        if network_first:
            window.run_network_test()
            window._job(lambda: None, Mock(), measuring=True)
        else:
            window._job(lambda: None, Mock(), measuring=True)
            window.run_network_test()
        assert len(workers) == 1
        assert window.busy
        assert window.network_tab.test_btn.cget('state') == 'disabled'
        workers[0]()
        window._drain()
        assert not window.busy
        assert window.network_tab.test_btn.cget('state') == 'normal'
    finally:
        window.busy = False
        window._measuring = False
        window._control_state = None
        window._controls()


@pytest.mark.parametrize('verified', [True, False])
def test_error_distinguishes_backup_from_verified_application(window, monkeypatch, verified):
    from types import SimpleNamespace
    monkeypatch.setattr('ui.main_window.messagebox.showerror', lambda *args, **kwargs: None)
    window.cycle = SimpleNamespace(baseline=None, plan=None, original={'up_limit': 0}, verified=verified)
    try:
        window.events.put(('error', 'Test failure'))
        window._drain()
        text = window.results_tab.summary.cget('text')
        assert ('Verified settings remain applied' if verified else 'application could not be verified') in text
    finally:
        window.cycle = None
        window._controls()


def test_saved_results_are_marked_as_historical_after_undo(window):
    from types import SimpleNamespace
    data = {'mean_download_mib_s': 50, 'mean_upload_mib_s': 0}
    window.outcome_tab.show_cycle(SimpleNamespace(baseline=data, optimized=data,
        original=None, path='test.json', comparison=lambda: 'Observed difference'))
    assert 'tuning is no longer applied' in window.outcome_tab.summary.cget('text')


def test_network_failure_unlocks_controls_and_retains_speed_inputs(window, monkeypatch):
    from unittest.mock import Mock
    workers = []
    def failed():
        raise OSError('network unavailable')
    monkeypatch.setattr('ui.main_window.threading.Thread', lambda **kwargs: workers.append(kwargs['target']) or Mock())
    monkeypatch.setattr('ui.main_window.NetworkTester.run_full_test', failed)
    monkeypatch.setattr('ui.main_window.messagebox.showerror', lambda *args, **kwargs: None)
    before = window.network_tab.get_settings()
    window.run_network_test()
    workers[0]()
    window._drain()
    assert not window.busy
    assert window.network_tab.test_btn.cget('state') == 'normal'
    assert window.network_tab.test_btn.cget('text') == 'Retry internet speed test'
    assert window.network_tab.get_settings() == before
