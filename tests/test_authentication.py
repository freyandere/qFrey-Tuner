from unittest.mock import Mock

import pytest
import requests

from optimizer.qbittorrent_client import QBittorrentClient, ClientError
from optimizer.config_manager import ConfigManager
from optimizer.diagnostics import configure_logging


def response(value, status=200):
    result = Mock(status_code=status, text=value if isinstance(value, str) else "")
    result.json.return_value = value
    return result


@pytest.mark.parametrize("key", ["", "private-api-key"])
def test_existing_access_skips_login_and_never_logs_secrets(monkeypatch, tmp_path, key):
    log = configure_logging(tmp_path)
    session = requests.Session()
    session.request = Mock(side_effect=[response("v5.2.3"), response("2.15.1"),
        response({"libtorrent": "1.2.20"}), response({"max_connec": 500})])
    monkeypatch.setattr(requests, "Session", lambda: session)
    client = QBittorrentClient("http://127.0.0.1:9560")
    client.connect("admin", "private-password", key)
    assert client.connected
    assert all(call.args[0] == "GET" for call in session.request.call_args_list)
    if key:
        assert session.headers["Authorization"] == "Bearer " + key
    content = log.read_text()
    assert "private-password" not in content and "private-api-key" not in content


def test_missing_credentials_never_posts_login(monkeypatch):
    session = requests.Session()
    session.request = Mock(return_value=response("Forbidden", 403))
    monkeypatch.setattr(requests, "Session", lambda: session)
    client = QBittorrentClient()
    with pytest.raises(ClientError, match="requires authentication"):
        client.connect()
    assert session.request.call_count == 1
    assert not client.connected


def test_connection_failure_is_actionable(monkeypatch):
    session = requests.Session()
    session.request = Mock(side_effect=requests.ConnectionError("raw-secret"))
    monkeypatch.setattr(requests, "Session", lambda: session)
    with pytest.raises(ClientError, match="port matches") as error:
        QBittorrentClient().connect()
    assert "raw-secret" not in str(error.value)


def test_profile_port_hint(tmp_path):
    path = tmp_path / "qBittorrent.ini"
    path.write_text("[Preferences]\nWebUI\\Port=9560\nWebUI\\Address=*\nWebUI\\Enabled=true\nWebUI\\Password_PBKDF2=secret\n")
    manager = ConfigManager()
    manager.config_path = path
    assert manager.webui_hint() == {"url": "http://127.0.0.1:9560", "username": "admin", "enabled": True}
