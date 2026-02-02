import argparse
import toml
import os
from datetime import datetime

PYPROJECT_PATH = "pyproject.toml"
CHANGELOG_PATH = "CHANGELOG.md"

def bump_version(part='patch'):
    with open(PYPROJECT_PATH, 'r', encoding='utf-8') as f:
        data = toml.load(f)
    
    current_version = data['project']['version']
    major, minor, patch = map(int, current_version.split('.'))
    
    if part == 'major':
        major += 1
        minor = 0
        patch = 0
    elif part == 'minor':
        minor += 1
        patch = 0
    else:
        patch += 1
        
    new_version = f"{major}.{minor}.{patch}"
    data['project']['version'] = new_version
    
    with open(PYPROJECT_PATH, 'w', encoding='utf-8') as f:
        toml.dump(data, f)
        
    return current_version, new_version

def update_changelog(new_version, message):
    date = datetime.now().strftime("%Y-%m-%d")
    header = f"## [{new_version}] - {date}\n"
    content = f"- {message}\n\n"
    
    if os.path.exists(CHANGELOG_PATH):
        with open(CHANGELOG_PATH, 'r', encoding='utf-8') as f:
            old_content = f.read()
    else:
        old_content = "# Changelog\n\n"
        
    with open(CHANGELOG_PATH, 'w', encoding='utf-8') as f:
        f.write(old_content.replace("# Changelog\n\n", f"# Changelog\n\n{header}{content}"))

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument('part', choices=['major', 'minor', 'patch'], default='patch')
    parser.add_argument('--msg', required=True, help="Release message for changelog")
    args = parser.parse_args()
    
    old_v, new_v = bump_version(args.part)
    update_changelog(new_v, args.msg)
    print(f"Bumped version from {old_v} to {new_v}")
