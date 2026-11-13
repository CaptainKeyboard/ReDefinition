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
             the cost in idle time: when the view's or its off frame's GPU idle ("frame start, GPU idle included") is above 10 percent of
             the frame (idle_pct is emitted per view), ms of TVC and of the own variants is the difference of the GPU-busy times
             (busy_ms = frame minus that idle) against the off frame's; a negative difference is flagged (NOT SENSIBLE) and replaced by the
             labels figure, or by none. The view is left out of markers_agree and the report notes it; busy_vs_labels (unlisted) compares
             the busy difference with the labels figure of the own variants.
  labels_ms  the cost of the clouds by labels: the own pass puts the markers "ReDefinition clouds: begin" and "ReDefinition clouds: end"
             around itself (GpuMark seam), and the profiler's group "ReDefinition clouds" in "By camera or light" is the sum of both
             segments (not cut by the 45-segment list). TVC: the segments whose label matches --tvc-pattern (a guess until a real report
             is read; the matched labels are listed in NOTES).

The own pass has both numbers; markers_agree is their largest relative gap. A view without a clean report gives no value, never a guess.

Output metrics: T09.perf.<view>.<W>x<H>.ms / .labels_ms / .frame_ms / .idle_pct per variant, T09.perf.own_over_tvc.mean and
T09.perf.recon4_over_plain.mean (ratios of the sums of the costs over the views, not means of ratios),
T09.perf.markers_agree, T09.profiler.cloud_segment_present, and the A/B variants at the "inside" view (edge, heightoff, cap2, cache, entry):
T09.ab.edge_over_plain.<res>, T09.ab.t13_cost_ms.<res>, T09.ab.cap2_over_plain.<res>. The cloud resolution levels recon2, recon3 and recon4
(recon2 and recon3 are measured in ground, inside and above only) over plain: T09.perf.<level>_over_plain.3views[.<res>] (ratio of sums over the
three common views) and T09.perf.<view>.<res>.<level>_over_plain. The noise rows of the probe (T09.noise.<view>.flicker / hf per variant) come
out as T09.noise.<view>.<stat>_over_plain and T09.noise.<stat>_over_plain.mean per level. Stdlib only.
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
# TVC's pass names. Known from the code and the installed packs: EVE draws "EVE/RaymarchCloud" (Graphics.Blit, EveRenderCompat) and its bundle is
# eveshaders.bundle; the StockVolumetricClouds pack puts its clouds into EVE's raymarched layers (raymarchedClouds.cfg). No TVC profiler report has
# been read yet, so the patterns are tiers: the first tier with a hit in a report is that report's TVC figure (the broad "cloud" tier only when no
# named tier hit), and the first report's costliest labels go into the notes for the real pattern. A label of our own pass never matches.
TVC_TIERS = (r"EVE|EnvironmentalVisualEnhancements|[Rr]aymarch|[Vv]olumetric|TVC|StockVolumetricClouds", r"[Cc]loud")
TOP_LABELS = 15
SENSIBLE_NEGATIVE_MS = 0.1   # a GPU-busy delta below minus this is not a cost (the two windows differ by more than the clouds)
OWN_VARIANTS = ("plain", "recon2", "recon3", "recon4", "entry", "edge", "heightoff", "cap2", "cache")
AB = {"edge": "edge_over_plain", "cap2": "cap2_over_plain", "cache": "cache_over_plain", "entry": "entry_over_plain"}
# The cloud resolution levels (the slider cloudResolution); recon2 and recon3 are measured in three views only, so every level's ratio to plain is also given over those.
LEVELS = ("recon2", "recon3", "recon4")
COMMON_VIEWS = ("ground", "inside", "above")
# The noise rows the probe emits per variant and view (T09ProbeNoise.cs) and the views they are read in.
NOISE_STATS = ("flicker", "hf")
NOISE_VIEWS = ("ground", "above")

# Lines the runner prints and records with the report: the labels a TVC pattern matched, and the top labels of the first clean TVC report.
NOTES = []


