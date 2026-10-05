@echo off
setlocal
cd /d "%~dp0"
set "PYTHONPYCACHEPREFIX=%CD%\.cache\pycache"
set "UV_CACHE_DIR=%CD%\.cache\uv"
set "UV_PROJECT_ENVIRONMENT=%CD%\.cache\venv"
set "PATH=%CD%\.cache\tools\uv\Scripts;%PATH%"
where uv >nul 2>nul
if errorlevel 1 (
    echo Building requires uv. Install it from https://docs.astral.sh/uv/getting-started/installation/
    echo Or use Python manually: python -m pip install ".[dev]"
    echo Then: python -m pytest tests/ and python scripts/build.py
    pause
    exit /b 1
)
uv sync --locked --extra dev
if errorlevel 1 goto failed
uv run --extra dev python main.py --check
if errorlevel 1 goto failed
uv run --extra dev python -m pytest tests/ -q -p no:cacheprovider
if errorlevel 1 goto failed
uv run --extra dev python tests/verify_startup.py
if errorlevel 1 goto failed
uv run --locked --extra dev python scripts/build.py
if errorlevel 1 goto failed
echo Build completed: artifacts\release\qFrey-Tuner.exe
exit /b 0
:failed
echo Build stopped because a prerequisite, test or packaging check failed.
pause
exit /b 1
