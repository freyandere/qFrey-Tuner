"""Логика расчёта оптимальных настроек qBittorrent."""

import random

from .models import (
    NetworkSettings,
    HardwareSettings,
    UsageSettings,
    OptimizedSettings,
    ConnectionType,
    StorageType,
    EnvironmentProfile,
    TrackerType,
    UserRole,
    ProtocolMode,
    EncryptionMode,
)


# ═══════════════════════════════════════════════════════════════════════════════
# ЛИМИТЫ UI qBittorrent
# ═══════════════════════════════════════════════════════════════════════════════
MAX_CONNECTIONS_GLOBAL = 2000
MAX_CONNECTIONS_PER_TORRENT = 2000
MAX_UPLOAD_SLOTS_GLOBAL = 2000
MAX_UPLOAD_SLOTS_PER_TORRENT = 500


def clamp(value: int, min_val: int, max_val: int) -> int:
    """Ограничить значение в диапазоне."""
    return max(min_val, min(value, max_val))


def calculate_optimal_settings(
    network: NetworkSettings,
    hardware: HardwareSettings,
    usage: UsageSettings,
) -> OptimizedSettings:
    """Рассчитать оптимальные настройки qBittorrent."""
    warnings: list[str] = []
    explanations: dict[str, str] = {}
    
    env = usage.environment
    is_private = usage.tracker_type == TrackerType.PRIVATE
    is_seedbox = env == EnvironmentProfile.SEEDBOX
    is_truenas = env == EnvironmentProfile.TRUENAS
    is_nas = env == EnvironmentProfile.NAS
    is_docker = env == EnvironmentProfile.DOCKER
    
    # ═══════════════════════════════════════════════════════════════════════════
    # CONNECTION LIMITS
    # ═══════════════════════════════════════════════════════════════════════════
    
    # Internet Mbps is decimal; the model's legacy *_kbps fields hold KiB/s.
    global_upload_limit = int(network.upload_speed_mbps * 1_000_000 / 8 * 0.8 / 1024)
    explanations["upload_limit"] = (
        "80% от скорости отдачи. Оставляет 20% для ACK-пакетов TCP."
    )
    
    global_download_limit = 0
    explanations["download_limit"] = "Без ограничений (0 = ∞)."
    
    # ─────────────────────────────────────────────────────────────────────────────
    # Upload Slots
    # ─────────────────────────────────────────────────────────────────────────────
    if is_private:
        upload_slots_global = clamp(50, 20, 100)
        upload_slots_per_torrent = clamp(6, 4, 8)
        explanations["upload_slots"] = (
            "4-8 слотов на торрент для приватных трекеров (racing strategy)."
        )
        warnings.append(
            "💡 Private Tracker: Upload slots снижены для «гонки» (racing)."
        )
    elif is_seedbox:
        upload_slots_global = 200
        upload_slots_per_torrent = 50
        explanations["upload_slots"] = "Высокие значения для Seedbox."
    else:
        if usage.user_role == UserRole.SEEDER:
            upload_slots_global = max(50, global_upload_limit // 8)
            upload_slots_per_torrent = max(10, global_upload_limit // 20)
        elif usage.user_role == UserRole.UPLOADER:
            upload_slots_global = max(50, global_upload_limit // 5)
            upload_slots_per_torrent = max(15, global_upload_limit // 15)
        else:
            upload_slots_global = max(30, global_upload_limit // 10)
            upload_slots_per_torrent = max(5, global_upload_limit // 30)
        
        explanations["upload_slots"] = f"Расчёт для {usage.user_role.value}."
    
    upload_slots_global = clamp(upload_slots_global, 1, MAX_UPLOAD_SLOTS_GLOBAL)
    upload_slots_per_torrent = clamp(upload_slots_per_torrent, 1, MAX_UPLOAD_SLOTS_PER_TORRENT)
    
    # ─────────────────────────────────────────────────────────────────────────────
    # Max Connections
    # ─────────────────────────────────────────────────────────────────────────────
    if is_private:
        max_connections = 200
        max_connections_per_torrent = 50
        explanations["max_connections"] = "100-300 для приватных трекеров."
    elif is_seedbox:
        max_connections = 2000
        max_connections_per_torrent = 500
        explanations["max_connections"] = "Максимум для Seedbox."
        warnings.append("⚡ Seedbox: максимальные соединения для высоких скоростей.")
    elif network.download_speed_mbps < 100:
        max_connections = 200
        max_connections_per_torrent = 50
        explanations["max_connections"] = "200 для скоростей до 100 Мбит/с."
    elif network.download_speed_mbps < 500:
        max_connections = 500
        max_connections_per_torrent = 125
        explanations["max_connections"] = "500 для средних скоростей."
    else:
        max_connections = 1000
        max_connections_per_torrent = 250
        explanations["max_connections"] = "1000 для быстрых каналов."
    
    max_connections = clamp(max_connections, 1, MAX_CONNECTIONS_GLOBAL)
    max_connections_per_torrent = clamp(max_connections_per_torrent, 1, MAX_CONNECTIONS_PER_TORRENT)
    
    # ═══════════════════════════════════════════════════════════════════════════
    # TORRENT QUEUEING
    # ═══════════════════════════════════════════════════════════════════════════
    if network.download_speed_mbps < 50:
        max_active_downloads = 2
        max_active_uploads = 3
    elif network.download_speed_mbps < 300:
        max_active_downloads = 5
        max_active_uploads = 8
    else:
        max_active_downloads = 10
        max_active_uploads = 15
    
    if usage.user_role == UserRole.SEEDER:
        max_active_uploads = int(max_active_uploads * 1.5)
    
    max_active_torrents = max_active_downloads + max_active_uploads
    explanations["queue"] = f"Downloads: {max_active_downloads}, Uploads: {max_active_uploads}"
    
    # ═══════════════════════════════════════════════════════════════════════════
    # DISK I/O — зависит от СРЕДЫ
    # ═══════════════════════════════════════════════════════════════════════════
    
    if is_truenas:
        # ZFS: отключаем кэш, пусть работает ARC
        disk_cache = 0
        enable_os_cache = True
        pre_allocate_disk = False
        explanations["disk_cache"] = (
            "Disk Cache = 0 для ZFS. Позвольте ZFS ARC управлять памятью."
        )
        explanations["pre_allocate"] = (
            "Pre-allocate выключен. ZFS использует Copy-on-Write."
        )
        warnings.append(
            "🗄️ TrueNAS/ZFS: Disk Cache отключён, OS Cache включён, Pre-allocate выключен."
        )
    elif is_nas:
        # Synology/QNAP: буфер для сетевых задержек
        disk_cache = 512
        enable_os_cache = False
        pre_allocate_disk = True
        explanations["disk_cache"] = "512 МБ буфер для сетевых задержек NAS."
        warnings.append("📦 NAS: OS Cache выключен для стабильности.")
    elif is_docker:
        disk_cache = -1  # Auto
        enable_os_cache = True
        pre_allocate_disk = True
        explanations["disk_cache"] = "Auto для Docker контейнера."
    elif is_seedbox:
        if hardware.ram_gb >= 32:
            disk_cache = 4096
        elif hardware.ram_gb >= 16:
            disk_cache = 2048
        else:
            disk_cache = 1024
        enable_os_cache = True
        pre_allocate_disk = True
        explanations["disk_cache"] = f"{disk_cache} МБ для Seedbox (высокая нагрузка)."
    elif hardware.storage_type == StorageType.HDD:
        if hardware.ram_gb >= 16:
            disk_cache = 2048
        elif hardware.ram_gb >= 8:
            disk_cache = 1024
        else:
            disk_cache = 512
        enable_os_cache = True
        pre_allocate_disk = True
        explanations["disk_cache"] = f"{disk_cache} МБ для HDD."
    elif hardware.storage_type == StorageType.SSD_SATA:
        disk_cache = 512 if hardware.ram_gb >= 8 else 256
        enable_os_cache = True
        pre_allocate_disk = True
        explanations["disk_cache"] = f"{disk_cache} МБ для SATA SSD."
    else:  # NVMe
        disk_cache = -1
        enable_os_cache = True
        pre_allocate_disk = True
        explanations["disk_cache"] = "Auto (-1) для NVMe."
    
    # Async I/O threads
    if hardware.is_hybrid_cpu and hardware.p_cores > 0:
        async_io = 4 * hardware.p_cores
        explanations["async_io"] = f"4 × {hardware.p_cores} P-cores = {async_io}"
    else:
        async_io = 4 * hardware.cpu_cores
        explanations["async_io"] = f"4 × {hardware.cpu_cores} ядер = {async_io}"
    
    coalesce = True
    explanations["coalesce"] = "Объединяет мелкие I/O операции."
    
    # ═══════════════════════════════════════════════════════════════════════════
    # NETWORK TUNING — зависит от СРЕДЫ
    # ═══════════════════════════════════════════════════════════════════════════
    
    if is_seedbox:
        send_buffer = 16000
        send_buffer_low = 160
        send_buffer_factor = 150
        socket_backlog = 1024
        outgoing_per_sec = 1000
        protocol = ProtocolMode.TCP_ONLY
        explanations["send_buffer"] = "16 МБ буфер для Seedbox (1+ Гбит/с)."
        explanations["socket_backlog"] = "Socket backlog 1024 для массовых подключений."
        explanations["protocol"] = "TCP only — μTP не нужен на сидбоксе."
    elif is_docker:
        send_buffer = 500
        send_buffer_low = 16
        send_buffer_factor = 100
        socket_backlog = 30
        outgoing_per_sec = 100
        protocol = ProtocolMode.TCP_ONLY
        explanations["protocol"] = "TCP only внутри VPN-туннеля."
    elif network.upload_speed_mbps > 500:
        send_buffer = 8000
        send_buffer_low = 160
        send_buffer_factor = 120
        socket_backlog = 200
        outgoing_per_sec = 500
        protocol = ProtocolMode.TCP_ONLY if network.connection_type == ConnectionType.FIBER else ProtocolMode.UTP_TCP
        explanations["send_buffer"] = "8 МБ буфер для высоких скоростей."
    elif network.upload_speed_mbps > 100:
        send_buffer = 5000
        send_buffer_low = 160
        send_buffer_factor = 120
        socket_backlog = 100
        outgoing_per_sec = 200
        protocol = ProtocolMode.UTP_TCP
        explanations["send_buffer"] = "5 МБ буфер."
    else:
        send_buffer = 500
        send_buffer_low = 16
        send_buffer_factor = 100
        socket_backlog = 30
        outgoing_per_sec = 100
        protocol = ProtocolMode.UTP_TCP
        explanations["send_buffer"] = "Стандартное значение."
    
    if network.connection_type == ConnectionType.FIBER and not is_docker:
        protocol = ProtocolMode.TCP_ONLY
        explanations["protocol"] = "TCP only для Fiber — μTP создаёт лишнюю нагрузку."
    
    # Listening port
    if network.isp_throttling:
        listening_port = f"Random ({random.randint(49152, 65535)})"
        warnings.append("Случайный порт для обхода DPI.")
        explanations["port"] = "Случайный высокий порт для обхода блокировок."
    else:
        listening_port = "Стандартный"
        explanations["port"] = "Оставьте текущий порт."
    
    # ═══════════════════════════════════════════════════════════════════════════
    # PRIVACY
    # ═══════════════════════════════════════════════════════════════════════════
    
    if network.isp_throttling:
        encryption = EncryptionMode.REQUIRE
        warnings.append("Принудительное шифрование для обхода DPI.")
        explanations["encryption"] = "Require encryption для обхода DPI."
    else:
        encryption = EncryptionMode.PREFER
        explanations["encryption"] = "Prefer encryption — стандарт."
    
    if is_private:
        anonymous = False
        explanations["anonymous"] = "ВЫКЛЮЧЕН для приватных трекеров!"
        warnings.append(
            "⚠️ Anonymous Mode ВЫКЛЮЧЕН — на приватных трекерах это обязательно!"
        )
    else:
        anonymous = True
        explanations["anonymous"] = "Включён для публичных трекеров."
    
    if is_private:
        enable_dht = False
        enable_pex = False
        enable_lsd = False
        warnings.append("⚠️ DHT, PeX, LSD отключены для защиты passkey.")
        explanations["dht_pex_lsd"] = "Отключены для приватных трекеров."
    else:
        enable_dht = True
        enable_pex = True
        enable_lsd = True
        explanations["dht_pex_lsd"] = "Включены для публичных трекеров."
    
    # VPN interface
    network_interface = ""
    if network.use_vpn or is_docker:
        if network.vpn_interface:
            network_interface = network.vpn_interface
            warnings.append(f"✅ Kill Switch: трафик привязан к {network.vpn_interface}")
            explanations["vpn_interface"] = f"Bind to {network.vpn_interface}."
        elif is_docker:
            network_interface = "tun0"
            warnings.append("🐳 Docker: привязка к tun0 (стандартный VPN интерфейс).")
            explanations["vpn_interface"] = "tun0 — стандартный VPN интерфейс в Docker."
        else:
            warnings.append("⚠️ VPN включен, но интерфейс не указан!")
    
    # Super Seeding
    super_seeding = usage.user_role == UserRole.UPLOADER
    if super_seeding:
        warnings.append("💡 Super Seeding: ускоряет первичное распространение.")
        explanations["super_seeding"] = "Включён для аплоадеров."
    else:
        explanations["super_seeding"] = "Выключен."
    
    return OptimizedSettings(
        global_upload_limit_kbps=global_upload_limit,
        global_download_limit_kbps=global_download_limit,
        upload_slots_global=upload_slots_global,
        upload_slots_per_torrent=upload_slots_per_torrent,
        max_connections_global=max_connections,
        max_connections_per_torrent=max_connections_per_torrent,
        max_active_downloads=max_active_downloads,
        max_active_uploads=max_active_uploads,
        max_active_torrents=max_active_torrents,
        disk_cache_mb=disk_cache,
        enable_os_cache=enable_os_cache,
        pre_allocate_disk=pre_allocate_disk,
        async_io_threads=async_io,
        coalesce_reads_writes=coalesce,
        protocol_mode=protocol,
        send_buffer_watermark_kb=send_buffer,
        send_buffer_low_watermark_kb=send_buffer_low,
        send_buffer_factor=send_buffer_factor,
        socket_backlog_size=socket_backlog,
        outgoing_connections_per_second=outgoing_per_sec,
        listening_port=listening_port,
        encryption_mode=encryption,
        anonymous_mode=anonymous,
        enable_dht=enable_dht,
        enable_pex=enable_pex,
        enable_lsd=enable_lsd,
        network_interface=network_interface,
        super_seeding=super_seeding,
        warnings=warnings,
        explanations=explanations,
    )
