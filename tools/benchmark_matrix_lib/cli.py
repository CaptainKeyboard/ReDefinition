"""The command line: run, plan, parse and render, with the options they share."""
import argparse
import json
import os
import sys

from .constants import DEFAULT_KSP, DEFAULT_MATRIX, DEFAULT_SCENE, REPO
from .machine import machine_record, vendor_of
from .matrix import expand, group_list, load_matrix, select_groups
from .probe_plan import estimate_seconds
from .record import build_record
from .report import render, write_results
from .runner import resources_of, run
from .scenarios import scenario_label

# The first paragraph of tools/benchmark_matrix.py's text, which the command line's help opens with.
DESCRIPTION = "The standing comparison of upscalers, frame generation, raytracing and latency models."


def plan_command(args):
    """The plan command: prints each start with its settings, proxy values and phases, what is skipped,
    and per group the starts, phases and minutes, without running anything."""
    matrix = load_matrix(args.matrix)
    resources = resources_of(args)
    vendor = args.vendor or vendor_of(machine_record())
    try:
        groups = select_groups(matrix, args.group)
        starts, skipped = expand(matrix, vendor, resources, [g["id"] for g in groups])
    except ValueError as e:
        sys.exit(str(e))
    if args.only:
        starts = [s for s in starts if any(o in s["id"] for o in args.only)]
    for s in starts:
        print("{}  ({} phases{}{})".format(s["id"], len(s["phases"]), ", DLSS-G package" if s["dlssg_package"] else "",
                                          ", scene " + s["scene"] if s.get("scene") not in (None, DEFAULT_SCENE) else ""))
        print("    settings.cfg: " + ", ".join("{} = {}".format(k, v) for k, v in s["mod_settings"].items()))
        print("    proxy at load: " + ", ".join("{}={}".format(k, v) for k, v in s["proxy_load"].items()))
        if s.get("dlssg_package"):
            print("    package ini: MaxGeneratedFrames={} {}".format(s["max_generated_frames"], json.dumps(s.get("dlssg_ini") or {})))
        for p in s["phases"]:
            print("    " + scenario_label(p["scenario"]) + ("   (rebuilt, {} s settling)".format(p["settle"]) if p.get("rebuild") else "")
                  + "   [" + ",".join("{}={}".format(k, v) for k, v in p["keys"].items()) + "]")
    for what, why in skipped:
        print("skipped: {} ({})".format(what, why))
    print("")
    print("{:<16} {:>7} {:>7} {:>9}".format("group", "starts", "phases", "minutes"))
    for g in groups:
        mine = [s for s in starts if s.get("group") == g["id"]]
        print("{:<16} {:>7} {:>7} {:>9.0f}   {}".format(g["id"], len(mine), sum(len(s["phases"]) for s in mine),
                                                       estimate_seconds(mine, False) / 60.0, g.get("name", "")))
    print("{} starts, {} phases, about {:.0f} minutes with the profile start (vendor {}); the other groups: {}".format(
        len(starts), sum(len(s["phases"]) for s in starts), estimate_seconds(starts) / 60.0, vendor or "unknown",
        ", ".join(g["id"] for g in group_list(matrix) if g["id"] not in [x["id"] for x in groups]) or "none"))


def parse_command(args):
    """The parse command: a run's raw folder read again into its record, written with the page."""
    record = build_record(os.path.abspath(args.raw_folder))
    write_results(record, args.no_doc)


def main(argv=None):
    """The command line: run, plan, parse and render, with the options they share."""
    ap = argparse.ArgumentParser(
        description=DESCRIPTION,
        epilog="Do not use the machine while a run is going: the game's window is kept in front (DLSS-G generates no\n"
               "frame otherwise), any program or key press in the meantime moves the frame rate and the latency the\n"
               "run measures, and the GPU is shared with whatever else uses it. REDEFINITION_BENCH_NO_FOREGROUND=1 leaves\n"
               "the window where Windows puts it.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)

    def common(p):
        p.add_argument("--matrix", default=DEFAULT_MATRIX, help="the scenario matrix (default tools/benchmark_matrix.json)")
        p.add_argument("--ksp", default=os.environ.get("REDEFINITION_KSP", DEFAULT_KSP), help="the KSP folder")
        p.add_argument("--dlssg-package", default=os.environ.get("REDEFINITION_DLSSG_PACKAGE", os.path.join(REPO, ".local", "dlssg_x")),
                       help="the unpacked DLSS-G package for Ampere (version.dll, dlssg_sm86.ini, dlssg_sm86/)")
        p.add_argument("--amd-dll-dir", default=os.environ.get("REDEFINITION_AMD_DLL_DIR", os.path.join(REPO, ".local", "amd")),
                       help="the folder with amd_fidelityfx_upscaler_dx12.dll, handed to the proxy as amdUpscalerDirectory")
        p.add_argument("--xess-dll-dir", default=os.environ.get("REDEFINITION_XESS_DLL_DIR", os.path.join(REPO, ".local", "xess")),
                       help="the folder with Intel's libxess.dll, handed to the proxy as xessDirectory")
        p.add_argument("--xess-fg-dll-dir", default=os.environ.get("REDEFINITION_XESS_FG_DLL_DIR", os.path.join(REPO, ".local", "xessfg")),
                       help="the folder with Intel's libxess_fg.dll and libxell.dll, handed to the proxy as xessFgDirectory")
        p.add_argument("--vendor", choices=["NVIDIA", "AMD", "Intel"], help="the GPU's vendor, if not the one Windows names")
        p.add_argument("--only", action="append", help="only the starts whose id contains this (repeatable)")
        p.add_argument("--group", action="append",
                       help="a group of the matrix to cover, repeatable, or all (default: the main matrix; the groups are"
                            " listed by plan, each with its starts, phases and minutes)")

    p = sub.add_parser("run", help="run the default group, or the --group ones, in KSP and write the results")
    common(p)
    p.add_argument("--settings-template", help="the mod's settings.cfg to start from (default: the install's, as it was)")
    p.add_argument("--raw", default=os.environ.get("REDEFINITION_BENCH_RAW", os.path.join(REPO, ".local", "benchmarks")),
                   help="where the logs and screenshots of each start go")
    p.add_argument("--no-profile", action="store_true", help="skip the start that applies the profile")
    p.add_argument("--no-doc", action="store_true", help="write the JSON only, not benchmarks.md")
    p.set_defaults(func=run)
    p = sub.add_parser("plan", help="print the starts, the phases and per group the minutes, without running anything")
    common(p)
    p.set_defaults(func=plan_command)
    p = sub.add_parser("parse", help="read a run's raw folder again and write its results")
    p.add_argument("raw_folder")
    p.add_argument("--no-doc", action="store_true")
    p.set_defaults(func=parse_command)
    p = sub.add_parser("render", help="write benchmarks.md from docs/development/benchmarks/*.json")
    p.set_defaults(func=render)
    args = ap.parse_args(argv)
    args.func(args)
