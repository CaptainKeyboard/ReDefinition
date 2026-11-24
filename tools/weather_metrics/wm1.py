"""WM1 (master plan section 26): what the volumetric clouds cost when today's layers are drawn from the package B weather maps instead of today's layer maps.

    python tools/weather_metrics/wm1.py --pin-prepare                     the game's pinned capture (every pass setting) as the base job
    WMSKY_OUT=<dump> WMSKY_UTS=... WMSKY_HEIGHT_ROOT=610 python tools/dotnet_test.py --filter FullyQualifiedName~WeatherMapSkyDump
                                                                          the maps of both skies and the column quantities (tests T09/WeatherMapSkyDump.cs)
    python tools/weather_metrics/wm1.py --prepare --dump <dump>           picks the situations, moves each one's weather to the capture's place, writes the jobs
    python tools/build_weather_player.py --series wm1-cost --no-build     one run of two alternating rounds (exclusive GPU lock); three runs give six rounds
    python tools/build_weather_player.py --series wm1-pic --no-build      the 2560 x 1440 cloud outputs
    python tools/weather_metrics/wm1.py --report                          the cost table over every wm1-cost run, the frequency-weighted estimate
    python tools/weather_metrics/wm1.py --pictures --pic-run <folder>     the picture pairs and the weather-map patches (German labels)

A job is the pin capture's job (the game's frame, light, march, reconstruction and noise settings) with one situation's maps and one camera. The situation's weather is moved onto
the capture's place (KSC) by a rotation of the sphere, so the game's camera, sun and terrain depth stay as they were. The two arms differ only in the layer maps and the height
maps' root offset: today = the module's chain (tests SkyMapsExplore), weather map = WeatherMapSky (cloud.weatherMapSky 1). Both arms have no height variation of today's height
model (neutral height maps; the game's are made in the game from the field and are not in the dump); the weather-map arm moves the cumuliform layers' base by its root offset.
"""
import argparse
import glob
import json
import math
import os
import shutil
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
MAIN = os.environ.get("ATMO_MAINREPO") or os.path.join("C:" + os.sep, "Users", "franz", "IdeaProjects", "ReDefinition")
CAPTURE = os.path.join(MAIN, ".local", "work", "series-pictures", "K-T2", "maps", "pin-ground-u2247600-perfNpic2560dlssqpin-20261119205559")
PIN_IN = os.path.join(BUILD, "wm1-pin")
COST_IN = os.path.join(BUILD, "wm1-cost-in")
PIC_IN = os.path.join(BUILD, "wm1-pic-in")
SITUATIONS = os.path.join(BUILD, "wm1-situations.json")
PICTURES = os.path.join(MAIN, ".local", "work", "series-pictures", "WM1")
LAYERS = ("Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus")
MAP_W, MAP_H = 512, 256
# The capture's place (the game's test site, KSC): latitude, longitude in degrees (SkyMapsExplore's SITE).
KSC = (-0.0972, -74.5577)
PIN_UT = 1486800
COST_SIZE = (1707, 960)
PIC_SIZE = (2560, 1440)
ROUNDS = int(os.environ.get("WM1_ROUNDS", "2") or 2)
VIEWS = ("ground", "above", "down")
ARMS = ("today", "wm")
SATURATED = 0.9999


# ------------------------------------------------------------------------------------------------------------------------------
# Maps and the rotation of the sphere
# ------------------------------------------------------------------------------------------------------------------------------
def unit(lat_deg, lon_deg):
    la, lo = np.radians(lat_deg), np.radians(lon_deg)
    return np.stack([np.cos(la) * np.cos(lo), np.cos(la) * np.sin(lo), np.sin(la)], axis=-1)


def texel_dirs():
    lat = 90.0 - (np.arange(MAP_H) + 0.5) * 180.0 / MAP_H
    lon = -180.0 + (np.arange(MAP_W) + 0.5) * 360.0 / MAP_W
    LON, LAT = np.meshgrid(lon, lat)
    return unit(LAT, LON)


def rotation(a, b):
    """The rotation that turns the unit vector a into b (Rodrigues)."""
    a, b = a / np.linalg.norm(a), b / np.linalg.norm(b)
    v, c = np.cross(a, b), float(a @ b)
    s = np.linalg.norm(v)
    if s < 1e-12:
        return np.eye(3)
    k = v / s
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    ang = math.atan2(s, c)
    return np.eye(3) + math.sin(ang) * K + (1 - math.cos(ang)) * K @ K


