"""qBittorrent Optimizer - Точка входа.

Портабельное приложение для расчёта оптимальных настроек qBittorrent.
"""

import sys
from pathlib import Path

# Добавляем путь к приложению для портабельности
APP_DIR = Path(__file__).parent.resolve()
sys.path.insert(0, str(APP_DIR))

import importlib.util


def check_prerequisites():
    if sys.version_info < (3, 11):
        return "Python 3.11 or newer is required. Use the standalone Windows release or install a supported Python."
    required = ["customtkinter", "requests", "packaging", "PIL", "psutil", "tkinter"]
    missing = [name for name in required if importlib.util.find_spec(name) is None]
    if missing:
        return "Missing dependencies: " + ", ".join(missing) + "\nFrom the repository run: python -m pip install .\nOr use the standalone Windows executable."
    try:
        for name in required:
            __import__(name)
        import tkinter
        if "--check" in sys.argv:
            root = tkinter.Tk()
            root.withdraw()
            root.destroy()
    except Exception as exc:
        return f"GUI runtime could not initialize: {exc}\nUse a Python distribution with working Tcl/Tk, or the standalone Windows release."
    return ""

def main():
    """Запуск приложения."""
    import os
    from optimizer.diagnostics import configure_logging, logger
    directory = Path(sys.executable).parent if getattr(sys, "frozen", False) else APP_DIR
    try:
        configure_logging(directory / "state")
    except OSError:
        configure_logging(Path(os.getenv("LOCALAPPDATA", str(Path.home()))) / "qFrey-Tuner")
    logger.info("Startup Python=%s packaged=%s", sys.version.split()[0], getattr(sys, "frozen", False))
    error = check_prerequisites()
    if "--check-report" in sys.argv:
        import json
        index = sys.argv.index("--check-report")
        if index + 1 >= len(sys.argv):
            return 2
        Path(sys.argv[index + 1]).write_text(json.dumps({"ready": not bool(error), "error": error}), encoding="utf-8")
    if error:
        logger.error("Startup prerequisites failed: %s", error)
        if sys.stderr is not None:
            print(error, file=sys.stderr)
        if "--check" not in sys.argv and getattr(sys, "frozen", False) and sys.platform == "win32":
            import ctypes
            ctypes.windll.user32.MessageBoxW(None, error, "qFrey-Tuner startup", 0x10)
        return 1
    if "--check" in sys.argv:
        if sys.stdout is not None:
            print("Python and application dependencies are ready.")
        return 0
    import customtkinter as ctk
    from ui.main_window import MainWindow
    from optimizer.config_manager import ConfigManager
    # Настройка темы
    ctk.set_appearance_mode("Dark")
    ctk.set_default_color_theme("blue")

    # Setup and target validation are now part of one application window.
    config_manager = ConfigManager()

    # 3. Запуск основного окна
    app = MainWindow(config_manager)
    if "--smoke-test" in sys.argv:
        app.withdraw()
        app.update_idletasks()
        assert app.tab_view.get() == "Connect"
        assert app.results_tab.apply_btn.cget("state") == "disabled"
        assert not app.client.connected
        app._close()
        return 0
    app.mainloop()
    return 0

if __name__ == "__main__":
    sys.exit(main())

