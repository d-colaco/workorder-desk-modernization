#!/usr/bin/env python3
"""Download AdventureWorks2022.bak into data/ and verify its pinned SHA-256."""
import hashlib
import sys
import urllib.request
from pathlib import Path

URL = ("https://github.com/microsoft/sql-server-samples/releases/download/"
       "adventureworks/AdventureWorks2022.bak")
SHA256 = "d17567adb1521f972e1dc183a7216cea869c4580b5d75632425bcadbaf82ce5e"
TARGET = Path(__file__).resolve().parent.parent / "data" / "AdventureWorks2022.bak"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    TARGET.parent.mkdir(exist_ok=True)
    if TARGET.exists() and sha256(TARGET) == SHA256:
        print(f"ok: {TARGET} already present")
        return 0
    print(f"downloading {URL}")
    urllib.request.urlretrieve(URL, TARGET)
    actual = sha256(TARGET)
    if actual != SHA256:
        TARGET.unlink()
        print(f"checksum mismatch: expected {SHA256}, got {actual}", file=sys.stderr)
        return 1
    print(f"ok: {TARGET}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
