"""Build and validate one Windows executable, then publish it to artifacts/release."""
from datetime import datetime, timezone
import argparse
import hashlib
import json
import os
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


def build_candidate(backend, version):
    staging = CACHE / 'staging'
    # A failed rebuild must not leave an old checksum beside a newly emitted candidate.
    (staging / 'build.json').unlink(missing_ok=True)
    if backend == 'dotnet':
        dotnet = CACHE / 'tools/dotnet/dotnet.exe'
        env = os.environ.copy()
        env.update(DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(CACHE / 'dotnet-home'),
                   DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false',
                   NUGET_PACKAGES=str(CACHE / 'nuget/packages'), NUGET_HTTP_CACHE_PATH=str(CACHE / 'nuget/http'))
        pnpm = shutil.which('pnpm.cmd') or shutil.which('pnpm')
        if not pnpm or not dotnet.is_file():
            raise RuntimeError('Prepare the local .NET SDK and pnpm first (scripts/dev-env.ps1).')
        for args in (['install', '--frozen-lockfile'], ['run', 'typecheck'], ['run', 'test:unit'], ['run', 'build']):
            subprocess.run([pnpm, '--dir', str(ROOT / 'frontend'), *args], cwd=ROOT, env=env, check=True)
        project = ROOT / 'src/QFrey.Desktop/QFrey.Desktop.csproj'
        subprocess.run([str(dotnet), 'restore', str(project), '--locked-mode'], cwd=ROOT, env=env, check=True)
        subprocess.run([str(dotnet), 'restore', str(ROOT / 'tests-dotnet/QFrey.Tests/QFrey.Tests.csproj'),
                        '--locked-mode'], cwd=ROOT, env=env, check=True)
        subprocess.run([str(dotnet), 'test', str(ROOT / 'tests-dotnet/QFrey.Tests/QFrey.Tests.csproj'),
                        '--no-restore'], cwd=ROOT, env=env, check=True)
        subprocess.run([str(dotnet), 'publish', str(project), '--no-restore', '-c', 'Release',
                        '-o', str(staging), '-p:Version=' + version], cwd=ROOT, env=env, check=True)
    else:
        subprocess.run([sys.executable, '-m', 'PyInstaller', str(ROOT / 'qFrey-Tuner.spec'),
                    '--noconfirm', '--clean', '--distpath', str(staging),
                    '--workpath', str(CACHE / 'pyinstaller')], cwd=ROOT, check=True)
    executable = staging / 'qFrey-Tuner.exe'
    for check in ('--check', '--smoke-test'):
        subprocess.run([str(executable), check], cwd=ROOT, check=True, timeout=90)
    return executable


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--backend', choices=('python', 'dotnet'), default='python')
    parser.add_argument('--candidate-only', action='store_true')
    args = parser.parse_args(argv)
    # G5 is not complete: a shell must never replace the current working application.
    if args.backend == 'dotnet' and not args.candidate_only:
        parser.error('The .NET migration requires --candidate-only until G5 signoff.')
    version = tomllib.loads((ROOT / 'pyproject.toml').read_text(encoding='utf-8'))['project']['version']
    executable = build_candidate(args.backend, version)
    if args.candidate_only:
        manifest = dict(version=version, built_at_utc=datetime.now(timezone.utc).isoformat(),
                        sha256=hashlib.sha256(executable.read_bytes()).hexdigest(), backend=args.backend,
                        candidate_only=True)
        (executable.parent / 'build.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(executable if args.candidate_only else publish(executable, version))


if __name__ == '__main__':
    main()
