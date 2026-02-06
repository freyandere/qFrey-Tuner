"""Вкладка настроек сети."""

from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QSpinBox,
    QSlider,
    QComboBox,
    QCheckBox,
    QLineEdit,
    QGroupBox,
    QFormLayout,
)
from PyQt6.QtCore import Qt

from optimizer.models import ConnectionType, NetworkSettings
from optimizer.network_tester import NetworkTester
from PyQt6.QtWidgets import QPushButton
from PyQt6.QtCore import QThread, pyqtSignal


# Ступенчатая прогрессия скоростей (Best Practice 2026)
SPEED_VALUES = [50, 100, 200, 300, 500, 800, 1000, 2500]
DEFAULT_SPEED_INDEX = 1  # 100 Мбит/с

# Отступ для компенсации ручки слайдера (примерно 10px с каждой стороны)
SLIDER_MARGIN = 10


class FixedSlider(QSlider):
    """Слайдер с фиксированными значениями."""
    
    def __init__(self, values: list[int], orientation=Qt.Orientation.Horizontal, parent=None):
        super().__init__(orientation, parent)
        self.values = values
        self.setRange(0, len(values) - 1)
        self.setTickPosition(QSlider.TickPosition.TicksBelow)
        self.setTickInterval(1)
    
    def get_value(self) -> int:
        return self.values[self.value()]
    
    def find_closest_index(self, real_value: int) -> int:
        return min(range(len(self.values)), key=lambda i: abs(self.values[i] - real_value))


