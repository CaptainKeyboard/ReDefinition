#!/usr/bin/env python3
"""The metric script of series K-T3 (package T09): one start, many switches; the cost and the picture of each switch against the plain own pass.

  python tools/weather_metrics/kt3.py LOG [LOG ...]
  python tools/weather_metrics/kt3.py --selftest

The probe (src/Atmospheres/Light/Cloud/Probe/T09ProbeK3.cs, the table in T09ProbeK3.Table.cs) runs the variants of a start one after another in the view the table gives
(ids end in the view's letter: g ground, a above, h horizon, s sky3). Each variant has a K-T2 window ("T09 view begin / end: <view> <id> <W>x<H>", the GPU profiler's
reports inside it) and, except in sky3, a numeric frame (rows T09d.<view>.k3.<id>.* against the frame of the view's "off" window). tools/weather_metrics/kt2.py reads
the windows; this script reads the rows.

Per variant id of a start (results named K3.<start>.<id>.<stat>, no variant):
  cost_ms     GPU-busy time of the window (frame minus the profiler's idle segment) minus the busy time of the view's "off" window: what the clouds and the switch cost
  labels_ms   the own pass's two marker segments ("ReDefinition clouds"), when the pass draws
  delta_ms    cost of the variant minus the cost of its reference (plain, or the variant the table names), by the labels where both have them and the pass is the
              thing measured, else by the GPU-busy cost (lightning, rain, wet surfaces, droplets cost outside the pass's labels)
  d_picture   the distance of the variant's numeric frame from its reference's: the largest of the change in deck fraction, in the closed share of the sky, in
              the relative change of the deck median
  d_ground    the relative change of the sunlit ground's median
  flashes     (storm variants) the flashes counted in the window
Per start: windows, costed, cost_missing, cost_misses, effect_misses, pictures_missing, and a note line per miss. Across the starts: the T13 cost and shimmer, the
T16 ratio, the recon ratios, the resolution ratio, the haze equality. EXPECT below is the table of what each switch should do (written before the run; a miss is a
finding, not a fault of the script). Stdlib only.
"""
import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kt2  # noqa: E402

NOTES = []
LINE = re.compile(r'ATMORESULT\s*(\{.*\})')
BEGIN_SERIES = re.compile(r"Probe: series K-T3 start (\S+) begins with variants (\S+)")
PICTURE = re.compile(r"Probe: picture (\S+?)\.png")

# --- What each switch should do ----------------------------------------------------------------------------------------------------------------------
# Bands of delta_ms (variant minus reference) in milliseconds. The GPU-busy delta has about 0.5 ms of noise (K-T2: every view is CPU-bound), the labels figure much less.
SAVES, ADDS, FREE = (-8.0, 0.3), (-0.3, 8.0), (-0.6, 0.6)
T13 = (-0.5, 0.1)
VIEW_LETTER = {"g": "ground", "a": "above", "h": "horizon", "s": "sky3", "r": "ground"}
# effect: "changes" (the picture moves: d_picture >= 0.02), "same" (d_picture <= 0.05), "ground" (the ground's median moves by 3 percent or more), "any" (cost and pictures only).
CHANGES_MIN, SAME_MAX, GROUND_MIN = 0.02, 0.05, 0.03
HEIGHT = 0.0


class Ex(object):
    def __init__(self, group, cost=FREE, effect="any", ref=None, busy=False, note=""):
        self.group, self.cost, self.effect, self.ref, self.busy, self.note = group, cost, effect, ref, busy, note


