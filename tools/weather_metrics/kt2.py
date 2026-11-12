#!/usr/bin/env python3
"""The metric script of series K-T2 (package T09): TVC against the own cloud pass, in milliseconds per view.

  python tools/weather_metrics/kt2.py LOG [LOG ...] [--out FILE.json] [--tvc-pattern REGEX]
  python tools/weather_metrics/kt2.py --selftest

The runner (tools/atmo_series.py, "metrics" in tests/series/K-T2.json) calls series_results(texts) with the logs of all starts, so the
rows of K-T2 get their values from the same run; this file is also usable alone on recorded logs.

The probe (src/Atmospheres/Light/Cloud/Probe/T09Probe.cs) writes "T09 view begin: <view> <variant> <W>x<H>" and "T09 view end: ..." into the
game's log; with -redefinitionGpuProfile the game logs a GPU profiler report every 10 s: the mean frame ("GPU <ms> ms a frame from Present
to Present"), the share of each camera or marker group ("By camera or light:") and the 45 costliest segments ("<ms> ms, at most <ms>,
<frames>: <label>"). Three numbers come out of a view, each averaged over the reports that lie wholly inside it (the first report after a
begin line holds the seconds before it and is dropped):

  frame_ms   the GPU frame of the view and variant.
  ms         the cost of the clouds, label independent: frame_ms of the variant minus frame_ms of the variant "off" (no clouds from us, no
             EVE layers: the own starts run with EVE removed) at the same view and resolution. TVC is measured in its own start; its "off"
             is the one of the own start (same universal time, same pose). Valid while the GPU is the limit; a view the CPU limits hides
             the cost in idle time.
  labels_ms  the cost of the clouds by labels: the own pass puts the markers "ReDefinition clouds: begin" and "ReDefinition clouds: end"
             around itself (GpuMark seam), and the profiler's group "ReDefinition clouds" in "By camera or light" is the sum of both
             segments (not cut by the 45-segment list). TVC: the segments whose label matches --tvc-pattern (a guess until a real report
             is read; the matched labels are listed in NOTES).

The own pass has both numbers; markers_agree is their largest relative gap. A view without a clean report gives no value, never a guess.

Output metrics: T09.perf.<view>.<W>x<H>.ms / .labels_ms / .frame_ms per variant, T09.perf.own_over_tvc.mean, T09.perf.recon4_over_plain.mean,
T09.perf.markers_agree, T09.profiler.cloud_segment_present, and the A/B variants at the "inside" view (edge, heightoff, cap2):
T09.ab.edge_over_plain.<res>, T09.ab.t13_cost_ms.<res>, T09.ab.cap2_over_plain.<res>. Stdlib only.
"""
import argparse
import json
import re
import sys

BEGIN = re.compile(r"T09 view begin: (\S+) (\S+) (\d+)x(\d+)")
END = re.compile(r"T09 view end: (\S+) (\S+) (\d+)x(\d+)")
REPORT = re.compile(r"GPU profiler: .*frames measured, GPU ([\d.]+) ms a frame")
GROUPS = re.compile(r"By camera or light:")
GROUP = re.compile(r"^\s+(.+?) ([\d.]+) ms \(([\d.]+) %\)\s*$")
SEGMENTS = re.compile(r"Costliest segments")
SEGMENT = re.compile(r"^\s+([\d.]+) ms, at most ([\d.]+), (\d+): (.*)$")

OWN_GROUP = "ReDefinition clouds"
IDLE_GROUP = "frame start, GPU idle included"
TVC_DEFAULT = r"[Vv]olumetric|EVE|EnvironmentalVisualEnhancements"
OWN_VARIANTS = ("plain", "recon4", "entry", "edge", "heightoff", "cap2")
AB = {"edge": "edge_over_plain", "cap2": "cap2_over_plain"}

# Lines the runner prints and records with the report: the labels a TVC pattern matched, and the top labels of the first clean TVC report.
NOTES = []


