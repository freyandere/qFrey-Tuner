from pathlib import Path

from optimizer.config_manager import ConfigManager
from optimizer.models import EnvironmentProfile


def test_offline_writes_are_blocked(tmp_path):
    path = tmp_path / "qBittorrent.ini"
    original = "[BitTorrent]\nSession\\MaxConnections=100\n"
    path.write_text(original)
    manager = ConfigManager()
    assert manager.set_manual_path(str(path))
    assert not manager.apply_settings(None)
    assert "Offline" in manager.last_error
    assert path.read_text() == original


def test_portable_discovery_respects_profile(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(ConfigManager, "app_directory", staticmethod(lambda: tmp_path))
    from optimizer.process_manager import ProcessManager
    monkeypatch.setattr(ProcessManager, "discover", lambda: [])
    system = tmp_path / "appdata/qBittorrent/qBittorrent.ini"
    system.parent.mkdir(parents=True)
    system.write_text("[BitTorrent]\n")
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    manager = ConfigManager()
    assert manager.config_path == system.resolve()
    manager.set_installation_type(EnvironmentProfile.PORTABLE)
    assert manager.config_path is None
    portable = tmp_path / "profile/qBittorrent/config/qBittorrent.ini"
    portable.parent.mkdir(parents=True)
    portable.write_text("[BitTorrent]\n")
    assert manager._find_config(EnvironmentProfile.PORTABLE) == portable.resolve()


def test_config_validation_and_remote_discovery(tmp_path):
    manager = ConfigManager()
    invalid = tmp_path / "bad.ini"
    invalid.write_text("not a configuration")
    assert not manager.set_manual_path(str(invalid))
    manager.set_installation_type(EnvironmentProfile.DOCKER)
    assert manager.config_path is None
