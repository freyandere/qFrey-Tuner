"""Диалог выбора режима установки (CustomTkinter)."""

import customtkinter as ctk
from typing import Optional
from optimizer.models import EnvironmentProfile

# Данные профилей
PROFILES_DATA = {
    EnvironmentProfile.SYSTEM: {
        "icon": "🖥️",
        "title": "System Desktop",
        "subtitle": "Windows / macOS / Linux",
        "description": "Стандартная установка. Конфиг в %APPDATA%.",
    },
    EnvironmentProfile.PORTABLE: {
        "icon": "🚀",
        "title": "Portable",
        "subtitle": "Windows (EXE folder)",
        "description": "Портабельная версия. Конфиг рядом с EXE.",
    },
    EnvironmentProfile.TRUENAS: {
        "icon": "🗄️",
        "title": "TrueNAS / ZFS",
        "subtitle": "FreeNAS, TrueNAS",
        "description": "ZFS ARC кэш.",
    },
    EnvironmentProfile.NAS: {
        "icon": "📦",
        "title": "NAS",
        "subtitle": "Synology / QNAP",
        "description": "Сетевые хранилища.",
    },
    EnvironmentProfile.DOCKER: {
        "icon": "🐳",
        "title": "Docker",
        "subtitle": "Контейнер с VPN",
        "description": "Для Docker контейнеров.",
    },
    EnvironmentProfile.SEEDBOX: {
        "icon": "⚡",
        "title": "Seedbox",
        "subtitle": "1-10 Gbps",
        "description": "Высокая скорость.",
    },
}

class ProfileCard(ctk.CTkFrame):
    """Карточка выбора профиля."""

    def __init__(self, master, profile: EnvironmentProfile, on_click):
        super().__init__(master, corner_radius=10, border_width=2, border_color="#333333", fg_color="#2b2b2b")
        self.profile = profile
        self.on_click = on_click
        self.selected = False

        # Данные
        data = PROFILES_DATA[profile]

        # Layout
        self.grid_columnconfigure(0, weight=1)

        # Event binding wrapper
        def click_handler(event=None):
            self.on_click(self)

        # Icon
        self.icon_label = ctk.CTkLabel(self, text=data["icon"], font=("Segoe UI Emoji", 32))
        self.icon_label.grid(row=0, column=0, pady=(15, 5))
        self.icon_label.bind("<Button-1>", click_handler)

        # Title
        self.title_label = ctk.CTkLabel(self, text=data["title"], font=("Segoe UI", 14, "bold"), text_color="#e0e0e0")
        self.title_label.grid(row=1, column=0, pady=2)
        self.title_label.bind("<Button-1>", click_handler)

        # Subtitle
        self.subtitle_label = ctk.CTkLabel(self, text=data["subtitle"], font=("Segoe UI", 11), text_color="#888888")
        self.subtitle_label.grid(row=2, column=0, pady=(0, 15))
        self.subtitle_label.bind("<Button-1>", click_handler)

        self.bind("<Button-1>", click_handler)
        self.bind("<Enter>", self.on_enter)
        self.bind("<Leave>", self.on_leave)

    def on_enter(self, event):
        if not self.selected:
            self.configure(border_color="#555555", fg_color="#333333")

    def on_leave(self, event):
        if not self.selected:
            self.configure(border_color="#333333", fg_color="#2b2b2b")

    def set_selected(self, selected: bool):
        self.selected = selected
        if selected:
            self.configure(border_color="#1f6feb", fg_color="#1a3a5c")
        else:
            self.configure(border_color="#333333", fg_color="#2b2b2b")


class InstallModeSelector(ctk.CTk):
    """Окно выбора режима установки."""

    def __init__(self):
        super().__init__()

        self.title("qFrey-Tuner - Setup")
        self.geometry("600x500")
        self.resizable(False, False)

        self.selected_profile: Optional[EnvironmentProfile] = None
        self.completed = False
        self.cards = {}

        self._setup_ui()

        # Default selection
        self._select_profile_by_enum(EnvironmentProfile.SYSTEM)

    def _setup_ui(self):
        # Header
        self.header_frame = ctk.CTkFrame(self, fg_color="transparent")
        self.header_frame.pack(pady=20)

        ctk.CTkLabel(self.header_frame, text="🚀 Welcome to qFrey-Tuner", font=("Segoe UI", 20, "bold")).pack()
        ctk.CTkLabel(self.header_frame, text="Select your installation type", font=("Segoe UI", 14), text_color="#aaaaaa").pack()

        # Grid for cards
        self.grid_frame = ctk.CTkFrame(self, fg_color="transparent")
        self.grid_frame.pack(pady=10, padx=20, fill="both", expand=True)

        # Configure grid 3x2
        self.grid_frame.grid_columnconfigure((0, 1, 2), weight=1)
        self.grid_frame.grid_rowconfigure((0, 1), weight=1)

        profiles = list(EnvironmentProfile)
        positions = [(0, 0), (0, 1), (0, 2), (1, 0), (1, 1), (1, 2)]

        for i, profile in enumerate(profiles):
            card = ProfileCard(self.grid_frame, profile, self._on_card_click)
            self.cards[profile] = card
            row, col = positions[i]
            card.grid(row=row, column=col, padx=10, pady=10, sticky="nsew")

        # Description
        self.desc_label = ctk.CTkLabel(self, text="", font=("Segoe UI", 12), text_color="#cccccc", wraplength=500)
        self.desc_label.pack(pady=10)

        # Continue Button
        self.btn = ctk.CTkButton(self, text="Continue", font=("Segoe UI", 14, "bold"), height=40, width=200, command=self.finish)
        self.btn.pack(pady=20)

    def _on_card_click(self, clicked_card):
        self._select_profile_by_enum(clicked_card.profile)

    def _select_profile_by_enum(self, profile):
        for p, card in self.cards.items():
            card.set_selected(p == profile)

        self.selected_profile = profile
        self.desc_label.configure(text=PROFILES_DATA[profile]["description"])

    def finish(self):
        self.completed = True
        self.destroy()
        # main loop exits, selected_profile remains available
