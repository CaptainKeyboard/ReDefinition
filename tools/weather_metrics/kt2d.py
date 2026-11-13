#!/usr/bin/env python3
"""The metric script of series K-T2d (package T09): the diagnosis of the black own clouds, read from the probe's rows.

  python tools/weather_metrics/kt2d.py LOG [LOG ...]
  python tools/weather_metrics/kt2d.py --selftest

The probe (src/Atmospheres/Light/Cloud/Probe/T09ProbeDiag.cs) writes ATMORESULT lines T09d.<view>.<cover>.<gain>.<stat> (raw luminance of the camera
target in three regions: sky above the horizon, sunlit sea or ground below it, the deck the pass changed against the baseline). This script adds, per
view and cover, three numbers that read the units without a constant fit, and a table in the report:

  T09d.<view>.<cover>.reach_gain   the sun gain at which the deck would reach the baseline sky under proportional growth: 1 / deck_over_off_sky(gain 1)
  T09d.<view>.<cover>.slope        the exponent of the deck median against the gain over the gains that stay below the clip: 1.0 means proportional
                                   (the pass's radiance is all there is), near 0 means something after the pass clamps or replaces it
  T09d.<view>.<cover>.verdict      1 prediction A (units or composition; nothing clamps: slope 0.8 to 1.2), -1 prediction B (slope under 0.5), 0 between
                                   or not measurable (no deck, or every gain clipped)

The expected values are in tests/series/K-T2d.json (description) and the rows of tests/series/rows/T09.json. Stdlib only.
"""
import argparse
import json
import math
import re
import sys

LINE = re.compile(r'ATMORESULT\s*(\{.*\})')
GAINS = (1, 4, 16)
NOTES = []
CLIP = 0.98


def parse(texts):
    """{(view, cover, gain tag, stat): value} from the logs ({start: text} or a list of lines)."""
    values = {}
    for text in texts:
        for line in text.splitlines():
            m = LINE.search(line)
            if not m:
                continue
            try:
                obj = json.loads(m.group(1))
            except ValueError:
                continue
            metric, value = obj.get("metric", ""), obj.get("value")
            parts = metric.split(".")
            if obj.get("series") != "K-T2d" or parts[0] != "T09d" or len(parts) < 5 or not isinstance(value, (int, float)):
                continue
            values[(parts[1], parts[2], parts[3], ".".join(parts[4:]))] = float(value)
    return values


def slope(points):
    """Least-squares exponent of value against gain in log-log space, or None for fewer than two usable points."""
    pts = [(math.log(g), math.log(v)) for g, v in points if v > 1e-6]
    if len(pts) < 2:
        return None
    mx = sum(x for x, _ in pts) / len(pts)
    my = sum(y for _, y in pts) / len(pts)
    var = sum((x - mx) ** 2 for x, _ in pts)
    if var <= 0:
        return None
    return sum((x - mx) * (y - my) for x, y in pts) / var


GPU_TOLERANCE = 0.05


def layers_table(values, view, cover, region, get, add, notes):
    """K-T2d start 50: the per-layer table of the ground view. Frames only<k> (layer k alone, shift 0) and only<k>c35 (shift -0.15) beside the layer rows
    L<k>.* (the CPU map, the GPU map, the pass's own sample, the model's closed share at the CPU map). Metrics T09d.<view>.<cover>.layers.*:
      L<k>.closed_p1 / closed_c35      the picture's closed share of the region with only layer k drawn
      L<k>.closed_minus_model_p1       the drawn closed share minus the model's at the CPU map value (a layer whose map the pass reads as the CPU does: near 0)
      L<k>.gpu_minus_cpu               the pass's map value minus the CPU readers' (the uploaded bytes at the vessel's place)
      sheet_layer                      the layer whose own closed share is the highest at shift 0 (-1: none measured)
      gpu_matches_cpu                  1 when every layer's GPU and pass-sample map are within GPU_TOLERANCE of the CPU map, -1 when one is not, absent without GPU rows
    """
    def row(k, stat):
        return values.get((view, cover, "L%d" % k, stat))

    ks = sorted({int(t[1:]) for (v, c, t, s) in values if v == view and c == cover and re.match(r"^L\d+$", t)}
                | {int(re.match(r"^only(\d+)", t).group(1)) for (v, c, t, s) in values if v == view and c == cover and re.match(r"^only\d+", t)})
    if not ks:
        return
    best, best_k, mismatch, have_gpu = -1.0, -1, False, False
    lines = []
    for k in ks:
        p1, c35 = get("only%d" % k, "%s_closed_share" % region), get("only%dc35" % k, "%s_closed_share" % region)
        f1, f35 = get("only%d" % k, "deck_fraction"), get("only%dc35" % k, "deck_fraction")
        m1, m35 = get("only%d" % k, "deck_median"), get("only%dc35" % k, "deck_median")
        cpu, gpu, sh = row(k, "cpu_cover"), row(k, "gpu_cover"), row(k, "shader_cover")
        mod1, mod35 = row(k, "model_closed_p1"), row(k, "model_closed_c35")
        pre = "T09d.%s.%s.layers.L%d." % (view, cover, k)
        if p1 is not None:
            add(pre + "closed_p1", p1)
            if p1 > best:
                best, best_k = p1, k
            if mod1 is not None:
                add(pre + "closed_minus_model_p1", p1 - mod1)
        if c35 is not None:
            add(pre + "closed_c35", c35)
            if mod35 is not None:
                add(pre + "closed_minus_model_c35", c35 - mod35)
        for name, val in (("gpu", gpu), ("shader", sh)):
            if val is not None and cpu is not None:
                have_gpu = True
                add(pre + name + "_minus_cpu", val - cpu)
                if abs(val - cpu) > GPU_TOLERANCE:
                    mismatch = True

        def f(v, fmt="%.3f"):
            return "-" if v is None else fmt % v
        lines.append("L%d cpu %s gpu %s pass %s (type %s) | drawn closed p1 %s c35 %s | model p1 %s c35 %s | deck_fraction %s / %s | deck median %s / %s" % (
            k, f(cpu), f(gpu), f(sh), f(row(k, "cpu_type")), f(p1, "%.2f"), f(c35, "%.2f"), f(mod1, "%.2f"), f(mod35, "%.2f"), f(f1, "%.2f"), f(f35, "%.2f"), f(m1), f(m35)))
    add("T09d.%s.%s.layers.sheet_layer" % (view, cover), best_k)
    if have_gpu:
        add("T09d.%s.%s.layers.gpu_matches_cpu" % (view, cover), -1 if mismatch else 1)
    surface, cold = values.get((view, cover, "layers", "surface_temp_k")), values.get((view, cover, "layers", "stack_cold"))
    head = "%s %s layers" % (view, cover)
    notes.append("%-12s one layer at a time, closed share of the %s (sheet layer %s; surface %s K, cold members %s)" % (
        head, region, "-" if best_k < 0 else "L%d" % best_k, "-" if surface is None else "%.1f" % surface, "-" if cold is None else "yes" if cold else "no"))
    for line in lines:
        notes.append("%-12s %s" % (head, line))