def build_expect():
    e = {}

    def add(i, group, cost=FREE, effect="any", ref=None, busy=False, note=""):
        e[i] = Ex(group, cost, effect, ref, busy, note)

    for v in "gahsr":
        add("off_" + v, "base", None)
    for v in "gahs":
        add("plain_" + v, "base", None)
    # shape noise (T02), height variation (T13), look constants of CloudLookTuning
    add("noise_off_g", "noise", SAVES, "changes", note="no shape noise: smooth blobs, cheaper")
    add("untile_off_g", "noise", (-1.0, 0.3))
    add("curl_off_g", "noise", (-1.0, 0.3))
    add("fine_off_g", "noise", (-1.0, 0.3))
    add("dlod_off_g", "noise", ADDS, note="the distance detail LOD saves when on, so off costs the same or more")
    add("tier_low_g", "noise", FREE, "changes")
    add("tier_high_g", "noise", FREE, "changes")
    add("hash_off_g", "noise", FREE)
    add("h_all_off_g", "t13", T13, "changes", note="T13 brief: the four switches cost 0.1 ms at most")
    add("h_bvar_off_g", "t13", T13)
    add("h_bphys_off_g", "t13", T13)
    add("h_ragged_off_g", "t13", T13)
    add("h_top_off_a", "t13", T13)
    add("h_all_off_a", "t13", T13, "changes")
    add("remap1_g", "look", FREE, "changes")
    add("combine1_g", "look", FREE)
    add("sigma2_g", "look", (-1.5, 0.6), "changes")
    add("soft2_g", "look", FREE, "changes")
    add("namt2_g", "look", FREE, "changes")
    add("nsize2_g", "look", FREE, "changes")
    # light, shadows, steps
    add("lightq_low_g", "light", SAVES, "changes")
    add("lightq_med_g", "light", SAVES)
    add("oct1_g", "light", SAVES, "changes")
    add("powder0_g", "light", FREE, "changes")
    add("suntint_off_g", "light", FREE)
    add("moon0_g", "light", (-1.0, 0.3), "same", note="day: the moons add no light to the deck")
    add("aotaps_g", "light", ADDS)
    add("aoreuse2_g", "light", ADDS)
    add("wb_off_g", "light", FREE)
    add("sun9_g", "light", FREE, note="T14 measured 1.00 of the 4 km march")
    add("shadows_off_g", "shadow", SAVES, "ground", note="the ground loses the cloud shadow")
    add("shq_low_g", "shadow", SAVES)
    add("shq_high_g", "shadow", ADDS)
    add("shfloor_g", "shadow", FREE)
    add("diffdecay_g", "light", FREE)
    add("stepq_low_g", "step", SAVES)
    add("stepq_high_g", "step", ADDS)
    add("growth0_g", "step", ADDS)
    add("msb_hi_g", "light", FREE, "changes")
    add("iso0_g", "light", FREE)
    add("amb0_g", "light", FREE, "changes")
    add("floor_hi_g", "light", FREE, "changes")
    add("diff0_g", "light", FREE)
    add("skytint0_g", "light", FREE)
    # T16 culling (horizon, sky3), the keys on top of all six switches, T06 far view
    add("skip_empty_h", "cull", SAVES, "same", note="cloudSkipEmpty claims to be exact")
    add("dcut_h", "cull", SAVES)
    add("stepg_h", "cull", SAVES)
    add("tap_h", "cull", SAVES)
    add("detail_h", "cull", SAVES)
    add("cull_all_h", "cull", SAVES)
    for k in ("kcut", "kstepg", "ktapwin", "ktapf", "kfoot", "kcapkeep"):
        add(k + "_h", "cull", SAVES, ref="cull_all_h")
    # the aerial perspective is on by default behind Scatterer's scattering: switching it off shows its effect, switching it on draws the plain picture
    add("nohaze_h", "far", (-0.5, 0.3), "changes")
    add("haze_h", "far", FREE, "same")
    add("haze_tune_h", "far", FREE, "same")
    add("hazestr2_h", "far", FREE, "changes", ref="haze_h")
    add("shapemean_h", "far", SAVES)
    add("minsamp_h", "far", ADDS)
    add("farpolicy_h", "far", FREE)
    add("mapfade_h", "far", FREE)
    add("fade40_h", "far", SAVES, "changes")
    add("skip_closed_s", "cull", SAVES, "any", note="cloudSkipClosed claims to be exact")
    add("cull_all_s", "cull", SAVES)
    # reconstruction options
    add("rlow_g", "recon", SAVES, "changes")
    add("edge_g", "recon", SAVES, "changes")
    add("bisect2_g", "recon", ADDS)
    add("contours_g", "recon", ADDS)
    add("edgemask_g", "recon", SAVES)
    # lightning (storm 12 km east): cost outside the pass's labels
    add("ln_on_h", "lightning", (-0.8, 3.0), busy=True, ref="plain_h")
    add("ln_bolts_off_h", "lightning", (-3.0, 0.8), busy=True, ref="ln_on_h")
    for i in ("ln_terrain_off_h", "ln_cloud_off_h", "ln_bright05_h", "ln_max1_h"):
        add(i, "lightning", (-1.5, 0.8), busy=True, ref="ln_on_h")
    # rain with the wet surfaces and droplets
    # (letter r: the ground view in the rain; the delta is the raw GPU-busy time against the reference's, the rain being what changes the scene)
    add("rain_r", "rain", (-0.8, 8.0), "changes", ref="plain_g", busy=True)
    add("rain_wet_r", "rain", (-0.8, 1.0), "ground", ref="rain_r", busy=True, note="T08 brief: 0.3 ms at 1080p at most")
    add("rain_drops_r", "rain", (-0.8, 1.0), ref="rain_r", busy=True, note="T08 brief: 0.25 ms at 1080p at most")
    add("rain_both_r", "rain", (-0.8, 1.5), "ground", ref="rain_r", busy=True)
    add("wetdiff1_r", "rain", (-0.8, 0.8), "ground", ref="rain_wet_r", busy=True)
    add("wetdiff03_r", "rain", (-0.8, 0.8), "ground", ref="rain_wet_r", busy=True)
    add("dropref0_r", "rain", (-0.8, 0.8), ref="rain_drops_r", busy=True)
    add("dropcover_r", "rain", (-0.8, 1.0), ref="rain_drops_r", busy=True)
    add("precip_lo_r", "rain", (-8.0, 0.8), ref="rain_r", busy=True)
    # 2560x1440: the levels, the cost-bearing switches again, the per-layer look keys
    for lvl in ("recon2", "recon3", "recon4"):
        add(lvl + "_g", "recon", SAVES, "changes")
        add(lvl + "_a", "recon", SAVES, "changes")
    add("entry_g", "recon", ADDS)
    add("cap2_g", "step", SAVES)
    add("cache_g", "light", (-1.5, 0.8), note="K-T2: the cache saves nothing inside")
    add("lshift_g", "look", FREE, "changes")
    add("tfair_g", "look", FREE)
    add("toverride_g", "look", FREE, "changes")
    return e


