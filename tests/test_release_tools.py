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
