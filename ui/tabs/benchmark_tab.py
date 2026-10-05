"""Guided workload preparation and measurement."""
import customtkinter as ctk


def card(parent, title, detail):
    frame = ctk.CTkFrame(parent, corner_radius=12)
    frame.pack(fill="x", padx=16, pady=8)
    ctk.CTkLabel(frame, text=title, font=("Segoe UI", 18, "bold"), anchor="w").pack(fill="x", padx=16, pady=(12, 4))
    ctk.CTkLabel(frame, text=detail, justify="left", anchor="w", wraplength=720, text_color="#b6c4d6").pack(fill="x", padx=16, pady=(0, 12))
    return frame


class BenchmarkTab(ctk.CTkScrollableFrame):
    def __init__(self, master, controller):
        super().__init__(master, fg_color="transparent")
        card(self, "Measure your starting speed", "1  Test now     →     2  Review changes     →     3  Apply & test again     →     4  Compare")
        workload = card(self, "Choose a download", "Use your active torrents, or choose the official image recommended for your download speed.\nThe test waits for traffic and records starting speed automatically. Nothing is installed.")
        self.choice_title = ctk.CTkLabel(workload, text="", anchor="w", font=("Segoe UI", 17, "bold"))
        self.choice_title.pack(fill="x", padx=16, pady=4)
        self.choice_detail = ctk.CTkLabel(workload, text="", anchor="w", justify="left", wraplength=720)
        self.choice_detail.pack(fill="x", padx=16, pady=(0, 8))
        self._choice_state = None
        self.recommendation = None
        self.path_entry = ctk.CTkEntry(workload, placeholder_text="Download folder on the computer running qBittorrent")
        self.path_entry.pack(fill="x", padx=16, pady=6)
        self.test_btn = ctk.CTkButton(workload, text="Download recommended image & test", command=controller.add_test_workload)
        self.test_btn.pack(fill="x", padx=16, pady=6)
        row = ctk.CTkFrame(workload, fg_color="transparent")
        row.pack(fill="x", padx=16, pady=(6, 12))
        row.grid_columnconfigure((0, 1), weight=1, uniform="test-actions")
        self.stop_test_btn = ctk.CTkButton(row, text="Stop • keep ISO", height=36, fg_color="#404956", command=controller.stop_test_workload)
        self.stop_test_btn.grid(row=0, column=0, sticky="ew", padx=(0, 6), pady=4)
        self.delete_test_btn = ctk.CTkButton(row, text="Delete test ISO…", height=36, fg_color="#663e46", command=controller.delete_test_workload)
        self.delete_test_btn.grid(row=0, column=1, sticky="ew", padx=(6, 0), pady=4)
        test = card(self, "Already downloading?", "Record 60 seconds of speed after a 10-second warm-up. Keep the same torrents active for both tests.\nFor an official-image after test, approval includes deleting the previous test ISO and downloading it again.\nIf the image finishes during a test, use a larger workload; the incomplete run is not saved.")
        self.base_btn = ctk.CTkButton(test, text="Test starting speed with my torrents", command=lambda: controller.measure("baseline"))
        self.base_btn.pack(fill="x", padx=16, pady=6)
        self.opt_btn = ctk.CTkButton(test, text="Retry speed test with new settings", command=lambda: controller.measure("optimized"))
        self.opt_btn.pack(fill="x", padx=16, pady=6)
        self.cancel_btn = ctk.CTkButton(test, text="Cancel test", fg_color="#404956", command=controller.cancel_measurement)
        self.cancel_btn.pack(anchor="w", padx=16, pady=6)
        self.progress = ctk.CTkProgressBar(self)
        self.progress.set(0)
        self.description = ctk.CTkLabel(self, text="Ready • no starting speed saved", anchor="w", wraplength=720)
        self.description.pack(fill="x", padx=16, pady=8)
        self.report = ctk.CTkLabel(self, text="", justify="left", anchor="w", wraplength=720)
        self.report.pack(fill="x", padx=16, pady=8)
        ctk.CTkButton(self, text="View recommendations without a speed test", fg_color="#404956", command=controller.guide_next).pack(anchor="w", padx=16, pady=12)

    def update_choice(self, speed_mbps, measured=False):
        state = (speed_mbps, measured)
        if state == self._choice_state:
            return
        self._choice_state = state
        from optimizer.workload_catalog import workload_recommendation
        try:
            self.recommendation = workload_recommendation(speed_mbps)
        except ValueError as exc:
            self.recommendation = {'workload': None}
            self.choice_title.configure(text="Download speed needed")
            self.choice_detail.configure(text=str(exc))
            return
        choice = self.recommendation
        image = choice['workload']
        source = "Measured by Speedtest" if measured else "Current download speed input"
        reasoning = (f"{source}: {speed_mbps:.1f} Mbps. Ubuntu would finish in about {choice['ubuntu_seconds']:.0f} seconds.\n"
                     "Allow 90 seconds: 10 warm-up + 60 measurement + 20 safety margin.\n"
                     f"Minimum size at ideal speed: {choice['required_bytes']/1e9:.2f} GB. Slower peers can extend the download.")
        if image is None:
            self.choice_title.configure(text="Both built-in images are too small at this speed")
            self.choice_detail.configure(text=reasoning + "\nUse a larger active workload with the manual speed test below.")
            self.test_btn.configure(text="Use a larger active workload")
        else:
            self.choice_title.configure(text=f"Recommended: {image.label} • {image.size_bytes/1e9:.2f} GB")
            duration = image.size_bytes / (speed_mbps * 1e6 / 8)
            self.choice_detail.configure(text=reasoning + f"\nSelected image: about {duration:.0f} seconds at ideal speed. Requires {image.size_bytes/1e9:.2f} GB free; each fresh test downloads it again, plus peer uploads.")
            self.test_btn.configure(text=f"Download {image.label} & test")

    def set_report(self, text):
        self.report.configure(text=text)