def source_index(lat_deg, lon_deg):
    """For every texel of the moved map, the texel of the situation's map it shows (nearest): the situation's place lands on KSC."""
    R = rotation(unit(*KSC), unit(lat_deg, lon_deg))
    d = texel_dirs() @ R.T
    lat = np.degrees(np.arcsin(np.clip(d[..., 2], -1, 1)))
    lon = np.degrees(np.arctan2(d[..., 1], d[..., 0]))
    j = np.clip(((90.0 - lat) / 180.0 * MAP_H).astype(int), 0, MAP_H - 1)
    i = np.clip(((lon + 180.0) / 360.0 * MAP_W).astype(int) % MAP_W, 0, MAP_W - 1)
    return j, i


def u8(dump, ut, name):
    return np.fromfile(os.path.join(dump, "u%d_%s.u8" % (ut, name)), dtype=np.uint8).reshape(MAP_H, MAP_W)


def f32(dump, ut, name):
    return np.fromfile(os.path.join(dump, "u%d_col_%s.f32" % (ut, name)), dtype=np.float32).reshape(MAP_H, MAP_W)


def uts_of(dump):
    return sorted({int(os.path.basename(p).split("_")[0][1:]) for p in glob.glob(os.path.join(dump, "u*_col_cover.f32"))})


def columns(dump, ut):
    c = {k: f32(dump, ut, k) for k in ("cover", "cumuliform", "sheet", "lowstratiform", "base", "top", "rhLow", "rhMid", "rhHigh", "precip", "reach", "stable", "warm", "cold", "inst")}
    c["depth"] = np.maximum(c["top"] - c["base"], 0.0)
    c["all3"] = (c["rhLow"] >= SATURATED) & (c["rhMid"] >= SATURATED) & (c["rhHigh"] >= SATURATED)
    return c


# ------------------------------------------------------------------------------------------------------------------------------
# The situations
# ------------------------------------------------------------------------------------------------------------------------------
CLASSES = {
    "a": "fair-weather shallow cumulus (cumuliform >= 0.05, no cell, depth 1 m to 2 km, not all levels saturated, precipitation < 0.1 mm/h)",
    "b": "broad sheet, overcast (cover >= 0.8, sheet >= 0.75, cumuliform < 0.1, no cell, not all levels saturated)",
    "c": "front with all three levels saturated (warm or cold front nearness >= 0.3)",
    "d": "storm cell (CellReach01 > 0.3, the highest top of all)",
    "e": "cumuliform water without depth (cumuliform >= 0.05, depth < 1 m, no cell)",
    "f": "the pinned M0 place (KSC, ut 1486800)",
}


def masks(c):
    lat = 90.0 - (np.arange(MAP_H) + 0.5) * 180.0 / MAP_H
    ok = np.abs(lat)[:, None] < 65.0
    m = {
        "a": (c["cumuliform"] >= 0.05) & (c["reach"] <= 0) & (c["depth"] >= 1) & (c["depth"] <= 2000) & ~c["all3"] & (c["precip"] < 0.1),
        "b": (c["cover"] >= 0.8) & (c["sheet"] >= 0.75) & (c["cumuliform"] < 0.1) & (c["reach"] <= 0) & ~c["all3"],
        "c": c["all3"] & ((c["warm"] >= 0.3) | (c["cold"] >= 0.3)),
        "d": c["reach"] > 0.3,
        "e": (c["cumuliform"] >= 0.05) & (c["depth"] < 1) & (c["reach"] <= 0),
    }
    return {k: v & ok for k, v in m.items()}


def box_mean(a, r=3):
    """The mean over a (2r+1)^2 box of texels, wrapping in longitude."""
    from scipy.ndimage import uniform_filter
    return uniform_filter(a.astype(np.float64), size=2 * r + 1, mode=("nearest", "wrap"))


def pick(dump):
    best = {}
    for ut in uts_of(dump):
        c = columns(dump, ut)
        m = masks(c)
        for k, mk in m.items():
            if k == "d":
                score = np.where(mk, c["top"], -1.0)
            else:
                weight = {"a": c["cumuliform"], "b": c["sheet"], "c": c["sheet"], "e": c["cumuliform"]}[k]
                score = np.where(mk, box_mean(mk) + 0.1 * box_mean(weight * mk), -1.0)
            j, i = np.unravel_index(int(np.argmax(score)), score.shape)
            s = float(score[j, i])
            if s > 0 and (k not in best or s > best[k]["score"]):
                best[k] = dict(ut=ut, j=int(j), i=int(i), score=s)
    out = []
    for k in "abcde":
        b = best[k]
        lat = 90.0 - (b["j"] + 0.5) * 180.0 / MAP_H
        lon = -180.0 + (b["i"] + 0.5) * 360.0 / MAP_W
        out.append(dict(key=k, what=CLASSES[k], ut=b["ut"], lat=lat, lon=lon, score=b["score"]))
    out.append(dict(key="f", what=CLASSES["f"], ut=PIN_UT, lat=KSC[0], lon=KSC[1], score=0.0))
    for s in out:
        c = columns(dump, s["ut"])
        j, i = source_index(s["lat"], s["lon"])
        jm, im = _ksc_texel()
        jj, ii = j[jm, im], i[jm, im]
        s["column"] = {k: float(c[k][jj, ii]) for k in ("cover", "cumuliform", "sheet", "lowstratiform", "base", "top", "depth", "rhLow", "rhMid", "rhHigh", "precip", "reach", "warm", "cold", "stable")}
        # The neighbourhood the views see most of (+-3 texels, about +-22 km at the equator): means.
        win = (slice(jm - 3, jm + 4), slice(im - 3, im + 4))
        s["around"] = {k: float(np.mean(c[k][j, i][win])) for k in ("cover", "cumuliform", "sheet", "base", "top", "depth")}
    return out


