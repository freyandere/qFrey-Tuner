"""Менеджер бенчмаркинга.

Взаимодействует с qBittorrent WebAPI для сбора метрик производительности.
"""

import time
import requests
from typing import Optional, Dict, Any, List

class BenchmarkManager:
    """Управление замерами производительности."""

    def __init__(self, host: str = "http://localhost:8080"):
        self.host = host
        self.session = requests.Session()
        self.is_connected = False

    def connect(self, username: str = "admin", password: str = "adminadmin") -> bool:
        """Авторизация в qBittorrent WebUI."""
        try:
            url = f"{self.host}/api/v2/auth/login"
            data = {"username": username, "password": password}
            # Попытка логина
            resp = self.session.post(url, data=data, timeout=5)
            if resp.status_code == 200 and "Ok" in resp.text:
                self.is_connected = True
                return True
        except Exception as e:
            print(f"Login error: {e}")
        return False

    def get_transfer_info(self) -> Optional[Dict[str, Any]]:
        """Получить общую информацию о скоростях."""
        if not self.is_connected:
            return None
        try:
            url = f"{self.host}/api/v2/transfer/info"
            resp = self.session.get(url, timeout=2)
            if resp.status_code == 200:
                return resp.json()
        except Exception:
            pass
        return None

    def get_main_stats(self) -> Dict[str, Any]:
        """Собрать основные показатели для отчета."""
        info = self.get_transfer_info()
        if not info:
            return {"dl_speed": 0, "ul_speed": 0, "dht_nodes": 0}
            
        return {
            "dl_speed": info.get("dl_info_speed", 0),  # bytes/s
            "ul_speed": info.get("up_info_speed", 0),  # bytes/s
            "dht_nodes": info.get("dht_nodes", 0),
            "connection_status": info.get("connection_status", "unknown")
        }

    def run_monitor(self, duration_sec: int = 60, interval_sec: int = 2) -> List[Dict[str, Any]]:
        """Запустить мониторинг на определенное время."""
        history = []
        end_time = time.time() + duration_sec
        
        while time.time() < end_time:
            history.append(self.get_main_stats())
            time.sleep(interval_sec)
            
        return history

    @staticmethod
    def analyze_results(history: List[Dict[str, Any]]) -> Dict[str, Any]:
        """Проанализировать стабильность и средние показатели."""
        if not history:
            return {}
            
        dl_speeds = [h["dl_speed"] for h in history]
        ul_speeds = [h["ul_speed"] for h in history]
        
        avg_dl = sum(dl_speeds) / len(dl_speeds)
        avg_ul = sum(ul_speeds) / len(ul_speeds)
        
        # Коэффициент вариации (стабильность)
        std_dl = (sum((s - avg_dl)**2 for s in dl_speeds) / len(dl_speeds))**0.5
        stability = 100 - (min(100, (std_dl / avg_dl * 100))) if avg_dl > 0 else 0
        
        return {
            "avg_dl_kbps": round(avg_dl / 1024, 1),
            "avg_ul_kbps": round(avg_ul / 1024, 1),
            "stability_score": round(stability, 1),
            "samples": len(history)
        }
