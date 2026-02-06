"""Вкладка бенчмаркинга и мониторинга."""

from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QLineEdit,
    QProgressBar,
    QGroupBox,
    QFrame,
    QGridLayout,
    QMessageBox,
)
from PyQt6.QtCore import Qt, QTimer, pyqtSignal

from optimizer.benchmark_manager import BenchmarkManager

class StatCard(QFrame):
    """Виджет карточки для отображения одного показателя."""
    
    def __init__(self, title: str, value: str, unit: str, color: str = "#6ea8fe"):
        super().__init__()
        self.setStyleSheet(f"""
            QFrame {{
                background: #1a1a1a;
                border: 1px solid #333;
                border-radius: 8px;
                padding: 10px;
            }}
        """)
        
        layout = QVBoxLayout(self)
        layout.setSpacing(2)
        
        t_lbl = QLabel(title)
        t_lbl.setStyleSheet("color: #888; font-size: 11px; font-weight: bold;")
        layout.addWidget(t_lbl)
        
        v_layout = QHBoxLayout()
        self.v_lbl = QLabel(value)
        self.v_lbl.setStyleSheet(f"color: {color}; font-size: 24px; font-weight: 800;")
        v_layout.addWidget(self.v_lbl)
        
        u_lbl = QLabel(unit)
        u_lbl.setStyleSheet("color: #555; font-size: 12px; margin-top: 8px;")
        v_layout.addWidget(u_lbl)
        v_layout.addStretch()
        
        layout.addLayout(v_layout)

    def set_value(self, value: str):
        self.v_lbl.setText(value)