LOOK_SET = re.compile(r"^k(C\d)(m8)?$")
# The acceptance rows of the E40 plan (section 4) that the look starts can read, with the numbers written there.
A1_BAND = (-0.20, 0.15)      # closed share at shift 0 between map - 0.20 and map + 0.15
A1_FALL = 0.10               # and a fall of at least this per -0.08 of shift
A2_TOWERS = (0.10, 0.30)     # ground high at shift 0
A2_OPEN = 0.15               # and under this at -0.08 (the plan's 0.05 is at -0.15; start 51 takes -0.08)
A4_DECK = 0.60               # the rain deck alone at the T2 pick
A5_BR = (0.80, 0.92)         # deck b over r for the lit part at 45 degrees
A5_PLATEAU = 0.05            # sky_over_off_sky and ground_over_off_ground within 1 +- this
ORDER_STEP_ON, ORDER_STEP_OFF = 0.08, 0.15     # E40 start 52: the colour step at the sea horizon's row over the baseline's, with cloud.afterScatterer 1 (at most) and 0 (at least)
A7_STEP = 0.10              # the horizon band's change against the baseline


PROFILE_BANDS = 5
PROFILE_FLOOR_MIN = 0.02       # a difference between the two orders counts as an effect when it exceeds this and twice the repeat's difference


def _mean(vals):
    vals = [v for v in vals if v is not None]
    return sum(vals) / len(vals) if vals else None


def order_profile(frames, get, add, notes, pre, head):
    """Start 52 follow-up: the vertical row profile (rows prof_b<k> below the horizon row, prof_a<k> above it; k 0 nearest) of the order frames ord1, ord0 and their repeats ord0b, ord1b
    (order 1, 0, 0, 1), the colour of the deck row by row with the mound rims left out. Per band and channel: the mean of the two ord0 frames minus the mean of the two ord1 frames
    (order.<band>.d_r / d_g / d_b), the floor (the larger of the repeats' differences |ord1 - ord1b| and |ord0 - ord0b|, largest channel; order.<band>.floor), a verdict
    order.<band>.moves (1 when the largest channel of d exceeds PROFILE_FLOOR_MIN and twice the floor, -1 when it does not), and the distance of each order's deck colour to the
    baseline's over the same samples (order.<band>.gap_ord1 / gap_ord0: the deck's distance to the haze; a deck fading into it approaches 0 towards the row)."""
    if not any(f.startswith("ord") for f in frames):
        return
    ons, offs = ("ord1", "ord1b"), ("ord0", "ord0b")
    for side, name in (("b", "below"), ("a", "above")):
        for k in range(PROFILE_BANDS):
            band = "%s%d" % (side, k)

            def p(frame, ch, band=band):
                return get(frame, "prof_%s_%s" % (band, ch))
            d, floor_parts, line = {}, [], []
            for ch in ("r", "g", "b"):
                on, off = _mean([p(f, ch) for f in ons]), _mean([p(f, ch) for f in offs])
                if on is None or off is None:
                    continue
                d[ch] = off - on
                add(pre + "order.%s.d_%s" % (band, ch), d[ch])
                for pair in (ons, offs):
                    a, b = p(pair[0], ch), p(pair[1], ch)
                    if a is not None and b is not None:
                        floor_parts.append(abs(a - b))
            if not d:
                continue
            floor = max(floor_parts) if floor_parts else None
            biggest = max(abs(v) for v in d.values())
            if floor is not None:
                add(pre + "order.%s.floor" % band, floor)
            add(pre + "order.%s.moves" % band, 1 if biggest > max(PROFILE_FLOOR_MIN, 2.0 * (floor or 0.0)) else -1)
            gaps = []
            for tag, frames_of in (("ord1", ons), ("ord0", offs)):
                best = None
                for f in frames_of:
                    vals = [(p(f, c), p(f, "b" + c)) for c in ("r", "g", "b")]
                    if all(a is not None and b is not None for a, b in vals):
                        dist = max(abs(a - b) for a, b in vals)
                        best = dist if best is None else (best + dist) / 2.0
                if best is not None:
                    add(pre + "order.%s.gap_%s" % (band, tag), best)
                    gaps.append("%s %.3f" % (tag, best))
            # The pass's own aerial perspective at the strength 1 and 2 (frames hh1, hh2; the order at its default 1) against the order 0 frames: Scatterer's air is the reference.
            for hh in ("hh1", "hh2"):
                parts = []
                for ch in ("r", "g", "b"):
                    a, ref = p(hh, ch), _mean([p(f, ch) for f in offs])
                    if a is not None and ref is not None:
                        add(pre + "order.%s.%s_minus_ord0_%s" % (band, hh, ch), a - ref)
                        parts.append("%s %+.3f" % (ch, a - ref))
                vals = [(p(hh, c), p(hh, "b" + c)) for c in ("r", "g", "b")]
                if all(a is not None and b is not None for a, b in vals):
                    add(pre + "order.%s.gap_%s" % (band, hh), max(abs(a - b) for a, b in vals))
                if parts:
                    line.append("%s - ord0 %s" % (hh, " ".join(parts)))
            line.append("ord0 - ord1 r %+.3f g %+.3f b %+.3f" % (d.get("r", float("nan")), d.get("g", float("nan")), d.get("b", float("nan"))))
            line.append("floor %s" % ("-" if floor is None else "%.3f" % floor))
            if gaps:
                line.append("distance to the baseline: " + ", ".join(gaps))
            notes.append("%-12s order profile %s band %d: %s" % (head, name, k, "; ".join(line)))


COLOUR55 = ("ca02t0", "ca02t5", "ca02t10", "caDt0", "caDt5", "caDt10", "cair", "ce14", "csky0", "csky1", "caDt0b")
GRAIN55 = ("gacc", "gleapacc")
FOG55 = ("wC5o0", "wC5o0f", "kC5", "kC5f")
NOISE55 = re.compile(r"^f\d+s\d+$")


