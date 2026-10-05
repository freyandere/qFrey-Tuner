import tomllib
from pathlib import Path

pyproject = Path(__file__).parent / "pyproject.toml"
with pyproject.open("rb") as f:
    version = tomllib.load(f)["project"]["version"]
print(f"pyproject version: {version}")

# Simulate _get_version path traversal
pyproject2 = Path(__file__).parent / "ui/main_window.py"
pyproject_path = pyproject2.parent.parent / "pyproject.toml"
if pyproject_path.exists():
    with pyproject_path.open("rb") as f:
        print(f"read via path traversal: {tomllib.load(f)['project']['version']}")
else:
    print("fallback: 0.3.0")