class NetworkTab(QWidget):
    """Вкладка для ввода параметров сети."""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self._download_touched = False
        self._upload_touched = False
        self.tester = NetworkTester()
        self._setup_ui()
    
    def _setup_ui(self):
        layout = QVBoxLayout(self)
        layout.setSpacing(16)
        
        # === Скорость соединения (ОБЯЗАТЕЛЬНО) ===
        speed_group = QGroupBox("Скорость интернета *")
        speed_group.setStyleSheet("QGroupBox { font-weight: bold; }")
        speed_group.setMinimumWidth(350)
        speed_layout = QVBoxLayout(speed_group)
        
        # === Test Button ===
        self.test_btn = QPushButton("🚀 Тестировать скорость (Спидтест)")
        self.test_btn.setMinimumHeight(35)
        self.test_btn.setStyleSheet("""
            QPushButton {
                background: #1a3c1a;
                color: #28a745;
                border: 1px solid #198754;
                border-radius: 6px;
                font-weight: bold;
                margin-bottom: 10px;
            }
            QPushButton:hover {
                background: #234c23;
            }
            QPushButton:disabled {
                background: #222;
                color: #555;
                border-color: #333;
            }
        """)
        self.test_btn.clicked.connect(self._on_test_connection)
        speed_layout.addWidget(self.test_btn)
        
        # Download
        download_layout = QVBoxLayout()
        download_header = QHBoxLayout()
        download_header.addWidget(QLabel("Скорость загрузки:"))
        self.download_value = QLabel(f"{SPEED_VALUES[DEFAULT_SPEED_INDEX]} Мбит/с")
        self.download_value.setStyleSheet("color: #28a745; font-weight: bold;")
        download_header.addWidget(self.download_value)
        download_header.addStretch()
        
        self.download_spin = QSpinBox()
        self.download_spin.setRange(1, 10000)
        self.download_spin.setValue(SPEED_VALUES[DEFAULT_SPEED_INDEX])
        self.download_spin.setSuffix(" Мбит/с")
        self.download_spin.setFixedWidth(120)
        self.download_spin.valueChanged.connect(self._on_download_spin_changed)
        download_header.addWidget(self.download_spin)
        download_layout.addLayout(download_header)
        
        self.download_slider = FixedSlider(SPEED_VALUES)
        self.download_slider.setValue(DEFAULT_SPEED_INDEX)
        self.download_slider.valueChanged.connect(self._on_download_slider)
        download_layout.addWidget(self.download_slider)
        
        download_layout.addLayout(self._create_slider_labels(SPEED_VALUES))
        speed_layout.addLayout(download_layout)
        speed_layout.addSpacing(10)
        
        # Upload
        upload_layout = QVBoxLayout()
        upload_header = QHBoxLayout()
        upload_header.addWidget(QLabel("Скорость отдачи:"))
        self.upload_value = QLabel(f"{SPEED_VALUES[DEFAULT_SPEED_INDEX]} Мбит/с")
        self.upload_value.setStyleSheet("color: #ffc107; font-weight: bold;")
        upload_header.addWidget(self.upload_value)
        upload_header.addStretch()
        
        self.upload_spin = QSpinBox()
        self.upload_spin.setRange(1, 10000)
        self.upload_spin.setValue(SPEED_VALUES[DEFAULT_SPEED_INDEX])
        self.upload_spin.setSuffix(" Мбит/с")
        self.upload_spin.setFixedWidth(120)
        self.upload_spin.valueChanged.connect(self._on_upload_spin_changed)
        upload_header.addWidget(self.upload_spin)
        upload_layout.addLayout(upload_header)
        
        self.upload_slider = FixedSlider(SPEED_VALUES)
        self.upload_slider.setValue(DEFAULT_SPEED_INDEX)
        self.upload_slider.valueChanged.connect(self._on_upload_slider)
        upload_layout.addWidget(self.upload_slider)
        
        upload_layout.addLayout(self._create_slider_labels(SPEED_VALUES))
        speed_layout.addLayout(upload_layout)
        
        hint = QLabel("Скорость вашего тарифа от провайдера (по спидтесту)")
        hint.setStyleSheet("color: #aaa; font-size: 11px;")
        speed_layout.addWidget(hint)
        
        layout.addWidget(speed_group)
        
        # === Тип соединения ===
        connection_group = QGroupBox("Тип соединения *")
        connection_group.setStyleSheet("QGroupBox { font-weight: bold; }")
        connection_layout = QFormLayout(connection_group)
        
        self.connection_combo = QComboBox()
        for conn_type in ConnectionType:
            self.connection_combo.addItem(conn_type.value, conn_type)
        connection_layout.addRow("Тип:", self.connection_combo)
        
        hint = QLabel("Влияет на выбор протокола TCP/uTP")
        hint.setStyleSheet("color: #aaa; font-size: 11px;")
        connection_layout.addRow(hint)
        
        layout.addWidget(connection_group)
        
        # === ISP Throttling ===
        isp_group = QGroupBox("Обход блокировок провайдера")
        isp_layout = QVBoxLayout(isp_group)
        
        self.isp_throttle_check = QCheckBox("Провайдер режет P2P / живу в стране с цензурой")
        isp_layout.addWidget(self.isp_throttle_check)
        
        hint = QLabel("Включит принудительное шифрование и случайный порт")
        hint.setStyleSheet("color: #aaa; font-size: 11px; margin-left: 20px;")
        isp_layout.addWidget(hint)
        
        layout.addWidget(isp_group)
        
        # === VPN ===
        vpn_group = QGroupBox("VPN")
        vpn_layout = QVBoxLayout(vpn_group)
        
        self.vpn_check = QCheckBox("Использую VPN")
        self.vpn_check.stateChanged.connect(self._on_vpn_toggled)
        vpn_layout.addWidget(self.vpn_check)
        
        interface_layout = QHBoxLayout()
        interface_layout.addWidget(QLabel("Интерфейс VPN:"))
        self.vpn_interface_edit = QLineEdit()
        self.vpn_interface_edit.setPlaceholderText("например: tun0, WireGuard, wg0")
        self.vpn_interface_edit.setEnabled(False)
        interface_layout.addWidget(self.vpn_interface_edit)
        vpn_layout.addLayout(interface_layout)
        
        hint = QLabel("Укажите имя адаптера VPN для Kill Switch")
        hint.setStyleSheet("color: #aaa; font-size: 11px;")
        vpn_layout.addWidget(hint)
        
        layout.addWidget(vpn_group)
        layout.addStretch()
    
    def _create_slider_labels(self, values: list[int]) -> QHBoxLayout:
        """Создать подписи для слайдера с учётом отступов ручки."""
        labels_layout = QHBoxLayout()
        # Отступы для компенсации ручки слайдера
        labels_layout.setContentsMargins(SLIDER_MARGIN, 0, SLIDER_MARGIN, 0)
        labels_layout.setSpacing(0)
        
        for i, val in enumerate(values):
            if val >= 1000:
                text = f"{val // 1000}G"
            else:
                text = str(val)
            
            lbl = QLabel(text)
            lbl.setStyleSheet("color: #888; font-size: 9px;")
            lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
            labels_layout.addWidget(lbl, 1)
        
        return labels_layout
    
    def _on_download_slider(self, index: int):
        value = SPEED_VALUES[index]
        self._download_touched = True
        self.download_spin.blockSignals(True)
        self.download_spin.setValue(value)
        self.download_spin.blockSignals(False)
        self.download_value.setText(f"{value} Мбит/с")
    
    def _on_download_spin_changed(self, value: int):
        self._download_touched = True
        self.download_value.setText(f"{value} Мбит/с")
        closest_idx = self.download_slider.find_closest_index(value)
        self.download_slider.blockSignals(True)
        self.download_slider.setValue(closest_idx)
        self.download_slider.blockSignals(False)
    
    def _on_upload_slider(self, index: int):
        value = SPEED_VALUES[index]
        self._upload_touched = True
        self.upload_spin.blockSignals(True)
        self.upload_spin.setValue(value)
        self.upload_spin.blockSignals(False)
        self.upload_value.setText(f"{value} Мбит/с")
    
    def _on_upload_spin_changed(self, value: int):
        self._upload_touched = True
        self.upload_value.setText(f"{value} Мбит/с")
        closest_idx = self.upload_slider.find_closest_index(value)
        self.upload_slider.blockSignals(True)
        self.upload_slider.setValue(closest_idx)
        self.upload_slider.blockSignals(False)
    
    def _on_vpn_toggled(self, state):
        self.vpn_interface_edit.setEnabled(state == Qt.CheckState.Checked.value)
    
    def get_untouched_fields(self) -> list[str]:
        fields = []
        if not self._download_touched:
            fields.append(f"Скорость загрузки: {self.download_spin.value()} Мбит/с")
        if not self._upload_touched:
            fields.append(f"Скорость отдачи: {self.upload_spin.value()} Мбит/с")
        return fields
    
    def set_settings(self, settings: NetworkSettings):
        """Восстановить настройки из объекта."""
        self.download_spin.setValue(settings.download_speed_mbps)
        self.upload_spin.setValue(settings.upload_speed_mbps)
        
        # Находим индекс в комбобоксе
        index = self.connection_combo.findData(settings.connection_type)
        if index >= 0:
            self.connection_combo.setCurrentIndex(index)
            
        self.vpn_check.setChecked(settings.use_vpn)
        self.vpn_interface_edit.setText(settings.vpn_interface)
        self.isp_throttle_check.setChecked(settings.isp_throttling)
        
        # Сбрасываем флаги touched так как это программная установка
        self._download_touched = True
        self._upload_touched = True

    def get_settings(self) -> NetworkSettings:
        return NetworkSettings(
            download_speed_mbps=self.download_spin.value(),
            upload_speed_mbps=self.upload_spin.value(),
            connection_type=self.connection_combo.currentData(),
            use_vpn=self.vpn_check.isChecked(),
            vpn_interface=self.vpn_interface_edit.text().strip(),
            isp_throttling=self.isp_throttle_check.isChecked(),
        )

    def _on_test_connection(self):
        """Запустить тест скорости в фоновом потоке."""
        self.test_btn.setEnabled(False)
        self.test_btn.setText("⏳ Тестирование... (до 20 сек)")
        
        class TestThread(QThread):
            finished = pyqtSignal(float, float, str)
            def run(self):
                dl, ul, server = NetworkTester.run_full_test()
                self.finished.emit(dl, ul, server)
        
        self.thread = TestThread(self)
        self.thread.finished.connect(self._on_test_finished)
        self.thread.start()

    def _on_test_finished(self, dl, ul, server):
        """Обработка результатов теста."""
        self.test_btn.setEnabled(True)
        self.test_btn.setText(f"🚀 Тест: {server}")
        
        if dl > 0:
            self.download_spin.setValue(int(dl))
            self._download_touched = True
        if ul > 0:
            self.upload_spin.setValue(int(ul))
            self._upload_touched = True
