"""Менеджер конфигурации qBittorrent.

Обеспечивает поиск файла настроек и обновление ключей.
"""

import os
import configparser
from pathlib import Path
from typing import Optional

from .models import OptimizedSettings, ProtocolMode, EncryptionMode


class ConfigManager:
    """Управление файлом настроек qBittorrent."""

    def __init__(self):
        self.config_path: Optional[Path] = self._find_config()
        self.config = configparser.ConfigParser(interpolation=None)
        # qBittorrent использует регистрозависимые ключи в некоторых секциях
        self.config.optionxform = str

    def _find_config(self) -> Optional[Path]:
        """Поиск файла qBittorrent.ini / qBittorrent.conf."""
        # 1. Проверяем портабельный режим (папка profile рядом с .exe)
        # В контексте разработки проверяем корень проекта или стандартные места
        portable_path = Path("profile/qBittorrent/config/qBittorrent.ini")
        if portable_path.exists():
            return portable_path

        # 2. Windows стандарт
        if os.name == "nt":
            appdata = os.getenv("APPDATA")
            if appdata:
                win_path = Path(appdata) / "qBittorrent" / "qBittorrent.ini"
                if win_path.exists():
                    return win_path

        # 3. Linux / macOS стандарт
        home_config = Path.home() / ".config" / "qBittorrent" / "qBittorrent.conf"
        if home_config.exists():
            return home_config

        return None

    def set_manual_path(self, path: str) -> bool:
        """Установить путь вручную и проверить его валидность."""
        p = Path(path)
        if p.exists() and p.is_file():
            # Проверяем, что это похоже на конфиг qBittorrent
            try:
                temp_cfg = configparser.ConfigParser(interpolation=None)
                temp_cfg.read(p, encoding="utf-8")
                if "BitTorrent" in temp_cfg or "LegalNotice" in temp_cfg:
                    self.config_path = p
                    return True
            except Exception:
                pass
        return False

    def apply_settings(self, settings: OptimizedSettings) -> bool:
        """Применить настройки к файлу."""
        if not self.config_path:
            return False

        try:
            self.config.read(self.config_path, encoding="utf-8")

            # ═══════════════════════════════════════════════════════════════════
            # Секция BitTorrent
            # ═══════════════════════════════════════════════════════════════════
            if "BitTorrent" not in self.config:
                self.config.add_section("BitTorrent")
            
            bt = self.config["BitTorrent"]
            bt["MaxConnections"] = str(settings.max_connections_global)
            bt["MaxConnectionsPerTorrent"] = str(settings.max_connections_per_torrent)
            bt["MaxUploadSlots"] = str(settings.upload_slots_global)
            bt["MaxUploadSlotsPerTorrent"] = str(settings.upload_slots_per_torrent)
            bt["uTP_rate_limited"] = "true"
            
            # Лимиты скорости (kbps -> bytes/sec)
            bt["UploadLimit"] = str(settings.global_upload_limit_kbps * 1024)
            bt["DownloadLimit"] = str(settings.global_download_limit_kbps * 1024)

            # ═══════════════════════════════════════════════════════════════════
            # Секция Connection
            # ═══════════════════════════════════════════════════════════════════
            if "Connection" not in self.config:
                self.config.add_section("Connection")
            
            conn = self.config["Connection"]
            if settings.listening_port != "Стандартный" and "Random" in settings.listening_port:
                import re
                port = re.findall(r'\d+', settings.listening_port)[0]
                conn["PortRangeMin"] = port
            
            if settings.network_interface:
                conn["Interface"] = settings.network_interface

            # ═══════════════════════════════════════════════════════════════════
            # Секция Downloads
            # ═══════════════════════════════════════════════════════════════════
            if "Downloads" not in self.config:
                self.config.add_section("Downloads")
            
            dl = self.config["Downloads"]
            dl["MaxActiveDownloads"] = str(settings.max_active_downloads)
            dl["MaxActiveUploads"] = str(settings.max_active_uploads)
            dl["MaxActiveTorrents"] = str(settings.max_active_torrents)
            dl["PreAllocation"] = "true" if settings.pre_allocate_disk else "false"

            # ═══════════════════════════════════════════════════════════════════
            # Секция Advanced
            # ═══════════════════════════════════════════════════════════════════
            if "Advanced" not in self.config:
                self.config.add_section("Advanced")
            
            adv = self.config["Advanced"]
            adv["DiskCache"] = str(settings.disk_cache_mb)
            adv["EnableOSCache"] = "true" if settings.enable_os_cache else "false"
            adv["AsyncIOThreads"] = str(settings.async_io_threads)
            adv["SocketBacklogSize"] = str(settings.socket_backlog_size)
            adv["OutgoingConnectionsPerSecond"] = str(settings.outgoing_connections_per_second)

            # Сохраняем файл
            with open(self.config_path, "w", encoding="utf-8") as f:
                self.config.write(f)

            return True
        except Exception as e:
            print(f"Error applying settings: {e}")
            return False