def parse(lines, tvc_pattern=None):
    """{(variant, view, "WxH"): {frame, labels, idle, reports, matched, top}} from the lines of one log.
    tvc_pattern: one regex that replaces the tiers TVC_TIERS."""
    tvc = [re.compile(p) for p in ((tvc_pattern,) if tvc_pattern else TVC_TIERS)]
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
            hits = []
            for tier in tvc:
                hits = [(l, ms) for l, ms in r["segments"] if tier.search(l) and OWN_GROUP not in l and "ReDefinition" not in l]
                if hits:
                    break
            matched.update(l for l, _ in hits)
            labels.append(sum(ms for _, ms in hits) if hits else None)
        elif variant == "off":
            labels.append(None)
        else:
            labels.append(r["groups"].get(OWN_GROUP))   # both segments, begin and end, in one group: the 45-segment list does not cut it
    return {"frame": mean(r["frame"] for r in clean), "labels": mean(labels), "idle": mean(r["groups"].get(IDLE_GROUP) for r in clean),
            "reports": len(clean), "matched": sorted(matched),
            "top": ["%s (%.2f ms)" % (l, ms) for l, ms in clean[0]["segments"][:TOP_LABELS]] if clean else []}


def merge(texts):
    found = {}
    for lines in texts:
        found.update(parse(lines))
    return found


CPU_BOUND = 0.10   # a view with more GPU idle than this share of its frame is limited by the CPU


def idle_share(v):
    """The share of the frame the GPU idles (the profiler's group IDLE_GROUP over the mean frame), or None."""
    if v is None or v["idle"] is None or not v["frame"]:
        return None
    return v["idle"] / v["frame"]


def busy_ms(v):
    """The GPU's busy time of a view: the mean frame minus the profiler's first segment (Present to the frame's first marker, GPU idle included), or None."""
    if v is None or v["frame"] is None or v["idle"] is None:
        return None
    return v["frame"] - v["idle"]


def ratio_of_sums(cost, top, bottom, res=None, views=None):
    """Sum of the costs of variant `top` over the sum of `bottom`, over the views where both have a cost (and `bottom` costs something);
    `views` limits the views."""
    pairs = [(cost[(top, w, r)], cost[(bottom, w, r)]) for (var, w, r) in cost
             if var == top and (res is None or r == res) and (views is None or w in views) and cost[(top, w, r)] is not None and (cost.get((bottom, w, r)) or 0.0) > 0.0]
    if not pairs:
        return None
    denominator = sum(b for _, b in pairs)
    return sum(t for t, _ in pairs) / denominator if denominator else None


def rnd(value):
    return None if value is None else round(value, 4)


