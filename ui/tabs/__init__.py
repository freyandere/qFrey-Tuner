"""Shared tab presentation helpers."""
import customtkinter as ctk


def card(parent, title, detail):
    frame = ctk.CTkFrame(parent, corner_radius=12)
    frame.pack(fill="x", padx=16, pady=8)
    ctk.CTkLabel(frame, text=title, font=("Segoe UI", 18, "bold"), anchor="w").pack(fill="x", padx=16, pady=(12, 4))
    ctk.CTkLabel(frame, text=detail, justify="left", anchor="w", wraplength=720, text_color="#b6c4d6").pack(fill="x", padx=16, pady=(0, 12))
    return frame
