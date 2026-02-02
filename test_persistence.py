"""Тест для ConfigManager."""

import os
import configparser
from pathlib import Path
from optimizer.config_manager import ConfigManager
from optimizer.models import (
    OptimizedSettings, ProtocolMode, EncryptionMode
)

def test_apply_settings():
    # Создаем временный конфиг
    test_conf = Path("test_qbittorrent.ini")
    if test_conf.exists():
        test_conf.unlink()
    
    with open(test_conf, "w", encoding="utf-8") as f:
        f.write("[BitTorrent]\nMaxConnections=100\n")
    
    # Инициализируем менеджер и подменяем путь
    mgr = ConfigManager()
    mgr.config_path = test_conf
    
    # Создаем тестовые настройки
    settings = OptimizedSettings(
        global_upload_limit_kbps=100,
        global_download_limit_kbps=0,
        upload_slots_global=50,
        upload_slots_per_torrent=10,
        max_connections_global=500,
        max_connections_per_torrent=125,
        max_active_downloads=5,
        max_active_uploads=8,
        max_active_torrents=13,
        disk_cache_mb=512,
        enable_os_cache=True,
        pre_allocate_disk=True,
        async_io_threads=16,
        coalesce_reads_writes=True,
        protocol_mode=ProtocolMode.UTP_TCP,
        send_buffer_watermark_kb=5000,
        send_buffer_low_watermark_kb=160,
        send_buffer_factor=120,
        socket_backlog_size=100,
        outgoing_connections_per_second=200,
        listening_port="64532",
        encryption_mode=EncryptionMode.PREFER,
        anonymous_mode=True,
        enable_dht=True,
        enable_pex=True,
        enable_lsd=True,
        network_interface="tun0",
        super_seeding=False,
        warnings=[],
        explanations={}
    )
    
    success = mgr.apply_settings(settings)
    assert success is True
    
    # Проверяем результат
    config = configparser.ConfigParser(interpolation=None)
    config.read(test_conf, encoding="utf-8")
    
    assert config["BitTorrent"]["MaxConnections"] == "500"
    assert config["BitTorrent"]["UploadLimit"] == str(100 * 1024)
    assert config["Connection"]["Interface"] == "tun0"
    assert config["Advanced"]["DiskCache"] == "512"
    
    print("✅ Test passed!")
    
    # Очистка
    if test_conf.exists():
        test_conf.unlink()

if __name__ == "__main__":
    test_apply_settings()
