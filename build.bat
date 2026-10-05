@echo off
setlocal
cd /d "%~dp0"
where uv >nul 2>nul
if errorlevel 1 (
    echo Building requires uv. Install it from https://docs.astral.sh/uv/getting-started/installation/
    echo Or use Python manually: python -m pip install ".[dev]"
    echo Then: python -m pytest tests/ and pyinstaller qFrey-Tuner.spec
    pause
    exit /b 1
)
uv sync --extra dev
if errorlevel 1 goto failed
uv run --extra dev python main.py --check
if errorlevel 1 goto failed
uv run --extra dev python -m pytest tests/ -q -p no:cacheprovider
if errorlevel 1 goto failed
uv run --extra dev python tests/verify_startup.py
if errorlevel 1 goto failed
uv run --extra dev pyinstaller qFrey-Tuner.spec --noconfirm --clean
if errorlevel 1 goto failed
echo Build completed. The versioned executable is in dist/.
exit /b 0
:failed
echo Build stopped because a prerequisite, test or packaging check failed.
pause
exit /b 1
