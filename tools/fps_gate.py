"""The fps gate (owner 2026-11-27): every view of every sky kind holds 48 fps, before every commit that touches the cloud pass.

    python tools/fps_gate.py run [--who <id>]      keeps the pictures of the runs before, runs the gate's starts one after the other, prints the table
    python tools/fps_gate.py report                the table from the newest log of each start (no run)
    python tools/fps_gate.py record <commit>       appends the newest logs' numbers to the history under that commit (after the commit is made)

The starts are those of series K-T2 that hold one sky kind each: the standard sky, the dense sky under rain, the storm, the view from space. A package that was
measured on one of them alone is not measured: between 2026-11-23 and 2026-11-26 the dense sky fell from 54 to 40 fps and nobody saw it for four days, because
every change was run on the standard sky only.

The table holds, per start: the fps of every view of every variant (FAIL below 48), the cloud pass's GPU time (the mean of "ReDefinition clouds" over the GPU
profiler's FLIGHT windows) and that time at the last recorded commit, so a rise shows on the day it comes. Exit code 0 only if every view holds 48 fps and every
start's probe passed. The history is .local/work/fps-gate-history.jsonl (one line per commit and start). Stdlib only.
"""
import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERIES = "K-T2"
# The sky kinds, one start each.
STARTS = (
    ("standard", "perfNpic2560dlssq48mshadow"),
    ("dense under rain", "perfNpic2560dlssq48mdeck"),
    ("storm", "perfNpic2560dlssq48mstorm"),
    ("from space", "dist2560dlssqair2"),
)
MIN_FPS = 48.0
HISTORY = os.path.join(REPO, ".local", "work", "fps-gate-history.jsonl")
PICTURES = os.path.join(REPO, ".local", "work", "series-pictures", SERIES)
KEPT = os.path.join(REPO, ".local", "work", "series-pictures", SERIES + "-kept")

FPS = re.compile(r'"metric":"T09\.view\.([a-z0-9_]+)\.fps","value":([0-9.]+),"variant":"([a-z0-9]+)"')
FLIGHT = "GPU profiler: FLIGHT"
CLOUDS = re.compile(r"^    ReDefinition clouds ([0-9.]+) ms")
VERDICT = re.compile(r"^Probe@(\S+)\s+(\S+)")


def newest_log(start):
    """The newest KSP log the series runner kept for the start (build/atmo-series/<series>-<time>-<start>.log), or None."""
    logs = glob.glob(os.path.join(REPO, "build", "atmo-series", "%s-*-%s.log" % (SERIES, start)))
    return max(logs, key=os.path.getmtime) if logs else None


def read_log(path):
    """(fps by variant in the order run: {variant: [(view, fps)]}, cloud pass ms or None, FLIGHT windows counted)."""
    fps, order = {}, []
    clouds, windows, pending = [], 0, 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = FPS.search(line)
            if m:
                view, value, variant = m.group(1), float(m.group(2)), m.group(3)
                if variant not in fps:
                    fps[variant] = []
                    order.append(variant)
                fps[variant].append((view, value))
                continue
            if FLIGHT in line:
                windows += 1
                pending = 16   # the table's rows follow within a few lines
                continue
            if pending:
                pending -= 1
                m = CLOUDS.match(line)
                if m:
                    clouds.append(float(m.group(1)))
                    pending = 0
    # The first window of a start holds the placement, as the runs of 2026-11-27 were read: windows 2 to 19.
    used = clouds[1:19] if len(clouds) > 2 else clouds
    return [(v, fps[v]) for v in order], (sum(used) / len(used) if used else None), windows


def probe_verdict(start):
    """The probe's verdict from the runner's report beside the log (the json of the same time stamp), or None."""
    log = newest_log(start)
    if not log:
        return None
    report = log[:-len("-%s.log" % start)] + ".json"
    try:
        with open(report, encoding="utf-8") as f:
            d = json.load(f)
        for row in d.get("table", []):
            if row.get("metric") == "Probe@" + start:
                return str(row.get("value"))
    except (OSError, ValueError):
        pass
    return None


def last_recorded(start):
    """(commit, cloud pass ms, lowest fps) of the start's newest history line, or None."""
    if not os.path.exists(HISTORY):
        return None
    last = None
    with open(HISTORY, encoding="utf-8") as f:
        for line in f:
            try:
                d = json.loads(line)
            except ValueError:
                continue
            if d.get("start") == start:
                last = d
    return last


