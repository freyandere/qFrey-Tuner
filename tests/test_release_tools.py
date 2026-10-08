import tomllib
from scripts import bump_version


def test_bump_changes_only_project_version(tmp_path, monkeypatch):
    path = tmp_path / 'pyproject.toml'
    source = '''# Keep comments and formatting
[project]
name = "demo"
version = "0.3.3" # release
dependencies = ["requests"]
[tool.example]
version = "8.0.0"
'''
    path.write_text(source, encoding='utf-8')
    monkeypatch.setattr(bump_version, 'PYPROJECT_PATH', path)
    assert bump_version.bump_version() == ('0.3.3', '0.3.4')
    updated = path.read_text(encoding='utf-8')
    assert updated == source.replace('version = "0.3.3"', 'version = "0.3.4"')
    assert tomllib.loads(updated)['tool']['example']['version'] == '8.0.0'


def test_publish_preserves_previous_executable_and_manifest(tmp_path, monkeypatch):
    import json
    import hashlib
    from scripts import build
    release = tmp_path / 'release'
    archive = tmp_path / 'archive'
    release.mkdir()
    (release / 'qFrey-Tuner.exe').write_bytes(b'previous exe')
    (release / 'build.json').write_text('{"version": "old"}')
    candidate = tmp_path / 'candidate.exe'
    candidate.write_bytes(b'new exe')
    monkeypatch.setattr(build, 'RELEASE', release)
    monkeypatch.setattr(build, 'ARCHIVE', archive)
    target = build.publish(candidate, '0.3.4')
    assert target.read_bytes() == b'new exe'
    saved = next(archive.iterdir())
    assert (saved / target.name).read_bytes() == b'previous exe'
    assert json.loads((saved / 'build.json').read_text())['version'] == 'old'
    manifest = json.loads((release / 'build.json').read_text())
    assert manifest['version'] == '0.3.4'
    assert manifest['sha256'] == hashlib.sha256(b'new exe').hexdigest()


def test_dotnet_candidate_cannot_publish_before_signoff(tmp_path, monkeypatch):
    import pytest
    from scripts import build
    calls = []
    candidate = tmp_path / 'qFrey-Tuner.exe'
    candidate.write_bytes(b'candidate')
    monkeypatch.setattr(build, 'build_candidate', lambda backend, version: calls.append(backend) or candidate)
    monkeypatch.setattr(build, 'publish', lambda *args: pytest.fail('candidate must not publish'))
    build.main(['--backend', 'dotnet', '--candidate-only'])
    assert calls == ['dotnet']
    with pytest.raises(SystemExit) as error:
        build.main(['--backend', 'dotnet'])
    assert error.value.code == 2
    assert calls == ['dotnet']


def test_failed_candidate_does_not_retain_previous_manifest(tmp_path, monkeypatch):
    import subprocess
    import pytest
    from scripts import build
    staging = tmp_path / 'staging'
    staging.mkdir()
    manifest = staging / 'build.json'
    manifest.write_text('{"sha256": "previous"}')
    monkeypatch.setattr(build, 'CACHE', tmp_path)

    def fail(*args, **kwargs):
        raise subprocess.CalledProcessError(3, args[0])

    monkeypatch.setattr(build.subprocess, 'run', fail)
    with pytest.raises(subprocess.CalledProcessError):
        build.build_candidate('python', '0.3.4')
    assert not manifest.exists()