EXPECT = build_expect()


# --- Reading the log -------------------------------------------------------------------------------------------------------------------------------

def rows_of(text):
    """{(variant, metric): last value} of the K-T3 ATMORESULT lines of one log."""
    out = {}
    for line in text.splitlines():
        m = LINE.search(line)
        if not m:
            continue
        try:
            obj = json.loads(m.group(1))
        except ValueError:
            continue
        if obj.get("series") != "K-T3" or not isinstance(obj.get("value"), (int, float)) or isinstance(obj.get("value"), bool):
            continue
        out[(obj.get("variant"), obj.get("metric"))] = float(obj["value"])
    return out


def start_of(text):
    """(start id, [variants]) from the probe's begin line, or (None, [])."""
    m = BEGIN_SERIES.search(text)
    return (m.group(1), m.group(2).split(",")) if m else (None, [])


def view_of(vid):
    return VIEW_LETTER.get(vid.rsplit("_", 1)[-1])


def off_of(vid):
    return "off_" + vid.rsplit("_", 1)[-1]


def plain_of(vid):
    return "plain_" + vid.rsplit("_", 1)[-1]


def busy(v):
    return kt2.busy_ms(v)


def window_cost(found, vid, res):
    """GPU-busy cost of a window against its view's off window, or None."""
    view = view_of(vid)
    v, base = found.get((vid, view, res)), found.get((off_of(vid), view, res))
    if v is None or base is None or busy(v) is None or busy(base) is None:
        return None
    return busy(v) - busy(base)


