#!/usr/bin/env python
"""Fetches AMD's FidelityFX runtimes for the player package.

The package ships amd_fidelityfx_framegeneration_dx12.dll, for frame generation, and
amd_fidelityfx_denoiser_dx12.dll, AMD FSR Ray Regeneration for the raytracing, next to
KSP_x64.exe, where the proxy (dxgi.dll) loads them (docs/development/packaging.md).
They are AMD's signed binaries, unmodified, redistributed under AMD's licence, which
the package reproduces next to them (licenses/AMD-FidelityFX-SDK-license.md). The DLLs
themselves are not kept in this repository: this script downloads exactly the pinned
files from AMD's FidelityFX SDK release and checks them byte for byte against the
SHA-256 in third_party/amd/, which the package build checks as well.

Usage:  python tools/fetch_amd_runtime.py
"""
import hashlib
import os
import sys
import urllib.request

SDK_TAG = "v2.3.0"   # FSR SDK v2.3.0, 2026-06-24 -- see third_party/amd/README.md
FILES = ["amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_denoiser_dx12.dll"]
BASE = ("https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/"
        + SDK_TAG + "/Kits/FidelityFX/signedbin/")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGET_DIR = os.path.join(ROOT, "third_party", "amd")


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch(name):
    target = os.path.join(TARGET_DIR, name)
    with open(target + ".sha256", "r", encoding="ascii") as f:
        pinned = f.read().strip().lower()

    if os.path.exists(target) and sha256(target) == pinned:
        print("Already there and matching the pin: " + target)
        return 0

    part = target + ".part"
    print("Downloading " + BASE + name)
    urllib.request.urlretrieve(BASE + name, part)
    got = sha256(part)
    if got != pinned:
        os.remove(part)
        print("FAIL  the download's SHA-256 is " + got + ", the pin says " + pinned
              + ". Nothing was kept.")
        return 1

    os.replace(part, target)
    print("Fetched and checked: " + target)
    return 0


def main():
    return max(fetch(name) for name in FILES)


if __name__ == "__main__":
    sys.exit(main())