def look55_table(frames, get, add, notes, pre, head, region):
    """Start look55 (E40, T09ProbeLook55): the colour of the deck (b over r of the deck's median colour, per frame), the grain pair, the fog frames, the noise-scale frames and the
    shadow pair, as rows and notes. The verdicts are the result file's; the rows only carry the numbers."""
    for f in COLOUR55:
        r, g, b = get(f, "deck_r"), get(f, "deck_g"), get(f, "deck_b")
        if r and b is not None:
            add(pre + "%s.deck_b_over_r" % f, b / r)
            notes.append("%-12s colour %-8s deck r %.3f g %s b %.3f (b/r %.3f), deck median %s" % (
                head, f, r, "-" if g is None else "%.3f" % g, b, b / r, "-" if get(f, "deck_median") is None else "%.3f" % get(f, "deck_median")))
    for v in GRAIN55:
        tstd, hfx = get(v, "grain_tstd"), get(v, "grain_hfx")
        if tstd is not None:
            notes.append("%-12s grain %-8s tstd %.4f hfx %s over %s pixels, history cuts %s" % (
                head, v, tstd, "-" if hfx is None else "%+.4f" % hfx, "-" if get(v, "grain_n") is None else "%d" % get(v, "grain_n"), "-" if get(v, "grain_cuts") is None else "%d" % get(v, "grain_cuts")))
    for f in FOG55:
        sky, gr = get(f, "sky_closed_share"), get(f, "ground_closed_share")
        if sky is not None or gr is not None:
            notes.append("%-12s fog frame %-6s closed sky %s ground %s, deck median %s" % (
                head, f, "-" if sky is None else "%.3f" % sky, "-" if gr is None else "%.3f" % gr, "-" if get(f, "deck_median") is None else "%.3f" % get(f, "deck_median")))
    for f in frames:
        if NOISE55.match(f):
            got = get(f, "%s_closed_share" % region)
            tau = get("aid1" + f, "aidtau_closed_share")
            if got is not None:
                add(pre + "%s.closed" % f, got)
            notes.append("%-12s noise-scale frame %-7s closed %s, tau closed %s, deck median %s" % (
                head, f, "-" if got is None else "%.3f" % got, "-" if tau is None else "%.3f" % tau, "-" if get(f, "deck_median") is None else "%.3f" % get(f, "deck_median")))
    s1, s0 = get("sh1", "ground_median"), get("sh0", "ground_median")
    if s1 is not None and s0 is not None:
        add(pre + "shadow.ground_median_sh0_minus_sh1", s0 - s1)
        notes.append("%-12s shadow shape noise: sea/ground median %.4f with 1, %.4f with 0 (difference %+.4f)" % (head, s1, s0, s0 - s1))


def cost55_rows(texts, notes):
    """Start look55's cost windows: the GPU profiler's reports between the probe's 'T09 view begin / end' lines (kt2.py's parser), per view the frame, the GPU-busy time and the labels
    figure of off / plain / leap / shape0 and the differences against plain. Empty where the log holds no report."""
    # The runner loads this script by its path, so a sibling is loaded by its path as well.
    import importlib.util
    import os
    spec = importlib.util.spec_from_file_location("atmo_kt2", os.path.join(os.path.dirname(os.path.abspath(__file__)), "kt2.py"))
    kt2 = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(kt2)
    lines = []
    for t in texts:
        lines.extend(t.splitlines())
    out = []
    try:
        found = kt2.parse(lines)
    except Exception as e:      # a log that kt2 cannot read gives no cost rows, not a failed script
        notes.append("cost windows: kt2.parse failed: %s" % e)
        return out
    for (variant, view, res), v in sorted(found.items()):
        if variant not in ("off", "plain", "leap", "shape0"):
            continue
        busy = kt2.busy_ms(v)
        idle = kt2.idle_share(v)
        base = found.get(("plain", view, res))
        pbusy = kt2.busy_ms(base) if base else None
        row = "T09d.%s.cost55.%s." % (view, variant)
        for name, value in (("frame_ms", v["frame"]), ("busy_ms", busy), ("labels_ms", v["labels"]), ("idle_pct", None if idle is None else 100.0 * idle), ("reports", v["reports"])):
            if value is not None:
                out.append({"metric": row + name, "value": round(value, 4) if isinstance(value, float) else value, "variant": variant})
        if base and variant != "plain":
            for name, a, b in (("frame_ms", v["frame"], base["frame"]), ("busy_ms", busy, pbusy), ("labels_ms", v["labels"], base["labels"])):
                if a is not None and b is not None:
                    out.append({"metric": row + "d_" + name + "_vs_plain", "value": round(a - b, 4), "variant": variant})
        notes.append("cost %s %s %s: frame %s ms, busy %s ms, labels %s ms, GPU idle %s percent, %d clean report(s)" % (
            view, res, variant, "-" if v["frame"] is None else "%.3f" % v["frame"], "-" if busy is None else "%.3f" % busy,
            "-" if v["labels"] is None else "%.3f" % v["labels"], "-" if idle is None else "%.0f" % (100.0 * idle), v["reports"]))
    return out