def picture_names(sid, view, vid):
    """[(kind, name part)] of the pictures a window must have saved. The numeric frame (T09ProbeDiag.DiagTake) names them t09-<start>-<view>_k3-<id> and
    t09-<start>-<view>_k3-aid1<id>; the sky3 view takes the K-T2 pictures t09-<start>-sky3-<id> (natural and aid-opticaldepth); an off window has the natural one only."""
    if view == "sky3":
        names = [("natural", "t09-%s-sky3-%s-" % (sid, vid))]
        return names if vid.startswith("off_") else names + [("aid-opticaldepth", "t09-%s-sky3-%s-" % (sid, vid))]
    names = [("natural", "t09-%s-%s_k3-%s-" % (sid, view, vid))]
    return names if vid.startswith("off_") else names + [("aid-direct", "t09-%s-%s_k3-aid1%s-" % (sid, view, vid))]


def picture_value(rows, view, vid, stat):
    return rows.get((vid, "T09d.%s.k3.%s.%s" % (view, vid, stat)))


def picture_distance(rows, view, vid, ref):
    """The largest of the changes in deck fraction, the sky's closed share and the relative change of the deck median between two numeric frames; None where none of
    them is in both."""
    parts = []
    for stat in ("deck_fraction", "sky_closed_share"):
        a, b = picture_value(rows, view, vid, stat), picture_value(rows, view, ref, stat)
        if a is not None and b is not None:
            parts.append(abs(a - b))
    a, b = picture_value(rows, view, vid, "deck_median"), picture_value(rows, view, ref, "deck_median")
    if a is not None and b is not None and b > 1e-3:
        parts.append(abs(a / b - 1.0))
    return max(parts) if parts else None


def ground_change(rows, view, vid, ref):
    a, b = picture_value(rows, view, vid, "ground_median"), picture_value(rows, view, ref, "ground_median")
    if a is None or b is None or b <= 1e-3:
        return None
    return abs(a / b - 1.0)


def rnd(x):
    return None if x is None else round(x, 4)


