"""Модели данных для qBittorrent Optimizer."""

from dataclasses import dataclass, field
from enum import Enum


class ConnectionType(Enum):
    """Тип интернет-соединения."""
    FIBER = "Оптоволокно (Fiber)"
    CABLE_DSL = "Кабель / DSL"
    MOBILE_4G = "4G / Starlink"


class EnvironmentProfile(Enum):
    """Среда установки qBittorrent."""
    DESKTOP = "Desktop (Windows/macOS/Linux)"
    TRUENAS = "TrueNAS / ZFS"
    NAS = "NAS (Synology/QNAP)"
    DOCKER = "Docker (с VPN)"
    SEEDBOX = "Seedbox (1-10 Gbps)"


class StorageType(Enum):
    """Тип накопителя."""
    HDD = "HDD"
    SSD_SATA = "SSD (SATA)"
    NVME = "NVMe"


class TrackerType(Enum):
    """Тип трекеров."""
    PUBLIC = "Публичные"
    PRIVATE = "Приватные"


class UserRole(Enum):
    """Роль пользователя."""
    LEECHER = "Личер"
    SEEDER = "Сидер"
    UPLOADER = "Аплоадер"


class ProtocolMode(Enum):
    """Режим протокола."""
    TCP_ONLY = "TCP only"
    UTP_TCP = "TCP and μTP"
    UTP_ONLY = "μTP only"


class EncryptionMode(Enum):
    """Режим шифрования."""
    PREFER = "Prefer encryption"
    REQUIRE = "Require encryption"
    DISABLED = "Disabled"


@dataclass
class NetworkSettings:
    """Настройки сети."""
    download_speed_mbps: float
    upload_speed_mbps: float
    connection_type: ConnectionType
    use_vpn: bool
    vpn_interface: str = ""
    isp_throttling: bool = False


@dataclass
class HardwareSettings:
    """Характеристики железа."""
    storage_type: StorageType
    ram_gb: int
    cpu_cores: int
    is_hybrid_cpu: bool = False
    p_cores: int = 0


@dataclass
class UsageSettings:
    """Сценарий использования."""
    tracker_type: TrackerType
    user_role: UserRole = UserRole.LEECHER
    environment: EnvironmentProfile = EnvironmentProfile.DESKTOP


@dataclass
class OptimizedSettings:
    """Рассчитанные оптимальные настройки."""
    # Connection limits
    global_upload_limit_kbps: int
    global_download_limit_kbps: int
    upload_slots_global: int
    upload_slots_per_torrent: int
    max_connections_global: int
    max_connections_per_torrent: int
    
    # Queue
    max_active_downloads: int
    max_active_uploads: int
    max_active_torrents: int
    
    # Disk I/O (Advanced)
    disk_cache_mb: int
    enable_os_cache: bool
    pre_allocate_disk: bool
    async_io_threads: int
    coalesce_reads_writes: bool
    
    # Network tuning (Advanced)
    protocol_mode: ProtocolMode
    send_buffer_watermark_kb: int
    send_buffer_low_watermark_kb: int
    send_buffer_factor: int
    socket_backlog_size: int
    outgoing_connections_per_second: int
    listening_port: str
    
    # Privacy
    encryption_mode: EncryptionMode
    anonymous_mode: bool
    enable_dht: bool
    enable_pex: bool
    enable_lsd: bool
    network_interface: str
    
    # Advanced
    super_seeding: bool
    
    # Meta
    warnings: list[str] = field(default_factory=list)
    explanations: dict[str, str] = field(default_factory=dict)
