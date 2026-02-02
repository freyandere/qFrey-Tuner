"""Менеджер сессий для сохранения введённых параметров."""

import json
from pathlib import Path
from typing import Optional, Any

class SessionManager:
    """Управление сохранением и загрузкой пользовательских данных."""

    def __init__(self, filename: str = "session.json"):
        # Сохраняем рядом с приложением или в profile если есть
        self.session_path = self._get_session_path(filename)

    def _get_session_path(self, filename: str) -> Path:
        # Проверяем портабельную папку
        portable_dir = Path("profile/qBittorrent")
        if portable_dir.exists():
            return portable_dir / filename
        
        # fallback в корень проекта (или appdata в будущем)
        return Path(filename)

    def save_session(self, data: dict[str, Any]):
        """Сохранить данные сессии в JSON."""
        try:
            self.session_path.parent.mkdir(parents=True, exist_ok=True)
            with open(self.session_path, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=4, ensure_ascii=False)
        except Exception as e:
            print(f"Error saving session: {e}")

    def load_session(self) -> dict[str, Any]:
        """Загрузить данные сессии из JSON."""
        if not self.session_path.exists():
            return {}
        
        try:
            with open(self.session_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"Error loading session: {e}")
            return {}