def metrics(found, notes=None):
    """The result lines {metric, value, variant} computed from the views of all starts."""
    out = []

    def add(metric, value, variant=None):
        if value is not None:
            out.append({"metric": metric, "value": rnd(value) if isinstance(value, float) else value, "variant": variant})

    gaps, busy_gaps, cost = [], [], {}
    for (variant, view, res), v in sorted(found.items()):
        base = found.get(("off", view, res))
        prefix = "T09.perf.%s.%s." % (view, res)
        add(prefix + "frame_ms", v["frame"], variant)
        idle = idle_share(v)
        add(prefix + "idle_pct", None if idle is None else 100.0 * idle, variant)
        add(prefix + "busy_ms", busy_ms(v), variant)
        if variant != "off":
            add(prefix + "labels_ms", v["labels"], variant)
            diff = v["frame"] - base["frame"] if v["frame"] is not None and base is not None and base["frame"] is not None else None
            # The frame difference is the cost only while the GPU is the limit. A view whose frame (or whose off frame) holds more than
            # 10 percent GPU idle is cpu-bound: the idle time hides the pass, so the cost is the difference of the GPU-busy times (frame minus
            # the profiler's first segment) of the variant and of the off frame, for TVC and the own pass alike. Not a cost (below minus
            # SENSIBLE_NEGATIVE_MS: the two windows differ by more than the clouds) is flagged and replaced by the labels figure, or none.
            # Without idle figures the own pass gets its labels figure. The view stays out of markers_agree (that compares frame and labels).
            bound = CPU_BOUND < max(idle if idle is not None else 0.0, idle_share(base) if base is not None and idle_share(base) is not None else 0.0)
            if bound:
                busy, base_busy = busy_ms(v), busy_ms(base)
                bdiff = busy - base_busy if busy is not None and base_busy is not None else None
                label_cost = v["labels"] if v["labels"] is not None and (variant in OWN_VARIANTS or variant == "tvc") else None
                if bdiff is not None and bdiff >= -SENSIBLE_NEGATIVE_MS:
                    diff, how = max(bdiff, 0.0), "the cost is the GPU-busy difference"
                    if label_cost is not None and max(diff, label_cost) > 0.5:
                        busy_gaps.append(abs(diff - label_cost) / max(diff, label_cost))
                elif bdiff is not None:
                    diff = label_cost
                    how = "NOT SENSIBLE: the GPU-busy difference is %.2f ms (negative); %s" % (
                        bdiff, "the cost is the labels figure" if label_cost is not None else "no cost is given")
                elif variant in OWN_VARIANTS and label_cost is not None:
                    diff, how = label_cost, "no idle figure; the cost is the labels figure"
                else:
                    how = "no idle figure; the frame difference stands, read it with care"
                if notes is not None:
                    notes.append("cpu-bound view %s %s %s (GPU idle %s percent of the frame, off frame %s percent): %s" % (
                        variant, view, res, "?" if idle is None else "%.0f" % (100.0 * idle),
                        "?" if base is None or idle_share(base) is None else "%.0f" % (100.0 * idle_share(base)), how))
            add(prefix + "ms", diff, variant)
            cost[(variant, view, res)] = diff
            if variant in OWN_VARIANTS and not bound and diff is not None and v["labels"] is not None and max(diff, v["labels"]) > 0.5:
                gaps.append(abs(diff - v["labels"]) / max(diff, v["labels"]))
        if notes is not None and variant == "tvc":
            notes.append("TVC %s %s: %d clean report(s); labels matched by the pattern: %s" % (view, res, v["reports"], ", ".join(v["matched"]) or "none"))
            if v["top"] and not any(n.startswith("TVC first report") for n in notes):
                notes.append("TVC first report, costliest labels: " + " | ".join(v["top"]))
    # Ratios of sums, not means of ratios: one view with a small cost (orbit_edge, 0.1 to 0.8 ms) would dominate a mean of per-view ratios.
    # The ".mean" in the two names is kept as the row key.
    for res in sorted({k[2] for k in found}):
        add("T09.perf.own_over_tvc." + res, ratio_of_sums(cost, "plain", "tvc", res))
    add("T09.perf.own_over_tvc.mean", ratio_of_sums(cost, "plain", "tvc"))
    add("T09.perf.recon4_over_plain.mean", ratio_of_sums(cost, "recon4", "plain"))
    # Every level against plain over the three views all levels share, per view and per resolution as well.
    for level in LEVELS:
        add("T09.perf.%s_over_plain.3views" % level, ratio_of_sums(cost, level, "plain", None, COMMON_VIEWS))
        for res in sorted({k[2] for k in found}):
            add("T09.perf.%s_over_plain.3views.%s" % (level, res), ratio_of_sums(cost, level, "plain", res, COMMON_VIEWS))
        for (var, view, res), value in sorted(cost.items()):
            plain = cost.get(("plain", view, res))
            if var == level and value is not None and plain:
                add("T09.perf.%s.%s.%s_over_plain" % (view, res, level), value / plain)
    add("T09.perf.markers_agree", max(gaps) if gaps else None)
    # The cpu-bound own views: the GPU-busy difference against the labels figure (unlisted, a cross-check).
    add("T09.perf.busy_vs_labels", max(busy_gaps) if busy_gaps else None)
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
    # The noise values are read per start (a run with two own starts keeps both): the canonical metric names carry the last start that has
    # noise rows (the rows of the series judge that one), and with more than one such start every start's ratios also come out under the name
    # plus ".<start id>".
    per_start = [(sid, noise_values(t), result_value(t, "T09.cover")) for sid, t in texts.items()]
    per_start = [p for p in per_start if p[1]]
    if per_start:
        out.extend(noise_ratios(per_start[-1][1]))
        out.extend(noise_floor_ratios(per_start[-1][1]))
    if len(per_start) > 1:
        for sid, values, _cover in per_start:
            out.extend(dict(r, metric=r["metric"] + "." + sid) for r in noise_ratios(values) + noise_floor_ratios(values))
    ok = region_ok(per_start, NOTES)
    if ok is not None:
        out.append({"metric": "T09.noise.region_ok", "value": ok, "variant": None})
    return out


