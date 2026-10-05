"""Smoke-check the real application window without contacting qBittorrent."""
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))


def main():
    from optimizer.config_manager import ConfigManager
    from optimizer.process_manager import ProcessManager
    from ui.main_window import MainWindow
    with tempfile.TemporaryDirectory() as directory:
        with patch.object(ConfigManager, "app_directory", return_value=Path(directory)), patch.object(ProcessManager, "discover", return_value=[]):
            app = MainWindow(ConfigManager())
            app.withdraw()
            try:
                app.update_idletasks()
                assert app.tab_view.get() == "Connect"
                assert len(app.profile_names) == 6
                assert app.results_tab.apply_btn.cget("state") == "disabled"
                assert app.benchmark_tab.base_btn.cget("state") == "disabled"
                assert not app.client.connected
                print("Main window initialized; six environments available; tuning locked without a validated target.")
            finally:
                app._close()
                import logging
                logging.shutdown()
    print("ALL CHECKS PASSED")


if __name__ == "__main__":
    main()