def start_results(sid, variants, text, found, rows, res, pictures, notes):
    """The results of one start: per id and the start's counts. Returns (results, per-id dict)."""
    out, per = [], {}
    stats = {"windows": 0, "costed": 0, "cost_missing": 0, "cost_misses": 0, "effect_misses": 0, "pictures_missing": 0}

    def add(name, value):
        if value is not None:
            out.append({"metric": "K3.%s.%s" % (sid, name), "value": rnd(value) if isinstance(value, float) else value, "variant": None})

    for vid in variants:
        ex = EXPECT.get(vid)
        view = view_of(vid)
        w = found.get((vid, view, res))
        if w is None:
            stats["cost_missing"] += 1
            notes.append("%s %s: no window (no clean report between its begin and end lines)" % (sid, vid))
            continue
        stats["windows"] += 1
        cost = window_cost(found, vid, res)
        labels = w["labels"]
        info = {"cost": cost, "labels": labels, "frame": w["frame"], "idle": w["idle"], "reports": w["reports"], "busy": busy(w)}
        per[vid] = info
        add("%s.cost_ms" % vid, cost)
        add("%s.labels_ms" % vid, labels)
        add("%s.frame_ms" % vid, w["frame"])
        if not vid.startswith("off_") and cost is None:
            stats["cost_missing"] += 1
            notes.append("%s %s: no cost (the window or its off window has no clean report)" % (sid, vid))
        if cost is not None:
            stats["costed"] += 1
        if ex is None:
            notes.append("%s %s: no expectation in kt3.EXPECT" % (sid, vid))
            continue
        flashes = rows.get((vid, "K3.flashes"))
        if flashes is not None:
            add("%s.flashes" % vid, flashes)
        # pictures: the natural one and, for a drawn variant, the aid one (names: see picture_names)
        for kind, name in picture_names(sid, view, vid):
            if not any(name in p and kind in p for p in pictures):
                stats["pictures_missing"] += 1
                notes.append("%s %s: no %s picture in the log" % (sid, vid, kind))
    # The judged part, against the references.
    for vid in variants:
        ex = EXPECT.get(vid)
        if ex is None or vid not in per or ex.cost is None:
            continue
        view = view_of(vid)
        ref = ex.ref or plain_of(vid)
        info, rinfo = per[vid], per.get(ref)
        delta = None
        if rinfo is not None:
            if ex.busy:
                if info["busy"] is not None and rinfo["busy"] is not None:
                    delta = info["busy"] - rinfo["busy"]
            elif info["labels"] is not None and rinfo["labels"] is not None:
                delta = info["labels"] - rinfo["labels"]
            elif info["cost"] is not None and rinfo["cost"] is not None:
                delta = info["cost"] - rinfo["cost"]
        add("%s.delta_ms" % vid, delta)
        info["delta"] = delta
        if delta is None:
            stats["cost_missing"] += 1
            notes.append("%s %s: no delta against %s" % (sid, vid, ref))
        elif not (ex.cost[0] <= delta <= ex.cost[1]):
            stats["cost_misses"] += 1
            notes.append("%s %s: delta %.2f ms against %s lies outside the expected [%.1f, %.1f]%s" % (sid, vid, delta, ref, ex.cost[0], ex.cost[1], " (" + ex.note + ")" if ex.note else ""))
        if ex.effect in ("any",) or view == "sky3":
            continue
        d, dg = picture_distance(rows, view, vid, ref), ground_change(rows, view, vid, ref)
        add("%s.d_picture" % vid, d)
        add("%s.d_ground" % vid, dg)
        if ex.effect == "changes":
            ok = d is not None and d >= CHANGES_MIN
        elif ex.effect == "same":
            ok = d is not None and d <= SAME_MAX
        elif ex.effect == "ground":
            ok = dg is not None and dg >= GROUND_MIN
        else:
            ok = True
        info["effect_ok"] = ok
        if not ok:
            stats["effect_misses"] += 1
            notes.append("%s %s: expected effect '%s' did not show (d_picture %s, d_ground %s)" % (sid, vid, ex.effect, "-" if d is None else "%.3f" % d, "-" if dg is None else "%.3f" % dg))
    for k, v in stats.items():
        add(k, v)
    return out, per


def series_results(texts):
    """What the runner calls: {start id: log text} -> the result lines of the series. NOTES is filled for the report."""
    del NOTES[:]
    out, per_start = [], {}
    for key, text in texts.items():
        sid, variants = start_of(text)
        sid = sid or key
        if not variants:
            NOTES.append("%s: no begin line of K-T3 in the log" % key)
            continue
        found = kt2.parse(text.splitlines())
        res = next((r for (_v, _view, r) in found), "1707x960")
        pictures = PICTURE.findall(text)
        rows = rows_of(text)
        results, per = start_results(sid, variants, text, found, rows, res, pictures, NOTES)
        out.extend(results)
        per_start[sid] = {"per": per, "rows": rows, "res": res, "found": found}
    out.extend(across(per_start))
    return out


