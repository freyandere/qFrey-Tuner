"""Discover local profiles for diagnostics. Writes require a validated live API."""
import configparser
import os
from pathlib import Path
import sys
from .models import EnvironmentProfile


class ConfigManager:
    def __init__(self):
        self.installation_type = "Unknown"
        self.profile = EnvironmentProfile.SYSTEM
        self.config_path = self._find_config()
        self.last_error = ""

    @staticmethod
    def app_directory():
        return Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent.parent

    def set_installation_type(self, profile):
        self.profile = profile
        self.config_path = self._find_config(profile)

    def _find_config(self, env_profile=None):
        profile = env_profile or self.profile
        if profile in {EnvironmentProfile.DOCKER, EnvironmentProfile.NAS, EnvironmentProfile.TRUENAS, EnvironmentProfile.SEEDBOX}:
            self.installation_type = "Remote profile"
            return None
        roots = [self.app_directory(), Path.cwd()]
        try:
            from .process_manager import ProcessManager
            for process in ProcessManager.discover():
                if process.get("exe"):
                    roots.append(Path(process["exe"]).parent)
                command = process.get("cmdline") or []
                for index, arg in enumerate(command):
                    if arg.startswith("--profile="):
                        roots.append(Path(arg.split("=", 1)[1]))
                    elif arg == "--profile" and index + 1 < len(command):
                        roots.append(Path(command[index + 1]))
        except (ImportError, OSError):
            pass
        candidates = []
        for root in roots:
            candidates.extend((root / "qBittorrent.ini", root / "profile/qBittorrent/config/qBittorrent.ini",
                               root / "qBittorrent/config/qBittorrent.ini"))
        if profile != EnvironmentProfile.PORTABLE:
            if os.getenv("APPDATA"):
                candidates.append(Path(os.environ["APPDATA"]) / "qBittorrent/qBittorrent.ini")
            candidates.extend((Path(os.getenv("XDG_CONFIG_HOME", str(Path.home()/".config"))) / "qBittorrent/qBittorrent.conf",
                               Path.home()/"Library/Preferences/qBittorrent/qBittorrent.ini"))
        for path in candidates:
            if path.is_file():
                self.installation_type = "Detected file"
                return path.resolve()
        self.installation_type = "Not detected"
        return None

    def set_manual_path(self, path):
        candidate = Path(path).resolve()
        parser = configparser.ConfigParser(interpolation=None)
        try:
            with candidate.open(encoding="utf-8-sig") as stream:
                parser.read_file(stream)
            if not any(section in parser for section in ("BitTorrent", "Preferences", "LegalNotice")):
                return False
        except (OSError, configparser.Error, UnicodeError):
            return False
        self.config_path = candidate
        self.installation_type = "Manually selected file"
        return True

    def apply_settings(self, settings):
        self.last_error = "Offline writes disabled. Connect to qBittorrent and use the verified optimization cycle."
        return False

    def webui_hint(self):
        """Read only non-secret Web UI fields from a selected profile."""
        if not self.config_path:
            return None
        parser = configparser.ConfigParser(interpolation=None)
        parser.optionxform = str
        try:
            parser.read(self.config_path, encoding="utf-8-sig")
            def value(key, default=""):
                for section, name in (("Preferences", "WebUI\\" + key), ("WebUI", key)):
                    if parser.has_option(section, name):
                        return parser.get(section, name)
                return default
            port = int(value("Port", "8080"))
            if not 1 <= port <= 65535:
                return None
            scheme = "https" if value("HTTPS\\Enabled", "false").lower() == "true" else "http"
            address = value("Address", "*")
            if address in ("*", "", "0.0.0.0", "::"):
                address = "127.0.0.1"
            if ":" in address and not address.startswith("["):
                address = f"[{address}]"
            return {"url": f"{scheme}://{address}:{port}", "username": value("Username", "admin"),
                    "enabled": value("Enabled", "false").lower() == "true"}
        except (OSError, ValueError, configparser.Error):
            return None
