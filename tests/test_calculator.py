import pytest
from optimizer.models import (
    NetworkSettings, HardwareSettings, UsageSettings, 
    ConnectionType, StorageType, EnvironmentProfile, 
    TrackerType, UserRole, OptimizedSettings
)
from optimizer.calculator import calculate_optimal_settings

def test_calculate_desktop_defaults():
    network = NetworkSettings(
        download_speed_mbps=100,
        upload_speed_mbps=100,
        connection_type=ConnectionType.FIBER,
        use_vpn=False
    )
    hardware = HardwareSettings(
        storage_type=StorageType.SSD_SATA,
        ram_gb=16,
        cpu_cores=8
    )
    usage = UsageSettings(
        tracker_type=TrackerType.PUBLIC,
        user_role=UserRole.LEECHER,
        environment=EnvironmentProfile.SYSTEM
    )
    
    settings = calculate_optimal_settings(network, hardware, usage)
    
    assert isinstance(settings, OptimizedSettings)
    assert settings.global_upload_limit_kbps == 10000 
    assert settings.async_io_threads == 32 
    assert settings.anonymous_mode # True for public trackers
    assert settings.enable_dht # True for Public

def test_calculate_private_tracker():
    network = NetworkSettings(
        download_speed_mbps=1000,
        upload_speed_mbps=1000,
        connection_type=ConnectionType.FIBER,
        use_vpn=True,
        vpn_interface="ovpn"
    )
    hardware = HardwareSettings(
        storage_type=StorageType.NVME,
        ram_gb=32,
        cpu_cores=16
    )
    usage = UsageSettings(
        tracker_type=TrackerType.PRIVATE,
        user_role=UserRole.UPLOADER,
        environment=EnvironmentProfile.SEEDBOX
    )
    
    settings = calculate_optimal_settings(network, hardware, usage)
    
    # Private tracker specific assert
    assert not settings.enable_dht
    assert not settings.enable_pex
    assert not settings.enable_lsd
    assert settings.network_interface == "ovpn"

def test_calculate_hybrid_cpu():
    network = NetworkSettings(500, 500, ConnectionType.FIBER, False)
    # Hybrid CPU: 8 P-cores, 16 Total
    hardware = HardwareSettings(StorageType.NVME, 64, 16, True, 8)
    usage = UsageSettings(TrackerType.PUBLIC)
    
    settings = calculate_optimal_settings(network, hardware, usage)
    
    # Should use P-cores for async I/O
    assert settings.async_io_threads == 32 # 8 P-cores * 4
