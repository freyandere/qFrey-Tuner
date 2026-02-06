"""Главное окно приложения."""

from pathlib import Path
from PyQt6.QtWidgets import (
    QMainWindow,
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QTabWidget,
    QPushButton,
    QTextEdit,
    QSplitter,
    QLabel,
    QMessageBox,
    QCheckBox,
    QFileDialog,
    QToolButton,
)
import os
import subprocess
from PyQt6.QtCore import Qt
from PyQt6.QtGui import QFont

from .tabs.network_tab import NetworkTab
from .tabs.hardware_tab import HardwareTab
from .tabs.usage_tab import UsageTab
from .tabs.benchmark_tab import BenchmarkTab
from .welcome_dialog import WelcomeDialog, PROFILES_DATA
from optimizer.calculator import calculate_optimal_settings
from optimizer.models import (
    OptimizedSettings, EnvironmentProfile, NetworkSettings, 
    HardwareSettings, UsageSettings, ConnectionType, StorageType,
    TrackerType, UserRole
)
from optimizer.config_manager import ConfigManager
from optimizer.session_manager import SessionManager


class MainWindow(QMainWindow):
    """Главное окно qBittorrent Optimizer."""
    
    def __init__(self):
        super().__init__()
        self.setWindowTitle("qBittorrent Optimizer")
        self.setMinimumSize(1000, 750)
        self._show_advanced = False
        self._environment = EnvironmentProfile.SYSTEM
        self._last_result: OptimizedSettings | None = None
        self.config_manager = ConfigManager()
        self.session_manager = SessionManager()
        self._setup_ui()
        if not self._load_session():
            self._show_welcome()
    
    def _setup_ui(self):
        central = QWidget()
        self.setCentralWidget(central)
        
        main_layout = QVBoxLayout(central)
        main_layout.setContentsMargins(16, 16, 16, 16)
        main_layout.setSpacing(16)
        
        # Header
        header = QLabel("qBittorrent Optimizer")
        header.setFont(QFont("Segoe UI", 18, QFont.Weight.Bold))
        header.setAlignment(Qt.AlignmentFlag.AlignCenter)
        main_layout.addWidget(header)
        
        subtitle = QLabel("Рассчитайте оптимальные настройки на основе вашего железа и сети")
        subtitle.setAlignment(Qt.AlignmentFlag.AlignCenter)
        subtitle.setStyleSheet("color: #aaa; margin-bottom: 10px;")
        main_layout.addWidget(subtitle)
        
        # Environment selection (Top Badge)
        self.env_btn = QPushButton()
        self.env_btn.setToolTip("Нажмите, чтобы сменить среду")
        self.env_btn.clicked.connect(self._show_welcome)
        self.env_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self.env_btn.setStyleSheet("""
            QPushButton {
                background: #1a3a5c;
                color: #6ea8fe;
                padding: 6px 16px;
                border-radius: 12px;
                font-weight: bold;
                border: 1px solid #2a4a6c;
            }
            QPushButton:hover {
                background: #2a4a6c;
                border: 1px solid #6ea8fe;
            }
        """)
        
        env_container = QHBoxLayout()
        env_container.addStretch()
        env_container.addWidget(self.env_btn)
        env_container.addStretch()
        main_layout.addLayout(env_container)
        
        # Legend
        legend = QLabel("* — обязательные поля")
        legend.setAlignment(Qt.AlignmentFlag.AlignCenter)
        legend.setStyleSheet("color: #888; font-size: 11px; margin-bottom: 5px;")
        main_layout.addWidget(legend)
        
        # Splitter
        splitter = QSplitter(Qt.Orientation.Horizontal)
        
        # === Left: Tabs ===
        tabs_widget = QWidget()
        tabs_layout = QVBoxLayout(tabs_widget)
        tabs_layout.setContentsMargins(0, 0, 0, 0)
        
        self.tabs = QTabWidget()
        self.network_tab = NetworkTab()
        self.hardware_tab = HardwareTab()
        self.usage_tab = UsageTab()
        self.benchmark_tab = BenchmarkTab()
        
        self.tabs.addTab(self.network_tab, "📡 Сеть")
        self.tabs.addTab(self.hardware_tab, "💻 Железо")
        self.tabs.addTab(self.usage_tab, "⚙️ Сценарий")
        self.tabs.addTab(self.benchmark_tab, "🚀 Бенчмарк")
        
        tabs_layout.addWidget(self.tabs)
        
        # Buttons
        buttons_layout = QHBoxLayout()
        
        tabs_layout.addStretch()
        
        # Calculate button
        self.calc_button = QPushButton("Рассчитать настройки")
        self.calc_button.setMinimumHeight(50)
        self.calc_button.setFont(QFont("Segoe UI", 12, QFont.Weight.Bold))
        self.calc_button.clicked.connect(self._on_calculate)
        self.calc_button.setStyleSheet("""
            QPushButton {
                background-color: #0d6efd;
                color: white;
                border: none;
                border-radius: 8px;
                padding: 12px;
            }
            QPushButton:hover {
                background-color: #0b5ed7;
            }
            QPushButton:pressed {
                background-color: #0a58ca;
            }
        """)
        tabs_layout.addWidget(self.calc_button)
        
        splitter.addWidget(tabs_widget)
        
        # === Right: Results ===
        results_widget = QWidget()
        results_layout = QVBoxLayout(results_widget)
        results_layout.setContentsMargins(0, 0, 0, 0)
        
        results_header = QLabel("Результаты")
        results_header.setFont(QFont("Segoe UI", 14, QFont.Weight.Bold))
        results_layout.addWidget(results_header)
        
        # Advanced checkbox
        self.advanced_check = QCheckBox("Показать Advanced настройки")
        self.advanced_check.setStyleSheet("color: #ffc107;")
        self.advanced_check.stateChanged.connect(self._on_advanced_toggled)
        results_layout.addWidget(self.advanced_check)
        
        self.results_text = QTextEdit()
        self.results_text.setReadOnly(True)
        self.results_text.setPlaceholderText(
            "Заполните обязательные поля (*) и нажмите\n"
            "'Рассчитать настройки' чтобы увидеть результаты..."
        )
        self.results_text.setFont(QFont("Consolas", 10))
        self.results_text.setStyleSheet("""
            QTextEdit {
                background-color: #1e1e1e;
                color: #e0e0e0;
                border: 1px solid #444;
                border-radius: 6px;
                padding: 8px;
            }
        """)
        results_layout.addWidget(self.results_text)
        
        # Apply button
        self.apply_button = QPushButton(" Применить в qBittorrent")
        self.apply_button.setMinimumHeight(45)
        self.apply_button.setFont(QFont("Segoe UI", 11, QFont.Weight.Bold))
        self.apply_button.clicked.connect(self._on_apply_settings)
        self.apply_button.setEnabled(False)
        self.apply_button.setStyleSheet("""
            QPushButton {
                background-color: #198754;
                color: white;
                border: none;
                border-radius: 8px;
                padding: 10px;
                margin-top: 10px;
            }
            QPushButton:hover {
                background-color: #157347;
            }
            QPushButton:disabled {
                background-color: #2c3e50;
                color: #7f8c8d;
            }
        """)
        results_layout.addWidget(self.apply_button)
        
        # Config path label
        self.config_status_label = QLabel()
        self._update_config_status()
        results_layout.addWidget(self.config_status_label)
        
        # Manual path button
        self.manual_config_btn = QPushButton("Выбрать конфиг вручную...")
        self.manual_config_btn.clicked.connect(self._on_select_manual_config)
        self.manual_config_btn.setStyleSheet("""
            QPushButton {
                background: transparent;
                color: #6ea8fe;
                border: none;
                text-decoration: underline;
                font-size: 11px;
                text-align: left;
                padding: 0;
            }
            QPushButton:hover {
                color: #8bb9fe;
            }
        """)
        results_layout.addWidget(self.manual_config_btn)
        
        # Help label for portable users
        help_label = QLabel(
            "Подсказка: Для портабельной версии ищите 'qBittorrent.ini' "
            "в папке 'profile/qBittorrent/config/'"
        )
        help_label.setWordWrap(True)
        help_label.setStyleSheet("color: #777; font-size: 10px; font-style: italic;")
        results_layout.addWidget(help_label)
        
        splitter.addWidget(results_widget)
        splitter.setSizes([500, 500])
        
        main_layout.addWidget(splitter)
    
    def _show_welcome(self):
        """Показать диалог выбора среды."""
        dialog = WelcomeDialog(self)
        if dialog.exec():
            self._environment = dialog.get_selected_profile()
            self.usage_tab.set_environment(self._environment)
            self._update_env_badge()
    
    def _update_env_badge(self):
        """Обновить badge среды."""
        data = PROFILES_DATA[self._environment]
        self.env_btn.setText(f"{data['icon']} {data['title']}")
    
    def _on_config_label_clicked(self, event):
        """Открыть папку с конфигом."""
        if self.config_manager.config_path:
            folder = self.config_manager.config_path.parent
            if os.path.exists(folder):
                os.startfile(folder)

    def _update_config_status(self):
        """Обновить статус конфига."""
        if hasattr(self, 'config_status_label'):
            if self.config_manager.config_path:
                type_str = f" ({self.config_manager.installation_type})"
                self.config_status_label.setText(f"📁 Обнаружен конфиг{type_str}: {self.config_manager.config_path.name}")
                self.config_status_label.setToolTip(f"Нажмите, чтобы открыть папку:\n{path_str}")
                self.config_status_label.setStyleSheet("""
                    QLabel { 
                        color: #28a745; 
                        font-size: 11px; 
                        text-decoration: underline;
                    }
                    QLabel:hover {
                        color: #34ce57;
                    }
                """)
                self.config_status_label.setCursor(Qt.CursorShape.PointingHandCursor)
                self.config_status_label.mouseReleaseEvent = self._on_config_label_clicked
            else:
                self.config_status_label.setText("❌ Конфиг qBittorrent не найден")
                self.config_status_label.setStyleSheet("color: #dc3545; font-size: 11px;")
                self.config_status_label.setToolTip("")
                self.config_status_label.setCursor(Qt.CursorShape.ArrowCursor)
                self.config_status_label.mouseReleaseEvent = None
    
    def _on_advanced_toggled(self, state):
        self._show_advanced = state == Qt.CheckState.Checked.value
        if self._last_result:
            output = self._format_results(self._last_result)
            self.results_text.setHtml(output)
    
    def _on_calculate(self):
        """Рассчитать и показать оптимальные настройки."""
        untouched_fields = []
        untouched_fields.extend(self.network_tab.get_untouched_fields())
        untouched_fields.extend(self.hardware_tab.get_untouched_fields())
        
        if untouched_fields:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Icon.Question)
            msg.setWindowTitle("Подтверждение")
            msg.setText("Некоторые обязательные поля остались с дефолтными значениями:")
            
            details = "\n".join(f"  • {f}" for f in untouched_fields)
            msg.setInformativeText(
                f"{details}\n\n"
                "Вы уверены что они соответствуют вашей системе?\n"
                "Или забыли их изменить?"
            )
            
            msg.setStandardButtons(
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
            )
            msg.button(QMessageBox.StandardButton.Yes).setText("Да, рассчитать")
            msg.button(QMessageBox.StandardButton.No).setText("Нет, изменю")
            
            if msg.exec() != QMessageBox.StandardButton.Yes:
                return
        
        network = self.network_tab.get_settings()
        hardware = self.hardware_tab.get_settings()
        usage = self.usage_tab.get_settings()
        
        result = calculate_optimal_settings(network, hardware, usage)
        self._last_result = result
        
        # Save session
        self._save_session(network, hardware, usage)
        
        output = self._format_results(result)
        self.results_text.setHtml(output)
        
        # Enable apply button if config found
        if self.config_manager.config_path:
            self.apply_button.setEnabled(True)

    def _on_apply_settings(self):
        """Записать настройки в файл."""
        if not self._last_result:
            return

        reply = QMessageBox.question(
            self, "Подтверждение",
            "Вы уверены, что хотите перезаписать настройки qBittorrent?\n\n"
            "⚠️ Рекомендуется закрыть qBittorrent перед применением.",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )

        if reply == QMessageBox.StandardButton.Yes:
            success = self.config_manager.apply_settings(self._last_result)
            if success:
                QMessageBox.information(
                    self, "Успех",
                    "Настройки успешно применены!\n"
                    "Перезапустите qBittorrent для активации изменений."
                )
            else:
                QMessageBox.critical(
                    self, "Ошибка",
                    "Не удалось записать настройки в файл."
                )

    def _save_session(self, n: NetworkSettings, h: HardwareSettings, u: UsageSettings):
        """Сохранить текущие параметры в JSON."""
        data = {
            "network": {
                "download": n.download_speed_mbps,
                "upload": n.upload_speed_mbps,
                "type": n.connection_type.name,
                "use_vpn": n.use_vpn,
                "vpn_interface": n.vpn_interface,
                "isp_throttling": n.isp_throttling,
            },
            "hardware": {
                "storage": h.storage_type.name,
                "ram": h.ram_gb,
                "cores": h.cpu_cores,
                "is_hybrid": h.is_hybrid_cpu,
                "p_cores": h.p_cores,
            },
            "usage": {
                "tracker": u.tracker_type.name,
                "role": u.user_role.name,
                "environment": u.environment.name,
            }
        }
        self.session_manager.save_session(data)

    def _load_session(self) -> bool:
        """Загрузить параметры из JSON. Возвращает True если загружено успешно."""
        data = self.session_manager.load_session()
        if not data:
            return False

        try:
            # Восстанавливаем Network
            nw = data["network"]
            n_settings = NetworkSettings(
                download_speed_mbps=nw["download"],
                upload_speed_mbps=nw["upload"],
                connection_type=ConnectionType[nw["type"]],
                use_vpn=nw["use_vpn"],
                vpn_interface=nw["vpn_interface"],
                isp_throttling=nw["isp_throttling"],
            )
            self.network_tab.set_settings(n_settings)

            # Восстанавливаем Hardware
            hw = data["hardware"]
            h_settings = HardwareSettings(
                storage_type=StorageType[hw["storage"]],
                ram_gb=hw["ram"],
                cpu_cores=hw["cores"],
                is_hybrid_cpu=hw["is_hybrid"],
                p_cores=hw["p_cores"],
            )
            self.hardware_tab.set_settings(h_settings)

            # Восстанавливаем Usage
            us = data["usage"]
            u_settings = UsageSettings(
                tracker_type=TrackerType[us["tracker"]],
                user_role=UserRole[us["role"]],
                environment=EnvironmentProfile[us["environment"]],
            )
            self.usage_tab.set_settings(u_settings)
            
            # Обновляем внутреннее состояние среды
            self._environment = u_settings.environment
            self._update_env_badge()
            return True

        except Exception as e:
            print(f"Error restoring session: {e}")
            return False

    def _on_select_manual_config(self):
        """Диалог выбора файла конфигурации."""
        file_path, _ = QFileDialog.getOpenFileName(
            self, "Выберите файл конфигурации qBittorrent",
            "", "Config files (*.ini *.conf);;All files (*.*)"
        )
        
        if file_path:
            if self.config_manager.set_manual_path(file_path):
                self._update_config_status()
                if self._last_result:
                    self.apply_button.setEnabled(True)
                QMessageBox.information(self, "Успех", "Конфигурационный файл успешно распознан.")
            else:
                QMessageBox.warning(
                    self, "Внимание", 
                    "Выбранный файл не похож на валидный конфиг qBittorrent.\n"
                    "Пожалуйста, выберите файл .ini или .conf, содержащий настройки."
                )

    def _format_results(self, r: OptimizedSettings) -> str:
        """Форматировать результаты в HTML."""
        
        # Warnings
        warnings_html = ""
        if r.warnings:
            warnings_html = """
            <div style='background: #4a3f00; padding: 10px; border-radius: 6px; 
                        margin-bottom: 15px; border: 1px solid #665500;'>
                <b style='color: #ffc107;'>Важные замечания:</b>
                <ul style='margin: 5px 0; color: #e0e0e0;'>
            """
            for w in r.warnings:
                warnings_html += f"<li>{w}</li>"
            warnings_html += "</ul></div>"
        
        def bool_html(val: bool, yes: str = "Вкл", no: str = "Выкл") -> str:
            if val:
                return f"<span class='bool-yes'>{yes}</span>"
            return f"<span class='bool-no'>{no}</span>"
        
        def explain(key: str) -> str:
            if key in r.explanations:
                return f"<div class='explain'>💡 {r.explanations[key]}</div>"
            return ""
        
        html = f"""
        <style>
            body {{ font-family: Segoe UI, Arial; line-height: 1.6; color: #e0e0e0; }}
            h3 {{ 
                color: #6ea8fe; margin-top: 18px; margin-bottom: 8px; 
                border-bottom: 1px solid #444; padding-bottom: 5px; 
            }}
            h3.advanced {{ color: #ffc107; }}
            .setting {{ margin: 6px 0; color: #e0e0e0; }}
            .path {{ color: #888; font-size: 11px; font-style: italic; }}
            .value {{ color: #6ea8fe; font-weight: bold; }}
            .bool-yes {{ color: #28a745; font-weight: bold; }}
            .bool-no {{ color: #ff6b6b; font-weight: bold; }}
            .explain {{ 
                color: #888; font-size: 10px; margin-left: 15px; 
                margin-top: 2px; font-style: italic;
            }}
            .warning-box {{
                background: #3d1f1f; 
                padding: 12px; 
                border-radius: 6px; 
                margin: 15px 0; 
                border: 1px solid #ff6b6b;
            }}
            .warning-title {{ color: #ff6b6b; font-weight: bold; }}
        </style>
        
        {warnings_html}
        
        <h3>Connection Limits</h3>
        <p class="path">Tools → Options → Connection → Connection Limits</p>
        
        <div class="setting">
            • Global maximum number of connections: 
            <span class="value">{r.max_connections_global}</span>
        </div>
        {explain("max_connections")}
        
        <div class="setting">
            • Maximum number of connections per torrent: 
            <span class="value">{r.max_connections_per_torrent}</span>
        </div>
        
        <div class="setting">
            • Global maximum number of upload slots: 
            <span class="value">{r.upload_slots_global}</span>
        </div>
        
        <div class="setting">
            • Maximum number of upload slots per torrent: 
            <span class="value">{r.upload_slots_per_torrent}</span>
        </div>
        {explain("upload_slots")}
        
        <h3>Speed Limits</h3>
        <p class="path">Tools → Options → Speed</p>
        
        <div class="setting">
            • Global upload rate limit: 
            <span class="value">{r.global_upload_limit_kbps} КБ/с</span>
        </div>
        {explain("upload_limit")}
        
        <div class="setting">
            • Global download rate limit: 
            <span class="value">{"∞ (без ограничений)" if r.global_download_limit_kbps == 0 else str(r.global_download_limit_kbps) + " КБ/с"}</span>
        </div>
        
        <h3>Torrent Queueing</h3>
        <p class="path">Tools → Options → BitTorrent → Torrent Queueing</p>
        
        <div class="setting">
            • Maximum active downloads: 
            <span class="value">{r.max_active_downloads}</span>
        </div>
        <div class="setting">
            • Maximum active uploads: 
            <span class="value">{r.max_active_uploads}</span>
        </div>
        <div class="setting">
            • Maximum active torrents: 
            <span class="value">{r.max_active_torrents}</span>
        </div>
        {explain("queue")}
        
        <h3>Privacy</h3>
        <p class="path">Tools → Options → BitTorrent</p>
        
        <div class="setting">
            • Encryption mode: 
            <span class="value">{r.encryption_mode.value}</span>
        </div>
        {explain("encryption")}
        
        <div class="setting">• DHT: {bool_html(r.enable_dht)}</div>
        <div class="setting">• PeX: {bool_html(r.enable_pex)}</div>
        <div class="setting">• LSD: {bool_html(r.enable_lsd)}</div>
        {explain("dht_pex_lsd")}
        
        <div class="setting">• Anonymous mode: {bool_html(r.anonymous_mode)}</div>
        {explain("anonymous")}
        
        {f"<div class='setting'>• Network interface: <span class='value'>{r.network_interface}</span></div>" + explain("vpn_interface") if r.network_interface else ""}
        """
        
        # Advanced section
        if self._show_advanced:
            html += f"""
            <div class="warning-box">
                <span class="warning-title">⚠️ Advanced Options</span><br>
                <span style="color: #ccc; font-size: 11px;">
                    Эти настройки для опытных пользователей.
                </span>
            </div>
            
            <h3 class="advanced">Disk I/O (Advanced)</h3>
            <p class="path">Tools → Options → Advanced → libtorrent Section</p>
            
            <div class="setting">
                • Disk cache: 
                <span class="value">{r.disk_cache_mb if r.disk_cache_mb != -1 else "Auto (-1)"} МБ</span>
            </div>
            {explain("disk_cache")}
            
            <div class="setting">
                • Enable OS cache: {bool_html(r.enable_os_cache)}
            </div>
            
            <div class="setting">
                • Pre-allocate disk space: {bool_html(r.pre_allocate_disk)}
            </div>
            {explain("pre_allocate")}
            
            <div class="setting">
                • Asynchronous I/O threads: 
                <span class="value">{r.async_io_threads}</span>
            </div>
            {explain("async_io")}
            
            <div class="setting">
                • Coalesce reads & writes: {bool_html(r.coalesce_reads_writes)}
            </div>
            {explain("coalesce")}
            
            <h3 class="advanced">Network Tuning (Advanced)</h3>
            <p class="path">Tools → Options → Advanced</p>
            
            <div class="setting">
                • Peer connection protocol: 
                <span class="value">{r.protocol_mode.value}</span>
            </div>
            {explain("protocol")}
            
            <div class="setting">
                • Send buffer watermark: 
                <span class="value">{r.send_buffer_watermark_kb} КБ</span>
            </div>
            <div class="setting">
                • Send buffer low watermark: 
                <span class="value">{r.send_buffer_low_watermark_kb} КБ</span>
            </div>
            <div class="setting">
                • Send buffer watermark factor: 
                <span class="value">{r.send_buffer_factor}%</span>
            </div>
            {explain("send_buffer")}
            
            <div class="setting">
                • Socket backlog size: 
                <span class="value">{r.socket_backlog_size}</span>
            </div>
            {explain("socket_backlog")}
            
            <div class="setting">
                • Outgoing connections per second: 
                <span class="value">{r.outgoing_connections_per_second}</span>
            </div>
            
            <div class="setting">
                • Listening port: 
                <span class="value">{r.listening_port}</span>
            </div>
            {explain("port")}
            
            <h3 class="advanced">Seeding (Advanced)</h3>
            <p class="path">Tools → Options → BitTorrent → Seeding Limits</p>
            
            <div class="setting">
                • Super Seeding mode: {bool_html(r.super_seeding, "Вкл (для новых раздач)", "Выкл")}
            </div>
            {explain("super_seeding")}
            """
        else:
            html += """
            <div style="margin-top: 20px; padding: 12px; background: #2a2a2a; 
                        border-radius: 6px; border: 1px dashed #555;">
                <span style="color: #888;">
                    ⚙️ Для просмотра Advanced настроек включите галочку выше.
                </span>
            </div>
            """
        
        return html
