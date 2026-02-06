"""Легковесный тестер скорости сети.

Использует HTTP запросы для оценки пропускной способности.
"""

import time
import requests
from typing import Tuple

class NetworkTester:
    """Оценка скорости интернет-соединения."""
    
    # Список качественных CDN и серверов для теста
    SERVERS = [
        {"name": "Cloudflare (Global)", "url": "https://speed.cloudflare.com/__down", "up_url": "https://speed.cloudflare.com/__up"},
        {"name": "Microsoft Azure (EU)", "url": "https://azspeedtest.blob.core.windows.net/speedtest/100MB.bin", "up_url": ""},
        {"name": "Google Cloud (US)", "url": "https://storage.googleapis.com/gcd-speedtest/100MB.bin", "up_url": ""},
    ]

    _session = requests.Session()

    @staticmethod
    def get_best_server() -> dict:
        """Выбрать сервер с наименьшей задержкой."""
        best_server = NetworkTester.SERVERS[0]
        min_latency = float('inf')
        
        for server in NetworkTester.SERVERS:
            try:
                start = time.time()
                NetworkTester._session.head(server["url"], timeout=2)
                latency = time.time() - start
                if latency < min_latency:
                    min_latency = latency
                    best_server = server
            except Exception:
                continue
        return best_server

    @staticmethod
    def _download_chunk(url: str, size: int) -> int:
        """Загрузить кусок данных и вернуть размер."""
        try:
            sep = "&" if "?" in url else "?"
            test_url = f"{url}{sep}bytes={size}&cb={time.time()}"
            headers = {"Cache-Control": "no-cache", "Pragma": "no-cache"}
            
            r = NetworkTester._session.get(test_url, timeout=15, stream=True, headers=headers)
            downloaded = 0
            for chunk in r.iter_content(chunk_size=512*1024):
                if chunk:
                    downloaded += len(chunk)
            return downloaded
        except Exception:
            return 0

    @staticmethod
    def test_download_speed_mbps() -> Tuple[float, str]:
        """Оценить скорость загрузки."""
        server = NetworkTester.get_best_server()
        num_threads = 8 # Для 1Gbps+
        chunk_size = 20 * 1024 * 1024 # 20MB
        
        from concurrent.futures import ThreadPoolExecutor
        
        start_time = time.time()
        with ThreadPoolExecutor(max_workers=num_threads) as executor:
            futures = [executor.submit(NetworkTester._download_chunk, server["url"], chunk_size) for _ in range(num_threads)]
            total_bytes = sum(f.result() for f in futures)
            
        duration = time.time() - start_time
        if duration > 0.1 and total_bytes > 0:
            mbps = (total_bytes * 8 / duration) / (1024 * 1024)
            # Если тест прошел слишком быстро на одном сервере, результат может быть неточным,
            # но мы хотя бы покажем число, а не ошибку.
            return round(mbps, 1), server["name"]
        
        return 0.0, "Error"

    @staticmethod
    def _upload_chunk(url: str, data: bytes) -> int:
        """Отправить кусок данных."""
        try:
            headers = {"Cache-Control": "no-cache", "Pragma": "no-cache"}
            r = NetworkTester._session.post(url, data=data, timeout=15, headers=headers)
            if r.status_code == 200:
                return len(data)
        except Exception:
            pass
        return 0

    @staticmethod
    def test_upload_speed_mbps() -> float:
        """Оценить скорость отдачи."""
        server = NetworkTester.SERVERS[0]
        if not server["up_url"]:
            return 0.0
            
        # Увеличиваем нагрузку для точного замера на 100Mbit+
        num_threads = 6
        chunk_size = 10 * 1024 * 1024 # 10MB
        data = b"0" * chunk_size
        
        from concurrent.futures import ThreadPoolExecutor
        
        start_time = time.time()
        with ThreadPoolExecutor(max_workers=num_threads) as executor:
            futures = [executor.submit(NetworkTester._upload_chunk, server["up_url"], data) for _ in range(num_threads)]
            total_bytes = sum(f.result() for f in futures)
            
        duration = time.time() - start_time
        if duration > 0.1 and total_bytes > 0:
            mbps = (total_bytes * 8 / duration) / (1024 * 1024)
            return round(mbps, 1)
        return 0.0

    @staticmethod
    def run_full_test() -> Tuple[float, float, str]:
        """Запустить полный тест."""
        dl, server_name = NetworkTester.test_download_speed_mbps()
        ul = NetworkTester.test_upload_speed_mbps()
        return dl, ul, server_name

if __name__ == "__main__":
    dl, ul, server = NetworkTester.run_full_test()
    print(f"Server: {server}")
    print(f"Download: {dl} Mbps")
    print(f"Upload: {ul} Mbps")
