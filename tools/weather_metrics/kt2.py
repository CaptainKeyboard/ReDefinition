#!/usr/bin/env python3
"""The metric script of series K-T2 (package T09): TVC against the own cloud pass, in milliseconds per view.

  python tools/weather_metrics/kt2.py LOG [LOG ...] [--out FILE.json] [--tvc-pattern REGEX] [--own-pattern REGEX]
  python tools/weather_metrics/kt2.py --selftest

The probe (src/Atmospheres/Light/Cloud/Probe/T09Probe.cs) writes "T09 view begin: <view> <variant> <W>x<H>" and "T09 view end: ..." into the
game's log; with -redefinitionGpuProfile the game logs a GPU profiler report every 10 s, whose "Costliest segments" lines read
"<ms> ms, at most <ms>, <frames>: <label>". The cost of the clouds in a view is the sum of the segments whose label matches the pattern of the
variant (the own pass: the markers "ReDefinition clouds: begin" and "ReDefinition clouds: end" it puts around itself through CloudBridgeLights.Mark; TVC: --tvc-pattern, the labels of EVE's
cloud buffers, to be read off the first report of the run), averaged over the reports that lie wholly inside the view. The first report after a
begin line holds the seconds before it and is dropped. A view without a clean report gives no value (null), never a guess.

Output: {"<variant>": {"T09.perf.<view>.<W>x<H>.ms": value | null}}, a table on stdout, and the ratios the rows of tests/series/rows/T09.json name
(own plain over TVC per view, recon4 over plain). Stdlib only.
"""
import argparse
import json
import re
import sys

BEGIN = re.compile(r"T09 view begin: (\S+) (\S+) (\d+)x(\d+)")
END = re.compile(r"T09 view end: (\S+) (\S+) (\d+)x(\d+)")
REPORT = re.compile(r"GPU profiler: .*frames measured, GPU")
SEGMENT = re.compile(r"^\s+([\d.]+) ms, at most ([\d.]+), (\d+): (.*)$")

# The labels of the markers the own pass puts around itself (CloudBridgeLights.MarkBegin / MarkEnd, assigned to GpuProfiler.Mark by the main mod).
# [VERIFY] on the first report which of the two markers names the segment between them; the pattern takes both.
OWN_DEFAULT = r"ReDefinition clouds"
TVC_DEFAULT = r"[Vv]olumetric|EVE|EnvironmentalVisualEnhancements"


def parse(lines, own_pattern, tvc_pattern):
    """The mean cloud ms per (variant, view, resolution) from the lines of one log."""
    own, tvc = re.compile(own_pattern), re.compile(tvc_pattern)
    view = None            # (view, variant, w, h) while a view is open
    reports = []           # the cloud ms of each report inside the open view, the first one dropped
    block = None           # [ms] of the report being read
    found = {}

    def close_block():
        nonlocal block
        if block is not None and view is not None:
            reports.append(block[0] if block[1] else None)   # a report with no matching label is no value, not 0 ms
        block = None

    for raw in lines:
        line = raw.rstrip("\n")
        m = BEGIN.search(line)
        if m:
            close_block()
            view, reports = (m.group(1), m.group(2), m.group(3), m.group(4)), []
            continue
        m = END.search(line)
        if m:
            close_block()
            if view is not None:
                clean = [r for r in reports[1:] if r is not None]
                key = (view[1], view[0], view[2] + "x" + view[3])
                found[key] = sum(clean) / len(clean) if clean else None
            view, reports = None, []
            continue
        if view is None:
            continue
        if REPORT.search(line):
            close_block()
            block = [0.0, False]
            continue
        m = SEGMENT.match(line)
        if m and block is not None:
            label = m.group(4)
            pattern = tvc if view[1] == "tvc" else own
            if pattern.search(label):
                block[0] += float(m.group(1))
                block[1] = True
    return found


def table(found):
    out = {}
    for (variant, view, res), ms in sorted(found.items()):
        out.setdefault(variant, {})["T09.perf.%s.%s.ms" % (view, res)] = None if ms is None else round(ms, 4)
    return out