class BenchmarkTab(QWidget):
    """Вкладка для проведения замеров производительности."""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.manager = BenchmarkManager()
        self.history = []
        self._setup_ui()
        
        self.timer = QTimer()
        self.timer.timeout.connect(self._update_stats)
        
    def _setup_ui(self):
        layout = QVBoxLayout(self)
        layout.setSpacing(20)
        
        # === Connection Panel ===
        conn_group = QGroupBox("Подключение к qBittorrent WebUI")
        conn_layout = QHBoxLayout(conn_group)
        
        self.host_edit = QLineEdit("http://localhost:8080")
        self.host_edit.setPlaceholderText("Адрес (host:port)")
        conn_layout.addWidget(self.host_edit)
        
        self.connect_btn = QPushButton("🔌 Подключиться")
        self.connect_btn.clicked.connect(self._toggle_connection)
        conn_layout.addWidget(self.connect_btn)
        
        # Add Credential inputs
        cred_group = QGroupBox("Авторизация")
        cred_layout = QHBoxLayout(cred_group)
        
        self.user_edit = QLineEdit("admin")
        self.user_edit.setPlaceholderText("Логин")
        self.user_edit.setToolTip("Имя пользователя из настроек Web UI")
        cred_layout.addWidget(self.user_edit)
        
        self.pass_edit = QLineEdit()
        self.pass_edit.setPlaceholderText("Пароль")
        self.pass_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.pass_edit.setToolTip("Пароль из настроек Web UI (обязательно в v4.6+)")
        cred_layout.addWidget(self.pass_edit)
        
        self.help_btn = QPushButton("❓ Как настроить?")
        self.help_btn.setStyleSheet("color: #6ea8fe; border: none; text-decoration: underline;")
        self.help_btn.clicked.connect(self._show_guide)
        cred_layout.addWidget(self.help_btn)
        
        layout.addWidget(cred_group)
        
        # === Live Stats ===
        stats_layout = QGridLayout()
        self.dl_card = StatCard("ЗАГРУЗКА", "0.0", "МБ/с", "#28a745")
        self.ul_card = StatCard("ОТДАЧА", "0.0", "МБ/с", "#ffc107")
        self.stable_card = StatCard("СТАБИЛЬНОСТЬ", "0%", "Score", "#6ea8fe")
        self.nodes_card = StatCard("DHT УЗЛЫ", "0", "nodes", "#aaa")
        
        stats_layout.addWidget(self.dl_card, 0, 0)
        stats_layout.addWidget(self.ul_card, 0, 1)
        stats_layout.addWidget(self.stable_card, 1, 0)
        stats_layout.addWidget(self.nodes_card, 1, 1)
        
        layout.addLayout(stats_layout)
        
        # === Bench Controls ===
        bench_group = QGroupBox("Управление замерами")
        bench_layout = QVBoxLayout(bench_group)
        
        btn_layout = QHBoxLayout()
        self.baseline_btn = QPushButton("📉 Замер Baseline (текущие)")
        self.baseline_btn.setEnabled(False)
        btn_layout.addWidget(self.baseline_btn)
        
        self.optimized_btn = QPushButton("🚀 Замер Optimized (новые)")
        self.optimized_btn.setEnabled(False)
        btn_layout.addWidget(self.optimized_btn)
        
        bench_layout.addLayout(btn_layout)
        
        self.progress = QProgressBar()
        self.progress.setTextVisible(False)
        self.progress.setStyleSheet("height: 4px; border: none; background: #222;")
        bench_layout.addWidget(self.progress)
        
        layout.addWidget(bench_group)
        
        # === Reports area ===
        self.report_label = QLabel("Подключитесь к qBittorrent для начала замеров.")
        self.report_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.report_label.setStyleSheet("color: #666; font-style: italic; border: 1px dashed #333; padding: 20px; border-radius: 8px;")
        layout.addWidget(self.report_label)
        
        layout.addStretch()

    def _toggle_connection(self):
        if self.timer.isActive():
            self.timer.stop()
            self.connect_btn.setText("🔌 Подключиться")
            self.baseline_btn.setEnabled(False)
            self.optimized_btn.setEnabled(False)
        else:
            host = self.host_edit.text()
            username = self.user_edit.text()
            password = self.pass_edit.text()
            
            self.manager.host = host
            if self.manager.connect(username, password):
                self.timer.start(2000)
                self.connect_btn.setText("🔴 Отключиться")
                self.baseline_btn.setEnabled(True)
                self.optimized_btn.setEnabled(True)
                self.report_label.setText("Соединение установлено. Готов к замерам.")
            else:
                self.report_label.setText("⚠ Ошибка подключения! Проверьте WebAPI (Логин/Пароль).")

    def _show_guide(self):
        """Показать инструкцию по настройке Web UI."""
        guide = (
            "Для подключения qFrey-Tuner к qBittorrent:\n\n"
            "1. Откройте qBittorrent -> Инструменты -> Настройки (Alt+O)\n"
            "2. Перейдите на вкладку 'Веб-интерфейс'\n"
            "3. Включите 'Веб-интерфейс (дистанционное управление)'\n"
            "4. Проверьте IP (обычно localhost или 127.0.0.1) и Порт (8080)\n"
            "5. ОБЯЗАТЕЛЬНО задайте Имя пользователя и Пароль\n"
            "   (в новых версиях пустой пароль запрещен)\n\n"
            "Совет: Включите 'Обходить аутентификацию для клиентов в локальной сети'"
        )
        QMessageBox.information(self, "Настройка Web UI", guide)

    def _update_stats(self):
        stats = self.manager.get_main_stats()
        self.dl_card.set_value(f"{stats['dl_speed'] / (1024*1024):.1f}")
        self.ul_card.set_value(f"{stats['ul_speed'] / (1024*1024):.1f}")
        self.nodes_card.set_value(str(stats['dht_nodes']))
        
        self.history.append(stats)
        if len(self.history) > 30:
            self.history.pop(0)
            
        # Рассчитываем стабильность на лету для последних 30 замеров
        analysis = self.manager.analyze_results(self.history)
        self.stable_card.set_value(f"{analysis.get('stability_score', 0)}%")