def across(per_start):
    """The numbers that need more than one id or start."""
    out = []

    def add(name, value, variant=None):
        if value is not None:
            out.append({"metric": name, "value": rnd(value) if isinstance(value, float) else value, "variant": variant})

    def label(sid, vid):
        info = per_start.get(sid, {}).get("per", {}).get(vid)
        return None if info is None else info["labels"]

    near, light, far, fx, res = (per_start.get(s) for s in ("k3near", "k3light", "k3far", "k3fx", "k3res"))
    if near:
        p, a = label("k3near", "plain_g"), label("k3near", "h_all_off_g")
        if p is not None and a is not None:
            add("K3.t13_cost_ms", p - a)
        flick = lambda vid: near["rows"].get((vid, "T09.noise.ground.flicker"))
        if flick("plain_g") is not None and flick("h_ragged_off_g") is not None:
            add("K3.t13_shimmer_delta", flick("plain_g") - flick("h_ragged_off_g"))
        for vid in ("noise_off_g", "tier_low_g", "tier_high_g", "h_all_off_g"):
            if flick(vid) is not None and flick("plain_g"):
                add("K3.noise_flicker_over_plain.%s" % vid, flick(vid) / flick("plain_g"))
    if far:
        pl, ca = label("k3far", "plain_h"), label("k3far", "cull_all_h")
        if pl and ca is not None:
            add("K3.cull_all_over_plain.horizon", ca / pl)
        # the haze switch and the tuning key that forces it must draw the same picture
        d = picture_distance(far["rows"], "horizon", "haze_tune_h", "haze_h")
        add("K3.haze_tune_equals_setting", None if d is None else int(d <= 0.03))
    if fx:
        on, one = fx["rows"].get(("ln_on_h", "K3.flashes")), fx["rows"].get(("ln_max1_h", "K3.flashes"))
        if on is not None:
            add("K3.flashes_on", on)
        if on is not None and one is not None:
            add("K3.flashes_max1_not_more", int(one <= on))
    if res:
        for lvl in ("recon2", "recon3", "recon4"):
            for v in ("g", "a"):
                p, r = label("k3res", "plain_" + v), label("k3res", "%s_%s" % (lvl, v))
                if p and r is not None:
                    add("K3.%s_over_plain.%s" % (lvl, VIEW_LETTER[v]), r / p)
        if near:
            a, b = label("k3near", "plain_g"), label("k3res", "plain_g")
            if a and b is not None:
                add("K3.res_cost_ratio.plain_ground", b / a)
    return out


# --- The table the report prints ---------------------------------------------------------------------------------------------------------------------

def report_lines(results):
    lines = []
    for r in results:
        lines.append("%-62s %s" % (r["metric"], r["value"]))
    return lines


# --- Selftest ----------------------------------------------------------------------------------------------------------------------------------------

def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def table_ids():
    """The ids of the C# table, in order (T09ProbeK3.Table.cs)."""
    path = os.path.join(repo_root(), "src", "Atmospheres", "Light", "Cloud", "Probe", "T09ProbeK3.Table.cs")
    with open(path, encoding="utf-8") as f:
        text = f.read()
    ids = re.findall(r'(?:Add|OffOf|AddHeightOff|AddCullAll|Storm|Rain)\("([a-z0-9_]+)"', text)
    return ids


