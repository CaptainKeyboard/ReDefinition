"""The series of package T15 (the resolution levels and the combinations of the sun march),
run through tools/build_weather_player.py --series <name>. The series T15-bisect-cost and T15-bisect-quality (the cheap forms of the entry refinement) went with the
entry refinement (E40 step (c)):

  T15-levels          scene 'recon-levels' (it was 'bisect-levels'): the resolution levels 1 to 4 through two turns: ghosting, trail and shimmer.
  T15-combo-cost     timing run (exclusive), scene 'light-cost': ms of the pass per view for the combinations of the sun march 9000 m with more ambient taps
                      and the light cache.

Resolution levels and the combinations carry no pass/fail limit: their numbers are for the owner's decision.
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
NAN = float("nan")


# ------------------------------------------------------------------------------------------------------------------------------------------------
# The series (importing this module registers them)
# ------------------------------------------------------------------------------------------------------------------------------------------------
from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import light as Lt  # noqa: E402
from weather_metrics import t14  # noqa: E402


def values_of(ctx, label="scene"):
    return {name: v[0] for name, v in ctx.player_metrics(label).items()}


def levels_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    for name, v in sorted(values_of(ctx).items()):
        if name.startswith("levels."):
            out.append(Metric.info("lv15." + name[len("levels."):], v))
    return out


register_series("T15-levels", [Stage("scene", ["-mode", "scene", "-scene", "recon-levels", "-in", L.INPUT], size=(1280, 720), proxy=False,
                                      timeout=1500)], levels_collect, exclusive=False, who="T15")

# ------------------------------------------------------------------------------------------------------------------------------------------------
# The base radiance combinations
# ------------------------------------------------------------------------------------------------------------------------------------------------
# The sun march and the ambient taps: name, light job overrides, sun march (m). 'default' is the shipped 4 km without taps (the defaults stay as they are).
COMBOS = (
    ("default", {}, 4000.0),
    ("sun9", {}, 9000.0),
    ("sun9ao2", dict(aoTaps=1, upReuse=2), 9000.0),
    ("sun9ao0", dict(aoTaps=1, upReuse=0), 9000.0),
    ("sun9ao0up4", dict(aoTaps=1, upReuse=0, upSteps=4), 9000.0),
    ("sun9ao0up8", dict(aoTaps=1, upReuse=0, upSteps=8), 9000.0),
    ("sun9ao0s8", dict(aoTaps=1, upReuse=0, sunSteps=8), 9000.0),
    ("sun9cacheao0", dict(aoTaps=1, upReuse=0, cache=1), 9000.0),
    ("sun9cacheao0up4", dict(aoTaps=1, upReuse=0, upSteps=4, cache=1), 9000.0),
)

COMBO_COST_INPUT = os.path.join(BUILD, "T15-combo-cost-in")


def combo_cost_stages():
    """The cost of the combinations on the four views of the T14 light-cost series (same jobs, the T15 combination table)."""
    L.write_inputs()
    os.makedirs(COMBO_COST_INPUT, exist_ok=True)
    for name in ("view-coverage.r8", "view-type.r8"):
        src = os.path.join(L.INPUT, name)
        if os.path.isfile(src):
            with open(src, "rb") as f, open(os.path.join(COMBO_COST_INPUT, name), "wb") as g:
                g.write(f.read())
    jobs = []
    for lj0 in L.picture_jobs():
        if lj0["id"] not in t14.COST_VIEWS:
            continue
        for vname, over, march in COMBOS:
            lj = dict(lj0)
            lj["sunColour"] = [3.0, 2.9, 2.7]
            lj["ambientTop"] = [0.35, 0.42, 0.55]
            lj["ambientBottom"] = [0.14, 0.15, 0.16]
            lj["sunMarch"] = march
            j = dict(id="%s|%s" % (lj["id"], vname), mode="lum", useDefaults=0, layerJob=lj)
            j.update(Lt.MODEL)
            j.update(over)
            if over.get("cache"):
                p, f = lj["position"], lj["forward"]
                c = [p[k] + 8000.0 * f[k] for k in range(3)]
                j.update(cacheMin=[c[k] - 16000.0 for k in range(3)], cacheSize=[32000.0] * 3, cacheFillSteps=12)
            jobs.append(j)
    with open(os.path.join(COMBO_COST_INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return [Stage("scene", ["-mode", "scene", "-scene", "light-cost", "-in", COMBO_COST_INPUT], size=(1707, 960), proxy=True, timeout=1500)]


def combo_cost_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    rows, views, variants = t14.light_cost_table(values_of(ctx), show=False)
    for view in views:
        base = rows.get((view, "default"), NAN)
        for var in variants:
            if (view, var) in rows:
                out.append(Metric.info("combo.cost_ms.%s.%s" % (view, var), rows[(view, var)]))
                out.append(Metric.info("combo.cost_ratio.%s.%s" % (view, var), rows[(view, var)] / base))
    return out


register_series("T15-combo-cost", combo_cost_stages(), combo_cost_collect, exclusive=True, who="T15")