def _ksc_texel():
    j = int((90.0 - KSC[0]) / 180.0 * MAP_H)
    i = int((KSC[1] + 180.0) / 360.0 * MAP_W)
    return j, i


# ------------------------------------------------------------------------------------------------------------------------------
# The jobs
# ------------------------------------------------------------------------------------------------------------------------------
def camera_at(job, alt_m, pitch_deg):
    """The capture's camera moved along the local up to this altitude over the sphere and turned to this pitch (same place and heading); e40puff.camera_at."""
    p = np.array(job["position"], dtype=np.float64)
    f = np.array(job["forward"], dtype=np.float64)
    up_local = p / np.linalg.norm(p)
    h = f - up_local * float(f @ up_local)
    h = h / np.linalg.norm(h)
    a = np.radians(pitch_deg)
    job["position"] = [float(x) for x in up_local * (float(job["planetRadius"]) + alt_m)]
    fw = np.cos(a) * h + np.sin(a) * up_local
    up = -np.sin(a) * h + np.cos(a) * up_local
    job["forward"] = [float(x) for x in fw]
    job["up"] = [float(x) for x in up]
    job["right"] = [float(x) for x in np.cross(up, fw)]


def pin_job():
    with open(os.path.join(PIN_IN, "jobs.json"), encoding="utf-8") as f:
        return json.load(f)["jobs"][0]


