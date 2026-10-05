# Changelog

## [0.3.4] - 2026-10-05
- Simplify architecture; serialize measurements; clarify backup and historical results; unify launcher build path.

All notable changes to this project will be documented in this file.

## [v0.2.9.1] - 2026-02-06
### Fixed
- **Startup Crash**: Resolved `IndexError` in `WelcomeDialog` caused by a mismatch between the 6 profile options and the 5-slot grid layout.
- **Verification**: Added `tests/verify_startup.py` to the build pipeline to prevent shipping broken UI in the future.

## [v0.2.9] - 2026-02-06
### Added
- **Installation Profiles**: Introduced distinct `SYSTEM` and `PORTABLE` environment profiles to correctly target config files.
    - *System*: `%APPDATA%\qBittorrent\qBittorrent.ini`
    - *Portable*: `profile\qBittorrent\config\qBittorrent.ini` (relative to executable)
- **Connection Guards**: `BenchmarkTab` now enforces a WebUI connection before allowing any benchmark actions.
- **Smart ISO Detection**: "Add Test ISO" now detects if the Ubuntu torrent already exists and uses it instead of failing.
- **Safe Cleanup**: Cleanup logic now respects existing user torrents and will not delete files if the ISO was pre-existing.

### Fixed
- **Crash**: Resolved `AttributeError: DESKTOP` which caused the app to crash immediately on launch.
- **Crash**: Resolved `NameError: path_str` in the Config Status label update logic.

### Roadmap
- **v0.3.0 (The Revamp)**: Migration to **CustomTkinter** is planned to address the file size and cold start issues of PyQt6.
    - *Goal*: Reduce executable size from ~60MB to ~15MB.
    - *Plan*: See `0.Inbox/ravamp` for the detailed architectural decision.
    - *Status*: In planning.

## [v0.2.8] - 2026-02-06
### Added
- **Advanced Mode**: Toggle to hide/show complex settings (pinned to `False` by default for safety).
- **Session Persistence**: App now remembers the last used "Advanced Mode" state.
- **Confirmation Dialogs**: Added mandatory consent checkboxes for applying settings and running benchmarks.

## [v0.2.7] - 2026-02-05
### Fixed
- **Benchmark Chart**: Fixed `AttributeError` when rendering the Matplotlib chart in the results tab.

## [v0.2.6] - 2026-02-05
### Added
- **Benchmark Tab**: Introduced the dedicated benchmarking tool with "Baseline" vs "Optimized" comparison.
- **WebAPI Integration**: `BenchmarkManager` now connects to qBittorrent to gather real-time transfer stats.

## [v0.2.5] - 2026-02-04
### Changed
- **Config Manager**: Rewrote path detection logic to support portable installations (preliminary support).

## [v0.2.4] - 2026-02-04
### Added
- **Hardware Tab**: Added detection for CPU cores and RAM to suggest `Async IO threads` count.

## [v0.2.3] - 2026-02-04
### Initial Release
- Basic UI with Network, Hardware, and Usage tabs.
- connection to `calculator` module for setting generation.
