"""qBittorrent Optimizer - Точка входа.

Портабельное приложение для расчёта оптимальных настроек qBittorrent.
"""

import sys
from pathlib import Path

# Добавляем путь к приложению для портабельности
APP_DIR = Path(__file__).parent.resolve()
sys.path.insert(0, str(APP_DIR))

from PyQt6.QtWidgets import QApplication
from PyQt6.QtGui import QFont
from PyQt6.QtCore import Qt

from ui.main_window import MainWindow


def main():
    """Запуск приложения."""
    # High DPI support
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
    )
    
    app = QApplication(sys.argv)
    app.setStyle("Fusion")  # Современный кроссплатформенный стиль
    
    # Шрифт по умолчанию
    font = QFont("Segoe UI", 10)
    app.setFont(font)
    
    # Создаём и показываем окно
    window = MainWindow()
    window.show()
    
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