MIN_REGION = 0.1      # the region of the pass must cover this share of the picture, else the noise rows are read over too few pixels
MIN_REAL_COVER = 0.5  # below this cover the clock did not pick a UT with a real cloud region


def result_value(text, metric):
    """The value of the (last) ATMORESULT line of the metric in one start's log, or None."""
    value = None
    for line in text.splitlines():
        m = UT_LINE.search(line)
        if not m or ('"%s"' % metric) not in m.group(1):
            continue
        try:
            obj = json.loads(m.group(1))
        except ValueError:
            continue
        if obj.get("metric") == metric and isinstance(obj.get("value"), (int, float)) and not isinstance(obj.get("value"), bool):
            value = float(obj["value"])
    return value


def region_ok(per_start, notes=None):
    """1 when every start's ground and above region_fraction is at least MIN_REGION; the check is waived (1, with a note) in a start whose clock reports a
    cover below MIN_REAL_COVER (no real cloud region to find: the rows judge the clock then, T09.cover). 0 when a region is too small at a real cover;
    None when no start has a region value."""
    verdicts = []
    for sid, values, cover in per_start:
        for view in NOISE_VIEWS:
            fraction = values.get(("plain", view, "region_fraction"))
            if fraction is None:
                continue
            if fraction >= MIN_REGION:
                verdicts.append(True)
            elif cover is not None and cover < MIN_REAL_COVER:
                verdicts.append(True)
                if notes is not None:
                    notes.append("%s %s: region_fraction %.3f is below %.1f, but the clock's cover is %.2f (below %.1f): no real cloud region to find, the check is waived "
                                 "(T09.cover judges the clock)" % (sid, view, fraction, MIN_REGION, cover, MIN_REAL_COVER))
            else:
                verdicts.append(False)
    return None if not verdicts else int(all(verdicts))


def noise_floor_ratios(values):
    """The plain pass's flicker over the floor of the same start (the camera jitter and the surface's own shimmer: the baseline pair), per view."""
    out = []
    for view in NOISE_VIEWS:
        flicker, floor = values.get(("plain", view, "flicker")), values.get(("plain", view, "floor_flicker"))
        if flicker is not None and floor:
            out.append({"metric": "T09.noise.%s.flicker_over_floor" % view, "value": rnd(flicker / floor), "variant": "plain"})
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


def noise_values(text):
    """{(variant, view, stat): value} of the probe's T09.noise.<view>.<stat> result lines in one start's log (the last one of a key wins)."""
    found = {}
    for line in text.splitlines():
        m = UT_LINE.search(line)
        if not m or '"T09.noise.' not in m.group(1):
            continue
        try:
            obj = json.loads(m.group(1))
        except ValueError:
            continue
        parts = str(obj.get("metric", "")).split(".")
        if len(parts) == 4 and parts[:2] == ["T09", "noise"] and isinstance(obj.get("value"), (int, float)) and obj.get("variant"):
            found[(obj["variant"], parts[2], parts[3])] = float(obj["value"])
    return found


def noise_ratios(values):
    """The noise of each reconstruction level over plain's, per view (T09.noise.<view>.<stat>_over_plain) and as the mean over the views
    (T09.noise.<stat>_over_plain.mean), for the flicker and the high-frequency energy. A level with no plain value in a view gives none."""
    out = []
    for stat in NOISE_STATS:
        for level in LEVELS:
            ratios = []
            for view in NOISE_VIEWS:
                top, bottom = values.get((level, view, stat)), values.get(("plain", view, stat))
                if top is None or not bottom:
                    continue
                ratios.append(top / bottom)
                out.append({"metric": "T09.noise.%s.%s_over_plain" % (view, stat), "value": rnd(top / bottom), "variant": level})
            if ratios:
                out.append({"metric": "T09.noise.%s_over_plain.mean" % stat, "value": rnd(sum(ratios) / len(ratios)), "variant": level})
    return out