def write_jobs(dump, sits, out, size):
    os.makedirs(out, exist_ok=True)
    base = pin_job()
    root = None
    jobs = []
    depth_name = ""
    if base.get("depthFile"):
        d = np.load(os.path.join(PIN_IN, base["depthFile"]))
        if d.shape[:2] != (size[1], size[0]):
            yy = (np.arange(size[1]) * d.shape[0] / size[1]).astype(int)
            xx = (np.arange(size[0]) * d.shape[1] / size[0]).astype(int)
            d = d[yy][:, xx]
        depth_name = "wm-depth-%dx%d.npy" % size
        np.save(os.path.join(out, depth_name), d.astype(np.float32))
    for s in sits:
        ut = s["ut"]
        j, i = source_index(s["lat"], s["lon"])
        for arm in ARMS:
            files = {}
            for layer in LAYERS:
                cov = u8(dump, ut, "%s_%s_cover" % (arm, layer))[j, i]
                typ = u8(dump, ut, "%s_%s_type" % (arm, layer))[j, i]
                hgt = np.full((MAP_H, MAP_W, 3), 128, np.uint8)
                rpath = os.path.join(dump, "u%d_%s_%s_root.u8" % (ut, arm, layer))
                if os.path.isfile(rpath):
                    hgt[..., 0] = np.fromfile(rpath, np.uint8).reshape(MAP_H, MAP_W)[j, i]
                stem = "wm-%s-%s-%s" % (s["key"], arm, layer)
                cov.tofile(os.path.join(out, stem + "-cover.r8"))
                typ.tofile(os.path.join(out, stem + "-type.r8"))
                hgt.tofile(os.path.join(out, stem + "-height.u8"))
                files[layer] = stem
            for view in VIEWS:
                job = json.loads(json.dumps(base))
                job["id"] = "wm-%s-%s-%s" % (s["key"], view, arm)
                job["width"], job["height"] = size
                for k in ("recordCamera", "recordRotation", "recordPlanetCenter", "hostJitter", "reachCov", "reachType"):
                    job.pop(k, None)
                for jl in job["layers"]:
                    stem = files[jl["name"]]
                    jl["coverageMap"], jl["typeMap"], jl["heightMap"] = stem + "-cover.r8", stem + "-type.r8", stem + "-height.u8"
                    jl["anvilDrawn"] = 1
                if view == "ground":
                    job["depthFile"] = depth_name
                else:
                    camera_at(job, 9000.0 if view == "above" else 15000.0, -15.0 if view == "above" else -90.0)
                    job["depthFile"] = ""
                job["tuning"] = [t for t in job.get("tuning", []) if t["key"] != "cloud.weatherMapSky"] + [dict(key="cloud.weatherMapSky", value=1.0 if arm == "wm" else 0.0)]
                jobs.append(job)
    with open(os.path.join(out, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def prepare(dump):
    sits = pick(dump)
    with open(SITUATIONS, "w", encoding="utf-8") as f:
        json.dump(dict(dump=os.path.abspath(dump), situations=sits), f, indent=1)
    for s in sits:
        c, a = s["column"], s["around"]
        print("%s ut %-8d lat %7.2f lon %8.2f  cover %.2f cumuliform %.2f sheet %.2f base %5.0f top %5.0f reach %.2f rh %.2f/%.2f/%.2f  around: cover %.2f cumuliform %.2f sheet %.2f depth %.0f  %s"
              % (s["key"], s["ut"], s["lat"], s["lon"], c["cover"], c["cumuliform"], c["sheet"], c["base"], c["top"], c["reach"], c["rhLow"], c["rhMid"], c["rhHigh"],
                 a["cover"], a["cumuliform"], a["sheet"], a["depth"], s["what"]))
    write_jobs(dump, sits, COST_IN, COST_SIZE)
    write_jobs(dump, sits, PIC_IN, PIC_SIZE)
    print("jobs: %s, %s" % (COST_IN, PIC_IN))


# ------------------------------------------------------------------------------------------------------------------------------
# The series
# ------------------------------------------------------------------------------------------------------------------------------
def collect_cost(ctx):
    metrics = []
    for k, v in sorted(ctx.player_metrics("cost").items()):
        if k.startswith("wmcost.") or k.startswith("wm_"):
            metrics.append(Metric.info(k, v[0]))
    return metrics


def collect_pic(ctx):
    metrics = []
    for k, v in sorted(ctx.player_metrics("pic").items()):
        if k.startswith("wm_"):
            metrics.append(Metric.info(k, v[0]))
    return metrics


register_series("wm1-cost", [Stage("cost", ["-mode", "scene", "-scene", "wm-cost", "-in", COST_IN, "-rounds", str(ROUNDS)], size=COST_SIZE, proxy=True, timeout=1500)],
                collect_cost, exclusive=True, who="wm1-cost")
# The layers' share of the cost: leave-one-out layer masks (bit i = layer i: 0 low stratiform rain layer, 1 cumulus, 2 cumulonimbus, 3 cirrus) on the cases whose ratio moved most.
ATTR_JOBS = ",".join("wm-%s-%s-%s" % (k, v, arm) for k, v in (("d", "ground"), ("a", "above"), ("e", "above"), ("f", "above"), ("b", "down"), ("f", "down"), ("c", "ground"))
                     for arm in ARMS)
register_series("wm1-attr", [Stage("cost", ["-mode", "scene", "-scene", "wm-cost", "-in", COST_IN, "-rounds", "2", "-only", ATTR_JOBS, "-masks", "0,14,13,11,7"],
                                   size=COST_SIZE, proxy=True, timeout=1500)], collect_cost, exclusive=True, who="wm1-attr")
register_series("wm1-pic", [Stage("pic", ["-mode", "scene", "-scene", "wm-picture", "-in", PIC_IN, "-frames", "48"], size=(1280, 720), proxy=False, timeout=1500)],
                collect_pic, exclusive=False, who="wm1-pic")


# ------------------------------------------------------------------------------------------------------------------------------
# The report
# ------------------------------------------------------------------------------------------------------------------------------
def cost_runs():
    runs = []
    for p in sorted(glob.glob(os.path.join(BUILD, "wm1-cost-*.json"))):
        with open(p, encoding="utf-8") as f:
            rows = json.load(f)
        runs.append((p, {r["metric"]: r["value"] for r in rows}))
    return runs


M1_SAMPLE = os.path.join(MAIN, ".local", "work", "m1-kerbin-shares", "kerbin-sky-seed1-sample-every8th-time.csv.gz")
# The rest of the columns (no situation's class) and the measured situation each part is counted with in the central estimate (an assumption, printed with the result).
REST_LIKE = {"r_all3_nofront": "c", "r_cell_weak": "d", "r_cumuliform_small_or_rain": "f", "r_sheet_only": "b", "r_clear": None}


def shares():
    """The situations' classes as a partition of the M1 count's sample (409 600 columns and times, seed 1), in the order d, c, b, a, e, then the rest split by what it is."""
    import gzip
    with gzip.open(M1_SAMPLE, "rt") as f:
        head = f.readline().strip().split(",")
        cols = ("cover", "precip", "base", "top", "cumuliform", "sheet", "rhLow", "rhMid", "rhHigh", "warm", "cold", "reach")
        data = np.loadtxt(f, delimiter=",", usecols=[head.index(k) for k in cols])
    c = dict(zip(cols, data.T))
    depth = np.maximum(c["top"] - c["base"], 0)
    all3 = (c["rhLow"] >= SATURATED) & (c["rhMid"] >= SATURATED) & (c["rhHigh"] >= SATURATED)
    left = np.ones(len(depth), bool)
    out = {}
    for name, m in (("d", c["reach"] > 0.3), ("c", all3 & ((c["warm"] >= 0.3) | (c["cold"] >= 0.3))),
                    ("b", (c["cover"] >= 0.8) & (c["sheet"] >= 0.75) & (c["cumuliform"] < 0.1) & (c["reach"] <= 0) & ~all3),
                    ("a", (c["cumuliform"] >= 0.05) & (c["reach"] <= 0) & (depth >= 1) & (depth <= 2000) & ~all3 & (c["precip"] < 0.1)),
                    ("e", (c["cumuliform"] >= 0.05) & (depth < 1) & (c["reach"] <= 0)), ("r_all3_nofront", all3), ("r_cell_weak", c["reach"] > 0),
                    ("r_cumuliform_small_or_rain", c["cumuliform"] > 0), ("r_sheet_only", c["sheet"] > 0), ("r_clear", np.ones(len(depth), bool))):
        m = m & left
        out[name] = float(m.mean())
        left &= ~m
    return out


def report():
    with open(SITUATIONS, encoding="utf-8") as f:
        sits = json.load(f)["situations"]
    runs = cost_runs()
    per = {}
    for path, m in runs:
        for k, v in m.items():
            if k.startswith("wmcost.marchr"):
                _, r, rest = k.split(".", 2)
                per.setdefault(rest, []).append(v)
    lines = ["WM1: march ms per round over %d runs (median of the rounds; min-max)" % len(runs)]
    table = {}
    for s in sits:
        for view in VIEWS:
            for light in ("lit", "nolight"):
                vals = {}
                for arm in ARMS:
                    key = "wm-%s-%s-%s.%s" % (s["key"], view, arm, light)
                    xs = per.get(key, [])
                    vals[arm] = (float(np.median(xs)) if xs else float("nan"), min(xs) if xs else float("nan"), max(xs) if xs else float("nan"), len(xs))
                table[(s["key"], view, light)] = vals
                t, w = vals["today"], vals["wm"]
                lines.append("  %s %-6s %-7s today %6.2f (%5.2f-%5.2f, n %d)  weather map %6.2f (%5.2f-%5.2f, n %d)  ratio %.2f"
                             % (s["key"], view, light, t[0], t[1], t[2], t[3], w[0], w[1], w[2], w[3], w[0] / t[0] if t[0] > 0 else float("nan")))
    sh = shares()
    lines.append("Class shares of the M1 sample (percent): " + ", ".join("%s %.2f" % (k, 100 * v) for k, v in sh.items()))
    lines.append("Frequency-weighted march ms (sum of share x ms; the rest counted as: %s; clear sky left out):"
                 % ", ".join("%s as %s" % (k, v) for k, v in REST_LIKE.items() if v))
    for view in VIEWS:
        for light in ("lit", "nolight"):
            num = den = 0.0
            ratios = []
            for k, v in sh.items():
                like = k if len(k) == 1 else REST_LIKE[k]
                if like is None:
                    continue
                t, w = table[(like, view, light)]["today"][0], table[(like, view, light)]["wm"][0]
                num += v * w
                den += v * t
                ratios.append(w / t)
            classes = [table[(k, view, light)] for k in "abcdef"]
            rs = [c["wm"][0] / c["today"][0] for c in classes]
            # Bounds: the measured classes as they are, the rest all at the smallest or all at the largest measured ratio.
            measured = sum(sh[k] for k in "abcde")
            base_t = sum(sh[k] * table[(k, view, light)]["today"][0] for k in "abcde")
            base_w = sum(sh[k] * table[(k, view, light)]["wm"][0] for k in "abcde")
            rest = sum(v for k, v in sh.items() if len(k) > 1 and REST_LIKE[k] is not None)
            mean_t = den / (measured + rest)
            lo = (base_w + rest * mean_t * min(rs)) / (base_t + rest * mean_t)
            hi = (base_w + rest * mean_t * max(rs)) / (base_t + rest * mean_t)
            lines.append("  %-6s %-7s today %.2f ms, weather map %.2f ms, ratio %.2f (rest at the smallest / largest measured ratio: %.2f / %.2f)"
                         % (view, light, den / (measured + rest), num / (measured + rest), num / den, lo, hi))
    print("\n".join(lines))
    return table


# ------------------------------------------------------------------------------------------------------------------------------
# The pictures
# ------------------------------------------------------------------------------------------------------------------------------
VIEW_DE = {"ground": "vom Boden", "above": "von oben (9 km, 15 Grad abwaerts)", "down": "senkrecht nach unten (15 km)"}
ARM_DE = {"today": "heute (heutige Schichtkarten)", "wm": "aus der Wetterkarte (Paket B)"}
SIT_DE = {
    "a": "a) Schoenwetter-Cumulus, flach",
    "b": "b) geschlossene Schicht (bedeckt)",
    "c": "c) Front, alle drei Hoehen gesaettigt",
    "d": "d) Gewitterzelle",
    "e": "e) Haufenwasser ohne Tiefe",
    "f": "f) M0-Ort (KSC, ut 1486800)",
}


def settings_value(job, path, default):
    for s in job.get("settings", []):
        if s["path"] == path:
            return float(s["values"][0])
    return default


def background(job, h, w, depth):
    """A schematic background in display units (not a game frame): a sky gradient above the horizon, a flat dark ground below it; for the ground view the capture's
    scene depth says where terrain and sea are."""
    fw, up = np.array(job["forward"]), np.array(job["up"])
    right = np.cross(up, fw)
    p = np.array(job["position"])
    zen = p / np.linalg.norm(p)
    t = math.tan(math.radians(job["fovV"]) / 2.0)
    ys = (np.arange(h)[::-1] + 0.5) / h * 2.0 - 1.0
    xs = (np.arange(w) + 0.5) / w * 2.0 - 1.0
    X, Y = np.meshgrid(xs * t * w / h, ys * t)
    d = fw[None, None, :] + X[..., None] * right[None, None, :] + Y[..., None] * up[None, None, :]
    d /= np.linalg.norm(d, axis=-1, keepdims=True)
    el = d @ zen
    r = np.linalg.norm(p)
    dip = -math.sqrt(max(0.0, 1.0 - (job["planetRadius"] / r) ** 2))
    sky = el > dip
    if depth is not None:
        sky = depth <= 0.0
    s = np.clip((el - dip) / (1.0 - dip), 0.0, 1.0)[..., None]
    sky_col = (1 - s) * np.array([0.62, 0.74, 0.88]) + s * np.array([0.22, 0.38, 0.70])
    ground = np.array([0.20, 0.24, 0.22])
    return np.where(sky[..., None], sky_col, ground)


def compose(cloud, bg, job):
    """The cloud output (premultiplied linear radiance, alpha) through the game's display stage (e40pp.compose) over the background."""
    white = settings_value(job, "Frame.OutputWhite", 1.0)
    gamma = settings_value(job, "Frame.OutputGamma", 2.2)
    exposure = settings_value(job, "Frame.OutputExposure", 1.0)
    tint = np.array([settings_value(job, "Frame.OutputWhiteTint." + c, 1.0) for c in "rgb"])
    a = np.clip(cloud[..., 3:4], 0.0, 1.0)
    rgb = cloud[..., :3] / np.maximum(a, 1e-4)
    colour = white * np.power(np.maximum(exposure * rgb / (white * tint), 0.0), 1.0 / gamma)
    return np.clip(colour * a + bg * (1.0 - a), 0.0, 1.0)


def label(img, lines):
    from PIL import Image, ImageDraw, ImageFont
    im = Image.fromarray((img * 255 + 0.5).astype(np.uint8))
    dr = ImageDraw.Draw(im)
    try:
        font = ImageFont.truetype("arial.ttf", 30)
    except OSError:
        font = ImageFont.load_default()
    y = 12
    for t in lines:
        bb = dr.textbbox((14, y), t, font=font)
        dr.rectangle((bb[0] - 6, bb[1] - 4, bb[2] + 6, bb[3] + 4), fill=(0, 0, 0))
        dr.text((14, y), t, fill=(255, 255, 255), font=font)
        y = bb[3] + 10
    return im


def local_frame(job):
    """East and north at the camera's place and the camera's heading, in the map frame (worldToMap)."""
    M = np.array(job["worldToMap"], dtype=np.float64).reshape(3, 3)
    p = M @ np.array(job["position"], dtype=np.float64)
    up = p / np.linalg.norm(p)
    east = np.cross(np.array([0.0, 0.0, 1.0]), up)
    east /= np.linalg.norm(east)
    north = np.cross(up, east)
    f = M @ np.array(job["forward"], dtype=np.float64)
    return up, east, north, math.degrees(math.atan2(float(f @ east), float(f @ north)))


def patch(dump, s, job, path, half_km=200.0, px=400):
    """The moved weather map round the camera's place, seen from above (north up), as the views see it: cover, cumuliform, sheet, base, top."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    c = columns(dump, s["ut"])
    j, i = source_index(s["lat"], s["lon"])
    up, east, north, heading = local_frame(job)
    R = 600000.0
    k = np.linspace(-half_km, half_km, px) * 1000.0
    E, N = np.meshgrid(k, k[::-1])
    d = up[None, None, :] * R + E[..., None] * east + N[..., None] * north
    d /= np.linalg.norm(d, axis=-1, keepdims=True)
    lat = np.degrees(np.arcsin(np.clip(d[..., 2], -1, 1)))
    lon = np.degrees(np.arctan2(d[..., 1], d[..., 0]))
    jj = np.clip(((90.0 - lat) / 180.0 * MAP_H).astype(int), 0, MAP_H - 1)
    ii = np.clip(((lon + 180.0) / 360.0 * MAP_W).astype(int) % MAP_W, 0, MAP_W - 1)
    sj, si = j[jj, ii], i[jj, ii]
    panels = [("Bedeckung (Anteil des Himmels)", c["cover"], 0, 1, "Blues_r"), ("Haufenwolken-Anteil", c["cumuliform"], 0, 1, "Blues_r"),
              ("Schichtwolken-Anteil", c["sheet"], 0, 1, "Blues_r"), ("Konvektive Untergrenze (m)", c["base"], 0, 4000, "viridis"),
              ("Konvektive Obergrenze (m)", c["top"], 0, 11000, "viridis"), ("Gesaettigte Hoehen (1 tief, 2 mittel, 4 hoch)",
               ((c["rhLow"] >= SATURATED) * 1 + (c["rhMid"] >= SATURATED) * 2 + (c["rhHigh"] >= SATURATED) * 4).astype(float), 0, 7, "tab10")]
    fig, axes = plt.subplots(2, 3, figsize=(19.2, 12.0), dpi=100)
    for ax, (title, arr, lo, hi, cmap) in zip(axes.flat, panels):
        im = ax.imshow(arr[sj, si], extent=(-half_km, half_km, -half_km, half_km), vmin=lo, vmax=hi, cmap=cmap, interpolation="nearest")
        ax.set_title(title, fontsize=14)
        ax.set_xlabel("Osten (km)")
        ax.set_ylabel("Norden (km)")
        fig.colorbar(im, ax=ax, fraction=0.046)
        ax.plot([0], [0], "r+", ms=16, mew=2)
        hx, hy = math.sin(math.radians(heading)), math.cos(math.radians(heading))
        ax.arrow(0, 0, 60 * hx, 60 * hy, color="red", width=1.5, head_width=10)
        # The straight-down picture: 17.4 km along the heading (its vertical), 30.8 km across.
        fx, fy, rx, ry = hx * 8.7, hy * 8.7, hy * 15.4, -hx * 15.4
        ax.plot([fx + rx, fx - rx, -fx - rx, -fx + rx, fx + rx], [fy + ry, fy - ry, -fy - ry, -fy + ry, fy + ry], "r-", lw=1.2)
    v = s["column"]
    fig.suptitle("%s - Wetterkarte am Kameraort (ut %d, Herkunft %.2f / %.2f Grad), Kreuz = Kamera, Pfeil = Blickrichtung, Rechteck = Bild senkrecht von 15 km\n"
                 "Spalte am Ort: Bedeckung %.2f, Haufen %.2f, Schicht %.2f, Untergrenze %.0f m, Obergrenze %.0f m, Zellreichweite %.2f, rF tief/mittel/hoch %.2f/%.2f/%.2f, Niederschlag %.2f mm/h"
                 % (SIT_DE[s["key"]], s["ut"], s["lat"], s["lon"], v["cover"], v["cumuliform"], v["sheet"], v["base"], v["top"], v["reach"], v["rhLow"], v["rhMid"], v["rhHigh"], v["precip"]),
                 fontsize=13)
    fig.tight_layout(rect=(0, 0, 1, 0.93))
    fig.savefig(path)
    plt.close(fig)


def pictures(run):
    with open(SITUATIONS, encoding="utf-8") as f:
        meta = json.load(f)
    dump, sits = meta["dump"], meta["situations"]
    with open(os.path.join(PIC_IN, "jobs.json"), encoding="utf-8") as f:
        jobs = {j["id"]: j for j in json.load(f)["jobs"]}
    os.makedirs(PICTURES, exist_ok=True)
    stats = []
    for s in sits:
        for view in VIEWS:
            for arm in ARMS:
                jid = "wm-%s-%s-%s" % (s["key"], view, arm)
                path = os.path.join(run, jid + "-cloud.npy")
                if not os.path.isfile(path):
                    print("missing", path)
                    continue
                job = jobs[jid]
                cloud = np.load(path)[::-1]
                depth = None
                if job.get("depthFile"):
                    depth = np.load(os.path.join(PIC_IN, job["depthFile"]))[::-1]
                bg = background(job, cloud.shape[0], cloud.shape[1], depth)
                img = compose(cloud, bg, job)
                a = cloud[..., 3]
                stats.append(dict(id=jid, alpha_mean=float(a.mean()), cloud_share=float((a > 0.05).mean()), closed_share=float((a > 0.9).mean())))
                v = s["column"]
                lines = ["WM1 %s, %s, %s" % (SIT_DE[s["key"]], VIEW_DE[view], ARM_DE[arm]),
                         "Spalte: Bedeckung %.2f, Haufen %.2f, Schicht %.2f, Untergrenze %.0f m, Obergrenze %.0f m; Wolkenanteil im Bild %.2f" % (
                             v["cover"], v["cumuliform"], v["sheet"], v["base"], v["top"], float((a > 0.05).mean())),
                         "Weather Player 2560x1440, Einstellungen der Spielaufnahme (pin u2247600), Sonne der Aufnahme; Hintergrund schematisch, kein Spielbild"]
                name = "WM1-%s-%s-%s-%s.png" % (s["key"], {"a": "schoenwetter-cumulus", "b": "bedeckt-schicht", "c": "front-alle-hoehen", "d": "gewitterzelle",
                                                           "e": "haufenwasser-ohne-tiefe", "f": "m0-ort-ksc"}[s["key"]], view, {"today": "heute", "wm": "wetterkarte"}[arm])
                label(img, lines).save(os.path.join(PICTURES, name))
        patch(dump, s, jobs["wm-%s-ground-today" % s["key"]], os.path.join(PICTURES, "WM1-%s-wetterkarte-ausschnitt.png" % s["key"]))
    with open(os.path.join(PICTURES, "WM1-picture-stats.json"), "w", encoding="utf-8") as f:
        json.dump(stats, f, indent=1)
    for st in stats:
        print("%-28s alpha %.3f cloud %.3f closed %.3f" % (st["id"], st["alpha_mean"], st["cloud_share"], st["closed_share"]))


def report_attr():
    """The layers' share of the march: per job and light, the march with all layers and the drop when one layer is left out (wm1-attr runs)."""
    per = {}
    for p in sorted(glob.glob(os.path.join(BUILD, "wm1-attr-*.json"))):
        with open(p, encoding="utf-8") as f:
            for r in json.load(f):
                if r["metric"].startswith("wmcost.marchr"):
                    _, _, rest = r["metric"].split(".", 2)
                    per.setdefault(rest, []).append(r["value"])
    names = {14: "lowstratiform", 13: "cumulus", 11: "cb", 7: "cirrus"}
    lines = ["WM1 attribution: march ms with all layers, and how much each layer costs (all minus that layer left out); median of the rounds"]
    for jid in ATTR_JOBS.split(","):
        for light in ("lit", "nolight"):
            base = per.get("%s.%s" % (jid, light), [])
            if not base:
                continue
            b = float(np.median(base))
            parts = []
            for m, n in names.items():
                xs = per.get("%s.%s.m%d" % (jid, light, m), [])
                if xs:
                    parts.append("%s %+.1f" % (n, b - float(np.median(xs))))
            lines.append("  %-22s %-7s all %6.2f ms | %s" % (jid, light, b, ", ".join(parts)))
    print("\n".join(lines))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pin-prepare", action="store_true")
    ap.add_argument("--prepare", action="store_true")
    ap.add_argument("--dump")
    ap.add_argument("--report", action="store_true")
    ap.add_argument("--pictures", action="store_true")
    ap.add_argument("--pic-run", default=os.path.join(REPO, "build", "WeatherPlayer", "wm1-pic-pic"))
    a = ap.parse_args()
    if a.pin_prepare:
        from weather_metrics import e40pp
        e40pp.prepare_pin([CAPTURE], out=PIN_IN)
    if a.prepare:
        prepare(a.dump)
    if a.report:
        report()
        report_attr()
    if a.pictures:
        pictures(a.pic_run)


if __name__ == "__main__":
    main()