def ratios(t):
    """own plain over TVC per view and the mean of it; recon4 over plain; None where a side is missing or zero."""
    result = {}

    def ratio(a, b):
        return a / b if a is not None and b else None

    def mean(values):
        values = [v for v in values if v is not None]
        return sum(values) / len(values) if values else None

    tvc, plain, recon = t.get("tvc", {}), t.get("plain", {}), t.get("recon4", {})
    own_over_tvc = [ratio(plain.get(k), tvc.get(k)) for k in plain]
    recon_over_plain = [ratio(recon.get(k), plain.get(k)) for k in recon]
    result["T09.perf.own_over_tvc.mean"] = mean(own_over_tvc)
    result["T09.perf.recon4_over_plain.mean"] = mean(recon_over_plain)
    return result


def selftest():
    log = """
[ReDefinition] noise before
T09 view begin: ground plain 1707x960
[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU 9.000 ms a frame from Present to Present (at most 12.000).
  Costliest segments, per frame on average, at most, in how many frames:
    9.000 ms, at most 9.5, 600: Camera 00: until the end of ReDefinition clouds: end
    9.900 ms, at most 9.9, 600: other thing
[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU 7.000 ms a frame from Present to Present (at most 12.000).
  Costliest segments, per frame on average, at most, in how many frames:
    2.000 ms, at most 2.5, 600: Camera 00: ReDefinition clouds: end
    0.250 ms, at most 0.3, 600: Camera 00: ReDefinition clouds: begin
    1.100 ms, at most 1.5, 600: Camera 00: something else
[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU 7.000 ms a frame from Present to Present (at most 12.000).
  Costliest segments, per frame on average, at most, in how many frames:
    2.400 ms, at most 2.5, 600: Camera 00: ReDefinition clouds: end
    0.250 ms, at most 0.3, 600: Camera 00: ReDefinition clouds: begin
T09 view end: ground plain 1707x960
T09 view begin: ground tvc 1707x960
[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU 12.000 ms a frame from Present to Present (at most 12.000).
  Costliest segments, per frame on average, at most, in how many frames:
    8.0 ms, at most 9.5, 600: Camera 00: EVE volumetric clouds
[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU 12.000 ms a frame from Present to Present (at most 12.000).
  Costliest segments, per frame on average, at most, in how many frames:
    6.000 ms, at most 9.5, 600: Camera 00: EVE volumetric clouds
T09 view end: ground tvc 1707x960
T09 view begin: inside plain 1707x960
[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU 12.000 ms a frame from Present to Present (at most 12.000).
T09 view end: inside plain 1707x960
""".splitlines()
    t = table(parse(log, OWN_DEFAULT, TVC_DEFAULT))
    assert abs(t["plain"]["T09.perf.ground.1707x960.ms"] - 2.45) < 1e-9, t        # the mean of (2.0 + 0.25) and (2.4 + 0.25)
    assert t["tvc"]["T09.perf.ground.1707x960.ms"] == 6.0, t                         # the first report is dropped
    assert t["plain"]["T09.perf.inside.1707x960.ms"] is None, t                     # no clean report: no value
    r = ratios(t)
    assert abs(r["T09.perf.own_over_tvc.mean"] - 2.45 / 6.0) < 1e-9, r
    assert r["T09.perf.recon4_over_plain.mean"] is None, r
    print("kt2 selftest: ok")


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="*")
    ap.add_argument("--out")
    ap.add_argument("--own-pattern", default=OWN_DEFAULT)
    ap.add_argument("--tvc-pattern", default=TVC_DEFAULT)
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        selftest()
        return 0
    if not a.logs:
        ap.error("a log is needed (or --selftest)")
    found = {}
    for path in a.logs:
        with open(path, encoding="utf-8", errors="replace") as f:
            found.update(parse(f, a.own_pattern, a.tvc_pattern))
    t = table(found)
    r = ratios(t)
    for variant, rows in t.items():
        for metric, value in rows.items():
            print("%-10s %-40s %s" % (variant, metric, "-" if value is None else "%.3f" % value))
    for metric, value in r.items():
        print("%-10s %-40s %s" % ("ratio", metric, "-" if value is None else "%.3f" % value))
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump({"variants": t, "ratios": r}, f, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