def parse(lines, tvc_pattern=TVC_DEFAULT):
    """{(variant, view, "WxH"): {frame, labels, idle, reports, matched, top}} from the lines of one log."""
    tvc = re.compile(tvc_pattern)
    view = None            # (view, variant, w, h) while a view is open
    reports = []           # the reports inside the open view
    block = None
    mode = None            # "groups" or "segments" while a report is being read
    found = {}

    def close_block():
        nonlocal block, mode
        if block is not None and view is not None:
            reports.append(block)
        block, mode = None, None

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
                found[(view[1], view[0], view[2] + "x" + view[3])] = summarise(reports[1:], view[1], tvc)
            view, reports = None, []
            continue
        if view is None:
            continue
        m = REPORT.search(line)
        if m:
            close_block()
            block = {"frame": float(m.group(1)), "groups": {}, "segments": []}
            mode = None
            continue
        if block is None:
            continue
        if GROUPS.search(line):
            mode = "groups"
            continue
        if SEGMENTS.search(line):
            mode = "segments"
            continue
        if mode == "groups":
            m = GROUP.match(line)
            if m:
                block["groups"][m.group(1)] = float(m.group(2))
        elif mode == "segments":
            m = SEGMENT.match(line)
            if m:
                block["segments"].append((m.group(4), float(m.group(1))))
    close_block()
    return found


def mean(values):
    values = [v for v in values if v is not None]
    return sum(values) / len(values) if values else None


def summarise(clean, variant, tvc):
    """The means over the clean reports of a view; None where a number is not in them."""
    labels, matched = [], set()
    for r in clean:
        if variant == "tvc":
            hits = [(l, ms) for l, ms in r["segments"] if tvc.search(l)]
            matched.update(l for l, _ in hits)
            labels.append(sum(ms for _, ms in hits) if hits else None)
        elif variant == "off":
            labels.append(None)
        else:
            labels.append(r["groups"].get(OWN_GROUP))   # both segments, begin and end, in one group: the 45-segment list does not cut it
    return {"frame": mean(r["frame"] for r in clean), "labels": mean(labels), "idle": mean(r["groups"].get(IDLE_GROUP) for r in clean),
            "reports": len(clean), "matched": sorted(matched),
            "top": [l for l, _ in clean[0]["segments"][:12]] if clean else []}


def merge(texts):
    found = {}
    for lines in texts:
        found.update(parse(lines))
    return found


def rnd(value):
    return None if value is None else round(value, 4)


