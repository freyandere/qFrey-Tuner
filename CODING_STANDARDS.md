# Coding standards

Read the section that matches the task; follow its linked documents before making changes.

## Coding and verification

- Use `uv sync --locked --extra dev` and `uv run --locked --extra dev ...` for reproducible Python checks. Update `uv.lock` locally when dependencies change.
- For manual runs, set `UV_CACHE_DIR=.cache/uv`, `UV_PROJECT_ENVIRONMENT=.cache/venv` and `PYTHONPYCACHEPREFIX=.cache/pycache`; `build.bat` sets these itself. Pytest's temporary and cache paths are configured in `pyproject.toml`.
- Run `uv run --locked --extra dev python -m pytest tests/ -q` and `uv run --locked --extra dev python scripts/check_qbittorrent_schema.py`. GUI tests require Windows/Tk; the schema check requires network access. These checks must not mutate a user's real qBittorrent.
- Report the checks actually run and any blocked checks.

## qBittorrent contracts

Read [Version compatibility](docs/version-compatibility.md) and [Schema audit](docs/qbittorrent-schema-audit-ru.md) for the supported branches, official sources and API contract.

- Verify keys, types, units and enums against the target release's controller and session implementation; Wiki examples may be outdated. Use official qBittorrent and libtorrent sources. Preset heuristics are not official recommendations.
- Apply settings through the validated Web API: save original values and verify apply/rollback by readback. Direct INI/CONF writes are prohibited; keep credentials out of persisted data.
- To support a new minor branch, audit its release tag, update the version gate and `scripts/check_qbittorrent_schema.py`, add version/contract regressions and update the compatibility documents. Future branches require this explicit audit.

## Build and release

Read [Build layout and release procedure](docs/build-layout-design.md) for artifact locations, archiving, cleanup and publication checks.

- Package through `build.bat`, or `uv run --locked --extra dev python scripts/build.py` in a prepared environment. `scripts/build.py` is the only packaging entry point.
- Run tests, upstream API checks and the full build locally before publishing a release; failed checks block EXE publication. GitHub Actions are not used. Automatic push/tag workflows require a separate user request.
- Preserve the current EXE and manifest before replacement, and preserve archives, state and experiment backups. Deleting old EXEs/archives requires a separate request. A locked EXE must fail safely without forced termination or deletion.
- Publish tags/releases only when requested. Follow the linked procedure to verify the project version, tested commit and manifest checksum before publication, then verify the release target, asset names/sizes and uploaded EXE checksum. Keep credentials out of the repository and assets; a source push is not a binary release.
