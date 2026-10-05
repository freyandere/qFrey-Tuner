# -*- mode: python ; coding: utf-8 -*-
import os
import tomllib
from pathlib import Path
from PyInstaller.config import CONF

root = Path(SPECPATH).resolve()
if Path(CONF['distpath']).resolve() != root / '.cache/staging' or Path(CONF['workpath']).resolve() != root / '.cache/pyinstaller/qFrey-Tuner':
    raise SystemExit('Build with python scripts/build.py (or build.bat); output paths are fixed by the build contract.')

# Load version from pyproject.toml
with open('pyproject.toml', 'rb') as f:
    config = tomllib.load(f)
    version = config.get('project', {}).get('version', 'unknown')

block_cipher = None

from PyInstaller.utils.hooks import collect_all

datas = [('pyproject.toml', '.')]
binaries = []
hiddenimports = []
tmp_ret = collect_all('customtkinter')
datas += tmp_ret[0]; binaries += tmp_ret[1]; hiddenimports += tmp_ret[2]
a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    # Pillow probes optional scientific packages; the UI only uses Tk Canvas.
    excludes=['PyQt6', 'PyQt6.QtCore', 'PyQt6.QtGui', 'PyQt6.QtWidgets', 'matplotlib', 'numpy'],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe_name = 'qFrey-Tuner'

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name=exe_name,
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=None,
)