def table():
    """Prints the gate's table from the newest logs; returns (ok, rows for the history)."""
    ok, rows = True, []
    print("%-18s %-10s %-44s %-9s %-22s %s" % ("sky", "variant", "fps by view", "lowest", "cloud pass ms (last)", "verdict"))
    for kind, start in STARTS:
        log = newest_log(start)
        if not log:
            print("%-18s %-10s no log of %s" % (kind, "-", start))
            ok = False
            continue
        variants, clouds, windows = read_log(log)
        verdict = probe_verdict(start)
        last = last_recorded(start)
        lowest_all = None
        if not variants:
            print("%-18s %-10s no fps line in %s" % (kind, "-", os.path.basename(log)))
            ok = False
        for i, (variant, views) in enumerate(variants):
            low = min(v for _, v in views)
            lowest_all = low if lowest_all is None else min(lowest_all, low)
            good = low >= MIN_FPS
            ok = ok and good
            text = " ".join("%s %.1f" % (view, v) for view, v in views)
            pass_text = ""
            if i == 0:
                pass_text = ("%.2f" % clouds if clouds is not None else "none")
                if last and last.get("cloud_pass_ms") is not None:
                    pass_text += " (%.2f at %s)" % (last["cloud_pass_ms"], last.get("commit", "?"))
            print("%-18s %-10s %-44s %-9.1f %-22s %s" % (kind if i == 0 else "", variant, text, low, pass_text, "ok" if good else "BELOW %g" % MIN_FPS))
        # The log's own time: a start that was not run again shows an old one (a number of another build).
        print("%-18s %-10s from %s, %d flight windows" % ("", "", time.strftime("%Y-%m-%d %H:%M", time.localtime(os.path.getmtime(log))), windows))
        if verdict != "PASSED":
            print("%-18s %-10s the probe's verdict: %s" % ("", "", verdict))
            ok = False
        rows.append({"start": start, "sky": kind, "log": os.path.basename(log), "cloud_pass_ms": None if clouds is None else round(clouds, 3),
                     "lowest_fps": lowest_all, "fps": {variant: {view: v for view, v in views} for variant, views in variants}, "windows": windows})
    print("FPS GATE: %s" % ("PASS" if ok else "FAIL"))
    return ok, rows


def keep_pictures():
    """Copies the pictures of the gate's starts that exist now into a folder of their own: a run overwrites them by name."""
    if not os.path.isdir(PICTURES):
        return None
    names = [n for n in os.listdir(PICTURES) if any(("-%s-" % start) in n for _, start in STARTS)]
    if not names:
        return None
    target = os.path.join(KEPT, time.strftime("%Y%m%d-%H%M") + "-before-gate")
    os.makedirs(target, exist_ok=True)
    for n in names:
        shutil.copy2(os.path.join(PICTURES, n), os.path.join(target, n))
    return target


def cmd_run(args):
    kept = keep_pictures()
    if kept:
        print("pictures of the runs before kept in %s" % kept)
    for kind, start in STARTS:
        print("run %s (%s) ..." % (start, kind), flush=True)
        out = os.path.join(REPO, ".local", "work", "fps-gate-%s.log" % start)
        with open(out, "w", encoding="utf-8") as f:
            # The runner's exit code is 1 when rows of other starts have no line: the probe's verdict and the fps decide here.
            subprocess.call([sys.executable, os.path.join(REPO, "tools", "atmo_series.py"), "run", SERIES, "--start", start, "--who", args.who], cwd=REPO, stdout=f,
                            stderr=subprocess.STDOUT)
        with open(out, encoding="utf-8", errors="replace") as f:
            text = f.read()
        if "REFUSED" in text:
            print("REFUSED: %s (see %s); the gate stops, nothing is run after a refusal" % (start, out))
            return 2
    ok, _ = table()
    return 0 if ok else 1


def cmd_report(args):
    ok, _ = table()
    return 0 if ok else 1


def cmd_record(args):
    ok, rows = table()
    os.makedirs(os.path.dirname(HISTORY), exist_ok=True)
    with open(HISTORY, "a", encoding="utf-8") as f:
        for row in rows:
            row["commit"] = args.commit
            row["recorded"] = time.strftime("%Y-%m-%d %H:%M")
            f.write(json.dumps(row) + "\n")
    print("recorded %d start(s) under %s" % (len(rows), args.commit))
    return 0 if ok else 1


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    r = sub.add_parser("run")
    r.add_argument("--who", default="fps-gate")
    r.set_defaults(func=cmd_run)
    sub.add_parser("report").set_defaults(func=cmd_report)
    c = sub.add_parser("record")
    c.add_argument("commit")
    c.set_defaults(func=cmd_record)
    args = p.parse_args()
    sys.exit(args.func(args))


if __name__ == "__main__":
    main()
