#!/usr/bin/env python3
"""Fails when a shipped sound file has no complete line in SOUNDS.json (package B15). Stdlib only.

  python tools/audio/check_manifest.py [--dir GameData/ReDefinition/Atmospheres/Sounds] [--max-download 4000000] [--max-decoded 12000000]
  python tools/audio/check_manifest.py --selftest

Exit code 0 when
  - SOUNDS.json and LICENSE-CC0.txt exist in the folder,
  - every sound file there (.ogg .wav .mp3 .flac .aif .aiff) has a line, and every line has its file,
  - every line carries file, source, derivedFrom, author, sourceUrl, archivedUrl (a web.archive.org address), licence, licenceLine,
    dateRead (YYYY-MM-DD) and processing, none of them empty, and the licence names CC0,
  - the files together are within the download limit and their decoded size (decodedBytes) within the decoded limit.
Otherwise it prints one line per problem and exits with 1.
"""
import argparse
import json
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_DIR = os.path.join(REPO, "GameData", "ReDefinition", "Atmospheres", "Sounds")
SOUND_EXT = (".ogg", ".wav", ".mp3", ".flac", ".aif", ".aiff")
REQUIRED = ("file", "source", "derivedFrom", "author", "sourceUrl", "archivedUrl", "licence", "licenceLine", "dateRead", "processing")


def check(folder, max_download=4000000, max_decoded=12000000):
    problems = []
    manifest_path = os.path.join(folder, "SOUNDS.json")
    if not os.path.isfile(os.path.join(folder, "LICENSE-CC0.txt")):
        problems.append("LICENSE-CC0.txt is missing")
    if not os.path.isfile(manifest_path):
        problems.append("SOUNDS.json is missing")
        return problems
    try:
        with open(manifest_path, encoding="utf8") as fh:
            document = json.load(fh)
            lines = document.get("files", [])
            sources = document.get("sources", {})
    except (ValueError, OSError) as e:
        return problems + ["SOUNDS.json cannot be read: %s" % e]
    on_disk = sorted(f for f in os.listdir(folder) if f.lower().endswith(SOUND_EXT))
    listed = {}
    for entry in lines:
        name = entry.get("file", "")
        for key in REQUIRED:
            value = entry.get(key)
            if not isinstance(value, str) or not value.strip():
                problems.append("%s: field '%s' is missing or empty" % (name or "<no file>", key))
        source = sources.get(entry.get("source", "")) if isinstance(sources, dict) else None
        if not isinstance(source, dict) or not str(source.get("pageText", "")).strip():
            problems.append("%s: the source '%s' has no copied page text in 'sources'" % (name, entry.get("source", "")))
        elif source.get("licenceLine", "") not in source.get("pageText", ""):
            problems.append("%s: the licence line is not part of the copied page text" % name)
        if "CC0" not in str(entry.get("licence", "")):
            problems.append("%s: the licence is not CC0" % name)
        if "web.archive.org" not in str(entry.get("archivedUrl", "")):
            problems.append("%s: archivedUrl is not a web.archive.org address" % name)
        if not re.match(r"^\d{4}-\d{2}-\d{2}$", str(entry.get("dateRead", ""))):
            problems.append("%s: dateRead is not YYYY-MM-DD" % name)
        if name in listed:
            problems.append("%s: listed twice" % name)
        listed[name] = entry
    for name in on_disk:
        if name not in listed:
            problems.append("%s: shipped without a line in SOUNDS.json" % name)
    for name in listed:
        if name and name not in on_disk:
            problems.append("%s: listed but not in the folder" % name)
    download = sum(os.path.getsize(os.path.join(folder, n)) for n in on_disk)
    decoded = sum(int(e.get("decodedBytes", 0)) for e in lines)
    if download > max_download:
        problems.append("the files are %d bytes, over the download limit of %d" % (download, max_download))
    if decoded > max_decoded:
        problems.append("the files decode to %d bytes, over the limit of %d" % (decoded, max_decoded))
    return problems


def selftest():
    good = {k: "x" for k in REQUIRED}
    good.update(file="a.ogg", licence="CC0 1.0", archivedUrl="http://web.archive.org/web/1/https://x", dateRead="2026-11-11", decodedBytes=10)
    tmp = tempfile.mkdtemp()
    try:
        def make(entry, with_file=True):
            for f in os.listdir(tmp):
                os.remove(os.path.join(tmp, f))
            open(os.path.join(tmp, "LICENSE-CC0.txt"), "w").write("x")
            if with_file:
                open(os.path.join(tmp, "a.ogg"), "wb").write(b"1234")
            json.dump({"sources": {"x": {"licenceLine": "L", "pageText": "page with L"}}, "files": [entry] if entry else []},
                      open(os.path.join(tmp, "SOUNDS.json"), "w"))
            return check(tmp)
        assert make(good) == [], make(good)
        no_line = dict(good, licenceLine="")
        assert any("licenceLine" in p for p in make(no_line))
        assert any("shipped without a line" in p for p in make(None))
        assert any("not in the folder" in p for p in make(good, with_file=False))
        assert any("not CC0" in p for p in make(dict(good, licence="CC-BY 3.0")))
        assert any("no copied page text" in p for p in make(dict(good, source="other")))
        assert any("web.archive.org" in p for p in make(dict(good, archivedUrl="https://x")))
    finally:
        shutil.rmtree(tmp)
    print("check_manifest selftest ok")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=DEFAULT_DIR)
    ap.add_argument("--max-download", type=int, default=4000000)
    ap.add_argument("--max-decoded", type=int, default=12000000)
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        selftest()
        return 0
    problems = check(a.dir, a.max_download, a.max_decoded)
    for p in problems:
        print(p)
    if problems:
        return 1
    print("manifest ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
