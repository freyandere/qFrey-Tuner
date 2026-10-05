"""Setup-gated, asynchronous optimization workflow for CustomTkinter."""
from dataclasses import asdict
import json
import os
from pathlib import Path
import queue
import threading
import tomllib
from tkinter import filedialog, messagebox

import customtkinter as ctk

from optimizer.calculator import calculate_optimal_settings
from optimizer.config_manager import ConfigManager
from optimizer.optimization_cycle import OptimizationCycle, json_value, save_json
from optimizer.process_manager import ProcessManager
from optimizer.network_tester import NetworkTester
from optimizer.models import EnvironmentProfile
from optimizer.qbittorrent_client import QBittorrentClient, ClientError
from optimizer.diagnostics import configure_logging, logger
from ui.tabs.network_tab import NetworkTab
from ui.tabs.hardware_tab import HardwareTab
from ui.tabs.usage_tab import UsageTab
from ui.tabs.benchmark_tab import BenchmarkTab
from ui.tabs.results_tab import ResultsTab, OutcomeTab


class WorkflowTabview(ctk.CTkTabview):
    def _grid_forget_all_tabs(self, exclude_name=None):
        # CTk queues cleanup with a captured page name. A later page switch
        # must win over that stale callback, or the visible page disappears.
        super()._grid_forget_all_tabs(exclude_name=self.get() or exclude_name)


