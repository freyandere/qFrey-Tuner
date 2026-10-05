from unittest.mock import Mock
import pytest
import psutil
from optimizer.process_manager import ProcessManager
from optimizer.qbittorrent_client import ClientError


def test_remote_lifecycle_blocked():
    with pytest.raises(ClientError, match="Remote"):
        ProcessManager.owner("http://nas.example:8080")


def test_shutdown_waits_and_preserves_restart_arguments(monkeypatch):
    process = Mock()
    process.cmdline.return_value = ["qbittorrent.exe", "--profile=D:/portable/profile"]
    process.cwd.return_value = "D:/portable"
    monkeypatch.setattr(ProcessManager, "owner", lambda _: process)
    launch = Mock()
    monkeypatch.setattr("optimizer.process_manager.subprocess.Popen", launch)
    client = Mock(host="http://127.0.0.1:8080")
    ProcessManager.stop(client, restart=True)
    client.shutdown.assert_called_once()
    process.wait.assert_called_once_with(timeout=30)
    assert launch.call_args.args[0] == process.cmdline.return_value
    assert launch.call_args.kwargs["cwd"] == "D:/portable"


def test_shutdown_timeout_never_kills_or_restarts(monkeypatch):
    process = Mock()
    process.wait.side_effect = psutil.TimeoutExpired(30)
    monkeypatch.setattr(ProcessManager, "owner", lambda _: process)
    launch = Mock()
    monkeypatch.setattr("optimizer.process_manager.subprocess.Popen", launch)
    with pytest.raises(ClientError, match="not forcibly killed"):
        ProcessManager.stop(Mock(host="http://127.0.0.1:8080"), restart=True)
    launch.assert_not_called()
    process.kill.assert_not_called()


def test_process_owner_matches_listening_port(monkeypatch):
    monkeypatch.setattr(ProcessManager, "discover", lambda: [{"pid": 12}, {"pid": 13}])
    processes = {12: Mock(), 13: Mock()}
    processes[12].net_connections.return_value = [Mock(status=psutil.CONN_LISTEN, laddr=Mock(port=8080))]
    processes[13].net_connections.return_value = [Mock(status=psutil.CONN_LISTEN, laddr=Mock(port=9090))]
    monkeypatch.setattr("optimizer.process_manager.psutil.Process", lambda pid: processes[pid])
    assert ProcessManager.owner("http://127.0.0.1:8080") is processes[12]
    with pytest.raises(ClientError, match="uniquely"):
        ProcessManager.owner("http://127.0.0.1:7070")
