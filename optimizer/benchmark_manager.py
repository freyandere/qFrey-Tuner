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
        self.baseline_results: Optional[Dict[str, Any]] = None
        self.optimized_results: Optional[Dict[str, Any]] = None

    def connect(self, username: str = "admin", password: str = "adminadmin") -> bool:
        """Авторизация в qBittorrent WebUI."""
        try:
            # Сбрасываем сессию при новом подключении
            self.session = requests.Session()
            url = f"{self.host}/api/v2/auth/login"
            data = {"username": username, "password": password}
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

    def add_torrent(self, magnet_url: str, save_path: str = "") -> bool:
        """Добавить торрент в qBittorrent."""
        if not self.is_connected:
            return False
        try:
            url = f"{self.host}/api/v2/torrents/add"
            data = {"urls": magnet_url}
            if save_path:
                data["savepath"] = save_path
            resp = self.session.post(url, data=data, timeout=5)
            return resp.status_code == 200
        except Exception:
            return False

    def delete_torrent(self, torrent_hash: str, delete_files: bool = True) -> bool:
        """Удалить торрент и (опционально) файлы."""
        if not self.is_connected:
            return False
        try:
            url = f"{self.host}/api/v2/torrents/delete"
            data = {"hashes": torrent_hash, "deleteFiles": str(delete_files).lower()}
            resp = self.session.post(url, data=data, timeout=5)
            return resp.status_code == 200
        except Exception:
            return False

    def get_torrent_stats(self, torrent_hash: str) -> Optional[Dict[str, Any]]:
        """Получить статистику по конкретному торренту."""
        if not self.is_connected:
            return None
        try:
            url = f"{self.host}/api/v2/torrents/info"
            params = {"hashes": torrent_hash}
            resp = self.session.get(url, params=params, timeout=2)
            if resp.status_code == 200:
                torrents = resp.json()
                if torrents:
                    t = torrents[0]
                    return {
                        "dl_speed": t.get("dlspeed", 0),
                        "ul_speed": t.get("upspeed", 0),
                        "progress": t.get("progress", 0),
                        "state": t.get("state", "unknown"),
                        "num_seeds": t.get("num_seeds", 0),
                        "num_leechs": t.get("num_leechs", 0)
                    }
        except Exception:
            pass
        return None

    def run_monitor(self, duration_sec: int = 60, interval_sec: int = 1) -> List[Dict[str, Any]]:
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
        dht_nodes = [h["dht_nodes"] for h in history]
        
        avg_dl = sum(dl_speeds) / len(dl_speeds)
        avg_ul = sum(ul_speeds) / len(ul_speeds)
        avg_dht = sum(dht_nodes) / len(dht_nodes)
        
        # Коэффициент вариации (стабильность)
        if avg_dl > 0:
            std_dl = (sum((s - avg_dl)**2 for s in dl_speeds) / len(dl_speeds))**0.5
            stability = 100 - (min(100, (std_dl / avg_dl * 100)))
        else:
            stability = 0
        
        return {
            "avg_dl_mbps": round(avg_dl / (1024*1024), 2),
            "avg_ul_mbps": round(avg_ul / (1024*1024), 2),
            "avg_dht": int(avg_dht),
            "stability_score": round(stability, 1),
            "samples": len(history)
        }

    def get_comparison_report(self) -> str:
        """Сгенерировать HTML отчет сравнения."""
        if not self.baseline_results or not self.optimized_results:
            return "Недостаточно данных для сравнения. Проведите оба замера."
        
        b = self.baseline_results
        o = self.optimized_results
        
        def get_diff(cur, prev):
            if prev == 0: return ""
            diff = ((cur - prev) / prev) * 100
            color = "#28a745" if diff >= 0 else "#dc3545"
            sign = "+" if diff > 0 else ""
            return f" <span style='color: {color}; font-size: 0.9em;'>({sign}{diff:.1f}%)</span>"

        report = f"""
        <div style='background: #1e1e1e; padding: 15px; border-radius: 8px; border: 1px solid #333;'>
            <h3 style='color: #6ea8fe; margin-top: 0;'>📊 Отчет сравнения</h3>
            <table style='width: 100%; border-collapse: collapse; color: #e0e0e0;'>
                <tr style='border-bottom: 1px solid #444;'>
                    <th style='text-align: left; padding: 8px;'>Показатель</th>
                    <th style='text-align: center; padding: 8px;'>Baseline</th>
                    <th style='text-align: center; padding: 8px;'>Optimized</th>
                </tr>
                <tr>
                    <td style='padding: 8px;'>Средняя загрузка</td>
                    <td style='text-align: center;'>{b['avg_dl_mbps']} МБ/с</td>
                    <td style='text-align: center;'>{o['avg_dl_mbps']} МБ/с{get_diff(o['avg_dl_mbps'], b['avg_dl_mbps'])}</td>
                </tr>
                <tr>
                    <td style='padding: 8px;'>Средняя отдача</td>
                    <td style='text-align: center;'>{b['avg_ul_mbps']} МБ/с</td>
                    <td style='text-align: center;'>{o['avg_ul_mbps']} МБ/с{get_diff(o['avg_ul_mbps'], b['avg_ul_mbps'])}</td>
                </tr>
                <tr>
                    <td style='padding: 8px;'>Стабильность</td>
                    <td style='text-align: center;'>{b['stability_score']}%</td>
                    <td style='text-align: center;'>{o['stability_score']}%{get_diff(o['stability_score'], b['stability_score'])}</td>
                </tr>
                <tr>
                    <td style='padding: 8px;'>DHT Узлы (avg)</td>
                    <td style='text-align: center;'>{b['avg_dht']}</td>
                    <td style='text-align: center;'>{o['avg_dht']}{get_diff(o['avg_dht'], b['avg_dht'])}</td>
                </tr>
            </table>
            <p style='color: #888; font-size: 0.8em; margin-top: 15px; font-style: italic;'>
                * Замеры проводились по {o['samples']} точкам (1 сек интервал).
            </p>
        </div>
        """
        return report
