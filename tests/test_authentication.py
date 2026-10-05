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


@pytest.mark.parametrize('version,api,libtorrent', [
    ('v4.6.0', '2.8.0', '1.2.19'), ('v4.6.7', '2.11.0', '2.0.11'),
    ('v5.0.0', '2.11.0', '2.0.11'), ('v5.1.0', '2.11.0', '2.0.11'),
    ('v5.2.3', '2.15.1', '1.2.20'),
])
def test_reviewed_versions_read_live_schema_before_connecting(monkeypatch, version, api, libtorrent):
    client = QBittorrentClient()
    client.request = Mock(side_effect=[response(version), response(api),
        response({'libtorrent': libtorrent}), response({'max_connec': 500})])
    client.connect()
    assert client.connected
    assert (client.version, client.api_version, client.libtorrent) == (version, api, libtorrent)
    assert client.request.call_args.args == ('GET', 'app/preferences')


@pytest.mark.parametrize('version,api,libtorrent', [
    ('v4.5.5', '2.8.0', '1.2.19'), ('v4.7.0', '2.11.0', '2.0.11'),
    ('v5.3.0', '2.15.1', '2.0.11'), ('v6.0.0', '2.15.1', '2.0.11'),
    ('v5.2.0rc1', '2.15.1', '2.0.11'), ('unknown', '2.11.0', '2.0.11'),
    ('v5.1.0', '2.7.0', '2.0.11'), ('v5.1.0', '3.0.0', '2.0.11'),
    ('v5.1.0', '2.11.0rc1', '2.0.11'), ('v5.1.0', 'bad', '2.0.11'),
    ('v5.1.0', '2.11.0', '3.0.0'), ('v5.1.0', '2.11.0', '2.bad'),
    ('v5.1.0', '2.11.0', '1.'), ('v5.1.0', '2.11.0', '2.0.11.dev1'),
    ('v5.1.0', '2.11.0', '2'), ('v5.1.0', '2.11.0', None),
    ('v5.1.0', '2.11.0', 2),
])
def test_unknown_versions_never_unlock_or_write(monkeypatch, version, api, libtorrent):
    client = QBittorrentClient()
    client.connected = True  # Failed reconnect must clear an earlier success.
    client.request = Mock(side_effect=[response(version), response(api), response({'libtorrent': libtorrent})])
    with pytest.raises(ClientError):
        client.connect()
    assert not client.connected
    assert client.request.call_count == 3
    assert all(call.args[0] == 'GET' for call in client.request.call_args_list)
