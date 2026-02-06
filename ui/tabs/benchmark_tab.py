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
    QCheckBox,
    QFileDialog,
)
from PyQt6.QtCore import Qt, QTimer, pyqtSignal

from optimizer.benchmark_manager import BenchmarkManager

class StatCard(QFrame):
    """Виджет карточки для отображения одного показателя."""
    
    def __init__(self, title: str, value: str, unit: str, color: str = "#6ea8fe"):
        super().__init__()
        self.setMinimumHeight(90)
        self.setStyleSheet(f"""
            QFrame {{
                background: #1a1a1a;
                border: 1px solid #333;
                border-radius: 8px;
            }}
        """)
        
        layout = QVBoxLayout(self)
        layout.setContentsMargins(12, 10, 12, 10)
        layout.setSpacing(2)
        
        t_lbl = QLabel(title)
        t_lbl.setStyleSheet("color: #888; font-size: 11px; font-weight: bold;")
        layout.addWidget(t_lbl)
        
        v_layout = QHBoxLayout()
        v_layout.setContentsMargins(0, 0, 0, 0)
        self.v_lbl = QLabel(value)
        self.v_lbl.setStyleSheet(f"color: {color}; font-size: 24px; font-weight: 800; padding: 0;")
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
        self._recording_state = None  # None, "baseline", "optimized"
        self._recording_samples = []
        self._recording_timer = QTimer()
        self._recording_timer.timeout.connect(self._on_record_tick)
        
        # Ubuntu 25.10 "Questing Quokka"
        self._test_hash = "6a40552b7dfe176a928ba556128445103ca7fe45" 
        self._is_standardized = False
        self._is_external_torrent = False
        self._test_magnet = (
            "magnet:?xt=urn:btih:c8295ce630f2064f08440db1534e4992cfe4862a"
            "&dn=ubuntu-25.10-desktop-amd64.iso"
            "&tr=https%3A%2F%2Ftorrent.ubuntu.com%2Fannounce"
        )
        
        self._setup_ui()
        
        self.timer = QTimer()
        self.timer.timeout.connect(self._update_stats)
        
    def _setup_ui(self):
        layout = QVBoxLayout(self)
        layout.setSpacing(15)
        
        # === Connection Panel ===
        conn_group = QGroupBox("Параметры подключения к Web UI")
        conn_layout = QGridLayout(conn_group)
        
        conn_layout.addWidget(QLabel("Хост:"), 0, 0)
        self.host_edit = QLineEdit("http://localhost:8080")
        self.host_edit.setPlaceholderText("http://localhost:8080")
        self.host_edit.returnPressed.connect(self._toggle_connection)
        conn_layout.addWidget(self.host_edit, 0, 1)
        
        conn_layout.addWidget(QLabel("Логин:"), 1, 0)
        self.user_edit = QLineEdit("admin")
        self.user_edit.setPlaceholderText("Имя пользователя")
        self.user_edit.returnPressed.connect(self._toggle_connection)
        conn_layout.addWidget(self.user_edit, 1, 1)
        
        conn_layout.addWidget(QLabel("Пароль:"), 2, 0)
        self.pass_edit = QLineEdit()
        self.pass_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.pass_edit.setPlaceholderText("Пароль")
        self.pass_edit.returnPressed.connect(self._toggle_connection)
        conn_layout.addWidget(self.pass_edit, 2, 1)
        
        self.connect_btn = QPushButton("🔌 Подключиться")
        self.connect_btn.setMinimumHeight(40)
        self.connect_btn.clicked.connect(self._toggle_connection)
        self.connect_btn.setStyleSheet("""
            QPushButton {
                background: #1a3c1a;
                color: #28a745;
                font-weight: bold;
                border-radius: 6px;
                border: 1px solid #198754;
            }
            QPushButton:hover {
                background: #234c23;
            }
        """)
        conn_layout.addWidget(self.connect_btn, 3, 0, 1, 2)
        
        # Help button in corner
        self.help_btn = QPushButton("❓ Инструкция")
        self.help_btn.setFixedWidth(120)
        self.help_btn.clicked.connect(self._show_guide)
        self.help_btn.setStyleSheet("""
            QPushButton {
                background: transparent;
                color: #ffc107;
                border: 1px solid #ffc107;
                border-radius: 4px;
                font-size: 11px;
                padding: 4px;
            }
            QPushButton:hover {
                background: #333;
            }
        """)
        
        header_layout = QHBoxLayout()
        header_layout.addWidget(conn_group)
        
        side_layout = QVBoxLayout()
        side_layout.addWidget(self.help_btn)
        side_layout.addStretch()
        header_layout.addLayout(side_layout)
        layout.addLayout(header_layout)
        
        # === Scientific Testing Section ===
        sci_group = QGroupBox("🔬 Научное сравнение (Standardized Test)")
        sci_layout = QVBoxLayout(sci_group)
        
        sci_desc = QLabel(
            "Использует публичный Ubuntu ISO для честного сравнения 'Baseline vs Optimized'.\n"
            "Это гарантирует точность и приватность ваших данных."
        )
        sci_desc.setStyleSheet("color: #aaa; font-size: 11px; font-style: italic;")
        sci_layout.addWidget(sci_desc)

        path_layout = QHBoxLayout()
        path_layout.addWidget(QLabel("Путь загрузки:"))
        self.save_path_edit = QLineEdit("D:\\")
        self.save_path_edit.setPlaceholderText("Например: D:\\")
        path_layout.addWidget(self.save_path_edit)
        
        self.browse_btn = QPushButton("📁 Обзор...")
        self.browse_btn.setFixedWidth(80)
        self.browse_btn.clicked.connect(self._browse_path)
        path_layout.addWidget(self.browse_btn)
        
        sci_layout.addLayout(path_layout)
        
        sci_btns = QHBoxLayout()
        self.add_iso_btn = QPushButton("💿 Добавить тест ISO")
        self.add_iso_btn.setEnabled(False)
        self.add_iso_btn.clicked.connect(self._add_test_iso)
        sci_btns.addWidget(self.add_iso_btn)
        
        self.cleanup_btn = QPushButton("🧹 Удалить тест ISO")
        self.cleanup_btn.setEnabled(False)
        self.cleanup_btn.clicked.connect(self._cleanup_test_iso)
        sci_btns.addWidget(self.cleanup_btn)
        
        sci_layout.addLayout(sci_btns)
        
        # --- Consent Section ---
        self.consent_check = QCheckBox("Я согласен(а) загрузить образ Ubuntu (5.3 ГБ) для теста")
        self.consent_check.setStyleSheet("color: #ffc107; font-size: 11px;")
        self.consent_check.toggled.connect(self._on_consent_toggled)
        sci_layout.addWidget(self.consent_check)
        
        layout.addWidget(sci_group)
        
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
        
        self.bench_desc = QLabel("Нажмите 'Baseline' для замера текущих показателей.")
        self.bench_desc.setStyleSheet("color: #888; font-size: 11px; margin-bottom: 5px;")
        bench_layout.addWidget(self.bench_desc)
        
        btn_layout = QHBoxLayout()
        self.baseline_btn = QPushButton("📉 Замер Baseline")
        self.baseline_btn.setEnabled(False)
        self.baseline_btn.clicked.connect(lambda: self._start_recording("baseline"))
        btn_layout.addWidget(self.baseline_btn)
        
        self.optimized_btn = QPushButton("🚀 Замер Optimized")
        self.optimized_btn.setEnabled(False)
        self.optimized_btn.clicked.connect(lambda: self._start_recording("optimized"))
        btn_layout.addWidget(self.optimized_btn)
        bench_layout.addLayout(btn_layout)
        
        self.progress = QProgressBar()
        self.progress.setTextVisible(False)
        self.progress.setRange(0, 30)
        self.progress.setValue(0)
        self.progress.setStyleSheet("""
            QProgressBar {
                height: 4px;
                border: none;
                background: #222;
            }
            QProgressBar::chunk {
                background: #6ea8fe;
            }
        """)
        bench_layout.addWidget(self.progress)
        layout.addWidget(bench_group)
        
        # === Reports area ===
        self.report_label = QLabel("Подключитесь к qBittorrent для начала замеров.")
        self.report_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.report_label.setStyleSheet("""
            QLabel {
                color: #666;
                font-style: italic;
                border: 1px dashed #333;
                padding: 20px;
                border-radius: 8px;
                background: #111;
            }
        """)
        self.report_label.setWordWrap(True)
        self.report_label.setTextFormat(Qt.TextFormat.RichText)
        layout.addWidget(self.report_label)
        
        layout.addStretch()

    def _browse_path(self):
        """Выбор папки для загрузки."""
        path = QFileDialog.getExistingDirectory(self, "Выберите папку для теста")
        if path:
            self.save_path_edit.setText(path)

    def _on_consent_toggled(self, checked):
        """Управление доступностью кнопок теста."""
        self.add_iso_btn.setEnabled(checked)

    def _toggle_connection(self):
        if self.timer.isActive():
            self.timer.stop()
            self.connect_btn.setText("🔌 Подключиться")
            self.baseline_btn.setEnabled(False)
            self.optimized_btn.setEnabled(False)
            self.add_iso_btn.setEnabled(False)
            self.cleanup_btn.setEnabled(False)
        else:
            host = self.host_edit.text()
            username = self.user_edit.text()
            password = self.pass_edit.text()
            
            self.manager.host = host
            if self.manager.connect(username, password):
                self.timer.start(1000)
                self.connect_btn.setText("🔴 Отключиться")
                self.baseline_btn.setEnabled(True)
                self.optimized_btn.setEnabled(True)
                self.add_iso_btn.setEnabled(True)
                self.cleanup_btn.setEnabled(True)
                self.report_label.setText("Соединение установлено. Готов к замерам.")
            else:
                self.report_label.setText("⚠ Ошибка подключения! Проверьте WebAPI (Логин/Пароль).")

    def _check_connection(self) -> bool:
        """Проверка подключения перед действием."""
        if not self.manager.is_connected:
            QMessageBox.warning(
                self, "Не подключено", 
                "Сначала необходимо подключиться к qBittorrent Web UI на этой вкладке."
            )
            return False
        return True

    def _add_test_iso(self):
        """Добавить тестовый ISO торрент."""
        if not self._check_connection():
            return

        # Проверяем, не добавлен ли уже этот торрент
        stats = self.manager.get_torrent_stats(self._test_hash)
        if stats:
            self._is_standardized = True
            self._is_external_torrent = True
            self.baseline_btn.setEnabled(True)
            self.optimized_btn.setEnabled(True)
            self.cleanup_btn.setEnabled(True)
            QMessageBox.information(
                self, "Уже добавлен", 
                "Тестовый Ubuntu ISO уже найден в списке торрентов. Будем использовать его, "
                "но при очистке файлы не удалим."
            )
            return

        save_path = self.save_path_edit.text().strip()
        if self.manager.add_torrent(self._test_magnet, save_path=save_path):
            self._is_standardized = True
            self._is_external_torrent = False
            QMessageBox.information(
                self, "Добавлено", 
                "Ubuntu 25.10 ISO успешно добавлен! Подождите 10-20 секунд, пока он подхватит сиды, "
                "прежде чем начинать замер."
            )
        else:
            QMessageBox.warning(self, "Ошибка", "Не удалось добавить торрент (проверьте Web API).")

    def _cleanup_test_iso(self):
        """Удалить тестовый ISO."""
        if not self._check_connection():
            return
        
        if self._is_external_torrent:
             # Если торрент был внешним, мы его просто "забываем" в контексте бенчмарка,
             # но не удаляем из клиента (или удаляем только задачу, без файлов).
             # Безопаснее всего - спросить или просто не удалять файлы.
             # Решение: Просто сбросить флаги в UI, сказав пользователю, что мы закончили.
             self._is_standardized = False
             self._is_external_torrent = False
             self.baseline_btn.setEnabled(False)
             self.optimized_btn.setEnabled(False)
             self.cleanup_btn.setEnabled(False)
             QMessageBox.information(self, "Готово", "Тест завершён. Ваш существующий торрент не был затронут.")
             return

        if self.manager.delete_torrent(self._test_hash):
            self._is_standardized = False
            self.baseline_btn.setEnabled(False)
            self.optimized_btn.setEnabled(False)
            self.cleanup_btn.setEnabled(False)
            QMessageBox.information(self, "Готово", "Тестовые данные удалены из qBittorrent.")
        else:
            QMessageBox.warning(self, "Ошибка", "Не удалось удалить торрент.")

    def _start_recording(self, mode: str):
        """Запустить процесс записи замеров."""
        if not self._check_connection():
            return
            
        self._recording_state = mode
        self._recording_samples = []
        self.progress.setValue(0)
        
        self.baseline_btn.setEnabled(False)
        self.optimized_btn.setEnabled(False)
        self.add_iso_btn.setEnabled(False)
        self.cleanup_btn.setEnabled(False)
        
        prefix = "🔬 [STANDARDIZED]" if self._is_standardized else "🔴 [LIVE]"
        self.bench_desc.setText(f"{prefix} Идет запись ({mode.upper()})... Ждите 30 сек.")
        
        self._recording_timer.start(1000)

    def _on_record_tick(self):
        """Очередной тик записи."""
        if self._is_standardized:
            stats = self.manager.get_torrent_stats(self._test_hash)
            if not stats:
                stats = {"dl_speed": 0, "ul_speed": 0, "dht_nodes": 0}
            else:
                # Add dht_nodes from general stats for complete consistency
                gen = self.manager.get_main_stats()
                stats["dht_nodes"] = gen.get("dht_nodes", 0)
        else:
            stats = self.manager.get_main_stats()
            
        self._recording_samples.append(stats)
        
        current_val = self.progress.value() + 1
        self.progress.setValue(current_val)
        
        if current_val >= 30:
            self._finish_recording()

    def _finish_recording(self):
        """Завершить запись и проанализировать."""
        self._recording_timer.stop()
        analysis = self.manager.analyze_results(self._recording_samples)
        
        if self._recording_state == "baseline":
            self.manager.baseline_results = analysis
            msg = "✅ Baseline замер завершен! Настройте qBittorrent и нажмите 'Optimized'."
        else:
            self.manager.optimized_results = analysis
            msg = "✅ Optimized замер завершен!"
            
        self.bench_desc.setText(msg)
        self.baseline_btn.setEnabled(True)
        self.optimized_btn.setEnabled(True)
        self.add_iso_btn.setEnabled(True)
        self.cleanup_btn.setEnabled(True)
        
        report = self.manager.get_comparison_report()
        self.report_label.setText(report)
        self._recording_state = None

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
        if self._is_standardized:
            stats = self.manager.get_torrent_stats(self._test_hash)
            if stats:
                # Get global DHT nodes for the nodes card
                gen = self.manager.get_main_stats()
                stats["dht_nodes"] = gen.get("dht_nodes", 0)
            else:
                stats = {"dl_speed": 0, "ul_speed": 0, "dht_nodes": 0}
        else:
            stats = self.manager.get_main_stats()

        self.dl_card.set_value(f"{stats['dl_speed'] / (1024*1024):.2f}")
        self.ul_card.set_value(f"{stats['ul_speed'] / (1024*1024):.2f}")
        self.nodes_card.set_value(str(stats['dht_nodes']))
        
        self.history.append(stats)
        if len(self.history) > 30:
            self.history.pop(0)
            
        analysis = self.manager.analyze_results(self.history)
        self.stable_card.set_value(f"{analysis.get('stability_score', 0)}%")
