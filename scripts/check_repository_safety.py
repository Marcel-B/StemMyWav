"""Fail CI if local artifacts or recognizable credentials enter the repository."""

from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
listed = subprocess.check_output(
    ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=ROOT
).split(b"\0")
forbidden_parts = {".venv", ".models", ".pip-cache", "test-results", "secrets", "bin", "obj", "publish"}
forbidden_names = {".env", "id_rsa", "id_ed25519"}
forbidden_suffixes = {".pem", ".p12", ".pfx", ".key", ".ckpt"}
secret_patterns = [
    re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    re.compile(rb"ghp_[A-Za-z0-9]{30,}"),
    re.compile(rb"github_pat_[A-Za-z0-9_]{60,}"),
    re.compile(rb"tskey-[A-Za-z0-9_-]{20,}"),
]
errors = []

for raw in filter(None, listed):
    relative = Path(raw.decode())
    file = ROOT / relative
    if not file.is_file():
        continue
    if (set(relative.parts) & forbidden_parts or relative.name in forbidden_names
            or relative.suffix.lower() in forbidden_suffixes):
        errors.append(f"Forbidden artifact: {relative}")
        continue
    if file.stat().st_size > 10 * 1024 * 1024:
        errors.append(f"File exceeds 10 MiB: {relative}")
        continue
    if file.stat().st_size <= 1024 * 1024:
        content = file.read_bytes()
        if any(pattern.search(content) for pattern in secret_patterns):
            errors.append(f"Possible credential: {relative}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    sys.exit(1)
print("Repository artifact and credential checks passed.")
