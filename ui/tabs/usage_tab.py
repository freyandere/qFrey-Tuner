"""Вкладка сценария использования (CustomTkinter)."""

import customtkinter as ctk
from optimizer.models import TrackerType, UserRole, EnvironmentProfile, UsageSettings

class UsageTab(ctk.CTkScrollableFrame):
    """Вкладка для выбора сценария использования."""

    def __init__(self, master):
        super().__init__(master, fg_color="transparent")
        from ui.tabs import card
        card(self, 'How you use torrents', 'Choose your priorities and tracker type. These guide sharing limits, download queues and peer discovery.')
        self._environment = EnvironmentProfile.SYSTEM
        self._setup_ui()

    def _setup_ui(self):
        # Tracker Type
        self.tracker_frame = ctk.CTkFrame(self)
        self.tracker_frame.pack(fill="x", pady=10)

        ctk.CTkLabel(self.tracker_frame, text="Tracker Type", font=("Segoe UI", 14, "bold")).pack(anchor="w", padx=10, pady=5)

        self.tracker_var = ctk.StringVar(value="Public")

        self.public_radio = ctk.CTkRadioButton(
            self.tracker_frame,
            text="Public Trackers (rutracker, 1337x)",
            variable=self.tracker_var,
            value="Public"
        )
        self.public_radio.pack(anchor="w", padx=10, pady=5)

        ctk.CTkLabel(self.tracker_frame, text="  Peer discovery enabled • share fewer client details", text_color="#28a745", font=("Segoe UI", 11)).pack(anchor="w", padx=20)

        self.private_radio = ctk.CTkRadioButton(
            self.tracker_frame,
            text="Private Trackers (Ratio-based)",
            variable=self.tracker_var,
            value="Private"
        )
        self.private_radio.pack(anchor="w", padx=10, pady=5)

        ctk.CTkLabel(self.tracker_frame, text="  DHT/PeX OFF, Anon OFF, Strict Slots", text_color="#ff6b6b", font=("Segoe UI", 11)).pack(anchor="w", padx=20, pady=(0, 10))

        # User Role
        self.role_frame = ctk.CTkFrame(self)
        self.role_frame.pack(fill="x", pady=10)

        ctk.CTkLabel(self.role_frame, text="primary Goal (Role)", font=("Segoe UI", 14, "bold")).pack(anchor="w", padx=10, pady=5)

        self.role_var = ctk.StringVar(value="Leecher")

        roles = [
            ("Leecher", "Prioritize Download Speed", "Leecher"),
            ("Seeder", "Prioritize Upload Stability", "Seeder"),
            ("Uploader", "Initial distribution; super seeding remains a manual per-torrent setting", "Uploader")
        ]

        for text, hint, val in roles:
            r = ctk.CTkRadioButton(self.role_frame, text=text, variable=self.role_var, value=val)
            r.pack(anchor="w", padx=10, pady=5)
            ctk.CTkLabel(self.role_frame, text=f"  {hint}", text_color="#aaaaaa", font=("Segoe UI", 11)).pack(anchor="w", padx=20)

    def set_environment(self, env: EnvironmentProfile):
        self._environment = env

    def set_settings(self, settings: UsageSettings):
        self._environment = settings.environment

        self.tracker_var.set("Public" if settings.tracker_type == TrackerType.PUBLIC else "Private")

        if settings.user_role == UserRole.LEECHER:
            self.role_var.set("Leecher")
        elif settings.user_role == UserRole.SEEDER:
            self.role_var.set("Seeder")
        else:
            self.role_var.set("Uploader")

    def get_settings(self) -> UsageSettings:
        t_type = TrackerType.PUBLIC if self.tracker_var.get() == "Public" else TrackerType.PRIVATE

        role_str = self.role_var.get()
        if role_str == "Leecher":
            role = UserRole.LEECHER
        elif role_str == "Seeder":
            role = UserRole.SEEDER
        else:
            role = UserRole.UPLOADER

        return UsageSettings(
            tracker_type=t_type,
            user_role=role,
            environment=self._environment
        )
