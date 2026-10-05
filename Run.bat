@echo off
setlocal
chcp 65001 >nul
title qFrey-Tuner
cd /d "%~dp0"

if exist "outputs\qFrey-Tuner_v0.3.3.exe" (
    start "" "outputs\qFrey-Tuner_v0.3.3.exe"
    exit /b 0
)
if exist "outputs\qFrey-Tuner_v0.3.2.exe" (
    start "" "outputs\qFrey-Tuner_v0.3.2.exe"
    exit /b 0
)
if exist "outputs\qFrey-Tuner.exe" (
    start "" "outputs\qFrey-Tuner.exe"
    exit /b 0
)
if exist "qFrey-Tuner.exe" (
    start "" "qFrey-Tuner.exe"
    exit /b 0
)

if exist ".venv\Scripts\python.exe" (
    ".venv\Scripts\python.exe" main.py --check
    if not errorlevel 1 (
        ".venv\Scripts\python.exe" main.py
        goto finished
    )
    echo The existing virtual environment is unavailable or incomplete.
)
where uv >nul 2>nul
if not errorlevel 1 (
    uv run python main.py
    goto finished
)
where py >nul 2>nul
if not errorlevel 1 (
    py -3 main.py --check
    if not errorlevel 1 (
        py -3 main.py
        goto finished
    )
)
where python >nul 2>nul
if not errorlevel 1 (
    python main.py --check
    if not errorlevel 1 (
        python main.py
        goto finished
    )
)
echo.
echo qFrey-Tuner could not start. No working source runtime was found.
echo Easiest option: download and run the standalone Windows EXE:
echo https://github.com/freyandere/qFrey-Tuner/releases
echo Source option: install Python 3.11+ and then run:
echo   python -m pip install .
echo   python main.py
echo Alternatively install uv from https://docs.astral.sh/uv/getting-started/installation/
echo and run: uv run python main.py
pause
exit /b 1

:finished
if errorlevel 1 (
    echo Application startup failed. See the error above and README.md.
    pause
    exit /b 1
)
exit /b 0
