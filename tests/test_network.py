import pytest
from optimizer.network_tester import NetworkTester

def test_test_download_speed_success(mocker):
    # Mock best server
    mocker.patch.object(NetworkTester, 'get_best_server', return_value={"name": "TestServer", "url": "http://dummy.url"})
    # Mock _download_chunk to return 10MB
    mocker.patch.object(NetworkTester, '_download_chunk', return_value=10 * 1024 * 1024)
    # Mock time to last 1s
    mocker.patch("time.time", side_effect=[0, 1])
    
    speed, server = NetworkTester.test_download_speed_mbps()
    # 10MB * 8 threads = 80MB. 80MB in 1s = 640 Mbps (wait, total_bytes is sum of f.result() which is 80MB)
    # Actually 10MB per thread * 8 threads = 80MB. 80MB * 8 / 1s / 1MB = 640 Mbps.
    assert speed == 640.0
    assert server == "TestServer"

def test_test_upload_speed_success(mocker):
    # Mock server
    mocker.patch.object(NetworkTester, '_upload_chunk', return_value=2 * 1024 * 1024)
    mocker.patch("time.time", side_effect=[0, 1])
    
    # 2MB * 4 threads = 8MB. 8MB * 8 / 1s / 1MB = 64 Mbps.
    speed = NetworkTester.test_upload_speed_mbps()
    assert speed == 64.0