def selftest():
    def report(frame, groups, idle):
        lines = ["GPU profiler: 200 frames measured, GPU %.3f ms a frame from Present to Present" % frame, "By camera or light:",
                 "  frame start, GPU idle included %.3f ms (10.0 %%)" % idle]
        for name, ms in groups.items():
            lines.append("  %s %.3f ms (1.0 %%)" % (name, ms))
        lines.append("Costliest segments:")
        return lines

    def window(view, vid, frame, idle, labels=None, res="1707x960"):
        groups = {"ReDefinition clouds": labels} if labels is not None else {}
        body = report(frame, groups, idle)
        return ["T09 view begin: %s %s %s" % (view, vid, res)] + body + body + body + ["T09 view end: %s %s %s" % (view, vid, res)]

    def row(vid, metric, value):
        return 'ATMORESULT {"series":"K-T3","metric":"%s","value":%s,"variant":"%s"}' % (metric, value, vid)

    def frame_rows(view, vid, frac, med, closed, ground, r=0.0, b=0.0):
        p = "T09d.%s.k3.%s." % (view, vid)
        return [row(vid, p + "deck_fraction", frac), row(vid, p + "deck_median", med), row(vid, p + "sky_closed_share", closed), row(vid, p + "ground_median", ground),
                row(vid, p + "deck_r", r), row(vid, p + "deck_b", b)]

    ids = ["off_g", "plain_g", "noise_off_g", "h_all_off_g", "remap1_g", "moon0_g", "shadows_off_g"]
    def make(t13_labels=4.15, remap_med=0.36, noise_labels=3.0, ids_made=None, pictures=True):
        made = ids_made or ids
        text = ["Probe: series K-T3 start k3near begins with variants " + ",".join(ids)]
        labels = {"plain_g": 4.2, "noise_off_g": noise_labels, "h_all_off_g": t13_labels, "remap1_g": 4.2, "moon0_g": 4.0, "shadows_off_g": 4.0}
        frames = {"off_g": 10.0, "plain_g": 12.0, "noise_off_g": 11.0, "h_all_off_g": 12.0, "remap1_g": 12.0, "moon0_g": 12.0, "shadows_off_g": 11.8}
        for vid in made:
            text += window("ground", vid, frames[vid], 3.0, labels=labels.get(vid))
        for vid, frac, med, closed, ground, r, b in (("plain_g", 0.40, 0.30, 0.20, 0.70, 0.30, 0.24), ("noise_off_g", 0.46, 0.36, 0.26, 0.70, 0.3, 0.24),
                                                     ("h_all_off_g", 0.43, 0.30, 0.20, 0.70, 0.3, 0.24), ("remap1_g", 0.40, remap_med, 0.20, 0.70, 0.30, 0.24),
                                                     ("moon0_g", 0.40, 0.30, 0.20, 0.70, 0.3, 0.24), ("shadows_off_g", 0.40, 0.30, 0.20, 0.78, 0.3, 0.24)):
            text += frame_rows("ground", vid, frac, med, closed, ground, r, b)
        text += [row("plain_g", "T09.noise.ground.flicker", 0.0100), row("h_ragged_off_g", "T09.noise.ground.flicker", 0.0098)]
        if pictures:
            for vid in ids:
                text.append("Probe: picture t09-k3near-ground_k3-%s-1707x960-natural-half.png (read, 1 bytes)" % vid)
                if not vid.startswith("off_"):
                    text.append("Probe: picture t09-k3near-ground_k3-aid1%s-1707x960-aid-direct-half.png (read, 1 bytes)" % vid)
        return "\n".join(text)

    out = series_results({"k3near": make()})
    by = {r["metric"]: r["value"] for r in out}
    assert by["K3.k3near.plain_g.cost_ms"] == 2.0 and by["K3.k3near.noise_off_g.cost_ms"] == 1.0, by
    assert abs(by["K3.k3near.noise_off_g.delta_ms"] + 1.2) < 1e-6, by                  # labels: 3.0 - 4.2
    assert abs(by["K3.t13_cost_ms"] - 0.05) < 1e-6, by
    assert by["K3.k3near.windows"] == 7 and by["K3.k3near.cost_missing"] == 0, by
    assert by["K3.k3near.cost_misses"] == 0, (by, NOTES)
    assert by["K3.k3near.pictures_missing"] == 0, (by, NOTES)
    assert abs(by["K3.k3near.noise_off_g.d_picture"] - 0.2) < 1e-6, by                 # the median moved by 20 percent
    assert by["K3.k3near.effect_misses"] == 0, (by, NOTES)                              # noise_off changes, remap1 changes, moon0 same, shadows_off moves the ground
    assert abs(by["K3.t13_shimmer_delta"] - 0.0002) < 1e-9, by
    # a miss is counted and named: the remap does not change the picture, the moon changes the picture, the T13 switches cost 0.8 ms
    out = series_results({"k3near": make(remap_med=0.30)})
    by = {r["metric"]: r["value"] for r in out}
    assert by["K3.k3near.effect_misses"] == 1 and by["K3.k3near.cost_misses"] == 0, (by, NOTES)
    assert any("remap1_g" in n and "effect" in n for n in NOTES), NOTES
    # the T13 switches cost 0.8 ms: a cost miss, named; noise_off that costs more than the plain pass: a second one
    out = series_results({"k3near": make(t13_labels=3.4, noise_labels=5.5)})
    by = {r["metric"]: r["value"] for r in out}
    assert by["K3.k3near.cost_misses"] == 2, (by, NOTES)
    assert any("noise_off_g" in n and "delta" in n for n in NOTES) and any("h_all_off_g" in n and "delta" in n for n in NOTES), NOTES
    # a variant without a window is named
    out = series_results({"k3near": make(ids_made=["off_g"])})
    by = {r["metric"]: r["value"] for r in out}
    assert by["K3.k3near.cost_missing"] >= 6 and any("plain_g" in n for n in NOTES), (by, NOTES)
    # pictures that are not in the log are counted
    out = series_results({"k3near": make(pictures=False)})
    by = {r["metric"]: r["value"] for r in out}
    assert by["K3.k3near.pictures_missing"] == 13, (by, NOTES)                                # 7 natural + 6 aid
    # a storm variant is judged by the raw GPU-busy time against its reference (its cost lies outside the pass's labels), the flashes come out per variant
    fx = ["Probe: series K-T3 start k3fx begins with variants off_h,plain_h,ln_on_h,ln_bolts_off_h"]
    fx += window("horizon", "off_h", 9.0, 3.0) + window("horizon", "plain_h", 11.0, 3.0, labels=2.0) + window("horizon", "ln_on_h", 11.9, 3.0, labels=2.0)
    fx += window("horizon", "ln_bolts_off_h", 11.2, 3.0, labels=2.0)
    fx += [row("ln_on_h", "K3.flashes", 14), row("ln_bolts_off_h", "K3.flashes", 13)]
    out = series_results({"k3fx": "\n".join(fx)})
    by = {r["metric"]: r["value"] for r in out}
    assert abs(by["K3.k3fx.ln_on_h.delta_ms"] - 0.9) < 1e-6 and abs(by["K3.k3fx.ln_bolts_off_h.delta_ms"] + 0.7) < 1e-6, by
    assert by["K3.k3fx.ln_on_h.flashes"] == 14 and by["K3.flashes_on"] == 14 and by["K3.k3fx.cost_misses"] == 0, (by, NOTES)
    # the table: the ids of the C# file are the keys of EXPECT, in a view the id's letter names; the JSON lists only known ids, 26 or fewer a start, every id once in a start
    ids_cs = table_ids()
    assert len(ids_cs) == len(set(ids_cs)), "duplicate id in the C# table: %s" % sorted(i for i in ids_cs if ids_cs.count(i) > 1)
    assert set(ids_cs) == set(EXPECT), "C# table and EXPECT differ: only in C# %s, only in EXPECT %s" % (sorted(set(ids_cs) - set(EXPECT)), sorted(set(EXPECT) - set(ids_cs)))
    for i in ids_cs:
        assert i.rsplit("_", 1)[-1] in VIEW_LETTER, i
    path = os.path.join(repo_root(), "tests", "series", "K-T3.json")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            defn = json.load(f)
        used = set()
        for st in defn["starts"]:
            assert len(st["variants"]) <= 26, "%s holds %d variants" % (st["id"], len(st["variants"]))
            assert len(st["variants"]) == len(set(st["variants"])), st["id"]
            assert set(st["variants"]) <= set(ids_cs), (st["id"], sorted(set(st["variants"]) - set(ids_cs)))
            used |= set(st["variants"])
            seen_letters = []
            for vid in st["variants"]:
                letter = vid.rsplit("_", 1)[-1]
                if not seen_letters or seen_letters[-1] != letter:
                    assert letter not in seen_letters, "%s visits the block %s twice" % (st["id"], letter)
                    seen_letters.append(letter)
                    assert vid.startswith("off_"), "%s: the block %s must open with its off window, not %s" % (st["id"], letter, vid)
            for vid in st["variants"]:
                ex = EXPECT[vid]
                ref = ex.ref or plain_of(vid)
                if ex.cost is not None:
                    ok = ref in st["variants"] and st["variants"].index(ref) < st["variants"].index(vid)
                    assert ok, "%s: %s needs its reference %s before it" % (st["id"], vid, ref)
        assert used == set(ids_cs), "ids in no start: %s" % sorted(set(ids_cs) - used)
    print("kt3 selftest: ok")


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="*")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        selftest()
        return 0
    if not a.logs:
        ap.error("a log is needed (or --selftest)")
    texts = {}
    for path in a.logs:
        with open(path, encoding="utf-8", errors="replace") as f:
            texts[os.path.basename(path)] = f.read()
    out = series_results(texts)
    for line in report_lines(out):
        print(line)
    for n in NOTES:
        print("note: " + n)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
