"""Вкладка характеристик железа."""

from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QFormLayout,
    QComboBox,
    QSpinBox,
    QSlider,
    QCheckBox,
    QGroupBox,
    QLabel,
)
from PyQt6.QtCore import Qt

from optimizer.models import StorageType, HardwareSettings


# Фиксированные значения на основе реальной статистики
RAM_VALUES = [4, 8, 16, 32, 64, 128]  # ГБ
CPU_VALUES = [2, 4, 6, 8, 12, 16, 24, 32]  # ядра

# Индексы дефолтных значений
DEFAULT_RAM_INDEX = 2  # 16 ГБ
DEFAULT_CORES_INDEX = 3  # 8 ядер
DEFAULT_STORAGE_INDEX = 2  # NVMe

# Отступ для компенсации ручки слайдера
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


class HardwareTab(QWidget):
    """Вкладка для ввода характеристик железа."""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self._storage_touched = False
        self._ram_touched = False
        self._cores_touched = False
        self._setup_ui()
    
    def _setup_ui(self):
        layout = QVBoxLayout(self)
        layout.setSpacing(16)
        
        # === Накопитель ===
        storage_group = QGroupBox("Накопитель для загрузок *")
        storage_group.setStyleSheet("QGroupBox { font-weight: bold; }")
        storage_group.setMinimumWidth(350)
        storage_layout = QVBoxLayout(storage_group)
        
        combo_layout = QHBoxLayout()
        combo_layout.addWidget(QLabel("Тип:"))
        self.storage_combo = QComboBox()
        for storage_type in StorageType:
            self.storage_combo.addItem(storage_type.value, storage_type)
        self.storage_combo.setCurrentIndex(DEFAULT_STORAGE_INDEX)
        self.storage_combo.currentIndexChanged.connect(self._on_storage_changed)
        combo_layout.addWidget(self.storage_combo)
        combo_layout.addStretch()
        storage_layout.addLayout(combo_layout)
        
        hint = QLabel("Тип накопителя, куда скачиваются торренты")
        hint.setStyleSheet("color: #aaa; font-size: 11px;")
        storage_layout.addWidget(hint)
        
        layout.addWidget(storage_group)
        
        # === RAM ===
        ram_group = QGroupBox("Оперативная память *")
        ram_group.setStyleSheet("QGroupBox { font-weight: bold; }")
        ram_group.setMinimumWidth(350)
        ram_layout = QVBoxLayout(ram_group)
        
        ram_header = QHBoxLayout()
        ram_header.addWidget(QLabel("Всего в системе:"))
        self.ram_value = QLabel(f"{RAM_VALUES[DEFAULT_RAM_INDEX]} ГБ")
        self.ram_value.setStyleSheet("color: #ffc107; font-weight: bold;")
        ram_header.addWidget(self.ram_value)
        ram_header.addStretch()
        
        self.ram_spin = QSpinBox()
        self.ram_spin.setRange(4, 256)
        self.ram_spin.setValue(RAM_VALUES[DEFAULT_RAM_INDEX])
        self.ram_spin.setSuffix(" ГБ")
        self.ram_spin.setFixedWidth(100)
        self.ram_spin.valueChanged.connect(self._on_ram_spin_changed)
        ram_header.addWidget(self.ram_spin)
        ram_layout.addLayout(ram_header)
        
        self.ram_slider = FixedSlider(RAM_VALUES)
        self.ram_slider.setValue(DEFAULT_RAM_INDEX)
        self.ram_slider.valueChanged.connect(self._on_ram_slider)
        ram_layout.addWidget(self.ram_slider)
        
        ram_layout.addLayout(self._create_slider_labels(RAM_VALUES))
        
        hint = QLabel("Общий объём RAM установленной в ПК (не сколько выделять)")
        hint.setStyleSheet("color: #aaa; font-size: 11px;")
        ram_layout.addWidget(hint)
        
        layout.addWidget(ram_group)
        
        # === CPU ===
        cpu_group = QGroupBox("Процессор *")
        cpu_group.setStyleSheet("QGroupBox { font-weight: bold; }")
        cpu_group.setMinimumWidth(350)
        cpu_layout = QVBoxLayout(cpu_group)
        
        cores_header = QHBoxLayout()
        cores_header.addWidget(QLabel("Физические ядра:"))
        self.cores_value = QLabel(str(CPU_VALUES[DEFAULT_CORES_INDEX]))
        self.cores_value.setStyleSheet("color: #ffc107; font-weight: bold;")
        cores_header.addWidget(self.cores_value)
        cores_header.addStretch()
        
        self.cores_spin = QSpinBox()
        self.cores_spin.setRange(1, 128)
        self.cores_spin.setValue(CPU_VALUES[DEFAULT_CORES_INDEX])
        self.cores_spin.setFixedWidth(80)
        self.cores_spin.valueChanged.connect(self._on_cores_spin_changed)
        cores_header.addWidget(self.cores_spin)
        cpu_layout.addLayout(cores_header)
        
        self.cores_slider = FixedSlider(CPU_VALUES)
        self.cores_slider.setValue(DEFAULT_CORES_INDEX)
        self.cores_slider.valueChanged.connect(self._on_cores_slider)
        cpu_layout.addWidget(self.cores_slider)
        
        cpu_layout.addLayout(self._create_slider_labels(CPU_VALUES))
        
        hint = QLabel("Количество физических ядер CPU (без учёта SMT/HT)")
        hint.setStyleSheet("color: #aaa; font-size: 11px;")
        cpu_layout.addWidget(hint)
        
        # Hybrid CPU
        cpu_layout.addSpacing(15)
        
        hybrid_label = QLabel("Гибридная архитектура:")
        hybrid_label.setStyleSheet("color: #aaa;")
        cpu_layout.addWidget(hybrid_label)
        
        self.hybrid_check = QCheckBox("Intel 12+ gen / ARM big.LITTLE")
        self.hybrid_check.stateChanged.connect(self._on_hybrid_toggled)
        cpu_layout.addWidget(self.hybrid_check)
        
        p_cores_layout = QHBoxLayout()
        p_cores_layout.addWidget(QLabel("P-cores (производительные):"))
        self.p_cores_spin = QSpinBox()
        self.p_cores_spin.setRange(1, 64)
        self.p_cores_spin.setValue(8)
        self.p_cores_spin.setEnabled(False)
        p_cores_layout.addWidget(self.p_cores_spin)
        p_cores_layout.addStretch()
        cpu_layout.addLayout(p_cores_layout)
        
        hint = QLabel("Для гибридных CPU I/O потоки рассчитываются по P-cores")
        hint.setStyleSheet("color: #aaa; font-size: 11px;")
        cpu_layout.addWidget(hint)
        
        layout.addWidget(cpu_group)
        layout.addStretch()
    
    def _create_slider_labels(self, values: list[int]) -> QHBoxLayout:
        """Создать подписи для слайдера с учётом отступов ручки."""
        labels_layout = QHBoxLayout()
        labels_layout.setContentsMargins(SLIDER_MARGIN, 0, SLIDER_MARGIN, 0)
        labels_layout.setSpacing(0)
        
        for val in values:
            lbl = QLabel(str(val))
            lbl.setStyleSheet("color: #888; font-size: 9px;")
            lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
            labels_layout.addWidget(lbl, 1)
        
        return labels_layout
    
    def _on_storage_changed(self):
        self._storage_touched = True
    
    def _on_ram_slider(self, index: int):
        value = RAM_VALUES[index]
        self._ram_touched = True
        self.ram_spin.blockSignals(True)
        self.ram_spin.setValue(value)
        self.ram_spin.blockSignals(False)
        self.ram_value.setText(f"{value} ГБ")
    
    def _on_ram_spin_changed(self, value: int):
        self._ram_touched = True
        self.ram_value.setText(f"{value} ГБ")
        closest_idx = min(range(len(RAM_VALUES)), key=lambda i: abs(RAM_VALUES[i] - value))
        self.ram_slider.blockSignals(True)
        self.ram_slider.setValue(closest_idx)
        self.ram_slider.blockSignals(False)
    
    def _on_cores_slider(self, index: int):
        value = CPU_VALUES[index]
        self._cores_touched = True
        self.cores_spin.blockSignals(True)
        self.cores_spin.setValue(value)
        self.cores_spin.blockSignals(False)
        self.cores_value.setText(str(value))
    
    def _on_cores_spin_changed(self, value: int):
        self._cores_touched = True
        self.cores_value.setText(str(value))
        closest_idx = min(range(len(CPU_VALUES)), key=lambda i: abs(CPU_VALUES[i] - value))
        self.cores_slider.blockSignals(True)
        self.cores_slider.setValue(closest_idx)
        self.cores_slider.blockSignals(False)
    
    def _on_hybrid_toggled(self, state):
        self.p_cores_spin.setEnabled(state == Qt.CheckState.Checked.value)
    
    def get_untouched_fields(self) -> list[str]:
        fields = []
        if not self._storage_touched:
            fields.append(f"Накопитель: {self.storage_combo.currentText()}")
        if not self._ram_touched:
            fields.append(f"RAM: {self.ram_spin.value()} ГБ")
        if not self._cores_touched:
            fields.append(f"CPU ядра: {self.cores_spin.value()}")
        return fields
    
    def set_settings(self, settings: HardwareSettings):
        """Восстановить характеристики железа."""
        index = self.storage_combo.findData(settings.storage_type)
        if index >= 0:
            self.storage_combo.setCurrentIndex(index)
            
        self.ram_spin.setValue(settings.ram_gb)
        self.cores_spin.setValue(settings.cpu_cores)
        self.hybrid_check.setChecked(settings.is_hybrid_cpu)
        self.p_cores_spin.setValue(settings.p_cores)
        
        self._storage_touched = True
        self._ram_touched = True
        self._cores_touched = True

    def get_settings(self) -> HardwareSettings:
        return HardwareSettings(
            storage_type=self.storage_combo.currentData(),
            ram_gb=self.ram_spin.value(),
            cpu_cores=self.cores_spin.value(),
            is_hybrid_cpu=self.hybrid_check.isChecked(),
            p_cores=self.p_cores_spin.value() if self.hybrid_check.isChecked() else 0,
        )
