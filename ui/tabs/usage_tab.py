"""Вкладка сценария использования."""

from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QRadioButton,
    QButtonGroup,
    QGroupBox,
    QLabel,
)
from PyQt6.QtCore import Qt

from optimizer.models import TrackerType, UserRole, EnvironmentProfile, UsageSettings


class UsageTab(QWidget):
    """Вкладка для выбора сценария использования."""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self._environment = EnvironmentProfile.DESKTOP
        self._setup_ui()
    
    def _setup_ui(self):
        layout = QVBoxLayout(self)
        layout.setSpacing(16)
        
        # === Тип трекеров (ОБЯЗАТЕЛЬНО) ===
        tracker_group = QGroupBox("Тип трекеров *")
        tracker_group.setStyleSheet("QGroupBox { font-weight: bold; }")
        tracker_layout = QVBoxLayout(tracker_group)
        
        self.tracker_group = QButtonGroup(self)
        
        # Public
        self.public_radio = QRadioButton("Публичные трекеры (rutracker, 1337x, и т.д.)")
        self.public_radio.setChecked(True)
        self.tracker_group.addButton(self.public_radio)
        tracker_layout.addWidget(self.public_radio)
        
        public_hint = QLabel("  DHT, PeX, LSD включены; Anonymous Mode активен")
        public_hint.setStyleSheet("color: #28a745; font-size: 11px; margin-left: 20px;")
        tracker_layout.addWidget(public_hint)
        
        # Private
        self.private_radio = QRadioButton("Приватные трекеры (с учётом ratio)")
        self.tracker_group.addButton(self.private_radio)
        tracker_layout.addWidget(self.private_radio)
        
        private_hint = QLabel("  DHT/PeX OFF, Anonymous OFF, Upload slots = 4-8")
        private_hint.setStyleSheet("color: #ff6b6b; font-size: 11px; margin-left: 20px;")
        tracker_layout.addWidget(private_hint)
        
        layout.addWidget(tracker_group)
        
        # === Роль ===
        role_group = QGroupBox("Ваша роль")
        role_layout = QVBoxLayout(role_group)
        
        role_label = QLabel("Выберите основной сценарий использования:")
        role_label.setStyleSheet("color: #bbb;")
        role_layout.addWidget(role_label)
        
        self.role_group = QButtonGroup(self)
        
        # Leecher
        self.leecher_radio = QRadioButton("Личер — в основном скачиваю")
        self.leecher_radio.setChecked(True)
        self.role_group.addButton(self.leecher_radio)
        role_layout.addWidget(self.leecher_radio)
        
        leecher_hint = QLabel("  Приоритет на скорость загрузки")
        leecher_hint.setStyleSheet("color: #aaa; font-size: 11px; margin-left: 20px;")
        role_layout.addWidget(leecher_hint)
        
        # Seeder
        self.seeder_radio = QRadioButton("Сидер — раздаю много торрентов")
        self.role_group.addButton(self.seeder_radio)
        role_layout.addWidget(self.seeder_radio)
        
        seeder_hint = QLabel("  Приоритет на стабильную отдачу, больше слотов")
        seeder_hint.setStyleSheet("color: #aaa; font-size: 11px; margin-left: 20px;")
        role_layout.addWidget(seeder_hint)
        
        # Uploader
        self.uploader_radio = QRadioButton("Аплоадер — создаю новые раздачи")
        self.role_group.addButton(self.uploader_radio)
        role_layout.addWidget(self.uploader_radio)
        
        uploader_hint = QLabel("  Super Seeding для быстрого распространения")
        uploader_hint.setStyleSheet("color: #aaa; font-size: 11px; margin-left: 20px;")
        role_layout.addWidget(uploader_hint)
        
        layout.addWidget(role_group)
        
        # === Справка ===
        info_group = QGroupBox("Справка")
        info_layout = QVBoxLayout(info_group)
        
        info_text = QLabel(
            "<span style='color: #e0e0e0;'>"
            "<b style='color: #6ea8fe;'>Публичные трекеры:</b> DHT и PeX помогают найти пиров<br><br>"
            "<b style='color: #ff6b6b;'>Приватные трекеры:</b> DHT/PeX могут привести к бану! "
            "Upload slots ограничены для «гонки» (racing)<br><br>"
            "<b style='color: #ffc107;'>Super Seeding:</b> Раздаёт каждый кусок только одному пиру"
            "</span>"
        )
        info_text.setWordWrap(True)
        info_text.setTextFormat(Qt.TextFormat.RichText)
        info_layout.addWidget(info_text)
        
        layout.addWidget(info_group)
        
        layout.addStretch()
    
    def set_environment(self, env: EnvironmentProfile):
        """Установить среду (из Welcome Dialog)."""
        self._environment = env

    def set_settings(self, settings: UsageSettings):
        """Восстановить сценарий использования."""
        self._environment = settings.environment
        
        if settings.tracker_type == TrackerType.PUBLIC:
            self.public_radio.setChecked(True)
        else:
            self.private_radio.setChecked(True)
            
        if settings.user_role == UserRole.LEECHER:
            self.leecher_radio.setChecked(True)
        elif settings.user_role == UserRole.SEEDER:
            self.seeder_radio.setChecked(True)
        else:
            self.uploader_radio.setChecked(True)

    
    def get_settings(self) -> UsageSettings:
        """Получить выбранный сценарий использования."""
        if self.public_radio.isChecked():
            tracker_type = TrackerType.PUBLIC
        else:
            tracker_type = TrackerType.PRIVATE
        
        if self.leecher_radio.isChecked():
            role = UserRole.LEECHER
        elif self.seeder_radio.isChecked():
            role = UserRole.SEEDER
        else:
            role = UserRole.UPLOADER
        
        return UsageSettings(
            tracker_type=tracker_type, 
            user_role=role,
            environment=self._environment,
        )
