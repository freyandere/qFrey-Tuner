"""Диалог приветствия для выбора среды установки."""

from PyQt6.QtWidgets import (
    QDialog,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QFrame,
    QGridLayout,
)
from PyQt6.QtCore import Qt
from PyQt6.QtGui import QFont

from optimizer.models import EnvironmentProfile


# Данные профилей: иконка, название, описание
PROFILES_DATA = {
    EnvironmentProfile.SYSTEM: {
        "icon": "🖥️",
        "title": "System Desktop",
        "subtitle": "Windows / macOS / Linux",
        "description": "Стандартная установка. Конфиг ищется в системных папках (%APPDATA% или .config).",
    },
    EnvironmentProfile.PORTABLE: {
        "icon": "🚀",
        "title": "Portable",
        "subtitle": "Windows (EXE folder)",
        "description": "Портабельная версия. Конфиг ищется в папке с программой или подпапке profile/.",
    },
    EnvironmentProfile.TRUENAS: {
        "icon": "🗄️",
        "title": "TrueNAS / ZFS",
        "subtitle": "FreeNAS, TrueNAS",
        "description": "Disk Cache отключён — ZFS ARC управляет кэшированием.",
    },
    EnvironmentProfile.NAS: {
        "icon": "📦",
        "title": "NAS",
        "subtitle": "Synology / QNAP",
        "description": "Настройки для сетевых хранилищ без ZFS.",
    },
    EnvironmentProfile.DOCKER: {
        "icon": "🐳",
        "title": "Docker",
        "subtitle": "Контейнер с VPN",
        "description": "Привязка к tun0/wg0 внутри контейнера.",
    },
    EnvironmentProfile.SEEDBOX: {
        "icon": "⚡",
        "title": "Seedbox",
        "subtitle": "1-10 Gbps",
        "description": "Экстремальные настройки для гигабитных каналов.",
    },
}


class ProfileCard(QFrame):
    """Карточка выбора профиля."""
    
    def __init__(self, profile: EnvironmentProfile, parent=None):
        super().__init__(parent)
        self.profile = profile
        self.selected = False
        self._setup_ui()
        self._update_style()
    
    def _setup_ui(self):
        data = PROFILES_DATA[self.profile]
        
        self.setFixedSize(160, 120)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        
        layout = QVBoxLayout(self)
        layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.setSpacing(4)
        
        # Иконка
        icon = QLabel(data["icon"])
        icon.setFont(QFont("Segoe UI Emoji", 28))
        icon.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(icon)
        
        # Название
        title = QLabel(data["title"])
        title.setFont(QFont("Segoe UI", 11, QFont.Weight.Bold))
        title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        title.setStyleSheet("color: #e0e0e0;")
        layout.addWidget(title)
        
        # Подзаголовок
        subtitle = QLabel(data["subtitle"])
        subtitle.setFont(QFont("Segoe UI", 9))
        subtitle.setAlignment(Qt.AlignmentFlag.AlignCenter)
        subtitle.setStyleSheet("color: #888;")
        layout.addWidget(subtitle)
    
    def _update_style(self):
        if self.selected:
            self.setStyleSheet("""
                ProfileCard {
                    background: #1a3a5c;
                    border: 2px solid #0d6efd;
                    border-radius: 12px;
                }
            """)
        else:
            self.setStyleSheet("""
                ProfileCard {
                    background: #2a2a2a;
                    border: 1px solid #444;
                    border-radius: 12px;
                }
                ProfileCard:hover {
                    background: #333;
                    border-color: #666;
                }
            """)
    
    def set_selected(self, selected: bool):
        self.selected = selected
        self._update_style()
    
    def mousePressEvent(self, event):
        if event.button() == Qt.MouseButton.LeftButton:
            # Emit custom signal via parent
            parent = self.parent()
            while parent and not isinstance(parent, WelcomeDialog):
                parent = parent.parent()
            if parent:
                parent._on_profile_selected(self.profile)
        super().mousePressEvent(event)


class WelcomeDialog(QDialog):
    """Диалог приветствия для выбора среды установки."""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.selected_profile = EnvironmentProfile.SYSTEM
        self._profile_cards: dict[EnvironmentProfile, ProfileCard] = {}
        self._setup_ui()
    
    def _setup_ui(self):
        self.setWindowTitle("qBittorrent Optimizer")
        self.setFixedSize(520, 420)
        self.setStyleSheet("""
            QDialog {
                background: #1e1e1e;
            }
        """)
        
        layout = QVBoxLayout(self)
        layout.setSpacing(16)
        layout.setContentsMargins(24, 24, 24, 24)
        
        # Заголовок
        title = QLabel("🚀 Добро пожаловать!")
        title.setFont(QFont("Segoe UI", 18, QFont.Weight.Bold))
        title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        title.setStyleSheet("color: #e0e0e0;")
        layout.addWidget(title)
        
        # Подзаголовок
        subtitle = QLabel("Где установлен ваш qBittorrent?")
        subtitle.setFont(QFont("Segoe UI", 12))
        subtitle.setAlignment(Qt.AlignmentFlag.AlignCenter)
        subtitle.setStyleSheet("color: #aaa;")
        layout.addWidget(subtitle)
        
        layout.addSpacing(10)
        
        # Карточки профилей
        cards_layout = QGridLayout()
        cards_layout.setSpacing(12)
        cards_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        profiles = list(EnvironmentProfile)
        positions = [(0, 0), (0, 1), (0, 2), (1, 0), (1, 1)]
        
        for i, profile in enumerate(profiles):
            card = ProfileCard(profile, self)
            self._profile_cards[profile] = card
            row, col = positions[i]
            cards_layout.addWidget(card, row, col)
        
        layout.addLayout(cards_layout)
        
        # Устанавливаем Desktop по умолчанию
        self._profile_cards[EnvironmentProfile.SYSTEM].set_selected(True)
        
        # Описание выбранного профиля
        self.description_label = QLabel(PROFILES_DATA[EnvironmentProfile.SYSTEM]["description"])
        self.description_label.setFont(QFont("Segoe UI", 10))
        self.description_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.description_label.setStyleSheet("color: #888; padding: 10px;")
        self.description_label.setWordWrap(True)
        layout.addWidget(self.description_label)
        
        layout.addStretch()
        
        # Кнопки
        buttons_layout = QHBoxLayout()
        buttons_layout.setSpacing(12)
        
        self.continue_btn = QPushButton("Продолжить")
        self.continue_btn.setMinimumHeight(45)
        self.continue_btn.setFont(QFont("Segoe UI", 11, QFont.Weight.Bold))
        self.continue_btn.clicked.connect(self.accept)
        self.continue_btn.setStyleSheet("""
            QPushButton {
                background: #0d6efd;
                color: white;
                border: none;
                border-radius: 8px;
                padding: 10px 30px;
            }
            QPushButton:hover {
                background: #0b5ed7;
            }
        """)
        buttons_layout.addWidget(self.continue_btn)
        
        layout.addLayout(buttons_layout)
    
    def _on_profile_selected(self, profile: EnvironmentProfile):
        """Обработка выбора профиля."""
        # Сбрасываем все карточки
        for card in self._profile_cards.values():
            card.set_selected(False)
        
        # Выбираем новую
        self._profile_cards[profile].set_selected(True)
        self.selected_profile = profile
        
        # Обновляем описание
        self.description_label.setText(PROFILES_DATA[profile]["description"])
    
    def get_selected_profile(self) -> EnvironmentProfile:
        """Получить выбранный профиль."""
        return self.selected_profile
