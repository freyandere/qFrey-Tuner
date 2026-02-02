@echo off
chcp 65001 >nul
title qBittorrent Optimizer
cd /d "%~dp0"
echo Запуск qBittorrent Optimizer...
uv run python main.py
pause
