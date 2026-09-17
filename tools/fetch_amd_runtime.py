#!/usr/bin/env python
"""Fetches AMD's FidelityFX frame generation runtime for the player package.

The package ships amd_fidelityfx_framegeneration_dx12.dll next to KSP_x64.exe,
where the proxy (dxgi.dll) loads it (docs/development/packaging.md). It is AMD's signed
binary, unmodified, redistributed under AMD's licence, which the package
reproduces next to it (licenses/AMD-FidelityFX-SDK-license.md). The DLL itself
is not kept in this repository: this script downloads exactly the pinned file
from AMD's FidelityFX SDK release and checks it byte for byte against the
SHA-256 in third_party/amd/, which the package build checks as well.

Usage:  python tools/fetch_amd_runtime.py
"""
import hashlib
import os
import sys
import urllib.request

SDK_TAG = "v2.3.0"   # FSR SDK v2.3.0, 2026-06-24 -- see third_party/amd/README.md
FILE = "amd_fidelityfx_framegeneration_dx12.dll"
URL = ("https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/"
       + SDK_TAG + "/Kits/FidelityFX/signedbin/" + FILE)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGET_DIR = os.path.join(ROOT, "third_party", "amd")
TARGET = os.path.join(TARGET_DIR, FILE)
PIN = os.path.join(TARGET_DIR, FILE + ".sha256")


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main():
    with open(PIN, "r", encoding="ascii") as f:
        pinned = f.read().strip().lower()

    if os.path.exists(TARGET) and sha256(TARGET) == pinned:
        print("Already there and matching the pin: " + TARGET)
        return 0

    part = TARGET + ".part"
    print("Downloading " + URL)
    urllib.request.urlretrieve(URL, part)
    got = sha256(part)
    if got != pinned:
        os.remove(part)
        print("FAIL  the download's SHA-256 is " + got + ", the pin says " + pinned
              + ". Nothing was kept.")
        return 1

    os.replace(part, TARGET)
    print("Fetched and checked: " + TARGET)
    return 0


if __name__ == "__main__":
    sys.exit(main())
