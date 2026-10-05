"""Local lifecycle restricted to the process owning the selected Web UI port."""
import os
from pathlib import Path
import subprocess
from urllib.parse import urlsplit
import psutil
from .qbittorrent_client import ClientError


class ProcessManager:
    @staticmethod
    def discover():
        found = []
        for process in psutil.process_iter(["pid", "name", "exe", "cmdline"]):
            try:
                if (process.info["name"] or "").lower() in {"qbittorrent.exe", "qbittorrent", "qbittorrent-nox"}:
                    found.append(process.info)
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        return found

    @staticmethod
    def owner(host):
        parsed = urlsplit(host)
        if parsed.hostname not in {"localhost", "127.0.0.1", "::1"}:
            raise ClientError("Remote lifecycle unavailable. Use the server/container supervisor.")
        port = parsed.port or (443 if parsed.scheme == "https" else 80)
        owners, denied = [], False
        for item in ProcessManager.discover():
            try:
                process = psutil.Process(item["pid"])
                if any(c.status == psutil.CONN_LISTEN and c.laddr.port == port for c in process.net_connections(kind="tcp")):
                    owners.append(process)
            except (psutil.AccessDenied, psutil.NoSuchProcess):
                denied = True
        if len(owners) != 1:
            raise ClientError("Cannot uniquely identify the local process owning this Web UI port" + (" (permission denied)." if denied else "."))
        return owners[0]

    @staticmethod
    def start(executable):
        path = Path(executable).resolve()
        if not path.is_file() or path.stem.lower() not in {"qbittorrent", "qbittorrent-nox"}:
            raise ClientError("Select a qBittorrent executable.")
        if ProcessManager.discover():
            raise ClientError("qBittorrent is already running. Connect to the existing instance first.")
        return subprocess.Popen([str(path)], cwd=path.parent, creationflags=0x08000000 if os.name == "nt" else 0)

    @staticmethod
    def stop(client, restart=False):
        process = ProcessManager.owner(client.host)
        command = process.cmdline()
        try:
            directory = process.cwd()
        except psutil.AccessDenied as exc:
            raise ClientError("Cannot read the original working directory; operation blocked.") from exc
        if restart and not command:
            raise ClientError("Cannot reconstruct the launch command; restart blocked.")
        client.shutdown()
        try:
            process.wait(timeout=30)
        except psutil.TimeoutExpired as exc:
            raise ClientError("Shutdown exceeded 30 seconds. The process was not forcibly killed or restarted.") from exc
        if restart:
            subprocess.Popen(command, cwd=directory, creationflags=0x08000000 if os.name == "nt" else 0)
