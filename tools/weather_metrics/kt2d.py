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
GAINS = (1, 4, 16, 64)
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
            cells = " ".join("g%d %s" % (g, "-" if v is None else "%.3f" % v) for g, v in medians)
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
    return analyse(parse(texts.values()), NOTES)


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
    for r in analyse(parse(texts), notes):
        print("%-44s %s" % (r["metric"], r["value"]))
    for n in notes:
        print("note: " + n)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