class MainWindow(ctk.CTk):
    def __init__(self, config_manager: ConfigManager):
        super().__init__()
        self.config_manager = config_manager
        self.client = QBittorrentClient()
        self.cycle = None
        self.busy = False
        self.cancelled = threading.Event()
        self.events = queue.Queue()
        self._closing = False
        self.reviewed = set()
        self._control_state = None
        self.state_dir = ConfigManager.app_directory() / "state"
        try:
            self.state_dir.mkdir(parents=True, exist_ok=True)
            probe = self.state_dir / ".write-test"
            probe.write_text("", encoding="utf-8")
            probe.unlink()
        except OSError:
            self.state_dir = Path(os.getenv("LOCALAPPDATA", str(Path.home()/".local/share"))) / "qFrey-Tuner"
            self.state_dir.mkdir(parents=True, exist_ok=True)
        self.log_path = configure_logging(self.state_dir)
        logger.info("Main window initialized; setup gating enabled")
        self.title(f"qFrey-Tuner v{self._get_version()}")
        self.geometry("1100x850")
        self.minsize(1000, 720)
        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(0, weight=1)
        self.tab_view = WorkflowTabview(self, command=self._gate_navigation)
        self.tab_view.grid(row=0, column=0, sticky="nsew", padx=16, pady=12)
        setup = self.tab_view.add("Connect")
        self.network_tab = NetworkTab(self.tab_view.add("Network"), self)
        self.hardware_tab = HardwareTab(self.tab_view.add("Hardware"))
        self.usage_tab = UsageTab(self.tab_view.add("Your goals"))
        self.usage_tab.set_environment(config_manager.profile)
        self.benchmark_tab = BenchmarkTab(self.tab_view.add("Speed test"), self)
        self.results_tab = ResultsTab(self.tab_view.add("Review changes"), self)
        self.outcome_tab = OutcomeTab(self.tab_view.add("Results"), self)
        for tab in (self.network_tab, self.hardware_tab, self.usage_tab, self.benchmark_tab, self.results_tab, self.outcome_tab):
            tab.pack(fill="both", expand=True)
        self.status_label = ctk.CTkLabel(self, text="Setup required: connect to qBittorrent before tuning.", anchor="w", wraplength=920)
        self.status_label.grid(row=1, column=0, sticky="ew", padx=20, pady=(0, 12))
        self._setup_connection(setup)
        guide = ctk.CTkFrame(self)
        guide.grid(row=2, column=0, sticky="ew", padx=16, pady=(0, 12))
        self.guide_label = ctk.CTkLabel(guide, text="Connect first, then follow the guided steps.")
        self.guide_label.pack(side="left", padx=12)
        self.next_btn = ctk.CTkButton(guide, text="Continue", command=self.guide_next)
        self.next_btn.pack(side="right", padx=10, pady=8)
        ctk.CTkButton(guide, text="Back", width=70, command=self.guide_back).pack(side="right", padx=4)
        self._restore_setup()
        self._watch_id = self.after(400, self._watch_inputs)
        self.after(50, self._drain)
        self.protocol("WM_DELETE_WINDOW", self._close)

    @staticmethod
    def _get_version():
        path = Path(__file__).resolve().parent.parent / "pyproject.toml"
        if path.is_file():
            with path.open("rb") as stream:
                return tomllib.load(stream)["project"]["version"]
        return "unknown"

    def _setup_connection(self, master):
        frame = ctk.CTkScrollableFrame(master, fg_color="transparent")
        frame.pack(fill="both", expand=True)
        ctk.CTkLabel(frame, text="Connect to qBittorrent", font=("Segoe UI", 22, "bold")).pack(anchor="w", padx=12, pady=12)
        self.profile_names = {
            "System desktop": EnvironmentProfile.SYSTEM, "Portable Windows": EnvironmentProfile.PORTABLE,
            "TrueNAS / ZFS": EnvironmentProfile.TRUENAS, "NAS": EnvironmentProfile.NAS,
            "Docker with VPN": EnvironmentProfile.DOCKER, "Seedbox": EnvironmentProfile.SEEDBOX,
        }
        self.profile_menu = ctk.CTkOptionMenu(frame, values=list(self.profile_names), command=self.change_profile)
        self.profile_menu.set(next(name for name, profile in self.profile_names.items() if profile == self.config_manager.profile))
        self.profile_menu.pack(anchor="w", padx=12, pady=8)
        from ui.tabs import card
        self.profile_detail_frame = card(frame, "What this choice changes", "")
        self.profile_detail_title = ctk.CTkLabel(self.profile_detail_frame, text="", font=("Segoe UI", 15, "bold"), anchor="w")
        self.profile_detail_title.pack(fill="x", padx=16, pady=4)
        self.profile_detail_text = ctk.CTkLabel(self.profile_detail_frame, text="", wraplength=780, justify="left", anchor="w")
        self.profile_detail_text.pack(fill="x", padx=16, pady=(0, 12))
        self._update_profile_detail()

        ctk.CTkLabel(frame, text="Enable qBittorrent's Web UI in Options, set credentials, and enter its address below.\nWe check the connection before reading or changing any settings.", justify="left", wraplength=800).pack(anchor="w", padx=12, pady=8)
        self.config_label = ctk.CTkLabel(frame, text="", justify="left", anchor="w", wraplength=800)
        self.config_label.pack(fill="x", padx=12, pady=8)
        row = ctk.CTkFrame(frame, fg_color="transparent")
        row.pack(fill="x", padx=12)
        ctk.CTkButton(row, text="Find local profiles", command=self.refresh_discovery).pack(side="left", padx=(0, 8))
        ctk.CTkButton(row, text="Choose config file", command=self.select_config).pack(side="left")
        self.process_label = ctk.CTkLabel(frame, text="Process discovery has not run.", justify="left", wraplength=800)
        self.process_label.pack(anchor="w", padx=12, pady=8)
        ctk.CTkLabel(frame, text="qBittorrent Web UI address", anchor="w").pack(fill="x", padx=12)
        self.host_entry = ctk.CTkEntry(frame, placeholder_text="Web UI address")
        self.host_entry.insert(0, "http://127.0.0.1:8080")
        self.host_entry.pack(fill="x", padx=12, pady=6)
        ctk.CTkLabel(frame, text="Username", anchor="w").pack(fill="x", padx=12)
        self.user_entry = ctk.CTkEntry(frame, placeholder_text="Username")
        self.user_entry.insert(0, "admin")
        self.user_entry.pack(fill="x", padx=12, pady=6)
        ctk.CTkLabel(frame, text="Password • leave blank if local access needs no login", anchor="w").pack(fill="x", padx=12)
        self.pass_entry = ctk.CTkEntry(frame, placeholder_text="Password", show="*")
        self.pass_entry.pack(fill="x", padx=12, pady=6)
        ctk.CTkLabel(frame, text="API key • optional, never saved", anchor="w").pack(fill="x", padx=12)
        self.api_key_entry = ctk.CTkEntry(frame, placeholder_text="API key (optional; never saved)", show="*")
        self.api_key_entry.pack(fill="x", padx=12, pady=6)
        for entry in (self.api_key_entry, self.pass_entry):
            entry.bind("<Control-v>", lambda event, field=entry: self.paste_credential(field))
            entry.bind("<Control-V>", lambda event, field=entry: self.paste_credential(field))
        ctk.CTkButton(frame, text="Paste API key from clipboard", command=lambda: self.paste_credential(self.api_key_entry)).pack(anchor="w", padx=12, pady=4)
        ctk.CTkLabel(frame, text="Localhost bypass: leave credentials blank. Otherwise enter the actual password or an API key.\nThe 'Change current password' text in qBittorrent is a placeholder, not your password.", justify="left", wraplength=800).pack(anchor="w", padx=12, pady=4)
        self.connect_btn = ctk.CTkButton(frame, text="Connect and validate", command=self.connect_target)
        self.connect_btn.pack(fill="x", padx=12, pady=8)
        self.validation_label = ctk.CTkLabel(frame, text="No validated target", anchor="w", justify="left", wraplength=800)
        self.validation_label.pack(fill="x", padx=12, pady=8)
        row = ctk.CTkFrame(frame, fg_color="transparent")
        row.pack(fill="x", padx=12, pady=8)
        ctk.CTkButton(row, text="Start local qBittorrent", command=self.start_target).pack(side="left", padx=(0, 8))
        self.stop_btn = ctk.CTkButton(row, text="Graceful shutdown", command=lambda: self.stop_target(False), state="disabled")
        self.stop_btn.pack(side="left", padx=(0, 8))
        self.restart_btn = ctk.CTkButton(row, text="Restart same process", command=lambda: self.stop_target(True), state="disabled")
        self.restart_btn.pack(side="left")
        ctk.CTkButton(frame, text="Open diagnostic log", command=self.open_log).pack(fill="x", padx=12, pady=8)
        self.webui_hint_label = ctk.CTkLabel(frame, text="", justify="left", anchor="w", wraplength=800)
        self.webui_hint_label.pack(fill="x", padx=12, pady=4)
        ctk.CTkLabel(frame, text="Config-file discovery is diagnostic. API changes and benchmarks always use this same endpoint.\nAPI settings take effect live; restart is optional. Local lifecycle requires identifying the port's owner.\nRemote servers and containers must be started/restarted by their supervisor.", justify="left", wraplength=800).pack(anchor="w", padx=12, pady=8)
        self._update_config_label()

    def _update_config_label(self):
        path = self.config_manager.config_path
        self.config_label.configure(text=f"Local configuration: {path}" if path else "Local configuration not found. Choose the file for diagnostics, or connect to the intended Web UI to read its effective settings.", text_color="#b6c4d6" if path else "#f0b95b")
        if hasattr(self, "webui_hint_label"):
            hint = self.config_manager.webui_hint()
            self.webui_hint_label.configure(text=f"Detected Web UI address: {hint['url']} (profile setting; validate before tuning)" if hint else "No Web UI address could be read from this profile.")
            if hint and not self.client.connected and self.host_entry.get().strip() == "http://127.0.0.1:8080":
                self.host_entry.delete(0, "end")
                self.host_entry.insert(0, hint["url"])
                logger.info("Web UI address suggested from detected profile")

    def open_log(self):
        if os.name == "nt":
            os.startfile(self.log_path)
        else:
            messagebox.showinfo("Diagnostic log", str(self.log_path), parent=self)

    def paste_credential(self, field):
        try:
            value = self.clipboard_get().strip()
            if value:
                field.delete(0, "end")
                field.insert(0, value)
        except Exception:
            messagebox.showinfo("Clipboard unavailable", "Copy the credential as plain text and try again.", parent=self)
        return "break"

    def _update_profile_detail(self):
        from ui.selection_details import PROFILE_DETAILS
        title, detail = PROFILE_DETAILS[self.config_manager.profile]
        self.profile_detail_title.configure(text=title)
        self.profile_detail_text.configure(text=detail)

    def change_profile(self, name):
        if self.busy or (self.cycle and self.cycle.original is not None):
            self.profile_menu.set(next(n for n, p in self.profile_names.items() if p == self.config_manager.profile))
            messagebox.showinfo("Cycle in progress", "Finish the current operation and roll back before changing environment.", parent=self)
            return
        profile = self.profile_names[name]
        self.config_manager.profile = profile
        self._update_profile_detail()
        self.usage_tab.set_environment(profile)
        def operation():
            self.config_manager.set_installation_type(profile)
        def done(_):
            self._update_config_label()
            self.status_label.configure(text="Environment updated. Validate the intended Web UI and confirm its hardware inputs.")
        self._job(operation, done)

    def _gate_navigation(self):
        if not self._target_ready() or self.busy:
            self.tab_view.set("Connect" if not self._target_ready() else "Speed test" if self._measuring else "Review changes")
        self._guide_status()

    def _guide_status(self):
        step = self.tab_view.get()
        if hasattr(self, "guide_label"):
            self.guide_label.configure(text=f"Step {['Connect','Network','Hardware','Your goals','Speed test','Review changes','Results'].index(step)+1}/7: {step}")
            labels = {"Connect": "Review connection", "Network": "Confirm internet connection", "Hardware": "Confirm hardware", "Your goals": "Confirm goals", "Speed test": "Preview without a test", "Review changes": "View test results", "Results": "Return to speed test"}
            self.next_btn.configure(text=labels[step])

    def guide_back(self):
        if self.busy:
            return
        steps = ["Connect", "Network", "Hardware", "Your goals", "Speed test", "Review changes", "Results"]
        self.tab_view.set(steps[max(0, steps.index(self.tab_view.get())-1)])
        self._guide_status()

    def guide_next(self):
        if self.busy or not self._target_ready():
            return
        steps = ["Connect", "Network", "Hardware", "Your goals", "Speed test", "Review changes", "Results"]
        step = self.tab_view.get()
        if step in ("Network", "Hardware", "Your goals"):
            self.reviewed.add(step)
        if step in ("Review changes", "Results"):
            self.tab_view.set("Results" if step == "Review changes" else "Speed test")
        else:
            self.tab_view.set(steps[steps.index(step)+1])
        self._guide_status()
        self._control_state = None
        self._controls()
        if self.tab_view.get() == "Review changes" and self.reviewed == {"Network", "Hardware", "Your goals"}:
            self.preview()

    def _target_ready(self):
        return self.client.connected and self.host_entry.get().strip().rstrip("/") == self.client.host

    def _require_target(self):
        if not self._target_ready():
            raise ClientError("Connect and validate qBittorrent on Setup first.")

    def _job(self, operation, success, measuring=False):
        if self.busy:
            return
        self.busy = True
        logger.info("Operation started action=%s measuring=%s", getattr(operation, "__name__", "operation"), measuring)
        self._measuring = measuring
        self.cancelled.clear()
        self.status_label.configure(text="Working…")
        self._controls()
        def run():
            try:
                result = operation()
                self.events.put(("done", success, result))
            except Exception as exc:
                logger.error("Operation failed action=%s type=%s", getattr(operation, "__name__", "operation"), type(exc).__name__)
                self.events.put(("error", str(exc)))
        threading.Thread(target=run, daemon=True).start()

    def run_network_test(self):
        if self.busy:
            return
        def done(result):
            self.network_tab._on_test_finished(*result)
            self.status_label.configure(text="Internet test finished. Review the measured download and upload speeds."
                                        if result[0] > 0 or result[1] > 0 else
                                        "Internet speed could not be measured. Previous speed inputs were kept; retry the test.")
        self._job(NetworkTester.run_full_test, done)
        self.network_tab.test_btn.configure(text="Testing internet speed…")

    def _drain(self):
        if self._closing:
            return
        try:
            while True:
                event = self.events.get_nowait()
                if event[0] == "progress":
                    self.benchmark_tab.progress.pack(fill="x", padx=16, pady=8)
                    self.benchmark_tab.progress.set(event[1])
                    self.benchmark_tab.description.configure(text=event[2])
                elif event[0] == "done":
                    logger.info("Operation completed")
                    self.busy = False
                    self._measuring = False
                    self.benchmark_tab.progress.pack_forget()
                    event[1](event[2])
                    self._controls()
                else:
                    self.busy = False
                    self._measuring = False
                    if self.network_tab.test_btn.cget('text') == "Testing internet speed…":
                        self.network_tab.test_btn.configure(text="Retry internet speed test")
                    self.benchmark_tab.progress.pack_forget()
                    self.status_label.configure(text=event[1])
                    self.benchmark_tab.description.configure(text=event[1])
                    if self.cycle and self.cycle.original is not None:
                        self.results_tab.set_report("Verified settings remain applied. Retry the speed test or Undo in Results."
                                                    if self.cycle.verified else
                                                    "Original settings are backed up, but application could not be verified. Some settings may have changed; Undo in Results restores the saved values.")
                    self._controls()
                    messagebox.showerror("Operation not completed", event[1], parent=self)
                    if not self.client.connected:
                        self.tab_view.set("Connect")
        except queue.Empty:
            pass
        self.after(50, self._drain)

    def _controls(self):
        ready = self._target_ready() and not self.busy
        cycle = self.cycle
        self.benchmark_tab.update_choice(self.network_tab.download_mbps, self.network_tab.measured_download_mbps is not None)
        state = (ready, self.busy, getattr(self, "_measuring", False), bool(cycle and cycle.baseline), bool(cycle and cycle.plan), bool(cycle and cycle.original is not None), bool(cycle and cycle.verified), frozenset(self.reviewed), self.network_tab.download_mbps, self.network_tab.measured_download_mbps)
        if state == self._control_state:
            return
        self._control_state = state
        self.network_tab.test_btn.configure(state="disabled" if self.busy else "normal")
        self.next_btn.configure(state="normal" if ready else "disabled")
        if not self._target_ready():
            self.tab_view.set("Connect")
        self.tab_view.configure(state="normal" if ready else "disabled")
        cycle = self.cycle
        self.connect_btn.configure(state="disabled" if self.busy else "normal")
        for button in (self.stop_btn, self.restart_btn):
            button.configure(state="normal" if ready else "disabled")
        self.benchmark_tab.base_btn.configure(state="normal" if ready and (not cycle or cycle.original is None) else "disabled")
        self.benchmark_tab.opt_btn.configure(state="normal" if ready and cycle and cycle.verified else "disabled")
        self.benchmark_tab.cancel_btn.configure(state="normal" if self.busy and self._measuring else "disabled")
        self.results_tab.calc_btn.configure(state="normal" if ready and cycle and cycle.original is None and len(self.reviewed) == 3 else "disabled")
        self.results_tab.apply_btn.configure(state="normal" if ready and cycle and cycle.baseline and cycle.plan and cycle.original is None else "disabled")
        self.outcome_tab.rollback_btn.configure(state="normal" if ready and cycle and cycle.original is not None else "disabled")
        self.benchmark_tab.test_btn.configure(state="normal" if ready and (not cycle or cycle.original is None) and self.benchmark_tab.recommendation["workload"] else "disabled")
        for button in (self.benchmark_tab.stop_test_btn, self.benchmark_tab.delete_test_btn):
            button.configure(state="normal" if ready else "disabled")

    def _inputs(self):
        return json_value({"network": asdict(self.network_tab.get_settings()), "hardware": asdict(self.hardware_tab.get_settings()), "usage": asdict(self.usage_tab.get_settings())})

    def _watch_inputs(self):
        if self._closing:
            return
        if self.cycle and self.cycle.plan and not self.busy and self.cycle.original is None and self._inputs() != self.cycle.plan["inputs"]:
            self.cycle.plan = None
            self.results_tab.set_report("Inputs changed. Calculate a fresh preview before applying.")
        self._controls()
        self._watch_id = self.after(400, self._watch_inputs)

    def _save_setup(self):
        save_json(self.state_dir / "setup.json", {"host": self.client.host, "username": self.user_entry.get(), "config": str(self.config_manager.config_path or ""), "environment": self.config_manager.profile.name})

    def _restore_setup(self):
        try:
            data = json.loads((self.state_dir / "setup.json").read_text(encoding="utf-8"))
            profile = EnvironmentProfile[data.get("environment", "SYSTEM")]
            self.config_manager.set_installation_type(profile)
            self.usage_tab.set_environment(profile)
            self.profile_menu.set(next(name for name, value in self.profile_names.items() if value == profile))
            for entry, key in ((self.host_entry, "host"), (self.user_entry, "username")):
                entry.delete(0, "end")
                entry.insert(0, data[key])
            if data.get("config"):
                self.config_manager.set_manual_path(data["config"])
                self._update_config_label()
        except (OSError, ValueError, KeyError):
            pass
        self._update_config_label()
        self._update_profile_detail()
        self._controls()

    def connect_target(self):
        if self.busy:
            return
        host, username, password = self.host_entry.get().strip().rstrip("/"), self.user_entry.get(), self.pass_entry.get()
        api_key = self.api_key_entry.get().strip()
        previous = self.cycle
        if previous and previous.original is not None and host != self.client.host:
            messagebox.showerror("Rollback pending", "Roll back the current target before switching endpoints. Your saved rollback file is preserved.", parent=self)
            return
        def operation():
            old_version = self.client.version
            self.client.host = host
            self.client.connect(username, password, api_key)
            self._download_path_hint = self.client.preferences().get("save_path", "")
            if previous and previous.original is not None and old_version != self.client.version:
                raise ClientError("Version changed with rollback pending. Review the saved backup before continuing.")
            if previous and previous.original is not None:
                current = self.client.preferences()
                previous.verified = all(current.get(k) == v for k, v in previous.applied.items())
                return previous
            return OptimizationCycle(self.client, self.state_dir)
        def done(cycle):
            self.cycle = cycle
            self.pass_entry.delete(0, "end")
            self.api_key_entry.delete(0, "end")
            self.validation_label.configure(text=f"Validated {self.client.host}\nqBittorrent {self.client.version} | API {self.client.api_version} | libtorrent {self.client.libtorrent}\nAccess: {self.client.auth_mode}", text_color="#73c991")
            self.status_label.configure(text="Connected. Confirm your internet connection, computer and goals to prepare a speed test.")
            self._save_setup()
            from urllib.parse import urlsplit
            if urlsplit(self.client.host).hostname in ("127.0.0.1", "localhost", "::1"):
                self.hardware_tab.disk_path.delete(0, "end")
                self.hardware_tab.disk_path.insert(0, self._download_path_hint)
            self.benchmark_tab.path_entry.delete(0, "end")
            self.benchmark_tab.path_entry.insert(0, self._download_path_hint)
            self._control_state = None
            self._controls()
            self.tab_view.set("Network")
            self.update_idletasks()
            self._guide_status()
        self._job(operation, done)

    def refresh_discovery(self):
        if self.busy:
            return
        def operation():
            self.config_manager.config_path = self.config_manager._find_config(self.config_manager.profile)
            return ProcessManager.discover()
        def done(processes):
            self._update_config_label()
            self.process_label.configure(text="\n".join(f"PID {p['pid']}: {p.get('exe') or p['name']}" for p in processes) or "No accessible local qBittorrent process found.")
            self.status_label.configure(text="Local discovery completed; validate the Web UI endpoint to continue.")
        self._job(operation, done)

    def select_config(self):
        if self.busy:
            return
        path = filedialog.askopenfilename(parent=self, title="Select qBittorrent configuration", filetypes=[("qBittorrent configuration", "*.ini *.conf")])
        if path:
            if self.config_manager.set_manual_path(path):
                self._update_config_label()
            else:
                messagebox.showerror("Invalid configuration", "The file is unreadable or has no recognized qBittorrent section.", parent=self)

    def measure(self, mode, restart_test=False):
        if self.busy:
            return
        try:
            self._require_target()
            cycle = self.cycle
            managed = mode == "optimized" and bool(cycle.test_workload)
            if managed and not restart_test and not messagebox.askyesno("Start a fresh image after test?",
                    f"Delete only the tuner's previous test ISO and download it again in the same folder? "
                    f"This creates real traffic for the after test (up to another {cycle.test_workload.get('bytes', 4762707968)/1e9:.2f} GB, plus uploads). "
                    "Other torrents and files are kept.", parent=self):
                return
            if mode == "baseline":
                cycle.test_workload = None
            def progress(value, text):
                self.events.put(("progress", value, text))
            def done(result):
                self.benchmark_tab.description.configure(text=f"Valid {mode} measurement saved")
                report = cycle.comparison() if cycle.optimized else f"Baseline: download {result['mean_download_mib_s']:.2f} MiB/s; upload {result['mean_upload_mib_s']:.2f} MiB/s.\nNext: calculate a preview on Results."
                self.benchmark_tab.set_report(report + f"\n\nSaved experiment: {cycle.path}")
                self.status_label.configure(text=f"{mode.title()} measurement completed.")
                self.tab_view.set("Results" if mode == "optimized" else "Review changes")
                self.outcome_tab.show_cycle(cycle)
                self._guide_status()
                if mode == "baseline" and len(self.reviewed) == 3:
                    self.after(100, self.preview)
            def operation():
                if managed:
                    from optimizer.test_workload import restart_workload
                    cycle.test_workload = restart_workload(self.client, cycle.test_workload,
                        self.state_dir / "test-workload.json", self.cancelled, progress)
                    cycle.persist()
                return cycle.measure(mode, progress=progress, cancelled=self.cancelled)
            self._job(operation, done, measuring=True)
        except ClientError as exc:
            messagebox.showerror("Setup required", str(exc), parent=self)

    def cancel_measurement(self):
        self.cancelled.set()

    def add_test_workload(self):
        if self.busy or not self._target_ready():
            return
        path = self.benchmark_tab.path_entry.get().strip()
        if not path:
            messagebox.showinfo("Download folder required", "Enter a folder on the qBittorrent host, with at least 5 GB free.", parent=self)
            return
        from optimizer.workload_catalog import workload_recommendation
        try:
            choice = workload_recommendation(self.network_tab.download_mbps)
        except ValueError as exc:
            messagebox.showinfo("Download speed needed", str(exc), parent=self)
            return
        workload = choice['workload']
        if workload is None:
            messagebox.showinfo("Larger workload needed", "Both official images are too small for 90 seconds at this speed. Use a larger active torrent workload and the manual speed test.", parent=self)
            return
        if not messagebox.askyesno("Allow official test download?",
                f"Recommended: {workload.label} ({workload.size_bytes/1e9:.2f} GB).\n"
                f"At {choice['speed_mbps']:.1f} Mbps, Ubuntu could finish in {choice['ubuntu_seconds']:.0f} seconds. "
                "The test needs 90 seconds including a safety margin.\n"
                f"Folder on {self.client.host}: {path}\n"
                f"Requires {workload.size_bytes/1e9:.2f} GB free and up to that much download traffic per test, plus peer uploads.\n"
                "Nothing is installed or executed. Other torrents are kept.\n"
                "The after test downloads the same image again, after your approval.\n\nStart the download and starting-speed test?", parent=self):
            return
        from optimizer.test_workload import add_workload
        def operation():
            record = add_workload(self.client, path, self.state_dir / "test-workload.json", workload)
            self.cycle.test_workload = record
            from optimizer.test_workload import wait_for_traffic
            wait_for_traffic(self.client, record, self.cancelled,
                             lambda value, text: self.events.put(("progress", value, text)))
            return self.cycle.measure("baseline", cancelled=self.cancelled,
                progress=lambda value, text: self.events.put(("progress", value, text)))
        def done(result):
            self.benchmark_tab.description.configure(text="Before test saved. Review the proposed changes next.")
            self.tab_view.set("Review changes")
            self._guide_status()
            if len(self.reviewed) == 3:
                self.after(100, self.preview)
        self._job(operation, done, measuring=True)

    def delete_test_workload(self):
        if self.busy or not self._target_ready():
            return
        if not messagebox.askyesno("Delete official test download?",
                "Remove only the official test torrent added by this tuner and delete its downloaded ISO on the qBittorrent host? Other torrents are kept. You can then start a fresh test.", parent=self):
            return
        from optimizer.test_workload import delete_workload
        def operation():
            from optimizer.test_workload import load_workload
            record = load_workload(self.client, self.state_dir / "test-workload.json")
            delete_workload(self.client, record)
        self._job(operation, lambda _: self.benchmark_tab.description.configure(text="Test download removed. Ready for a fresh download."))

    def stop_test_workload(self):
        if self.busy or not self._target_ready():
            return
        from optimizer.test_workload import stop_workload
        def operation():
            from optimizer.test_workload import load_workload
            record = load_workload(self.client, self.state_dir / "test-workload.json")
            stop_workload(self.client, record)
        self._job(operation, lambda _: self.status_label.configure(text="Test torrent stop requested. Files retained; verify its state in qBittorrent."))

    def preview(self):
        if self.busy:
            return
        if len(self.reviewed) != 3:
            messagebox.showinfo("Confirm inputs first", "Use Confirm and continue on Network, Hardware and Your goals before calculating.", parent=self)
            return
        self._require_target()
        network, hardware, usage = self.network_tab.get_settings(), self.hardware_tab.get_settings(), self.usage_tab.get_settings()
        if hardware.is_hybrid_cpu and not 0 < hardware.p_cores <= hardware.cpu_cores:
            messagebox.showerror("Invalid CPU input", "Performance cores must be between one and the total physical core count.", parent=self)
            return
        if network.use_vpn and not network.vpn_interface.strip():
            messagebox.showerror("VPN interface required", "Enter the target host's VPN interface before calculating.", parent=self)
            return
        inputs = self._inputs()
        settings = calculate_optimal_settings(network, hardware, usage)
        def done(plan):
            self.results_tab.show_plan(plan)
            self.status_label.configure(text="Preview ready. Review every change before applying.")
        self._job(lambda: self.cycle.preview(settings, inputs), done)

    def apply(self):
        if self.busy:
            return
        test_note = ("\n\nFor the after test, the tuner's previous test torrent and ISO will be deleted and downloaded again "
                     f"in the same folder (up to another {self.cycle.test_workload.get('bytes', 4762707968)/1e9:.2f} GB, plus uploads). Other torrents are kept."
                     if self.cycle.test_workload else
                     "\n\nYour existing torrents will be measured again. Keep the same downloads active; completed downloads cannot provide an after download test.")
        if not messagebox.askyesno("Apply reviewed changes & test", "Apply the displayed settings? Original values are saved first and checked after applying." + test_note, parent=self):
            return
        inputs = self._inputs()
        def done(_):
            self.results_tab.set_report(f"All requested changes read back successfully.\nRollback file: {self.cycle.path}\nThe second speed test starts automatically.\nReadback confirms effective preferences, not a performance improvement.")
            self.status_label.configure(text="Changes verified. Performance measurement is still required.")
            self.tab_view.set("Speed test")
            self.outcome_tab.show_cycle(self.cycle)
            self._guide_status()
            self.after(100, lambda: self.measure("optimized", restart_test=True))
        self._job(lambda: self.cycle.apply(inputs), done)

    def rollback(self):
        if self.busy:
            return
        if not messagebox.askyesno("Restore original settings", "Restore and verify the settings saved before this tuning? Downloaded files and torrents will be kept.", parent=self):
            return
        def done(_):
            self.outcome_tab.show_cycle(self.cycle)
            self.results_tab.set_report("Original settings restored. Run a new before test to review fresh recommendations.")
            self.status_label.configure(text="Original settings restored. Downloaded files were kept.")
            self.tab_view.set("Speed test")
        self._job(self.cycle.rollback, done)

    def restore_backup(self):
        if self.busy or not self.client.connected:
            messagebox.showerror("Setup required", "Connect to the original target before restoring a backup.", parent=self)
            return
        path = filedialog.askopenfilename(parent=self, initialdir=self.state_dir, filetypes=[("Cycle backup", "*.json")])
        if path and messagebox.askyesno("Restore saved settings", "Restore the original preference values from this experiment file?", parent=self):
            self._job(lambda: self.cycle.restore_backup(path), lambda _: self.status_label.configure(text="Saved settings restored and verified."))

    def start_target(self):
        if self.busy:
            return
        path = filedialog.askopenfilename(parent=self, title="Select qBittorrent executable")
        if path:
            self._job(lambda: ProcessManager.start(path), lambda _: self.status_label.configure(text="qBittorrent launched. Enable its Web UI and connect. Custom profiles must be launched with their normal shortcut."))

    def stop_target(self, restart):
        if not self.client.connected or self.busy:
            return
        if not messagebox.askyesno("Restart" if restart else "Shutdown", "Gracefully stop the local qBittorrent process owning this Web UI port?" + (" It will be relaunched with the original arguments." if restart else ""), parent=self):
            return
        self._job(lambda: ProcessManager.stop(self.client, restart), lambda _: self._lifecycle_done(restart))

    def _lifecycle_done(self, restart):
        self.status_label.configure(text="Restart requested. Reconnect to verify settings again." if restart else "qBittorrent stopped. Tuning locked until reconnect.")
        self.validation_label.configure(text="Disconnected; live verification required.")
        self.tab_view.set("Connect")

    def _close(self):
        if self.busy:
            self.cancelled.set()
            messagebox.showinfo("Operation in progress", "Wait for the operation to finish or cancel before closing. Any saved rollback file is retained.", parent=self)
            return
        self._closing = True
        self.client.disconnect()
        self.destroy()