def look_table(values, view, cover, get, add, notes):
    """The look starts' frames (T09ProbeLook): per set the closed share at shift 0 and -0.08 beside the expectation the CPU oracle wrote before the frame, the forced-map frame,
    the layer-alone and rain-deck frames, the light variants and the horizon. Verdicts are 1 (inside), -1 (outside) against the plan's acceptance rows."""
    frames = sorted({f for (v, c, f, s) in values if v == view and c == cover})
    sets = sorted({LOOK_SET.match(f).group(1) for f in frames if LOOK_SET.match(f)})
    region = "sky" if view == "ground" else "ground"
    head = "%s %s" % (view, cover)
    pre = "T09d.%s.%s.look." % (view, cover)
    sheet_map = values.get((view, cover, "L1", "map_at_0_km"))
    for st in sets:
        parts = []
        s0, s8 = get("k%s" % st, "%s_closed_share" % region), get("k%sm8" % st, "%s_closed_share" % region)
        e0, e8 = get("k%s" % st, "expect_closed_mean"), get("k%sm8" % st, "expect_closed_mean")
        for tag, got, exp, lo, hi in (("s0", s0, e0, get("k%s" % st, "expect_closed_p10"), get("k%s" % st, "expect_closed_p90")),
                                      ("s8", s8, e8, get("k%sm8" % st, "expect_closed_p10"), get("k%sm8" % st, "expect_closed_p90"))):
            if got is None:
                continue
            add(pre + "%s.closed_%s" % (st, tag), got)
            parts.append("%s %.2f" % (tag, got))
            if exp is not None:
                add(pre + "%s.closed_minus_expect_%s" % (st, tag), got - exp)
                parts[-1] += " (expect %.2f)" % exp
                if lo is not None and hi is not None:
                    add(pre + "%s.in_spread_%s" % (st, tag), 1 if lo - 0.05 <= got <= hi + 0.05 else -1)
        if s0 is not None and s8 is not None:
            add(pre + "%s.fall" % st, s0 - s8)
            if view == "ground" and cover == "low":
                # A1 at the one map of this pick (the sheet layer's value at the vessel); the plan's three maps are in the player's table.
                ok_band = sheet_map is not None and sheet_map + A1_BAND[0] <= s0 <= sheet_map + A1_BAND[1]
                add(pre + "%s.a1_band" % st, 1 if ok_band else -1)
                add(pre + "%s.a1_fall" % st, 1 if s0 - s8 >= A1_FALL else -1)
            if view == "ground" and cover == "high":
                add(pre + "%s.a2_towers" % st, 1 if (A2_TOWERS[0] <= s0 <= A2_TOWERS[1] and s8 <= A2_OPEN) else -1)
        notes.append("%-12s set %s closed share of the %s: %s" % (head, st, region, ", ".join(parts)))
    # The optical depth (aid frame 1) of a set's frame beside its colour-change closed share and the oracle's zenith expectation (E40 follow-up of start 51: closed by colour counts a thin
    # saturated veil as closed; the tau separates a veil from a thick sheet). The aid tau closed share counts tau >= 0.92 over the same region.
    for f in frames:
        m = re.match(r"^aid1(k(C\d)|w(C\d)o\d|l0in|l0mid|l0up)$", f)
        if not m:
            continue
        natural = f[4:]
        tag = m.group(2) or natural
        tau_closed, tau_med, tau_deep = get(f, "aidtau_closed_share"), get(f, "aidtau_median"), get(f, "aidtau_deep_share")
        by_colour = get(natural, "%s_closed_share" % region)
        if tau_closed is None:
            continue
        add(pre + "%s.aidtau_closed" % tag, tau_closed)
        if tau_med is not None:
            add(pre + "%s.aidtau_median" % tag, tau_med)
        if by_colour is not None:
            add(pre + "%s.closed_colour_minus_tau" % tag, by_colour - tau_closed)
        zen, zlo, zhi = get(natural, "expect_tau_zenith_mean"), get(natural, "expect_tau_zenith_p10"), get(natural, "expect_tau_zenith_p90")
        top = get(f, "aidtau_top_median")
        parts = ["tau closed %.2f" % tau_closed, "deep %s" % ("-" if tau_deep is None else "%.2f" % tau_deep), "median %s" % ("-" if tau_med is None else "%.2f" % tau_med),
                 "by colour %s" % ("-" if by_colour is None else "%.2f" % by_colour)]
        if top is not None:
            parts.append("top rows median %.2f" % top)
        if zen is not None:
            parts.append("oracle zenith tau %.2f (%.2f to %.2f)" % (zen, zlo if zlo is not None else float("nan"), zhi if zhi is not None else float("nan")))
        notes.append("%-12s aid tau %s: %s" % (head, natural, ", ".join(parts)))
    # The forced map: the pass at a constant map the oracle knows.
    ovr, expo = get("kovr", "%s_closed_share" % region), get("kovr", "expect_closed_mean")
    base = get("kC0", "%s_closed_share" % region)
    if ovr is not None and expo is not None:
        add(pre + "ovr.closed", ovr)
        add(pre + "ovr.closed_minus_expect", ovr - expo)
        verdict = None
        if base is not None and get("kC0", "expect_closed_mean") is not None:
            expect_base = get("kC0", "expect_closed_mean")
            if abs(ovr - expo) <= 0.20 and base - expect_base > 0.25:
                verdict = 1      # the map varied over the view: the game with a constant map is the oracle again, the game with its own map is not
            elif ovr - expo > 0.25:
                verdict = -1     # the sheet stays under a constant map: the pass (or the frame's settings) differs from the CPU
            elif abs(base - expect_base) <= 0.25:
                verdict = 0      # no sheet to explain here
            if verdict is not None:
                add(pre + "ovr.map_varies_over_view", verdict)
        notes.append("%-12s forced map: closed %.2f against the oracle's %.2f; own map %s against %s" % (
            head, ovr, expo, "-" if base is None else "%.2f" % base, "-" if get("kC0", "expect_closed_mean") is None else "%.2f" % get("kC0", "expect_closed_mean")))
    # The layers alone under the winning set, the rain deck at light precipitation (A4) and from inside and above.
    for f in frames:
        m = re.match(r"^w(C\d)o(\d)$", f)
        if m:
            got = get(f, "%s_closed_share" % region)
            if got is not None:
                add(pre + "%s.only%s.closed" % (m.group(1), m.group(2)), got)
                if m.group(2) == "0" and cover == "high":
                    add(pre + "%s.a4_rain_deck" % m.group(1), 1 if got >= A4_DECK else -1)
        if f in ("l0in", "l0mid", "l0up"):
            sky = get(f, "sky_closed_share")
            gr = get(f, "ground_closed_share")
            if sky is not None or gr is not None:
                add(pre + "%s.closed" % f, max(x for x in (sky, gr) if x is not None))
    # E40 start 52: the order pair (cloud.afterScatterer 1 / 0) and the colour next to the sea horizon's row. The step is the largest channel of |frame colour above the row - below it|;
    # the baseline's own step is what sky and sea differ by. The rule written before the run: with the switch on the step is no more than 0.08 over the baseline's, with it off the
    # step returns (0.15 over the baseline's or more, as start 51's above-low kC0: 0.28 against 0.06).
    off_step = get("off", "line_step_colour")
    for f in frames:
        s = get(f, "line_step_colour")
        if s is not None and off_step is not None:
            add(pre + "%s.line_step_over_off" % f, s - off_step)
    on, offf = get("ord1", "line_step_colour"), get("ord0", "line_step_colour")
    if on is not None and offf is not None and off_step is not None:
        add(pre + "order.step_on", on - off_step)
        add(pre + "order.step_off", offf - off_step)
        add(pre + "order.step_vanishes", 1 if (on - off_step <= ORDER_STEP_ON and offf - off_step >= ORDER_STEP_OFF) else -1)
        moves1, moves0 = get("ord1", "order_moves"), get("ord0", "order_moves")
        notes.append("%-12s order pair: step over the baseline's %.3f with afterScatterer 1, %.3f with 0 (moved frames %s / %s)" % (
            head, on - off_step, offf - off_step, "-" if moves1 is None else "%d" % moves1, "-" if moves0 is None else "%d" % moves0))
    order_profile(frames, get, add, notes, pre, head)
    for l in ("L0", "L1", "L2", "L3"):
        root = values.get((view, cover, l, "root_m"))
        if root is not None:
            notes.append("%-12s %s height offsets at the vessel: root %.0f m, top %.0f m, variation %.0f m" % (
                head, l, root, values.get((view, cover, l, "top_m"), float("nan")), values.get((view, cover, l, "variation_m"), float("nan"))))
    for f in ("q50", "q42"):
        got = get(f, "%s_closed_share" % region)
        if got is not None:
            add(pre + "%s.closed" % f, got)
    # Look B: the colour of the deck and the plateau rows.
    for f in ("t0", "t5", "t10", "e14", "air1"):
        r, b = get(f, "deck_r"), get(f, "deck_b")
        if r and b is not None:
            add(pre + "%s.deck_b_over_r" % f, b / r)
            add(pre + "%s.a5_colour" % f, 1 if A5_BR[0] <= b / r <= A5_BR[1] else -1)
        for stat in ("sky_over_off_sky", "ground_over_off_ground"):
            v = get(f, stat)
            if v is not None:
                add(pre + "%s.%s_ok" % (f, stat), 1 if abs(v - 1.0) <= A5_PLATEAU else -1)
        clip = get(f, "deck_clipped")
        if clip is not None:
            add(pre + "%s.a5_clipped" % f, 1 if clip < 0.001 else -1)
    look55_table(frames, get, add, notes, pre, head, region)
    # The horizon: the band above the horizon the sky region leaves out.
    for f in frames:
        if re.match(r"^hz\d+h[01]$", f):
            band, change = get(f, "horizon_band_closed"), get(f, "horizon_band_change")
            if band is not None:
                add(pre + "%s.band_closed" % f, band)
            if change is not None:
                add(pre + "%s.band_change" % f, change)
                add(pre + "%s.a7_step" % f, 1 if change <= A7_STEP else -1)
    if any(re.match(r"^hz\d+h[01]$", f) for f in frames):
        notes.append("%-12s horizon band change against the baseline: %s" % (head, ", ".join(
            "%s %.2f" % (f, get(f, "horizon_band_change")) for f in frames if re.match(r"^hz\d+h[01]$", f) and get(f, "horizon_band_change") is not None)))