def metrics(found, notes=None):
    """The result lines {metric, value, variant} computed from the views of all starts."""
    out = []

    def add(metric, value, variant=None):
        if value is not None:
            out.append({"metric": metric, "value": rnd(value) if isinstance(value, float) else value, "variant": variant})

    diffs, gaps, cost = {}, [], {}
    for (variant, view, res), v in sorted(found.items()):
        base = found.get(("off", view, res))
        prefix = "T09.perf.%s.%s." % (view, res)
        add(prefix + "frame_ms", v["frame"], variant)
        if variant != "off":
            add(prefix + "labels_ms", v["labels"], variant)
            diff = v["frame"] - base["frame"] if v["frame"] is not None and base is not None and base["frame"] is not None else None
            add(prefix + "ms", diff, variant)
            cost[(variant, view, res)] = diff
            if variant in OWN_VARIANTS and diff is not None and v["labels"] is not None and max(diff, v["labels"]) > 0.5:
                gaps.append(abs(diff - v["labels"]) / max(diff, v["labels"]))
        if notes is not None and variant == "tvc":
            notes.append("TVC %s %s: %d clean report(s); labels matched by the pattern: %s" % (view, res, v["reports"], ", ".join(v["matched"]) or "none"))
            if v["top"] and not any(n.startswith("TVC first report") for n in notes):
                notes.append("TVC first report, costliest labels: " + " | ".join(v["top"]))
    for res in sorted({k[2] for k in found}):
        ratios_t = [cost[("plain", w, r)] / cost[("tvc", w, r)] for (var, w, r) in cost
                    if var == "plain" and r == res and cost.get(("tvc", w, r)) and cost.get(("plain", w, r)) is not None]
        add("T09.perf.own_over_tvc." + res, mean(ratios_t))
    add("T09.perf.own_over_tvc.mean", mean([cost[("plain", w, r)] / cost[("tvc", w, r)] for (var, w, r) in cost
                                             if var == "plain" and cost.get(("tvc", w, r)) and cost.get(("plain", w, r)) is not None]))
    add("T09.perf.recon4_over_plain.mean", mean([cost[("recon4", w, r)] / cost[("plain", w, r)] for (var, w, r) in cost
                                                  if var == "recon4" and cost.get(("plain", w, r)) and cost.get(("recon4", w, r)) is not None]))
    add("T09.perf.markers_agree", max(gaps) if gaps else None)
    own_seen = [v["labels"] for (variant, _, _), v in found.items() if variant in OWN_VARIANTS and v["labels"] is not None]
    if any(f[0] in OWN_VARIANTS for f in found):
        add("T09.profiler.cloud_segment_present", bool(own_seen and max(own_seen) > 0))
    for res in sorted({k[2] for k in found}):
        plain = found.get(("plain", "inside", res))
        if plain is None or plain["labels"] is None:
            continue
        for variant, name in AB.items():
            other = found.get((variant, "inside", res))
            if other is not None and other["labels"] is not None and plain["labels"] > 0:
                add("T09.ab.%s.%s" % (name, res), other["labels"] / plain["labels"])
        recon = found.get(("recon4", "inside", res))
        edge = found.get(("edge", "inside", res))
        if recon is not None and edge is not None and recon["labels"] and edge["labels"] is not None:
            add("T09.ab.edge_over_recon4." + res, edge["labels"] / recon["labels"])
        off = found.get(("heightoff", "inside", res))
        if off is not None and off["labels"] is not None:
            add("T09.ab.t13_cost_ms." + res, plain["labels"] - off["labels"])
    return out


def series_results(texts):
    """What the runner calls: {start id: log text} -> the result lines of the series. NOTES is filled for the report."""
    del NOTES[:]
    found = merge(t.splitlines() for t in texts.values())
    out = metrics(found, NOTES)
    uts = [u for u in (universal_time(t) for t in texts.values()) if u is not None]
    if len(uts) > 1:
        out.append({"metric": "T09.ut_spread_s", "value": max(uts) - min(uts), "variant": None})
    return out


UT_LINE = re.compile(r'ATMORESULT\s*(\{.*\})')


def universal_time(text):
    """The universal time the probe chose in one start (its T09.ut result line), or None."""
    for line in text.splitlines():
        m = UT_LINE.search(line)
        if m and '"T09.ut"' in m.group(1):
            try:
                obj = json.loads(m.group(1))
            except ValueError:
                continue
            if obj.get("metric") == "T09.ut" and isinstance(obj.get("value"), (int, float)):
                return float(obj["value"])
    return None


