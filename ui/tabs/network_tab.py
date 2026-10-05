"""Вкладка настроек сети (CustomTkinter)."""

import customtkinter as ctk
from typing import Optional
from optimizer.models import ConnectionType, NetworkSettings
from optimizer.network_tester import NetworkTester
import threading
import queue
from ui.tooltip import tooltip

SPEED_VALUES = [50, 100, 200, 300, 500, 800, 1000, 2500]
DEFAULT_SPEED_INDEX = 1

class NetworkTab(ctk.CTkScrollableFrame):
    """Вкладка для ввода параметров сети."""

    def __init__(self, master):
        super().__init__(master, fg_color="transparent")
        from ui.tabs.benchmark_tab import card
        card(self, 'Your internet connection', 'Set the speed available to qBittorrent. Upload capacity determines how much room to leave for other apps.')

        self.tester = NetworkTester()
        self._download_touched = False
        self._upload_touched = False
        self.download_mbps = 100.0
        self.measured_download_mbps = None
        self.upload_mbps = 100.0
        self._test_results = queue.Queue()
        self._testing = False

        self._setup_ui()
        self.after(100, self._poll_speedtest)

    def _update_connection_detail(self, *_):
        from ui.selection_details import CONNECTION_DETAILS
        title, detail = CONNECTION_DETAILS[ConnectionType(self.conn_type_var.get())]
        self.conn_detail_title.configure(text=title)
        self.conn_detail_text.configure(text=detail + " Docker/VPN and Seedbox profiles can override this connection method. These are preset suggestions, not measured improvements.")

    def _setup_ui(self):
        # 1. Connection Group
        self.conn_frame = ctk.CTkFrame(self)
        self.conn_frame.pack(fill="x", pady=10)

        ctk.CTkLabel(self.conn_frame, text="Connection Type", font=("Segoe UI", 14, "bold")).pack(anchor="w", padx=10, pady=5)

        self.conn_type_var = ctk.StringVar(value=ConnectionType.FIBER.value)
        self.conn_combo = ctk.CTkOptionMenu(
            self.conn_frame,
            values=[t.value for t in ConnectionType],
            variable=self.conn_type_var
        )
        self.conn_combo.pack(padx=10, pady=5, anchor="w")
        self.conn_detail_title = ctk.CTkLabel(self.conn_frame, text="", anchor="w", font=("Segoe UI", 15, "bold"))
        self.conn_detail_title.pack(fill="x", padx=10, pady=(6, 2))
        self.conn_detail_text = ctk.CTkLabel(self.conn_frame, text="", anchor="w", justify="left", wraplength=740)
        self.conn_detail_text.pack(fill="x", padx=10, pady=(0, 10))
        self.conn_type_var.trace_add("write", self._update_connection_detail)
        self._update_connection_detail()


        # 2. Speed Group
        self.speed_frame = ctk.CTkFrame(self)
        self.speed_frame.pack(fill="x", pady=10)

        ctk.CTkLabel(self.speed_frame, text="Internet Speed", font=("Segoe UI", 14, "bold")).pack(anchor="w", padx=10, pady=5)

        # Test Button
        self.test_btn = ctk.CTkButton(
            self.speed_frame,
            text="🚀 Run Speedtest",
            fg_color="#1a3c1a",
            hover_color="#234c23",
            border_color="#198754",
            border_width=1,
            command=self._run_speedtest
        )
        self.test_btn.pack(padx=10, pady=5, fill="x")

        # Download
        self.dl_frame = ctk.CTkFrame(self.speed_frame, fg_color="transparent")
        self.dl_frame.pack(fill="x", padx=10, pady=5)

        self.dl_label = ctk.CTkLabel(self.dl_frame, text="Download: 100 Mbps", text_color="#28a745")
        self.dl_label.pack(side="left")

        self.dl_slider = ctk.CTkSlider(
            self.speed_frame,
            from_=0,
            to=len(SPEED_VALUES)-1,
            number_of_steps=len(SPEED_VALUES)-1,
            command=self._on_dl_slider
        )
        self.dl_slider.set(DEFAULT_SPEED_INDEX)
        self.dl_slider.pack(fill="x", padx=10, pady=(0, 10))

        # Upload
        self.ul_frame = ctk.CTkFrame(self.speed_frame, fg_color="transparent")
        self.ul_frame.pack(fill="x", padx=10, pady=5)

        self.ul_label = ctk.CTkLabel(self.ul_frame, text="Upload: 100 Mbps", text_color="#ffc107")
        self.ul_label.pack(side="left")

        self.ul_slider = ctk.CTkSlider(
            self.speed_frame,
            from_=0,
            to=len(SPEED_VALUES)-1,
            number_of_steps=len(SPEED_VALUES)-1,
            command=self._on_ul_slider
        )
        self.ul_slider.set(DEFAULT_SPEED_INDEX)
        self.ul_slider.pack(fill="x", padx=10, pady=(0, 10))

        # 3. Features Group
        self.feat_frame = ctk.CTkFrame(self)
        self.feat_frame.pack(fill="x", pady=10)

        self.isp_check = ctk.CTkCheckBox(self.feat_frame, text="Suspected ISP restrictions (require encryption)")
        self.isp_check.pack(anchor="w", padx=10, pady=5)
        tooltip(self.isp_check, "Only select if you suspect your ISP restricts BitTorrent. Requires peer encryption and suggests a high listening port. It cannot guarantee bypass and may reduce compatible peers or speed. Leave off unless needed.")

        self.vpn_check = ctk.CTkCheckBox(self.feat_frame, text="Use VPN", command=self._on_vpn_toggle)
        self.vpn_check.pack(anchor="w", padx=10, pady=5)
        tooltip(self.vpn_check, "This does not install or connect a VPN. Connect your VPN first, then enter its network interface on the qBittorrent host. The tuner validates and binds qBittorrent to that interface; a wrong interface can stop traffic.")

        self.vpn_entry = ctk.CTkEntry(self.feat_frame, placeholder_text="Interface (e.g. tun0)")
        self.vpn_entry.pack(anchor="w", padx=10, pady=5, fill="x")
        self.vpn_entry.configure(state="disabled")

    def _on_dl_slider(self, value):
        idx = int(value)
        speed = SPEED_VALUES[idx]
        self.download_mbps = float(speed)
        self.measured_download_mbps = None
        self.dl_label.configure(text=f"Download: {speed} Mbps")
        self._download_touched = True

    def _on_ul_slider(self, value):
        idx = int(value)
        speed = SPEED_VALUES[idx]
        self.upload_mbps = float(speed)
        self.ul_label.configure(text=f"Upload: {speed} Mbps")
        self._upload_touched = True

    def _on_vpn_toggle(self):
        state = "normal" if self.vpn_check.get() else "disabled"
        self.vpn_entry.configure(state=state)

    def _run_speedtest(self):
        self._testing = True
        self.test_btn.configure(state="disabled", text="Testing... (Wait ~20s)")

        def run():
            try:
                self._test_results.put(NetworkTester.run_full_test())
            except Exception:
                self._test_results.put((0, 0, "Failed; previous speeds retained"))

        threading.Thread(target=run, daemon=True).start()

    def _poll_speedtest(self):
        try:
            dl, ul, server = self._test_results.get_nowait()
            self._on_test_finished(dl, ul, server)
        except queue.Empty:
            pass
        self.after(100, self._poll_speedtest)

    def _on_test_finished(self, dl, ul, server):
        self._testing = False
        self.test_btn.configure(state="normal", text=f"Result: {server}")

        if dl > 0:
            # Find closest index
            idx = min(range(len(SPEED_VALUES)), key=lambda i: abs(SPEED_VALUES[i] - dl))
            self.dl_slider.set(idx)
            self._on_dl_slider(idx)
            self.download_mbps = dl
            self.measured_download_mbps = dl
            self.dl_label.configure(text=f"Download: {dl:.1f} Mbps (measured)")

        if ul > 0:
            idx = min(range(len(SPEED_VALUES)), key=lambda i: abs(SPEED_VALUES[i] - ul))
            self.ul_slider.set(idx)
            self._on_ul_slider(idx)
            self.upload_mbps = ul
            self.ul_label.configure(text=f"Upload: {ul:.1f} Mbps (measured)")

    def get_settings(self) -> NetworkSettings:
        dl_idx = int(self.dl_slider.get())
        ul_idx = int(self.ul_slider.get())

        # Map string back to Enum
        conn_str = self.conn_type_var.get()
        conn_enum = next((t for t in ConnectionType if t.value == conn_str), ConnectionType.FIBER)

        return NetworkSettings(
            download_speed_mbps=self.download_mbps,
            upload_speed_mbps=self.upload_mbps,
            connection_type=conn_enum,
            use_vpn=bool(self.vpn_check.get()),
            vpn_interface=self.vpn_entry.get().strip() if self.vpn_check.get() else "",
            isp_throttling=bool(self.isp_check.get())
        )
