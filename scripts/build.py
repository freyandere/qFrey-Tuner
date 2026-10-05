"""Build and validate one Windows executable, then publish it to artifacts/release."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tomllib

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / '.cache'
RELEASE = ROOT / 'artifacts/release'
ARCHIVE = ROOT / 'artifacts/archive/builds'


def publish(executable, version):
    RELEASE.mkdir(parents=True, exist_ok=True)
    target = RELEASE / 'qFrey-Tuner.exe'
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    if target.exists():
        previous = ARCHIVE / stamp
        previous.mkdir(parents=True)
        shutil.copy2(target, previous / target.name)
        manifest = RELEASE / 'build.json'
        if manifest.exists():
            shutil.copy2(manifest, previous / manifest.name)
    # Replace on the same filesystem; a running/locked EXE fails without deleting it.
    executable.replace(target)
    manifest = dict(version=version, built_at_utc=stamp,
                    sha256=hashlib.sha256(target.read_bytes()).hexdigest())
    (RELEASE / 'build.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    return target


def main():
    staging = CACHE / 'staging'
    subprocess.run([sys.executable, '-m', 'PyInstaller', str(ROOT / 'qFrey-Tuner.spec'),
                    '--noconfirm', '--clean', '--distpath', str(staging),
                    '--workpath', str(CACHE / 'pyinstaller')], cwd=ROOT, check=True)
    executable = staging / 'qFrey-Tuner.exe'
    for check in ('--check', '--smoke-test'):
        subprocess.run([str(executable), check], cwd=ROOT, check=True)
    version = tomllib.loads((ROOT / 'pyproject.toml').read_text(encoding='utf-8'))['project']['version']
    print(publish(executable, version))


if __name__ == '__main__':
    main()
