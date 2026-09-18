#!/usr/bin/env python
"""Checks that every field of the FidelityFX descriptors the proxy fills is accounted for.

A `= {}` initialiser leaves an unset field without a compiler warning, a runtime
error or a log line -- `frameGenerationCallback`, without which the swapchain
never asks for an interpolated frame, or the camera basis vectors PrepareV2
requires. Every field of the descriptors is enumerated from the vendored headers
and matched against what FrameGeneration*.cpp assign. A field left out is listed
below with its reason, or this fails.

Usage:  python tools/audit_ffx_fields.py
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HEADER = os.path.join(ROOT, "src", "DxgiProxy", "extern", "FidelityFX",
                      "framegeneration", "include", "ffx_framegeneration.h")
# Frame generation's files, all of them: a descriptor filled in one of them counts.
IMPL = [os.path.join(ROOT, "src", "DxgiProxy", name)
        for name in ("FrameGeneration.cpp", "FrameGenerationInputs.cpp", "FrameGenerationCheck.cpp")]

# Fields left at their zero value, each with its reason.
INTENTIONALLY_UNSET = {
    "ffxConfigureDescFrameGeneration": {
        "presentCallback":
            "UI composition is handled through HUDLessColor, not a callback.",
        "presentCallbackUserContext":
            "Unused without presentCallback.",
        "onlyPresentGenerated":
            "false is the wanted behaviour: present real and generated frames.",
    },
    "ffxDispatchDescFrameGenerationPrepareV2": {
        "flags":
            "No debug dispatch flags in normal operation.",
    },
}

# Pointer and reference types are matched too: those are the fields most likely
# to be left null.
DECLARATION = re.compile(
    r"^\s*(?:struct\s+|const\s+|unsigned\s+)*[A-Za-z_][A-Za-z_0-9:]*"
    r"\s*[*&]*\s+"
    r"([A-Za-z_][A-Za-z_0-9]*)\s*(?:\[\s*\d+\s*\])?\s*;")

ASSIGNED_AS = {
    "ffxConfigureDescFrameGeneration": "config.",
    "ffxDispatchDescFrameGenerationPrepareV2": "prepare.",
}


def read(path):
    return io.open(path, encoding="utf-8", errors="replace").read()


def fields_of(text, struct):
    match = re.search(r"^struct\s+" + struct + r"\s*$", text, re.M)
    if match is None:
        raise SystemExit("structure not found in the header: " + struct)

    body = text[match.end():]
    body = body[:body.index("\n};")]

    names = []
    for line in body.split("\n"):
        line = line.split("///")[0].split("//")[0]
        hit = DECLARATION.match(line)
        if hit and hit.group(1) != "header":
            names.append(hit.group(1))
    return names


def main():
    header = read(HEADER)
    impl = "\n".join(read(path) for path in IMPL)

    failures = []
    for struct, prefix in ASSIGNED_AS.items():
        names = fields_of(header, struct)
        allowed = INTENTIONALLY_UNSET.get(struct, {})

        print(struct + "  (%d fields)" % len(names))
        for name in names:
            if (prefix + name) in impl:
                print("   set          %s" % name)
            elif name in allowed:
                print("   left unset   %-28s %s" % (name, allowed[name]))
            else:
                print("   MISSING      %s" % name)
                failures.append("%s.%s" % (struct, name))
        print("")

    if failures:
        print("Unaccounted fields, each of which will fail silently at run time:")
        for name in failures:
            print("   " + name)
        print("\nSet it, or add it to INTENTIONALLY_UNSET with a reason.")
        return 1

    print("Every descriptor field is either set or accounted for.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