def analyse(values, notes):
    out = []

    def add(metric, value):
        out.append({"metric": metric, "value": value, "variant": "plain"})

    views = sorted({k[0] for k in values})
    for view in views:
        for cover in sorted({k[1] for k in values if k[0] == view}):
            def get(gain, stat):
                # A frame whose pose was not the view's (pose_ok 0) is not used: the numbers of the wrong place say nothing about the deck.
                if values.get((view, cover, gain, "pose_ok")) == 0.0:
                    return None
                return values.get((view, cover, gain, stat))
            medians = [(g, get("g%d" % g, "deck_median")) for g in GAINS]
            below_clip = [(g, v) for g, v in medians if v is not None and (get("g%d" % g, "deck_clipped") or 0.0) < 0.05 and v < CLIP]
            s = slope(below_clip)
            ratio = get("g1", "deck_over_off_sky")
            head = "%s %s" % (view, cover)
            if ratio is not None and ratio > 1e-6:
                add("T09d.%s.%s.reach_gain" % (view, cover), 1.0 / ratio)
            if s is not None:
                add("T09d.%s.%s.slope" % (view, cover), s)
            verdict = 0
            if s is not None and len(below_clip) >= 2:
                verdict = 1 if 0.8 <= s <= 1.2 else (-1 if s < 0.5 else 0)
            add("T09d.%s.%s.verdict" % (view, cover), verdict)
            # The output stage (CloudOutputStage), gain 1, measured after the ladder: u1 the units alone (sun unit pi, no gamma), p1 the product's stage
            # (pi, gamma 2.2, exposure 1), pe2 the same at exposure 2. Expected, written before the start (ground view: sun 15.6 degrees up, sea 0.443, sky 0.458):
            #   u1 / g1 = pi (3.0 to 3.3: the deck is direct light; from above the ambient part is not scaled, so 1.5 to 2.6),
            #   p1 / off sea: ground 0.27 to 0.43 (0.34), above 0.8 to 1.4 (1.1); p1 / off sky (ground) 0.27 to 0.40,
            #   pe2 / p1 = 2 ^ (1 / 2.2) = 1.37 (1.30 to 1.40) while the deck is below the white (0.415).
            u1, p1, pe2, g1 = get("u1", "deck_median"), get("p1", "deck_median"), get("pe2", "deck_median"), get("g1", "deck_median")
            sea, sky = get("off", "ground_median"), get("off", "sky_median")
            stage_ok = None
            if u1 is not None and g1:
                add("T09d.%s.%s.stage.u1_over_g1" % (view, cover), u1 / g1)
            if p1 is not None and sea:
                add("T09d.%s.%s.stage.p1_over_sea" % (view, cover), p1 / sea)
                lo, hi = (0.27, 0.43) if view == "ground" else (0.8, 1.4)
                stage_ok = lo <= p1 / sea <= hi
            if p1 is not None and sky and view == "ground":
                add("T09d.%s.%s.stage.p1_over_sky" % (view, cover), p1 / sky)
            if p1 is not None and pe2 is not None and p1 > 1e-6:
                add("T09d.%s.%s.stage.pe2_over_p1" % (view, cover), pe2 / p1)
            if stage_ok is not None:
                add("T09d.%s.%s.stage.verdict" % (view, cover), 1 if stage_ok else -1)
                notes.append("%-12s stage: u1 %s p1 %s pe2 %s | p1 / sea %.2f -> %s" % (
                    head, "-" if u1 is None else "%.3f" % u1, "%.3f" % p1, "-" if pe2 is None else "%.3f" % pe2, p1 / sea,
                    "commensurable" if stage_ok else "outside the expected band"))
            # The coverage variants (cloud.coverageShift 0 = p1, -0.08 = c42, -0.15 = c35): the picture's closed share of the region where the deck is seen (the sky of the ground view,
            # the ground region of the above view) against CloudCoverModel's share at the pass's map value (model_closed, written by the probe before the frame is read).
            region = "sky" if view == "ground" else "ground"
            parts = []
            for tag in ("p1", "c42", "c35"):
                got, model = get(tag, "%s_closed_share" % region), get(tag, "model_closed")
                if got is None:
                    continue
                parts.append("%s %.2f%s" % (tag, got, "" if model is None else " (model %.2f)" % model))
                if model is not None:
                    add("T09d.%s.%s.cover.%s_closed_minus_model" % (view, cover, tag), got - model)
            p1c, c42c, c35c = (get(t, "%s_closed_share" % region) for t in ("p1", "c42", "c35"))
            if p1c is not None and c42c is not None and c35c is not None:
                # Lowering the threshold must lower the closed share (or leave it at 0 / 1 at the ends).
                add("T09d.%s.%s.cover.monotone" % (view, cover), 1 if (p1c + 0.02 >= c42c >= c35c - 0.02) else -1)
            if parts:
                notes.append("%-12s closed share of the %s: %s" % (head, region, ", ".join(parts)))
            layers_table(values, view, cover, region, get, add, notes)
            look_table(values, view, cover, get, add, notes)
            cells =" ".join("g%d %s" % (g, "-" if v is None else "%.3f" % v) for g, v in medians)
            notes.append("%-12s deck median: %s | off sky %s, off sea %s | slope %s verdict %s | reach gain %s" % (
                head, cells,
                "-" if get("off", "sky_median") is None else "%.3f" % get("off", "sky_median"),
                "-" if get("off", "ground_median") is None else "%.3f" % get("off", "ground_median"),
                "-" if s is None else "%.2f" % s, {1: "A (units)", -1: "B (clamped)", 0: "open"}[verdict],
                "-" if not ratio else "%.1f" % (1.0 / ratio)))
    return out


def series_results(texts):
    """What the runner calls: {start id: log text} -> extra result lines. NOTES is filled for the report."""
    del NOTES[:]
    return analyse(parse(texts.values()), NOTES) + cost55_rows(list(texts.values()), NOTES)


