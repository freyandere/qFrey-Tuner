# qBittorrent compatibility

- Before changing settings or API calls, read `docs/version-compatibility.md` and `docs/qbittorrent-schema-audit-ru.md`.
- Use official qBittorrent Wiki, tagged qBittorrent source, and libtorrent documentation. Wiki examples can be outdated; verify keys/types/units/enums against the target release's controller and session implementation. Preset heuristics are not official recommendations.
- Never introduce direct INI/CONF writes. Use the validated Web API, save original values before mutation, and verify apply/rollback by readback. Keep credentials out of persisted data.
- For a new supported minor branch, audit its release tag, update the version gate and `scripts/check_qbittorrent_schema.py`, add version/contract regressions, and update the compatibility documents. Do not automatically admit future versions.
- Run `python -m pytest tests/ -q` and `python scripts/check_qbittorrent_schema.py`. The second command reads public upstream sources and needs network access. Neither command should change a user's real qBittorrent.

# Local verification and release contract

- Read `docs/build-layout-design.md` before changing build scripts, artifact paths, cleanup rules or release procedures. Use only `scripts/build.py` as the packaging entry point; do not add independent PyInstaller commands or alternate output directories.
- GitHub Actions are not used. Run all tests, upstream API checks and builds locally before pushing a release. Do not add automatic push/tag workflows without a separate user request.
- Use `uv sync --locked --extra dev` and `uv run --locked --extra dev ...` for reproducible local checks. Update `uv.lock` locally when dependencies change.
- Keep generated files inside `UV_CACHE_DIR=.cache/uv`, `UV_PROJECT_ENVIRONMENT=.cache/venv`, `PYTHONPYCACHEPREFIX=.cache/pycache`. Pytest uses `.cache/tests` and `.cache/pytest` through `pyproject.toml`.
- Required checks: `uv run --locked --extra dev python -m pytest tests/ -q` and `uv run --locked --extra dev python scripts/check_qbittorrent_schema.py`. GUI tests need Windows/Tk; the upstream check needs network access. Do not skip a failed check to publish an EXE.
- Full local build: `build.bat`. Prepared environment: `uv run --locked --extra dev python scripts/build.py`. The module compiles into `.cache/staging`, uses `.cache/pyinstaller`, and runs packaged `--check` and `--smoke-test` before publishing locally.
- The only current executable is `artifacts/release/qFrey-Tuner.exe`; its manifest is `artifacts/release/build.json` (version, UTC build time and SHA-256). Upload exactly these two files to GitHub Release manually or with an authenticated local CLI. `Run.bat` selects this EXE, never an archived build. Never compile into the root, `outputs`, `dist` or `distribution`.
- Before replacing the current EXE, preserve it and its manifest in `artifacts/archive/builds/<UTC>/`. Preserve `artifacts/archive/legacy/`, state and experiment backups. Do not delete old EXEs/archives without a separate user request. A locked EXE must fail safely, not be forcibly terminated or deleted.
- `pyproject.toml` is the version source; builds do not bump it. Before publishing `v<version>`, verify the tag matches the project version, points to the locally tested commit, and the manifest checksum matches the EXE. Publish tags/releases only when requested. Keep credentials out of the repository and release assets.
- Report the local checks actually run. After publication, verify the release target, asset names/sizes and uploaded EXE checksum. A source push alone is not a binary release.