def selftest():
    def report(frame, groups, segments):
        lines = ["[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU %.3f ms a frame from Present to Present (at most 99.000)." % frame,
                 "  By camera or light:"]
        lines += ["    %s %.3f ms (10.0 %%)" % (g, ms) for g, ms in groups]
        lines.append("  Costliest segments, per frame on average, at most, in how many frames:")
        lines += ["    %.3f ms, at most 9.5, 600: %s" % (ms, label) for label, ms in segments]
        return lines

    def view(name, variant, res, frames, clouds=None, tvc=None, idle=None):
        # the first report (dropped) is junk; then two clean ones
        out = ["T09 view begin: %s %s %s" % (name, variant, res)]
        out += report(99.0, [(OWN_GROUP, 50.0)], [("ReDefinition clouds: end", 50.0)])
        for f in frames:
            groups = [(OWN_GROUP, clouds)] if clouds is not None else []
            if idle is not None:
                groups.append((IDLE_GROUP, idle))
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
    # ratios are ratios of sums: the small orbit cost does not dominate (a mean of ratios would read 0.75)
    two = view("ground", "off", "1707x960", [7.0, 7.0]) + view("ground", "plain", "1707x960", [9.0, 9.0], clouds=2.0) \
        + view("ground", "recon4", "1707x960", [8.0, 8.0], clouds=1.0) \
        + view("orbit_edge", "off", "1707x960", [5.0, 5.0]) + view("orbit_edge", "plain", "1707x960", [5.2, 5.2], clouds=0.2) \
        + view("orbit_edge", "recon4", "1707x960", [5.2, 5.2], clouds=0.2)
    rows = {(r["metric"], r["variant"]): r["value"] for r in metrics(merge([two]))}
    assert abs(rows[("T09.perf.recon4_over_plain.mean", None)] - 1.2 / 2.2) < 1e-3, rows
    # the resolution levels over plain, over the three common views: recon4 has the orbit view too, recon2 and recon3 do not
    levels = view("ground", "off", "1707x960", [7.0, 7.0]) + view("ground", "plain", "1707x960", [9.0, 9.0], clouds=2.0) \
        + view("ground", "recon2", "1707x960", [8.0, 8.0], clouds=1.0) + view("ground", "recon3", "1707x960", [7.8, 7.8], clouds=0.8) \
        + view("ground", "recon4", "1707x960", [7.6, 7.6], clouds=0.6) \
        + view("above", "off", "1707x960", [6.0, 6.0]) + view("above", "plain", "1707x960", [10.0, 10.0], clouds=4.0) \
        + view("above", "recon2", "1707x960", [8.0, 8.0], clouds=2.0) + view("above", "recon3", "1707x960", [7.5, 7.5], clouds=1.5) \
        + view("above", "recon4", "1707x960", [7.0, 7.0], clouds=1.0) \
        + view("orbit_edge", "off", "1707x960", [5.0, 5.0]) + view("orbit_edge", "plain", "1707x960", [5.4, 5.4], clouds=0.4) \
        + view("orbit_edge", "recon4", "1707x960", [5.2, 5.2], clouds=0.2)
    rows = {(r["metric"], r["variant"]): r["value"] for r in metrics(merge([levels]))}
    assert abs(rows[("T09.perf.recon2_over_plain.3views", None)] - (1.0 + 2.0) / (2.0 + 4.0)) < 1e-3, rows      # frame differences: ground 1 and 2, above 2 and 4
    assert abs(rows[("T09.perf.recon4_over_plain.3views", None)] - (0.6 + 1.0) / (2.0 + 4.0)) < 1e-3, rows     # without the orbit view that only recon4 and plain have
    assert abs(rows[("T09.perf.ground.1707x960.recon2_over_plain", None)] - 0.5) < 1e-3, rows
    assert ("T09.perf.recon3_over_plain.3views.1707x960", None) in rows and ("T09.perf.ground.1707x960.recon2_over_plain", None) in rows, rows
    # the noise rows: the level over plain per view and as the mean
    def noise(variant, view, stat, value):
        return 'ATMORESULT {"series":"K-T2","metric":"T09.noise.%s.%s","value":%s,"variant":"%s"}' % (view, stat, value, variant)
    text = "\n".join([noise("plain", "ground", "flicker", 0.02), noise("recon2", "ground", "flicker", 0.008), noise("recon4", "ground", "flicker", 0.0024),
                      noise("plain", "above", "flicker", 0.04), noise("recon4", "above", "flicker", 0.008), noise("plain", "ground", "hf", 0.02), noise("recon4", "ground", "hf", 0.004)])
    rows = {(r["metric"], r["variant"]): r["value"] for r in noise_ratios(noise_values(text))}
    assert abs(rows[("T09.noise.ground.flicker_over_plain", "recon2")] - 0.4) < 1e-3, rows
    assert abs(rows[("T09.noise.ground.flicker_over_plain", "recon4")] - 0.12) < 1e-3, rows
    assert abs(rows[("T09.noise.flicker_over_plain.mean", "recon4")] - 0.16) < 1e-3, rows                    # (0.12 + 0.2) / 2
    assert abs(rows[("T09.noise.hf_over_plain.mean", "recon4")] - 0.2) < 1e-3 and ("T09.noise.above.flicker_over_plain", "recon2") not in rows, rows
    assert ("T09.noise.flicker_over_plain.mean", "recon3") not in rows, rows
    out = series_results({"own": text})
    assert any(r["metric"] == "T09.noise.flicker_over_plain.mean" for r in out), out
    # a cpu-bound view (more than 10 percent GPU idle): the cost is the labels figure, the view is out of markers_agree, the idle share is a value
    bound = view("ground", "off", "1707x960", [7.0, 7.0], idle=2.0) + view("ground", "plain", "1707x960", [7.1, 7.1], clouds=0.9, idle=1.5)
    notes = []
    rows = {(r["metric"], r["variant"]): r["value"] for r in metrics(merge([bound]), notes)}
    assert abs(rows[("T09.perf.ground.1707x960.ms", "plain")] - 0.6) < 1e-9, rows              # GPU busy: (7.1 - 1.5) - (7.0 - 2.0)
    assert abs(rows[("T09.perf.ground.1707x960.labels_ms", "plain")] - 0.9) < 1e-9, rows       # the labels figure stays as the cross-check
    assert abs(rows[("T09.perf.ground.1707x960.idle_pct", "off")] - 100.0 * 2.0 / 7.0) < 1e-3, rows
    assert ("T09.perf.markers_agree", None) not in rows and any("cpu-bound" in n for n in notes), (rows, notes)
    # a CPU-bound fixture: the off frame is 60 percent idle and the plain ground frame is SHORTER than the off frame (as in start 42).
    # ground: busy 4.0 against 6.4 is not a cost -> flagged, the labels figure stands; inside: busy 7.9 against 6.4 = 1.5 ms, TVC 7.2 against 6.4 = 0.8 ms
    fixture = view("ground", "off", "1707x960", [16.0, 16.0], idle=9.6) + view("ground", "plain", "1707x960", [14.0, 14.0], clouds=1.5, idle=10.0) \
        + view("inside", "off", "1707x960", [16.0, 16.0], idle=9.6) + view("inside", "plain", "1707x960", [16.5, 16.5], clouds=1.4, idle=8.6) \
        + view("inside", "tvc", "1707x960", [16.2, 16.2], idle=9.0) + view("ground", "tvc", "1707x960", [15.0, 15.0], tvc=0.7, idle=9.5)
    notes = []
    rows = {(r["metric"], r["variant"]): r["value"] for r in metrics(merge([fixture]), notes)}
    assert abs(rows[("T09.perf.inside.1707x960.ms", "plain")] - 1.5) < 1e-9, rows              # the frame difference would say 0.5
    assert abs(rows[("T09.perf.inside.1707x960.ms", "tvc")] - 0.8) < 1e-9, rows                # no TVC label matched: the GPU-busy delta, not none
    assert abs(rows[("T09.perf.inside.1707x960.busy_ms", "tvc")] - 7.2) < 1e-9, rows
    assert abs(rows[("T09.perf.ground.1707x960.ms", "plain")] - 1.5) < 1e-9, rows              # non-sensible busy delta -> labels figure
    assert any("NOT SENSIBLE" in n and "plain ground" in n for n in notes), notes
    assert abs(rows[("T09.perf.ground.1707x960.ms", "tvc")] - 0.7) < 1e-9, rows                # busy 5.5 against 6.4 is negative -> its matched label, 0.7
    assert abs(rows[("T09.perf.own_over_tvc.mean", None)] - (1.5 + 1.5) / (0.8 + 0.7)) < 1e-6, rows      # comparable quantities on both sides
    assert any("TVC inside" in n and "none" in n for n in notes) and any(n.startswith("TVC first report") and "EVE volumetric" in n for n in notes), notes
    # a non-sensible cost with no label to fall back on is no value (and never a negative one in a ratio)
    nothing = view("ground", "off", "1707x960", [16.0, 16.0], idle=9.6) + view("ground", "tvc", "1707x960", [14.0, 14.0], idle=10.0)
    rows = {(r["metric"], r["variant"]): r["value"] for r in metrics(merge([nothing]), [])}
    assert ("T09.perf.ground.1707x960.ms", "tvc") not in rows, rows
    # the tiers: a named EVE label wins over the broad cloud tier, a broad cloud label is used only when no named one is there, our own label never matches
    def tvc_report(label):
        one = report(9.0, [], [("ReDefinition clouds: end", 5.0), (label, 1.2), ("other", 1.0)])
        return ["T09 view begin: ground tvc 1707x960"] + one + one + ["T09 view end: ground tvc 1707x960"]
    found = merge([tvc_report("Camera: EVE/RaymarchCloud pass 1")])
    assert found[("tvc", "ground", "1707x960")]["labels"] == 1.2, found
    found = merge([tvc_report("Camera: sky clouds")])
    assert found[("tvc", "ground", "1707x960")]["labels"] == 1.2 and found[("tvc", "ground", "1707x960")]["matched"] == ["Camera: sky clouds"], found
    # two own starts keep both noise sets; the region check is waived at a cover below 0.5 and not at a real one
    def start_text(cover, region, flicker, floor):
        return "\n".join(['ATMORESULT {"series":"K-T2","metric":"T09.cover","value":%s}' % cover, noise("plain", "ground", "flicker", flicker),
                          noise("plain", "ground", "floor_flicker", floor), noise("plain", "ground", "region_fraction", region), noise("recon4", "ground", "flicker", flicker / 5.0)])
    out = series_results({"own1707": start_text(0.65, 0.3, 0.02, 0.005), "own2560": start_text(0.65, 0.3, 0.04, 0.005)})
    by = {(r["metric"], r["variant"]): r["value"] for r in out}
    assert by[("T09.noise.ground.flicker_over_plain.own1707", "recon4")] == 0.2 and by[("T09.noise.ground.flicker_over_plain.own2560", "recon4")] == 0.2, by
    assert by[("T09.noise.ground.flicker_over_floor.own1707", "plain")] == 4.0 and by[("T09.noise.ground.flicker_over_floor.own2560", "plain")] == 8.0, by
    assert by[("T09.noise.ground.flicker_over_floor", "plain")] == 8.0 and by[("T09.noise.region_ok", None)] == 1, by                        # the canonical name: the last start
    out = series_results({"own1707": start_text(0.3, 0.02, 0.02, 0.005)})
    assert {(r["metric"], r["variant"]): r["value"] for r in out}[("T09.noise.region_ok", None)] == 1 and any("waived" in n for n in NOTES), NOTES
    out = series_results({"own1707": start_text(0.65, 0.02, 0.02, 0.005)})
    assert {(r["metric"], r["variant"]): r["value"] for r in out}[("T09.noise.region_ok", None)] == 0, out
    print("kt2 selftest: ok")


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="*")
    ap.add_argument("--out")
    ap.add_argument("--tvc-pattern", default=None)
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