def _fake(view, cover, off_sky, off_sea, deck1, gain_exp, clip=1.0):
    """The rows a run would write for a deck of deck1 at gain 1 growing as gain ** gain_exp (clipped at clip)."""
    lines = []

    def row(metric, value):
        lines.append('ATMORESULT {"series":"K-T2d","metric":"T09d.%s.%s.%s","value":%r,"variant":"plain"}' % (view, cover, metric, value))
    row("off.sky_median", off_sky)
    row("off.ground_median", off_sea)
    for g in GAINS:
        v = min(clip, deck1 * g ** gain_exp)
        row("g%d.deck_median" % g, v)
        row("g%d.deck_clipped" % g, 1.0 if v >= clip else 0.0)
        row("g%d.deck_over_off_sky" % g, v / off_sky)
    return "\n".join(lines)


def selftest():
    # Prediction A: proportional growth until the clip (0.05 -> 0.2 -> 0.8 -> clipped).
    notes = []
    rows = analyse(parse([_fake("ground", "high", 0.5, 0.7, 0.05, 1.0)]), notes)
    got = {r["metric"]: r["value"] for r in rows}
    assert abs(got["T09d.ground.high.slope"] - 1.0) < 0.02, got
    assert got["T09d.ground.high.verdict"] == 1, got
    assert abs(got["T09d.ground.high.reach_gain"] - 10.0) < 1e-6, got
    assert notes and "A (units)" in notes[0], notes
    # Prediction B: the deck does not follow the gain.
    notes = []
    rows = analyse(parse([_fake("above", "low", 0.5, 0.7, 0.12, 0.05)]), notes)
    got = {r["metric"]: r["value"] for r in rows}
    assert got["T09d.above.low.verdict"] == -1 and got["T09d.above.low.slope"] < 0.2, got
    assert "B (clamped)" in notes[0], notes
    # Every gain clipped: not measurable, verdict 0.
    rows = analyse(parse([_fake("ground", "low", 0.5, 0.7, 2.0, 1.0)]), [])
    got = {r["metric"]: r["value"] for r in rows}
    assert got["T09d.ground.low.verdict"] == 0 and "T09d.ground.low.slope" not in got, got
    # A frame flagged pose_ok 0 is left out.
    bad = _fake("ground", "high", 0.5, 0.7, 0.05, 1.0) + '\nATMORESULT {"series":"K-T2d","metric":"T09d.ground.high.g4.pose_ok","value":0.0}'
    rows = analyse(parse([bad]), [])
    got = {r["metric"]: r["value"] for r in rows}
    assert abs(got["T09d.ground.high.slope"] - 1.0) < 0.1 and got["T09d.ground.high.verdict"] in (0, 1), got
    # The output stage: the values the unit test pins for the K-T2d ground view (raw 0.0145, sea 0.443, sky 0.458): u1 pi times, p1 0.152, pe2 1.37 times p1.
    stage = _fake("ground", "high", 0.458, 0.443, 0.0145, 1.0) + "".join(
        '\nATMORESULT {"series":"K-T2d","metric":"T09d.ground.high.%s.deck_median","value":%r,"variant":"plain"}' % (tag, v)
        for tag, v in (("u1", 0.0456), ("p1", 0.152), ("pe2", 0.208)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([stage]), notes)}
    assert abs(got["T09d.ground.high.stage.u1_over_g1"] - 3.14) < 0.05, got
    assert abs(got["T09d.ground.high.stage.p1_over_sea"] - 0.343) < 0.01 and got["T09d.ground.high.stage.verdict"] == 1, got
    assert abs(got["T09d.ground.high.stage.p1_over_sky"] - 0.332) < 0.01, got
    assert abs(got["T09d.ground.high.stage.pe2_over_p1"] - 1.37) < 0.02, got
    assert any("commensurable" in n for n in notes), notes
    black = _fake("ground", "high", 0.458, 0.443, 0.0145, 1.0) + '\nATMORESULT {"series":"K-T2d","metric":"T09d.ground.high.p1.deck_median","value":0.0145}'
    assert {r["metric"]: r["value"] for r in analyse(parse([black]), [])}["T09d.ground.high.stage.verdict"] == -1
    # The coverage variants: the picture's closed share against the model's, and the order p1 >= c42 >= c35.
    cover = "".join(
        '\nATMORESULT {"series":"K-T2d","metric":"T09d.ground.high.%s","value":%r,"variant":"plain"}' % (m, v)
        for m, v in (("p1.sky_closed_share", 0.70), ("p1.model_closed", 0.71), ("c42.sky_closed_share", 0.40), ("c42.model_closed", 0.44),
                     ("c35.sky_closed_share", 0.25), ("c35.model_closed", 0.21)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([cover]), notes)}
    assert abs(got["T09d.ground.high.cover.p1_closed_minus_model"] + 0.01) < 1e-9, got
    assert abs(got["T09d.ground.high.cover.c42_closed_minus_model"] + 0.04) < 1e-9 and got["T09d.ground.high.cover.monotone"] == 1, got
    assert any("closed share of the sky" in n and "c35 0.25 (model 0.21)" in n for n in notes), notes
    wrong = cover + '\nATMORESULT {"series":"K-T2d","metric":"T09d.ground.high.c35.sky_closed_share","value":0.9}'
    assert {r["metric"]: r["value"] for r in analyse(parse([wrong]), [])}["T09d.ground.high.cover.monotone"] == -1
    # The per-layer table: layer 1 alone draws the sheet (closed 0.99 at shift 0, 0.97 at -0.15) while the model says 0.73 / 0.30; its pass map reads 0.90 where the CPU readers see 0.665.
    layers = "".join(
        '\nATMORESULT {"series":"K-T2d","metric":"T09d.ground.low.%s","value":%r,"variant":"plain"}' % (m, v)
        for m, v in (("L0.cpu_cover", 0.406), ("L0.gpu_cover", 0.406), ("L0.shader_cover", 0.406), ("L0.model_closed_p1", 0.34), ("L0.model_closed_c35", 0.0),
                     ("L1.cpu_cover", 0.665), ("L1.gpu_cover", 0.90), ("L1.shader_cover", 0.90), ("L1.model_closed_p1", 0.73), ("L1.model_closed_c35", 0.30),
                     ("only0.sky_closed_share", 0.05), ("only0c35.sky_closed_share", 0.0), ("only1.sky_closed_share", 0.99), ("only1c35.sky_closed_share", 0.97),
                     ("only1.deck_fraction", 0.6), ("only1.deck_median", 0.4), ("layers.surface_temp_k", 288.0), ("layers.stack_cold", 0)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([layers]), notes)}
    assert got["T09d.ground.low.layers.sheet_layer"] == 1, got
    assert abs(got["T09d.ground.low.layers.L1.closed_minus_model_p1"] - 0.26) < 1e-9 and abs(got["T09d.ground.low.layers.L1.closed_c35"] - 0.97) < 1e-9, got
    assert abs(got["T09d.ground.low.layers.L1.gpu_minus_cpu"] - 0.235) < 1e-9 and got["T09d.ground.low.layers.gpu_matches_cpu"] == -1, got
    assert abs(got["T09d.ground.low.layers.L0.gpu_minus_cpu"]) < 1e-9, got
    assert any("sheet layer L1" in n and "cold members no" in n for n in notes) and any("L1 cpu 0.665 gpu 0.900" in n for n in notes), notes
    same = layers.replace('"value":0.9,', '"value":0.665,')
    assert {r["metric"]: r["value"] for r in analyse(parse([same]), [])}["T09d.ground.low.layers.gpu_matches_cpu"] == 1
    assert not any(".layers." in r["metric"] for r in analyse(parse([_fake("above", "low", 0.5, 0.7, 0.12, 1.0)]), [])), "no layer rows, no layer table"
    # The look starts (E40): per set the closed share beside the oracle's expectation, the acceptance rows, the forced-map verdict, the colour and the horizon.
    def look_rows(view, cover, pairs):
        return "".join('\nATMORESULT {"series":"K-T2d","metric":"T09d.%s.%s.%s","value":%r,"variant":"plain"}' % (view, cover, m, v) for m, v in pairs)
    low = look_rows("ground", "low", (
        ("L1.map_at_0_km", 0.665),
        ("kC0.sky_closed_share", 0.99), ("kC0.expect_closed_mean", 0.73), ("kC0.expect_closed_p10", 0.21), ("kC0.expect_closed_p90", 0.985),
        ("kC0m8.sky_closed_share", 0.97), ("kC0m8.expect_closed_mean", 0.55), ("kC0m8.expect_closed_p10", 0.10), ("kC0m8.expect_closed_p90", 0.90),
        ("kC4.sky_closed_share", 0.70), ("kC4.expect_closed_mean", 0.68), ("kC4.expect_closed_p10", 0.40), ("kC4.expect_closed_p90", 0.95),
        ("kC4m8.sky_closed_share", 0.50), ("kC4m8.expect_closed_mean", 0.45), ("kC4m8.expect_closed_p10", 0.20), ("kC4m8.expect_closed_p90", 0.80),
        ("kovr.sky_closed_share", 0.72), ("kovr.expect_closed_mean", 0.73)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("ground", "low", 0.5, 0.7, 0.05, 1.0) + low]), notes)}
    pre = "T09d.ground.low.look."
    assert abs(got[pre + "C0.closed_minus_expect_s0"] - 0.26) < 1e-9 and got[pre + "C0.in_spread_s0"] == 1, got      # 0.99 lies inside the oracle's spread (p90 0.985 + 0.05)
    assert got[pre + "C0.in_spread_s8"] == -1 and got[pre + "C4.in_spread_s0"] == 1, got
    assert abs(got[pre + "C0.fall"] - 0.02) < 1e-9 and got[pre + "C0.a1_fall"] == -1 and got[pre + "C4.a1_fall"] == 1, got
    assert got[pre + "C0.a1_band"] == -1 and got[pre + "C4.a1_band"] == 1, got      # 0.99 is above 0.665 + 0.15; 0.70 is inside
    assert got[pre + "ovr.map_varies_over_view"] == 1, got                            # the forced map gives the oracle's share, the own map a sheet
    assert any("forced map: closed 0.72 against the oracle's 0.73" in n for n in notes), notes
    # The aid frame's optical depth beside the colour-change measure: a sheet that is closed by colour (0.99) but thin by tau (0.30 closed) shows as a positive difference.
    tau_low = look_rows("ground", "low", (("aid1kC0.aidtau_closed_share", 0.30), ("aid1kC0.aidtau_median", 0.4), ("aid1kC0.aidtau_deep_share", 0.05), ("aid1kC0.aidtau_top_median", 0.5),
                                         ("kC0.expect_tau_zenith_mean", 12.5), ("kC0.expect_tau_zenith_p10", 1.0), ("kC0.expect_tau_zenith_p90", 30.0)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("ground", "low", 0.5, 0.7, 0.05, 1.0) + low + tau_low]), notes)}
    assert abs(got[pre + "C0.closed_colour_minus_tau"] - 0.69) < 1e-9 and got[pre + "C0.aidtau_closed"] == 0.30, got
    assert any("aid tau kC0" in n and "tau closed 0.30" in n and "oracle zenith tau 12.50 (1.00 to 30.00)" in n for n in notes), notes
    stays = low.replace('"value":0.72,', '"value":0.99,')
    assert {r["metric"]: r["value"] for r in analyse(parse([_fake("ground", "low", 0.5, 0.7, 0.05, 1.0) + stays]), [])}[pre + "ovr.map_varies_over_view"] == -1
    high = look_rows("ground", "high", (("kC4.sky_closed_share", 0.20), ("kC4m8.sky_closed_share", 0.05), ("wC4o0.sky_closed_share", 0.75)))
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("ground", "high", 0.5, 0.7, 0.05, 1.0) + high]), [])}
    assert got["T09d.ground.high.look.C4.a2_towers"] == 1 and got["T09d.ground.high.look.C4.a4_rain_deck"] == 1, got
    light = look_rows("ground", "high", (("t0.deck_r", 0.5), ("t0.deck_b", 0.35), ("t10.deck_r", 0.4), ("t10.deck_b", 0.36), ("t0.sky_over_off_sky", 1.0), ("t0.ground_over_off_ground", 1.2),
                                        ("t0.deck_clipped", 0.0)))
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("ground", "high", 0.5, 0.7, 0.05, 1.0) + light]), [])}
    assert got["T09d.ground.high.look.t0.a5_colour"] == -1 and got["T09d.ground.high.look.t10.a5_colour"] == 1, got       # 0.70 is beige, 0.90 is inside the band
    assert got["T09d.ground.high.look.t0.sky_over_off_sky_ok"] == 1 and got["T09d.ground.high.look.t0.ground_over_off_ground_ok"] == -1 and got["T09d.ground.high.look.t0.a5_clipped"] == 1, got
    hz = look_rows("horizon", "low", (("hz0h0.horizon_band_closed", 0.95), ("hz0h0.horizon_band_change", 0.40), ("hz40h1.horizon_band_closed", 0.1), ("hz40h1.horizon_band_change", 0.04)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([hz]), notes)}
    assert got["T09d.horizon.low.look.hz0h0.a7_step"] == -1 and got["T09d.horizon.low.look.hz40h1.a7_step"] == 1, got
    assert any("horizon band change against the baseline" in n and "hz0h0 0.40" in n for n in notes), notes
    order = look_rows("above", "low", (("off.line_step_colour", 0.06), ("ord1.line_step_colour", 0.09), ("ord0.line_step_colour", 0.28), ("ord1.order_moves", 60), ("ord0.order_moves", 0)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("above", "low", 0.5, 0.7, 0.05, 1.0) + order]), notes)}
    assert got["T09d.above.low.look.order.step_vanishes"] == 1 and abs(got["T09d.above.low.look.order.step_off"] - 0.22) < 1e-9, got
    assert any("order pair" in n and "moved frames 60 / 0" in n for n in notes), notes
    # The order profile (order 1, 0, 0, 1): band b2 moves with the order (ord0 bluer by 0.06 against a repeat floor of 0.01), band b0 does not (the dark rim: both orders the same).
    def prof(frame, band, r, g, b, base):
        return [("%s.prof_%s_%s" % (frame, band, ch), v) for ch, v in (("r", r), ("g", g), ("b", b), ("br", base[0]), ("bg", base[1]), ("bb", base[2]))]
    rows = []
    for frame, b0, b2 in (("ord1", (0.40, 0.40, 0.40), (0.64, 0.63, 0.63)), ("ord1b", (0.40, 0.40, 0.40), (0.645, 0.635, 0.635)),
                          ("ord0", (0.40, 0.40, 0.40), (0.655, 0.675, 0.70)), ("ord0b", (0.40, 0.40, 0.40), (0.65, 0.67, 0.695))):
        rows += prof(frame, "b0", b0[0], b0[1], b0[2], (0.65, 0.73, 0.77)) + prof(frame, "b2", b2[0], b2[1], b2[2], (0.50, 0.66, 0.74))
    prof_rows = look_rows("above", "low", rows)
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("above", "low", 0.5, 0.7, 0.05, 1.0) + prof_rows]), notes)}
    assert got["T09d.above.low.look.order.b2.moves"] == 1 and got["T09d.above.low.look.order.b0.moves"] == -1, got
    assert abs(got["T09d.above.low.look.order.b2.d_b"] - 0.065) < 1e-9 and abs(got["T09d.above.low.look.order.b2.floor"] - 0.005) < 1e-9, got
    assert abs(got["T09d.above.low.look.order.b0.gap_ord1"] - 0.37) < 1e-9, got
    assert any("order profile below band 2" in n and "ord0 - ord1 r +0.010 g +0.040 b +0.065" in n for n in notes), notes
    hh = look_rows("above", "low", prof("hh2", "b2", 0.655, 0.675, 0.70, (0.50, 0.66, 0.74)) + prof("hh1", "b2", 0.65, 0.65, 0.66, (0.50, 0.66, 0.74)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("above", "low", 0.5, 0.7, 0.05, 1.0) + prof_rows + hh]), notes)}
    assert abs(got["T09d.above.low.look.order.b2.hh2_minus_ord0_b"] - 0.0025) < 1e-9 and abs(got["T09d.above.low.look.order.b2.hh1_minus_ord0_b"] + 0.0375) < 1e-9, got
    assert abs(got["T09d.above.low.look.order.b2.gap_hh2"] - 0.155) < 1e-9, got
    assert any("hh2 - ord0 r +0.002 g +0.002 b +0.002" in n for n in notes), notes
    assert not any(k.startswith("T09d.above.low.look.order.") for k in {r["metric"]: 1 for r in analyse(parse([_fake("above", "low", 0.5, 0.7, 0.05, 1.0)]), [])}), "no order frames, no order rows"
    # Start look55: the colour and grain notes and rows, and the cost windows from the profiler's reports (kt2.py's parser) with the differences against plain.
    colour = look_rows("ground", "low", (("caDt0.deck_r", 0.60), ("caDt0.deck_g", 0.55), ("caDt0.deck_b", 0.42), ("ca02t10.deck_r", 0.50), ("ca02t10.deck_b", 0.48),
                                         ("gacc.grain_tstd", 0.0077), ("gacc.grain_hfx", -0.0018), ("gacc.grain_n", 1000), ("sh1.ground_median", 0.30), ("sh0.ground_median", 0.28)))
    notes = []
    got = {r["metric"]: r["value"] for r in analyse(parse([_fake("ground", "low", 0.5, 0.7, 0.05, 1.0) + colour]), notes)}
    assert abs(got["T09d.ground.low.look.caDt0.deck_b_over_r"] - 0.7) < 1e-9 and abs(got["T09d.ground.low.look.ca02t10.deck_b_over_r"] - 0.96) < 1e-9, got
    assert abs(got["T09d.ground.low.look.shadow.ground_median_sh0_minus_sh1"] + 0.02) < 1e-9, got
    assert any("colour caDt0" in n and "b/r 0.700" in n for n in notes) and any("grain gacc" in n and "tstd 0.0077" in n for n in notes), notes

    def report(frame, idle, own):
        return ("Probe: GPU profiler: FLIGHT, 100 frames measured, GPU %.3f ms a frame from Present to Present (at most 20.000).\n  By camera or light:\n"
                "    frame start, GPU idle included %.3f ms (5.0 %%)\n    ReDefinition clouds %.3f ms (10.0 %%)\n  Costliest segments:\n    1.000 ms, at most 2.000, 3: x\n" % (frame, idle, own))
    def window(variant, frame, own):
        return "T09 view begin: ground %s 1707x960\n" % variant + report(frame + 1, 0.5, own) + report(frame, 0.5, own) + report(frame, 0.5, own) + "T09 view end: ground %s 1707x960\n" % variant
    log = window("off", 10.0, 0.0) + window("plain", 11.0, 1.0) + window("leap", 11.4, 1.4) + window("shape0", 10.7, 1.0)
    notes = []
    rows = {r["metric"]: r["value"] for r in cost55_rows([log], notes)}
    assert abs(rows["T09d.ground.cost55.plain.busy_ms"] - 10.5) < 1e-9 and abs(rows["T09d.ground.cost55.leap.d_busy_ms_vs_plain"] - 0.4) < 1e-9, rows
    assert abs(rows["T09d.ground.cost55.shape0.d_frame_ms_vs_plain"] + 0.3) < 1e-9 and abs(rows["T09d.ground.cost55.leap.d_labels_ms_vs_plain"] - 0.4) < 1e-9, rows
    assert cost55_rows(["nothing"], []) == [] and any("cost ground 1707x960 leap" in n for n in notes), notes
    # Lines of another series and text values are ignored; an empty log gives nothing.
    assert parse(['ATMORESULT {"series":"K-T2","metric":"T09d.a.b.c.d","value":1.0}', 'ATMORESULT {"series":"K-T2d","metric":"T09d.env.sunLight","value":"x"}']) == {}
    assert analyse({}, []) == []
    print("kt2d selftest: ok")


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
    texts = []
    for path in a.logs:
        with open(path, encoding="utf-8", errors="replace") as f:
            texts.append(f.read())
    notes = []
    for r in analyse(parse(texts), notes) + cost55_rows(texts, notes):
        print("%-44s %s" % (r["metric"], r["value"]))
    for n in notes:
        print("note: " + n)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
