#!/usr/bin/env python3
"""Re-syncs the game's weather generator with the offline proof's core. Stdlib only.

  python tools/sync_generator_core.py            record the golden values from the core and run the tests of the game's assembly
  python tools/sync_generator_core.py --check    record into a temporary folder and compare with tests/data/generator (no change, exit 1 on a difference)
  python tools/sync_generator_core.py --record   record only
  python tools/sync_generator_core.py --test     run only the tests

The core is tools/atmosphere-sim/AtmoSim.Core. src/Atmospheres/ReDefinition.Atmospheres.csproj compiles its source files into the game's
assembly by a pattern (no copy), so after the core changed (a tuning round merged) the assembly already has the new algorithm; what is left is
to record the golden values of the tests (tests/data/generator/golden.json and proof-lat.bin) from the core by the recorder
tools/generator_golden and to run the tests that compare the game's assembly with them. One command, no hand editing.
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
CORE = os.path.join(HERE, "atmosphere-sim", "AtmoSim.Core")
GOLDEN_DIR = os.path.join(ROOT, "tests", "data", "generator")
RECORDER = os.path.join(HERE, "generator_golden", "GeneratorGolden.csproj")
CSPROJ = os.path.join(ROOT, "src", "Atmospheres", "ReDefinition.Atmospheres.csproj")
DOTNET = r"C:\Users\franz\.dotnet\dotnet.exe"
FILES = ("golden.json", "proof-lat.bin")


def dotnet():
    return DOTNET if os.path.exists(DOTNET) else "dotnet"


def link_is_wired():
    """The project compiles the whole core folder by a pattern, so no file of the core can be left out."""
    with open(CSPROJ, encoding="utf-8") as f:
        text = f.read()
    return "tools\\atmosphere-sim\\AtmoSim.Core\\**\\*.cs" in text and "Generator\\Core\\" in text


def record(into):
    os.makedirs(into, exist_ok=True)
    cmd = [dotnet(), "run", "--project", RECORDER, "-c", "Release", "--", CORE, into]
    result = subprocess.run(cmd, cwd=ROOT)
    return result.returncode == 0


def read(path):
    """The bytes of a file; the text file golden.json is compared with line ends normalised (a CRLF checkout equals an LF one)."""
    with open(path, "rb") as f:
        data = f.read()
    if path.endswith(".json"):
        data = data.replace(b"\r\n", b"\n")
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--record", action="store_true")
    parser.add_argument("--test", action="store_true")
    args = parser.parse_args()

    if not link_is_wired():
        print("the project does not compile the core by the pattern any more: " + CSPROJ)
        return 1
    do_record = args.record or not (args.check or args.test)
    do_test = args.test or not (args.check or args.record)

    if args.check:
        tmp = tempfile.mkdtemp(prefix="generator-golden-")
        try:
            if not record(tmp):
                return 1
            stale = [name for name in FILES if not os.path.exists(os.path.join(GOLDEN_DIR, name)) or read(os.path.join(tmp, name)) != read(os.path.join(GOLDEN_DIR, name))]
            if stale:
                print("tests/data/generator is out of date for: " + ", ".join(stale) + " (run tools/sync_generator_core.py)")
                return 1
            print("tests/data/generator matches the core")
            return 0
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    if do_record and not record(GOLDEN_DIR):
        return 1
    if do_test:
        cmd = [sys.executable, os.path.join(HERE, "dotnet_test.py"), "--filter", "FullyQualifiedName~Atmospheres.C25"]
        return subprocess.run(cmd, cwd=ROOT).returncode
    return 0


if __name__ == "__main__":
    sys.exit(main())
