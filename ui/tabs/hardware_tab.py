"""Вкладка характеристик железа (CustomTkinter)."""

import customtkinter as ctk
from optimizer.models import StorageType, HardwareSettings
from optimizer.hardware_detector import HardwareDetector

RAM_VALUES = [4, 8, 16, 32, 64, 128]
CPU_VALUES = [2, 4, 6, 8, 12, 16, 24, 32]
DEFAULT_RAM_INDEX = 2
DEFAULT_CORES_INDEX = 3

class HardwareTab(ctk.CTkScrollableFrame):
    """Вкладка для ввода характеристик железа."""

    def __init__(self, master):
        super().__init__(master, fg_color="transparent")
        from ui.tabs.benchmark_tab import card
        card(self, 'The computer running qBittorrent', 'Choose its memory and download drive. These control how much work can run without overwhelming your computer.')
        self.detector = HardwareDetector()
        self._setup_ui()

    def _setup_ui(self):
        # Auto-detect
        self.detect_btn = ctk.CTkButton(
            self,
            text="✨ Auto-Detect Hardware",
            fg_color="#1a3a5c",
            hover_color="#234c7a",
            border_color="#0d6efd",
            border_width=1,
            command=self._on_autodetect
        )
        self.detect_btn.pack(fill="x", pady=10)
        self.disk_path = ctk.CTkEntry(self, placeholder_text="qBittorrent download folder on this computer (blank: system drive)")
        self.disk_path.pack(fill="x", pady=4)
        self.disk_status = ctk.CTkLabel(self, text="Choose the drive where torrents are saved. Remote storage requires manual selection.", wraplength=760)
        self.disk_status.pack(fill="x")

        # Storage
        self.storage_frame = ctk.CTkFrame(self)
        self.storage_frame.pack(fill="x", pady=10)

        ctk.CTkLabel(self.storage_frame, text="Storage Type", font=("Segoe UI", 14, "bold")).pack(anchor="w", padx=10, pady=5)

        self.storage_var = ctk.StringVar(value=StorageType.HDD.value)
        self.storage_combo = ctk.CTkOptionMenu(
            self.storage_frame,
            values=[t.value for t in StorageType],
            variable=self.storage_var
        )
        self.storage_combo.pack(fill="x", padx=10, pady=5)

        # RAM
        self.ram_frame = ctk.CTkFrame(self)
        self.ram_frame.pack(fill="x", pady=10)

        self.ram_label = ctk.CTkLabel(self.ram_frame, text="RAM: 16 GB", font=("Segoe UI", 14, "bold"))
        self.ram_label.pack(anchor="w", padx=10, pady=5)

        self.ram_slider = ctk.CTkSlider(
            self.ram_frame,
            from_=0,
            to=len(RAM_VALUES)-1,
            number_of_steps=len(RAM_VALUES)-1,
            command=self._on_ram_slider
        )
        self.ram_slider.set(DEFAULT_RAM_INDEX)
        self.ram_slider.pack(fill="x", padx=10, pady=10)

        # CPU
        self.cpu_frame = ctk.CTkFrame(self)
        self.cpu_frame.pack(fill="x", pady=10)

        self.cpu_label = ctk.CTkLabel(self.cpu_frame, text="Physical Cores: 8", font=("Segoe UI", 14, "bold"))
        self.cpu_label.pack(anchor="w", padx=10, pady=5)

        self.cpu_slider = ctk.CTkSlider(
            self.cpu_frame,
            from_=0,
            to=len(CPU_VALUES)-1,
            number_of_steps=len(CPU_VALUES)-1,
            command=self._on_cpu_slider
        )
        self.cpu_slider.set(DEFAULT_CORES_INDEX)
        self.cpu_slider.pack(fill="x", padx=10, pady=10)

        # Hybrid CPU
        self.hybrid_check = ctk.CTkCheckBox(self.cpu_frame, text="Hybrid Arch (Intel 12+ / ARM)", command=self._on_hybrid_toggle)
        self.hybrid_check.pack(anchor="w", padx=10, pady=5)

        self.p_cores_slider = ctk.CTkSlider(self.cpu_frame, from_=0, to=16, number_of_steps=16)
        self.p_cores_slider.set(8)
        self.p_cores_slider.pack(fill="x", padx=10, pady=5)
        self.p_cores_slider.configure(state="disabled")

    def _on_ram_slider(self, value):
        val = RAM_VALUES[int(value)]
        self.ram_label.configure(text=f"RAM: {val} GB")

    def _on_cpu_slider(self, value):
        val = CPU_VALUES[int(value)]
        self.cpu_label.configure(text=f"Physical Cores: {val}")

    def _on_hybrid_toggle(self):
        state = "normal" if self.hybrid_check.get() else "disabled"
        self.p_cores_slider.configure(state=state)

    def _on_autodetect(self):
        disk = self.detector.get_main_disk_type(self.disk_path.get().strip() or None)
        if disk in ("NVMe", "SSD", "HDD"):
            self.storage_var.set({"NVMe": StorageType.NVME.value, "SSD": StorageType.SSD_SATA.value, "HDD": StorageType.HDD.value}[disk])
        self.disk_status.configure(text=f"Selected volume: {disk}." if disk != "Unknown" else "Storage could not be identified. Select the actual download drive type manually.")
        # RAM
        ram = self.detector.get_total_ram_gb()
        idx = min(range(len(RAM_VALUES)), key=lambda i: abs(RAM_VALUES[i] - ram))
        self.ram_slider.set(idx)
        self._on_ram_slider(idx)

        # CPU
        cpu = self.detector.get_cpu_info()
        c_idx = min(range(len(CPU_VALUES)), key=lambda i: abs(CPU_VALUES[i] - cpu["physical_cores"]))
        self.cpu_slider.set(c_idx)
        self._on_cpu_slider(c_idx)

        if cpu["is_hybrid"]:
            self.hybrid_check.select()
            self.p_cores_slider.set(cpu["p_cores"])
            self._on_hybrid_toggle()
        else:
            self.hybrid_check.deselect()
            self._on_hybrid_toggle()

    def get_settings(self) -> HardwareSettings:
        ram_idx = int(self.ram_slider.get())
        cpu_idx = int(self.cpu_slider.get())

        # Map string back to Enum
        st_str = self.storage_var.get()
        st_enum = next((t for t in StorageType if t.value == st_str), StorageType.HDD)

        return HardwareSettings(
            storage_type=st_enum,
            ram_gb=RAM_VALUES[ram_idx],
            cpu_cores=CPU_VALUES[cpu_idx],
            is_hybrid_cpu=bool(self.hybrid_check.get()),
            p_cores=int(self.p_cores_slider.get()) if self.hybrid_check.get() else 0
        )
