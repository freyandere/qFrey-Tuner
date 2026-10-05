# qFrey-Tuner

The launcher uses `outputs/qFrey-Tuner.exe`, updated by `build.bat` from the exact version in `pyproject.toml`. Versioned copies are kept in `dist/` for sharing. Internet tests and torrent measurements run through one background-job queue and cannot overlap.

qFrey-Tuner calculates rule-based qBittorrent recommendations and runs a verified before/after optimization cycle using CustomTkinter.

**Canonical repository and sync remote:** [freyandere/qFrey-Tuner](https://github.com/freyandere/qFrey-Tuner)

```text
origin https://github.com/freyandere/qFrey-Tuner
```

See [the checkout comparison](docs/checkout-status.md) and [workflow and compatibility details](docs/optimization-cycle.md). Nothing is pushed by launching the application.

## Running the application

### Standalone Windows application

Download a Windows executable from [Releases](https://github.com/freyandere/qFrey-Tuner/releases). A bundled executable does not require Python or uv. Published releases may still contain the older 0.2.x interface until this local work is committed and released.

Run from a writable folder. Experiment files are stored in `state/` beside the source application/executable; if that location cannot be written, the application uses `%LOCALAPPDATA%/qFrey-Tuner` on Windows or `~/.local/share/qFrey-Tuner` elsewhere.

A locally built application can be placed at `outputs/qFrey-Tuner.exe`; `Run.bat` launches that standalone build before checking source runtimes. When changing source code, rebuild that executable before using the launcher, or run `python main.py` directly to test the source.


### Web UI connection and diagnostic logs

Use the port shown in qBittorrent **Options > Web UI**, which may differ from the default 8080. The tuner suggests an address from a discovered profile, then validates it against the running API. With localhost authentication bypass enabled, connect to `http://127.0.0.1:<port>` with the password and API key blank. Otherwise provide the actual Web UI password or an API key in its separate field. “Change current password” is a placeholder, not a password.

The tuner first checks existing access before attempting password login. API keys use Bearer authentication. Blank credentials do not trigger repeated login attempts. Profile discovery can become more complete after qBittorrent starts, because its executable and `--profile` arguments become available.

Use **Open diagnostic log** on Setup. Rotating logs are stored at `state/logs/qfrey-tuner.log` beside the executable (for the local build: `outputs/state/logs/qfrey-tuner.log`), with the same fallback directory as experiments. They record startup, operation outcomes, API paths/status codes/timing, and validated versions. Passwords, API keys, cookies, request headers and preference payloads are not logged or saved. Four log files are retained, up to 2 MB each.

### From source

Requirements: Python 3.11+, a working Tcl/Tk GUI runtime, and the dependencies in `pyproject.toml`.

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install .
.\.venv\Scripts\python.exe main.py --check
.\.venv\Scripts\python.exe main.py
```

Alternatively, with [uv installed](https://docs.astral.sh/uv/getting-started/installation/):

```powershell
uv run python main.py
```

Without a local standalone build, `Run.bat` checks an existing virtual environment, then uv, the Python launcher, and Python on PATH. If none works it displays recovery instructions and the standalone download link. It does not silently install system tools. A copied `.venv` can be broken if its original Python installation no longer exists; recreate it using a working interpreter.

## Optimization workflow

1. On **Setup**, choose the environment in which qBittorrent runs.
2. On **Setup**, enable and connect to the intended qBittorrent Web UI. The app checks the reported qBittorrent version, API version, libtorrent version, and live preference schema. Other tabs are locked until this succeeds.
3. Use **Confirm and continue** to review **Network**, **Hardware**, and **Usage** in order. Hardware detection describes the local computer; enter the server's hardware manually for remote installations. Storage detection checks the chosen download volume, using Windows device properties and a Storage cmdlet fallback. Unknown storage remains a manual choice.
4. On **Benchmark**, start your representative torrents and wait for sustained traffic. Alternatively, opt into the official Ubuntu test torrent after reviewing its consent dialog. Record a baseline: ten seconds of warm-up and sixty samples at one-second intervals. **Skip measurement and view recommendations** works while idle; applying the measured cycle still requires a baseline.
5. On **Results**, calculate and review the exact `current -> proposed` preference diff, omissions, and heuristic explanations.
6. Apply the reviewed changes. Original values are saved before mutation. Each requested value is read back; a write alone is never reported as verified success.
7. Record the after measurement using the same active torrent set. Review observed improvement, regression, or an inconclusive result. Repeat comparable trials before drawing conclusions.
8. Keep the settings or use **Roll back and verify**. After relaunching the tuner, **Restore saved rollback file** can recover the original values from the saved experiment.

Failed API reads, no traffic, changed workloads/settings, and invalid sampling abort a measurement. Passwords and authentication cookies are never written to experiment or setup files. Experiment files include the endpoint, versions, relevant preferences, samples, torrent hashes/progress, proposed changes, and original values.

The optional test downloads the [official Ubuntu 22.04.5 desktop ISO](https://releases.ubuntu.com/22.04.5/) through the same qBittorrent API, after fetching and validating its torrent metadata over HTTPS. Budget 5 GB free space and download traffic plus peer uploads. Files are never installed or executed. It does not pause other torrents or guarantee speed/upload demand; at gigabit speed it may finish before both runs. Stop it explicitly using the tuner's stop button or qBittorrent; the tuner only stops its matching hash and unique tag and retains files. An existing copy is never claimed by the tuner. The endpoint, hash, tag and save path are saved in `state/test-workload.json` for stopping after relaunch. Changing the test workload between measurements invalidates comparison. Delete unwanted files manually in qBittorrent after finishing.

Network checkboxes have hover/focus help. **Suspected ISP restrictions** requires encryption and proposes a high listening port; it does not guarantee bypass and can reduce peers. **Use VPN** binds to an existing validated interface on the target, and does not create or connect a VPN. Windows storage queries follow the [documented device property API](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ne-winioctl-storage_property_id).

## Configuration discovery and lifecycle

Configuration-file detection is diagnostic; this application does not rewrite guessed INI keys. It looks beside the tuner, in portable profile layouts, beside accessible running qBittorrent executables, in explicit `--profile` paths, and in standard OS locations. Portable mode does not fall back to an unrelated system configuration. A file can also be chosen manually.

A local file is not required for a remote server: a validated Web API supplies its effective configuration. Merely finding or selecting a file does not unlock tuning.

The app can start a manually selected local executable. Graceful shutdown/restart uses the API only after identifying the local qBittorrent process listening on that Web UI port. Restart preserves that process's launch arguments and working directory and waits for shutdown. It does not force-kill a process, guess a custom profile, or manage remote container/server services. Reconnect afterward to validate again. Normal API preference changes apply live, so restart is optional.

## Compatibility and limitations

- Accepted targets: qBittorrent 4.6 through 5.x with Web API v2.8+ in the v2 family and a recognized libtorrent 1.x/2.x build. This range is guarded by live capabilities; it is not a claim that every release has been tested on a real client.
- Only known preference keys exposed by the connected target with the expected type are proposed. Missing required privacy/connection settings block the plan. Unsupported optional settings are listed explicitly.
- Legacy disk-cache, OS-cache, and coalescing recommendations are excluded for libtorrent 2. Super seeding is per torrent and remains manual.
- VPN interfaces are checked against the target host's interface list. Existing bindings are preserved when no new binding is requested.
- Recommendations remain heuristics. Readback establishes effective preference values, not improved performance or a tested VPN kill switch.
- The benchmark observes the user's active torrents. It does not add/download a hard-coded test torrent. Peer availability, disk cache, and external traffic can change; the comparison does not prove causation.
- Process discovery can be restricted by OS permissions. Lifecycle actions fail clearly when ownership cannot be established.
- Offline schema writes are deliberately blocked; there is no legacy guessed-key writer.

## Development and verification

```powershell
python -m pip install ".[dev]"
python -m pytest tests/ -q -p no:cacheprovider
python tests/verify_startup.py
pyinstaller qFrey-Tuner.spec --clean
```

GUI smoke tests require a graphical session and working Tcl/Tk. Tests exercise API transport using a local HTTP fixture, cycle ordering, compatibility, stale-input guards, durable backups, readback failures, workload mismatch, rollback, process ownership, and setup gating. They do not change a real qBittorrent installation.

The release workflow builds a Windows executable on `v*` tags. Follow [the canonical repository](https://github.com/freyandere/qFrey-Tuner) for released versions.
