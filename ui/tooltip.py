"""Small keyboard- and mouse-accessible help bubbles."""
import customtkinter as ctk


def tooltip(widget, text):
    bubble = [None]
    def hide(event=None):
        if bubble[0] is not None:
            bubble[0].destroy()
            bubble[0] = None
    def show(event=None):
        hide()
        top = ctk.CTkToplevel(widget)
        bubble[0] = top
        top.overrideredirect(True)
        top.geometry(f"+{widget.winfo_rootx()+10}+{widget.winfo_rooty()+widget.winfo_height()+4}")
        ctk.CTkLabel(top, text=text, wraplength=420, justify="left").pack(padx=12, pady=10)
    widget.bind("<Enter>", show, add="+")
    widget.bind("<Leave>", hide, add="+")
    widget.bind("<FocusIn>", show, add="+")
    widget.bind("<FocusOut>", hide, add="+")