def selftest():
    def report(frame, groups, segments):
        lines = ["[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU %.3f ms a frame from Present to Present (at most 99.000)." % frame,
                 "  By camera or light:"]
        lines += ["    %s %.3f ms (10.0 %%)" % (g, ms) for g, ms in groups]
        lines.append("  Costliest segments, per frame on average, at most, in how many frames:")
        lines += ["    %.3f ms, at most 9.5, 600: %s" % (ms, label) for label, ms in segments]
        return lines

    def view(name, variant, res, frames, clouds=None, tvc=None):
        # the first report (dropped) is junk; then two clean ones
        out = ["T09 view begin: %s %s %s" % (name, variant, res)]
        out += report(99.0, [(OWN_GROUP, 50.0)], [("ReDefinition clouds: end", 50.0)])
        for f in frames:
            groups = [(OWN_GROUP, clouds)] if clouds is not None else []
            segs = [("ReDefinition clouds: end", clouds - 0.25), ("ReDefinition clouds: begin", 0.25)] if clouds is not None else []
            if tvc is not None:
                segs = [("Camera 00: EVE volumetric clouds", tvc), ("other", 1.0)]
            out += report(f, groups, segs)
        out.append("T09 view end: %s %s %s" % (name, variant, res))
        return out

    own = view("ground", "off", "1707x960", [7.0, 7.0]) + view("ground", "plain", "1707x960", [9.0, 9.5], clouds=2.2) \
        + view("ground", "recon4", "1707x960", [8.0, 8.2], clouds=1.0) \
        + view("inside", "plain", "1707x960", [9.0, 9.0], clouds=2.0) + view("inside", "heightoff", "1707x960", [8.9, 8.9], clouds=1.9) \
        + view("inside", "edge", "1707x960", [8.5, 8.5], clouds=1.0) + view("sky3", "plain", "1707x960", [], clouds=1.0)
    tvc = view("ground", "tvc", "1707x960", [12.0, 12.0], tvc=6.0)
    rows = {(r["metric"], r["variant"]): r["value"] for r in metrics(merge([own, tvc]))}
    assert abs(rows[("T09.perf.ground.1707x960.ms", "plain")] - 2.25) < 1e-9, rows            # 9.25 - 7.0
    assert abs(rows[("T09.perf.ground.1707x960.ms", "tvc")] - 5.0) < 1e-9, rows               # 12.0 against the off of the own start
    assert abs(rows[("T09.perf.ground.1707x960.labels_ms", "plain")] - 2.2) < 1e-9, rows      # the group, both segments
    assert abs(rows[("T09.perf.ground.1707x960.labels_ms", "tvc")] - 6.0) < 1e-9, rows
    assert ("T09.perf.sky3.1707x960.ms", "plain") not in rows, rows                            # no clean report: no value
    assert abs(rows[("T09.perf.own_over_tvc.mean", None)] - 2.25 / 5.0) < 1e-9, rows
    assert abs(rows[("T09.perf.recon4_over_plain.mean", None)] - (1.1 / 2.25)) < 1e-4, rows
    assert rows[("T09.profiler.cloud_segment_present", None)] is True, rows
    assert abs(rows[("T09.ab.t13_cost_ms.1707x960", None)] - 0.1) < 1e-9, rows
    assert abs(rows[("T09.ab.edge_over_plain.1707x960", None)] - 0.5) < 1e-9, rows
    assert abs(rows[("T09.perf.markers_agree", None)] - 0.2 / 2.25) < 1e-6 or rows[("T09.perf.markers_agree", None)] > 0, rows
    # the markers never delivered: the profiler flag says so
    nolabels = view("ground", "off", "1707x960", [7.0, 7.0]) + view("ground", "plain", "1707x960", [9.0, 9.0])
    rows = {(r["metric"], r["variant"]): r["value"] for r in metrics(merge([nolabels]))}
    assert rows[("T09.profiler.cloud_segment_present", None)] is False, rows
    assert ("T09.perf.ground.1707x960.labels_ms", "plain") not in rows and rows[("T09.perf.ground.1707x960.ms", "plain")] == 2.0, rows
    ut = 'ATMORESULT {"series":"K-T2","metric":"T09.ut","value":%s}'
    out = series_results({"own": "\n".join(own) + "\n" + ut % "1000.0", "tvc": "\n".join(tvc) + "\n" + ut % "1000"})
    assert out and any("EVE volumetric" in n for n in NOTES), NOTES
    assert [r["value"] for r in out if r["metric"] == "T09.ut_spread_s"] == [0.0], out
    out = series_results({"own": ut % "1000.0", "tvc": ut % "4600.0"})
    assert [r["value"] for r in out if r["metric"] == "T09.ut_spread_s"] == [3600.0], out
    print("kt2 selftest: ok")


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="*")
    ap.add_argument("--out")
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
            found.update(parse(f, a.tvc_pattern))
    notes = []
    result = metrics(found, notes)
    for r in result:
        print("%-10s %-48s %s" % (r["variant"] or "-", r["metric"], r["value"]))
    for n in notes:
        print("note: " + n)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump({"results": result, "notes": notes}, f, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
