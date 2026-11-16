#!/usr/bin/env python3
"""Reads a KSP.log (or a series log, build/atmo-series/*.log) and says whether the active vessel was torn apart and whether Parsek recorded it.

  python -I tools/checks/vessel_break_check.py <log> [<log> ...]       exit 0: no break, exit 2: a break or a Parsek recording was found
  python -I tools/checks/vessel_break_check.py --selftest

The lines it counts (all from the logs of 2026-11-15/16, see .local/plans/atmospheres/surface3-viewer-placement-plausibility-v2.md):
  ground contact! - error   the game snapped a vessel that was moved while packed ('Moving Vessel down -208.829m')
  EXPLODE                   Collision Enhancer exploded a part
  debris vessels            'Generating data for Kerbal X Debris' (the PR lines), 'Decouple created vessel', 'JointBreak'
  Parsek recording          'Captured rewind save' / 'Recording started' (a rewind save was written into the save, a recording tree started)
A run that places nothing (K-T3 t18) has none of them; the viewer's placements before the fix had all.
"""
import re
import sys

PATTERNS = (
    ("ground contact error", re.compile(r"ground contact! - error\. Moving Vessel")),
    ("explode", re.compile(r"\[Collision Enhancer\] EXPLODE on")),
    ("debris data", re.compile(r"\[PR\] Generating data for .* Debris")),
    ("decouple created vessel", re.compile(r"Decouple created vessel during recording")),
    ("joint break", re.compile(r"OnPartJointBreak: .*(structural=T|JointBreak)")),
    ("Parsek rewind save", re.compile(r"\[Parsek\]\[INFO\]\[Recorder\] Captured rewind save")),
    ("Parsek recording started", re.compile(r"\[Parsek\]\[INFO\]\[Recorder\] Recording started")),
)
# Counted, not judged: how many placements the viewer or the T09 probe made.
PLACEMENTS = re.compile(r"cloud viewer: vessel placed|T09 view placed|Atmospheres: vessel hold: ")


def read(path):
    counts = {name: 0 for name, _ in PATTERNS}
    placements = 0
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            for name, rx in PATTERNS:
                if rx.search(line):
                    counts[name] += 1
            if PLACEMENTS.search(line):
                placements += 1
    return counts, placements


def verdict(counts):
    """The names that are not zero; empty means the vessel and the save are untouched."""
    return [n for n, c in counts.items() if c]


def report(path):
    counts, placements = read(path)
    found = verdict(counts)
    lines = ["%s: %d placement lines" % (path, placements)]
    for name, _ in PATTERNS:
        lines.append("  %-26s %d" % (name, counts[name]))
    lines.append("  RESULT: %s" % ("PASS (no break, no Parsek recording)" if not found else "FAIL (" + ", ".join(found) + ")"))
    return "\n".join(lines), not found


def selftest():
    import os
    import tempfile
    broken = "\n".join([
        "[LOG 19:05:44.005] [ReDefinition] Atmospheres: cloud viewer: vessel placed 200 m above the ground (265 m) for the ground pose",
        "[LOG 19:05:47.491] [Kerbal X]: ground contact! - error. Moving Vessel  down -208.829m",
        '[LOG 19:05:47.864] [Collision Enhancer] EXPLODE on "HeatShield2" (vel: 8080)',
        "[LOG 19:05:47.737] [PR] Generating data for Kerbal X Debris",
        "[LOG 16:03:32.732] [Parsek][INFO][Recorder] Captured rewind save at UT 1881021.16: vessel \"Kerbal X\" in LANDED (save: parsek_rw_2b44fe)",
    ])
    clean = "[LOG 10:00:00.000] [ReDefinition] Atmospheres: cloud viewer: vessel placed 300 m above the ground (365 m) for the cb_far pose\n[LOG 10:00:01.000] nothing else\n"
    ok = True
    with tempfile.TemporaryDirectory() as d:
        for name, text, want in (("broken.log", broken, False), ("clean.log", clean, True)):
            p = os.path.join(d, name)
            with open(p, "w", encoding="utf-8") as f:
                f.write(text)
            _text, passed = report(p)
            ok &= passed == want
    print("selftest: %s" % ("PASSED" if ok else "FAILED"))
    return 0 if ok else 1


def main(argv):
    if len(argv) >= 2 and argv[1] == "--selftest":
        return selftest()
    if len(argv) < 2:
        print(__doc__)
        return 1
    worst = 0
    for path in argv[1:]:
        text, passed = report(path)
        print(text)
        if not passed:
            worst = 2
    return worst


if __name__ == "__main__":
    sys.exit(main(sys.argv))
