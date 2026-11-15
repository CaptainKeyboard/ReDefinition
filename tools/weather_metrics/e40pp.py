"""E40 PP: the weather player reproduces what the game shows (series e40-pp; the definition is .local/plans/atmospheres/E40-player-parity.md).

    python tools/weather_metrics/e40pp.py --prepare [--log LOG] [--maps MAPS] [--state start62|now]   the views of a K-T2 soft60 start as player jobs
    python tools/build_weather_player.py --series e40-pp [--no-build]
    python tools/weather_metrics/e40pp.py --report                                                  the table of the last run
    python tools/weather_metrics/e40pp.py --selftest

A view of the game is replayed with what the game ran: the pose and the field of view, the clock and the sun's direction (the K-T2 log), the pass's sun colour and the
display stage's white (the log's frameSun and composition lines), the ambient light (frameAmbient), the layer maps the probe dumped, and the far haze. Where the dump
of the view holds pass.json (the probe's E40 PP dump, taken in the frame of the picture) the replay takes every number from it instead: the camera relative to the
body's centre in the game's world axes and the world-to-map rotation (the noise is looked up in world axes), the noise's drift, wind and seed, the height maps, the
anvils already drawn into the maps, the haze uniforms, the display stage; and the player's own readout of the uniforms is compared with the game's, name by name.
The state start62 replays the cfg of start 62's commit (ed1272e7) with the type tops not closed and no anvils (what that start ran); now replays today's cfg.
The cloud is laid over the game's own frame without clouds with the game's display stage (e40v1.compose), and the pair game | replay is measured with the same
masks: per region (3 x 3) the luminance and the colour of the cloud pixels, the share of cloud pixels, the outline's width.
"""
import argparse
import json
import math
import os
import re
import shutil
import subprocess
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40c1 as c1  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import e40v1 as v1  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40pp-in")
RESULTS = os.path.join(BUILD, "e40pp-results.json")
MAIN = os.environ.get("ATMO_MAINREPO") or os.path.join("C:" + os.sep, "Users", "franz", "IdeaProjects", "ReDefinition")
GAME = os.path.join(MAIN, ".local", "work", "series-pictures", "K-T2")
MAPS = os.path.join(GAME, "maps")
LOG = os.path.join(MAIN, "build", "atmo-series", "K-T2-20261115-063326-soft60.log")
PICTURES = os.path.join(MAIN, ".local", "work", "series-pictures", "E40")
GAME_START = 62
START_COMMIT = "ed1272e7"
CFG_REL = "GameData/ReDefinition/Atmospheres/Bodies/Clouds-Kerbin.cfg"
W, H = 1708, 960
SOLAR_DAY = 21600.0
# The tolerance of parity (the definition, section 3): cloud-pixel mean colour, cover share, outline width.
TOL_COLOUR, TOL_COVER, TOL_EDGE = 0.03, 0.03, 0.15
# A pixel is cloud where the colour distance to the frame without clouds is above this (display units, the same for the game and the replay).
CLOUD_SIGNAL = 0.03
# ... and its saturation is this much lower than the baseline's (cloud_stats)
CLOUD_GREYING = 0.06

# name -> the K-T2 view (pose rows, picture names), the scene, the dump prefix (None: the type view's own dump), the cell target of a type view
VIEWS = {
    "gfair": dict(view="ground", scene="fair", picture="ground_fair", dump="fw_off-ground-"),
    "hfair": dict(view="horizon", scene="fair", picture="horizon_fair", dump="fw_off-ground-"),
    "afair": dict(view="above", scene="fair", picture="above_fair", dump="fw_off-above-"),
    "gclock": dict(view="ground", scene="clock", picture="ground", dump="k_off-ground-"),
    "iclock": dict(view="inside", scene="clock", picture="inside", dump="k_off-inside-"),
    "aclock": dict(view="above", scene="clock", picture="above", dump="k_off-above-"),
    "sclock": dict(view="sky3", scene="clock", picture="sky3", dump="k_off-sky3-"),
}
for _t in ("humilis", "congestus", "cumulonimbus", "cirrus"):
    for _p in ("g", "a"):
        VIEWS["ty%s%s" % (_t[:3], _p)] = dict(view="ty_%s_%s" % (_t, _p), scene="clock", picture="ty_%s_%s" % (_t, _p), dump="clock-ty_%s_%s-" % (_t, _p), target=_t)


# --------------------------------------------------------------------------------------------------------------------------------
# The game's numbers from the K-T2 log
# --------------------------------------------------------------------------------------------------------------------------------
TIME = re.compile(r"^\[(?:LOG|WRN|ERR|EXC) (\d\d):(\d\d):(\d\d\.\d+)\]")
ROW = re.compile(r'"metric":"([^"]+)","value":(-?[0-9.eE+-]+)')
TRIPLE = r"\((-?[0-9.]+), (-?[0-9.]+), (-?[0-9.]+)\)"


def _seconds(line):
    m = TIME.match(line)
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3)) if m else None


def parse_log(path):
    """The numbers of a K-T2 soft60 log the replay needs (see the module's docstring), per scene ("fair", "clock")."""
    out = dict(scenes={}, poses={}, clock=[], grains={}, bearings={}, moved={}, dumps={})
    scene = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            t = _seconds(line)
            if t is None:
                continue
            m = re.search(r"T09 soft scene (\w+): ut (\d+)", line)
            if m:
                scene = m.group(1)
                out["scenes"].setdefault(scene, {})
                out["clock"].append((t, float(m.group(2)), scene))
                continue
            if scene is None:
                continue
            s = out["scenes"][scene]
            m = re.search(r"T09d env frameSun: SunColour " + TRIPLE + r".*elevation over the local up (-?[0-9.]+) degrees", line)
            if m and "sun" not in s:
                s["sun"] = [float(m.group(i)) for i in (1, 2, 3)]
                s["sun_elev"] = float(m.group(4))
                s["sun_time"] = t
            m = re.search(r"T09d env frameAmbient: AmbientTop " + TRIPLE + r".*AmbientBottom " + TRIPLE, line)
            if m and "ambient_top" not in s:
                s["ambient_top"] = [float(m.group(i)) for i in (1, 2, 3)]
                s["ambient_bottom"] = [float(m.group(i)) for i in (4, 5, 6)]
            m = re.search(r"T09d env composition: .*white ([0-9.]+), pass sun colour ([0-9.]+)", line)
            if m and "white" not in s:
                s["white"], s["sun_grey"] = float(m.group(1)), float(m.group(2))
            m = re.search(r"T09d env camera: .*field of view ([0-9.]+)", line)
            if m and "fov" not in s:
                s["fov"] = float(m.group(1))
            m = re.search(r"dumped the layer maps .* to .*[/\\]maps[/\\]([^ /\\]+?-u(\d+))\s*$", line.rstrip())
            if m:
                out["clock"].append((t, float(m.group(2)), scene))
                out["dumps"][m.group(1)] = dict(time=t, ut=float(m.group(2)), scene=scene)
            m = re.search(r"T09 soft grain (\w+) soft:", line)
            if m:
                out["grains"][(scene, m.group(1))] = t
            m = re.search(r"T09 types \(clock\): (.*)$", line)
            if m:
                for name, deg in re.findall(r"(\w+) [0-9.]+ km at ([0-9.]+) degrees", m.group(1)):
                    out["bearings"][name] = float(deg)
            for name, value in ROW.findall(line):
                k = re.match(r"T09\.view\.(\w+)\.(altitude_m|elevation_deg|azimuth_deg)$", name)
                if k:
                    out["poses"].setdefault((scene, k.group(1)), {})[k.group(2)] = float(value)
                k = re.match(r"T09s\.clock\.(ty_\w+)\.vessel_moved_km$", name)
                if k:
                    out["moved"][k.group(1)] = float(value)
    out["clock"].sort()
    return out


def ut_at(log, t, scene):
    """The game's clock at a log time: piecewise linear through the (log time, ut) pairs of the scene (the game runs slower than the wall while it loads)."""
    pairs = [(a, u) for a, u, s in log["clock"] if s == scene]
    if not pairs:
        raise ValueError("no clock for " + scene)
    if len(pairs) == 1:
        return pairs[0][1] + 0.65 * (t - pairs[0][0])
    for (a0, u0), (a1, u1) in zip(pairs, pairs[1:]):
        if t <= a1 or (a1, u1) == pairs[-1]:
            rate = min(max((u1 - u0) / max(a1 - a0, 1e-3), 0.3), 1.2)
            return u0 + rate * (t - a0)
    return pairs[-1][1]


# --------------------------------------------------------------------------------------------------------------------------------
# Geometry: the site, the vessel of a type view, the sun
# --------------------------------------------------------------------------------------------------------------------------------
def unit(lat_deg, lon_deg):
    la, lo = math.radians(lat_deg), math.radians(lon_deg)
    return np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)])


def destination(lat_deg, lon_deg, bearing_deg, km, radius=c1.R):
    """The point km along the great circle from (lat, lon) at the bearing (degrees from north towards east)."""
    d = km * 1000.0 / radius
    la, lo, b = math.radians(lat_deg), math.radians(lon_deg), math.radians(bearing_deg)
    la2 = math.asin(math.sin(la) * math.cos(d) + math.cos(la) * math.sin(d) * math.cos(b))
    lo2 = lo + math.atan2(math.sin(b) * math.sin(d) * math.cos(la), math.cos(d) - math.sin(la) * math.sin(la2))
    return math.degrees(la2), math.degrees(lo2)


def subsolar_lon(scene_info, ut_ref, ut):
    """Kerbin has no tilt: the sun stands over the equator, west of the site in these afternoon scenes, and moves west by 360 degrees a solar day."""
    el = scene_info["sun_elev"]
    cos_phi = math.cos(math.radians(c1.SITE_LAT))
    hour = math.degrees(math.acos(min(1.0, math.sin(math.radians(el)) / cos_phi)))
    return c1.SITE_LON - hour - (ut - ut_ref) * 360.0 / SOLAR_DAY


def sun_dir(lon_s):
    return unit(0.0, lon_s)


# --------------------------------------------------------------------------------------------------------------------------------
# The jobs
# --------------------------------------------------------------------------------------------------------------------------------
def view_tag(name):
    return "pp-" + name


def map_name(name, layer, kind):
    return "%s-%s-%s.r8" % (view_tag(name), layer, kind)


def find_dump(maps, prefix):
    found = sorted(e for e in os.listdir(maps) if e.startswith(prefix) and os.path.isdir(os.path.join(maps, e)))
    # the picture's own dump (E40 PP: pic-<picture>-soft-u<ut>) where the start wrote one
    return os.path.join(maps, found[-1]) if found else None


def pic_dump(maps, picture):
    found = sorted(e for e in os.listdir(maps) if e.startswith("pic-%s-soft-" % picture) and os.path.isdir(os.path.join(maps, e)))
    return os.path.join(maps, found[-1]) if found else None


def cfg_for(state):
    """The cfg the state replays: start62 the cfg of start 62's commit (a copy in the inputs), now the tree's."""
    if state == "now":
        return e40o.CFG
    path = os.path.join(INPUT, "Clouds-Kerbin-%s.cfg" % START_COMMIT)
    if not os.path.isfile(path):
        text = subprocess.run(["git", "-C", REPO, "show", "%s:%s" % (START_COMMIT, CFG_REL)], capture_output=True, text=True, check=True).stdout
        os.makedirs(INPUT, exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    return path


def job_layers(name, state, pass_json):
    cfg = cfg_for(state)
    old = e40ty.CFG_PATH
    e40ty.CFG_PATH = cfg
    try:
        specs = {s["name"]: s for s in e40o.kerbin_layers(cfg)}
        extras = e40ty.cfg_extras()
    finally:
        e40ty.CFG_PATH = old
    out = []
    dumped = {l["name"]: l for l in (pass_json or {}).get("layers", [])}
    noise_layers = (pass_json or {}).get("noise", {}).get("layers", [])
    for index, layer in enumerate(c1.STACK):
        lk, tk = extras.get(layer, ({}, {}))
        jl = e40o.job_layer(specs[layer], map_name(name, layer, "cover"), map_name(name, layer, "type"))
        jl.update(lk)
        for t in jl["types"]:
            t.update(tk.get(t["name"], {}))
            if state == "start62":
                t["topClose"] = 0.0          # start 62 had no closing tops (58113ee6 came after it)
        if state == "start62" or dumped.get(layer, {}).get("anvilDrawn"):
            jl["anvilType"] = -1.0           # start 62 drew no anvils; a newer dump has them in its maps already
        elif lk.get("anvilType", -1.0) >= 0.0:
            # a dump from before E40 CB holds the precipitation level as the cumulonimbus type (e40c1.old_dump_type_scale)
            rain = c1.layer_rain_type(layer)
            path = os.path.join(INPUT, map_name(name, layer, "type"))
            if rain < 1.0 and os.path.isfile(path) and int(np.fromfile(path, dtype=np.uint8).max()) > rain * 255.0 + 2.0:
                jl["typeScale"] = rain
        d = dumped.get(layer)
        if d and d.get("height") and os.path.isfile(os.path.join(INPUT, "%s-%s-height.u8" % (view_tag(name), layer))):
            h = d["height"]
            jl.update(heightMap="%s-%s-height.u8" % (view_tag(name), layer), heightWidth=h["width"], heightHeight=h["height"], heightRoot=h["root"], heightTop=h["top"],
                      heightVariation=h["variation"], heightSeed=int(d.get("heightSeed", -1)))
        if index < len(noise_layers):
            n = noise_layers[index]
            jl.update(drift=[float(x) for x in n["drift"]], rise=float(n["rise"]), wind=[float(x) for x in n["wind"]])
        out.append(jl)
    return out


def pose(log, name, dump=None):
    """The camera, the clock and the sun of a view from the log (no pass.json): position in the map frame (the replay's world), heading, pitch."""
    spec = VIEWS[name]
    scene = spec["scene"]
    p = log["poses"][(scene, spec["view"])]
    lat, lon = c1.SITE_LAT, c1.SITE_LON
    if "target" in spec:
        lat, lon = destination(lat, lon, log["bearings"][spec["target"]], log["moved"][spec["view"]])
    info = log["scenes"][scene]
    grain = log["grains"].get((scene, spec["view"]))
    # the picture: the first frame of the variant soft, about two seconds before the grain line that follows its frames; a type view without a grain line (no light rows)
    # reads it about seven seconds after its dump (the baseline frame and the settling between)
    d = log["dumps"].get(dump) if dump else None
    t_pic = (grain - 2.0) if grain is not None else (d["time"] + 7.0 if d else info["sun_time"])
    ut = ut_at(log, t_pic, scene)
    ut_ref = ut_at(log, info["sun_time"], scene)
    sun = sun_dir(subsolar_lon(info, ut_ref, ut))
    pos = unit(lat, lon) * (c1.R + p["altitude_m"])
    return dict(lat=lat, lon=lon, alt=p["altitude_m"], heading=p["azimuth_deg"], pitch=p["elevation_deg"], ut=ut, sun=sun, pos=pos,
                sin_el=float(sun @ (pos / np.linalg.norm(pos))))


def view_light(log, name, ps):
    """The pass's sun colour, the stage's white and the tint, the ambient light: the scene's own numbers (log), the white scaled by the sun's air at the view's place and
    height against the scene's ground view (the game's white is the scene's sun as Scatterer colours it at the active vessel)."""
    info = log["scenes"][VIEWS[name]["scene"]]
    ground = log["poses"][(VIEWS[name]["scene"], "ground")]
    sin_ref = math.sin(math.radians(info["sun_elev"]))
    luma = v1.LUMA
    # The white follows the sun's elevation at the place, not the vessel's height (E40PP_WHITE_ALT=1 scales by the height too): which of the two the game's Scatterer colour
    # does is not logged; on start 62 the elevation alone gives the lower mean colour residual over the eleven views (8.7 against 10.3 %, the views from above 8.7 / 1.3
    # against 17 / 6.6 %), the height wins at the congestus view from above only. pass.json carries the game's white.
    alt = ps["alt"] if os.environ.get("E40PP_WHITE_ALT", "0") == "1" else ground["altitude_m"]
    scale = float(e40ty.sun_air(ps["sin_el"], alt) @ luma) / float(e40ty.sun_air(sin_ref, ground["altitude_m"]) @ luma)
    white = info["white"] * scale
    elev = math.degrees(math.asin(max(-1.0, min(1.0, ps["sin_el"]))))
    return dict(sun=list(info["sun"]), sun_grey=info["sun_grey"], white=white, elev=elev, ambient_top=info["ambient_top"], ambient_bottom=info["ambient_bottom"])


HAZE_TINT_START62 = np.array([1.35, 1.48, 1.17])      # the cfg's hazeTintR / G / B at start 62 (E40 FL far; replaced by E40 H1's horizon sky after it)


def far_block(light, state, relative_az):
    """The far block of the state: start 62's haze (the sun's in-scatter over the horizon's 0.35 blue, times the tint and hazeTint), or today's (e40v1.haze_block, E40 H1's
    colours from the air along the view ray at the sun's azimuth relative to the view)."""
    spec = dict(sun=light["sun_grey"], elev=light["elev"], white=light["white"])
    if state != "start62":
        return v1.haze_block(spec, v1.HEADING + relative_az)
    ray = np.array(v1.SC_RAYLEIGH)
    tint = v1.tint_of(spec)

    def inscatter(exponent):
        return np.maximum(spec["sun"] * 0.23 * (ray / ray.mean()) ** exponent, np.array(gn.AMBIENT_TOP))

    return dict(haze=1, shapeMean=0, policy=0, minSamples=0, rayleigh=list(v1.SC_RAYLEIGH), mie=v1.SC_MIE, rayleighHeight=v1.SC_HR, mieHeight=v1.SC_HM, hazeStrength=1.0,
                inscatter=[float(x) for x in inscatter(0.35) * tint * HAZE_TINT_START62], inscatterNear=[float(x) for x in inscatter(0.0) * tint])


def sun_relative_azimuth(ps):
    """The sun's azimuth at the view's place relative to the camera's heading (degrees, 0 to 180)."""
    up = ps["pos"] / np.linalg.norm(ps["pos"])
    east = np.cross(np.array([0.0, 0.0, 1.0]), up)
    east /= np.linalg.norm(east)
    north = np.cross(up, east)
    az = math.degrees(math.atan2(float(ps["sun"] @ east), float(ps["sun"] @ north)))
    rel = abs(az - ps["heading"]) % 360.0
    return 360.0 - rel if rel > 180.0 else rel


def far_from_uniforms(u):
    a, b, c, e = (u["vectors"][k] for k in ("_FarA", "_FarB", "_FarC", "_FarE"))
    return dict(haze=int(a[0] > 0.5), shapeMean=int(u["vectors"]["_FarD"][0] > 0.5), policy=0, minSamples=0, hazeStrength=a[1], rayleighHeight=a[2], mieHeight=a[3],
                rayleigh=b[:3], mie=b[3], inscatter=c[:3], inscatterNear=e[:3])


def build_job(name, log, state, pass_json=None, dump=None):
    jid = "gn-%s-prod-f0" % view_tag(name)
    layers = job_layers(name, state, pass_json)
    args = gn.prod()
    if pass_json:
        cam = pass_json["camera"]
        u = pass_json["uniforms"]
        out = pass_json["output"]
        job = lay.job(jid, "lum", pr.Camera(np.array(cam["position"]), 0.0, 0.0, W, H, cam["fov"]), layers, W, H, planet=pass_json["planetRadius"], mapWidth=c1.MAP_W,
                      mapHeight=c1.MAP_H, frameNumber=0, soft=0.5, sun=pass_json["sun"]["towards"], sunColour=pass_json["sun"]["colour"], ambientTop=pass_json["ambientTop"],
                      ambientBottom=pass_json["ambientBottom"], far=far_from_uniforms(u), **args)
        job.update(position=cam["position"], forward=cam["forward"], right=cam["right"], up=cam["up"], fovV=cam["fov"],
                   worldToMap=[float(x) for row in pass_json["worldToMap"] for x in row])
        noise = pass_json.get("noise", {})
        job["noise"] = dict(seed=int(noise.get("seed", 1)), flowTime=float(noise.get("flowTime", 0.0)))
        stage = dict(white=out["white"], tint=out["whiteTint"], gamma=out["gamma"], exposure=out["exposure"], ut=pass_json["ut"], source="pass.json",
                     alt=float(np.linalg.norm(cam["position"]) - pass_json["planetRadius"]))
        return job, stage
    ps = pose(log, name, dump)
    light = view_light(log, name, ps)
    cam = pr.Camera(ps["pos"], ps["heading"], ps["pitch"], W, H, log["scenes"][VIEWS[name]["scene"]].get("fov", gn.FOV))
    job = lay.job(jid, "lum", cam, layers, W, H, planet=c1.R, mapWidth=c1.MAP_W, mapHeight=c1.MAP_H, frameNumber=0, soft=0.5, sun=[float(x) for x in ps["sun"]],
                  sunColour=light["sun"], ambientTop=light["ambient_top"], ambientBottom=light["ambient_bottom"], far=far_block(light, state, sun_relative_azimuth(ps)), **args)
    stage = dict(white=light["white"], tint=[float(x) for x in v1.tint_of(dict(elev=light["elev"]))], gamma=e40ty.GAMMA, exposure=1.0, ut=ps["ut"], source="log",
                 sun_elev=light["elev"], alt=ps["alt"])
    return job, stage


def prepare(log_path=LOG, maps=MAPS, state=None):
    """Copies each view's maps into the inputs and writes the jobs (state None: start62 where the dump has no pass.json, else now)."""
    log = parse_log(log_path)
    os.makedirs(INPUT, exist_ok=True)
    jobs, stages, specs = [], {}, []
    for name, spec in VIEWS.items():
        dump = pic_dump(maps, spec["picture"]) or find_dump(maps, spec["dump"])
        if dump is None:
            print("%s: no dump %s* in %s, left out" % (name, spec["dump"], maps))
            continue
        pj_path = os.path.join(dump, "pass.json")
        pass_json = None
        if os.path.isfile(pj_path):
            with open(pj_path, encoding="utf-8") as f:
                pass_json = json.load(f)
        st = state or ("now" if pass_json else "start62")
        for layer in c1.STACK:
            for kind in ("cover", "type"):
                shutil.copyfile(os.path.join(dump, "%s-%s.u8" % (layer, kind)), os.path.join(INPUT, map_name(name, layer, kind)))
            hsrc = os.path.join(dump, "%s-height.u8" % layer)
            if os.path.isfile(hsrc):
                shutil.copyfile(hsrc, os.path.join(INPUT, "%s-%s-height.u8" % (view_tag(name), layer)))
        shutil.copyfile(os.path.join(dump, "humidity.u8"), os.path.join(INPUT, "%s-humidity.r8" % view_tag(name)))
        try:
            job, stage = build_job(name, log, st, pass_json, os.path.basename(dump))
        except KeyError as e:
            print("%s: the log lacks %s, left out" % (name, e))
            continue
        stage.update(state=st, dump=os.path.basename(dump))
        jobs.append(job)
        stages[name] = stage
        specs.append(dict(id=job["id"], variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-humidity.r8" % view_tag(name), updraftMap="", seed=664216370 & 0xFFFFFF))
        print("%s <- %s (%s, %s, ut %.0f)" % (name, os.path.basename(dump), st, stage["source"], stage["ut"]))
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)
    with open(os.path.join(INPUT, "stages.json"), "w", encoding="utf-8") as f:
        json.dump(stages, f, indent=1)
    return jobs


def prepared_stages():
    path = os.path.join(INPUT, "stages.json")
    if not os.path.isfile(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


# --------------------------------------------------------------------------------------------------------------------------------
# The parity check
# --------------------------------------------------------------------------------------------------------------------------------
def game_frame(spec, kind):
    path = os.path.join(GAME, "start%d" % GAME_START, "t09-soft60-%s-%s-1707x960-natural.png" % (spec["picture"], kind))
    if not os.path.isfile(path):
        path = os.path.join(GAME, "t09-soft60-%s-%s-1707x960-natural.png" % (spec["picture"], kind))
    return e40ty.load_png(path) if os.path.isfile(path) else None


def compose(m, off, stage):
    """The replay's radiance over the game's frame without clouds through the game's display stage (pass 4: Blend One OneMinusSrcAlpha of the encoded colour)."""
    a = np.clip(m[:, :off.shape[1], 3:4], 0.0, 1.0)
    white, tint = stage["white"], np.asarray(stage["tint"])
    rgb = m[:, :off.shape[1], :3] / np.maximum(a, 1e-4)
    colour = white * np.power(np.maximum(stage.get("exposure", 1.0) * rgb / (white * tint), 0.0), 1.0 / stage.get("gamma", e40ty.GAMMA))
    return np.clip(colour * a + off * (1.0 - a), 0.0, 1.0)


def measure_mask(name, ps_alt, shape):
    """The pixels compared: not the vessel in the middle (ground and inside views; a view from above looks down past it), not the bottom fifth of a view from the
    ground (terrain and sea, which the replay does not have; the clouds there stand behind them)."""
    h, w = shape[:2]
    ok = np.ones((h, w), dtype=bool)
    if ps_alt < 3000.0:
        ok[:, 770:940] = False
        ok[int(h * 0.80):, :] = False
    else:
        ok[int(h * 0.35):int(h * 0.75), 770:940] = False
    return ok


def regions(ok, n=3):
    h, w = ok.shape
    for i in range(n):
        for j in range(n):
            r = np.zeros_like(ok)
            r[i * h // n:(i + 1) * h // n, j * w // n:(j + 1) * w // n] = True
            yield "%d%d" % (i, j), r & ok


def saturation(img):
    hi, lo = img.max(axis=-1), img.min(axis=-1)
    return (hi - lo) / np.maximum(hi, 1e-4)


def cloud_stats(img, off, sel):
    """Cloud pixels: the colour moved away from the frame without clouds AND towards grey (a cloud is less saturated than the blue sky or sea behind it). The game's frame
    also differs from its baseline where no cloud is (the cloud shadows on the sea, the far haze over clear sky, a few hundredths everywhere in the views from above),
    which the replay does not draw; those keep their hue and are not counted."""
    sig = np.sqrt(((img - off) ** 2).sum(axis=-1))
    greying = saturation(off) - saturation(img)
    cloud = sel & (sig > CLOUD_SIGNAL) & (greying > CLOUD_GREYING)
    # the outline is measured on the signal of the greying pixels only (a shadow's edge is not a cloud's)
    sig = sig * np.clip(greying / (2.0 * CLOUD_GREYING), 0.0, 1.0)
    out = dict(cover=float(cloud.sum() / max(sel.sum(), 1)), n=int(cloud.sum()))
    if cloud.sum() >= 200:
        rgb = img[cloud].mean(axis=0)
        out.update(rgb=[float(x) for x in rgb], luma=float(rgb @ v1.LUMA))
    return out, sig, cloud


def compare(name, game, replay, off, alt):
    ok = measure_mask(name, alt, game.shape)
    g, gsig, gc = cloud_stats(game, off, ok)
    r, rsig, rc = cloud_stats(replay, off, ok)
    row = dict(game=g, replay=r, regions={})
    row["d_cover"] = r["cover"] - g["cover"]
    if "rgb" in g and "rgb" in r:
        gr, rr = np.array(g["rgb"]), np.array(r["rgb"])
        row["d_luma"] = (r["luma"] - g["luma"]) / g["luma"]
        row["d_colour"] = float(np.max(np.abs(rr - gr)) / g["luma"])
        row["d_chroma"] = float(np.max(np.abs(rr / r["luma"] - gr / g["luma"])))
    ge, re_ = v1.edge_stats(gsig, ok), v1.edge_stats(rsig, ok)
    row["edge_game"], row["edge_replay"] = ge["edge"], re_["edge"]
    row["d_edge"] = (re_["edge"] - ge["edge"]) / ge["edge"] if ge["edge"] == ge["edge"] and ge["edge"] > 0 else float("nan")
    for key, sel in regions(ok):
        gg, _, _ = cloud_stats(game, off, sel)
        rr2, _, _ = cloud_stats(replay, off, sel)
        reg = dict(cover_game=gg["cover"], cover_replay=rr2["cover"])
        if "rgb" in gg and "rgb" in rr2:
            reg["d_luma"] = (rr2["luma"] - gg["luma"]) / gg["luma"]
            reg["d_colour"] = float(np.max(np.abs(np.array(rr2["rgb"]) - np.array(gg["rgb"]))) / gg["luma"])
        row["regions"][key] = reg
    row["pass_colour"] = abs(row.get("d_colour", float("inf"))) <= TOL_COLOUR
    row["pass_cover"] = abs(row["d_cover"]) <= TOL_COVER
    row["pass_edge"] = abs(row["d_edge"]) <= TOL_EDGE if row["d_edge"] == row["d_edge"] else False
    row["parity"] = row["pass_colour"] and row["pass_cover"] and row["pass_edge"]
    return row


def residual_words(row):
    return "colour %s, cover %+.3f, edge %s" % ("%.1f %%" % (100 * row["d_colour"]) if "d_colour" in row else "n/a", row["d_cover"],
                                                 "%+.0f %%" % (100 * row["d_edge"]) if row["d_edge"] == row["d_edge"] else "n/a")


def pair_picture(name, game, replay, row, state):
    from PIL import Image
    sys.path.insert(0, os.path.join(REPO, "tools"))
    import picture_label as pl
    halves = []
    for img, text in ((game, pl.text_for(game=GAME_START)), (replay, pl.text_for(parity="%s; state %s" % (residual_words(row), state)))):
        p = Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8), "RGB").resize((img.shape[1] // 2, img.shape[0] // 2), Image.BILINEAR)
        halves.append(pl.label_image(p, text))
    out = Image.new("RGB", (halves[0].width + halves[1].width + 6, max(h.height for h in halves)), (0, 0, 0))
    out.paste(halves[0], (0, 0))
    out.paste(halves[1], (halves[0].width + 6, 0))
    from PIL import PngImagePlugin
    meta = PngImagePlugin.PngInfo()
    meta.add_text(pl.KEY, "%s | %s" % (pl.text_for(game=GAME_START), pl.text_for(parity=residual_words(row))))
    os.makedirs(PICTURES, exist_ok=True)
    path = os.path.join(PICTURES, "e40-pp-%s-%s.png" % (state, name))
    out.save(path, pnginfo=meta)
    return path


def uniform_diff(pass_json, player_json, rel=1e-3):
    """The uniforms whose values differ between the game's pass.json and the player's readout (vectors, arrays, matrices, floats; a texture by its size)."""
    out = []
    g, p = pass_json["uniforms"], player_json
    for group in ("floats", "vectors", "arrays", "matrices"):
        for k, gv in g[group].items():
            pv = p[group].get(k)
            a, b = np.array(gv if gv is not None else [], dtype=float).ravel(), np.array(pv if pv is not None else [], dtype=float).ravel()
            if a.shape != b.shape:
                out.append((k, "shape %s / %s" % (a.shape, b.shape)))
                continue
            if a.size and not np.allclose(a, b, rtol=rel, atol=1e-6, equal_nan=True):
                i = int(np.argmax(np.abs(a - b)))
                out.append((k, "game %.6g player %.6g (element %d)" % (a[i], b[i], i)))
    for k, gv in g["textures"].items():
        if gv != p["textures"].get(k):
            out.append((k, "texture %s / %s" % (gv, p["textures"].get(k))))
    return out


LIGHT = ["-light", "product", "-dump", "last"]
MARCH = c1.R33
LABEL = "pp-a"


def stage_list():
    stages = prepared_stages()
    if not stages:
        return []
    views = ",".join(view_tag(n) for n in stages)
    return [Stage(LABEL, ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", MARCH] + LIGHT, size=(W, H), proxy=False,
                  timeout=3400)]


def case_file(name, march):
    tag = view_tag(name)
    return "gs-%s-pr50-static-%s-acc20-f39" % (tag, march) if march != "ref" else "gs-%s-pr50-static-ref-f39" % tag


def collect(ctx):
    rows, metrics = {}, []
    folder = ctx.folder(LABEL)
    stages = prepared_stages()
    for name, stage in stages.items():
        spec = VIEWS[name]
        off, game = game_frame(spec, "off_a"), game_frame(spec, "soft_a")
        path = os.path.join(folder, case_file(name, MARCH) + ".npy")
        if off is None or game is None or not os.path.isfile(path):
            rows[name] = dict(missing="baseline" if off is None else "game frame" if game is None else "replay")
            continue
        m = e40ty.load_lum(folder, case_file(name, MARCH))
        replay = compose(m, off, stage)
        row = compare(name, game, replay, off, stage["alt"])
        row.update(state=stage["state"], source=stage["source"], dump=stage["dump"], ut=stage["ut"], white=stage["white"])
        row["picture"] = pair_picture(name, game, replay, row, stage["state"])
        dump_pj = os.path.join(MAPS, stage["dump"], "pass.json")
        uni = os.path.join(folder, case_file(name, MARCH) + "-uniforms.json")
        if os.path.isfile(dump_pj) and os.path.isfile(uni):
            with open(dump_pj, encoding="utf-8") as f:
                pj = json.load(f)
            with open(uni, encoding="utf-8") as f:
                row["uniform_diff"] = uniform_diff(pj, json.load(f))
        rows[name] = row
        metrics.append(Metric.info("e40pp.%s.d_colour" % name, row.get("d_colour", float("nan"))))
        metrics.append(Metric.info("e40pp.%s.d_cover" % name, row["d_cover"]))
        metrics.append(Metric.info("e40pp.%s.d_edge" % name, row["d_edge"]))
    old = {}
    if os.path.isfile(RESULTS):
        with open(RESULTS, encoding="utf-8") as f:
            old = json.load(f)
    run = os.environ.get("E40PP_RUN", "last")
    old[run] = rows
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(old, f, indent=1)
    print(report(rows))
    return metrics


def report(rows):
    lines = ["E40 PP parity (tolerance: colour %.0f %%, cover %.2f, edge %.0f %%)" % (100 * TOL_COLOUR, TOL_COVER, 100 * TOL_EDGE),
             "   %-8s %-7s %6s %6s | %6s %6s %7s | %6s %6s %6s | %s" % ("view", "state", "cov g", "cov r", "dLuma", "dColour", "dChroma", "edge g", "edge r", "dEdge", "verdict")]
    for name, r in rows.items():
        if "missing" in r:
            lines.append("   %-8s no %s" % (name, r["missing"]))
            continue
        lines.append("   %-8s %-7s %6.3f %6.3f | %+6.3f %6.3f %7.3f | %6.1f %6.1f %+6.2f | %s" % (
            name, r["state"], r["game"]["cover"], r["replay"]["cover"], r.get("d_luma", float("nan")), r.get("d_colour", float("nan")), r.get("d_chroma", float("nan")),
            r["edge_game"], r["edge_replay"], r["d_edge"], "PARITY" if r["parity"] else "differs (%s)" % ", ".join(k[5:] for k in ("pass_colour", "pass_cover", "pass_edge") if not r[k])))
        for k, v in r.get("uniform_diff", [])[:12]:
            lines.append("        uniform %s: %s" % (k, v))
    return "\n".join(lines)


register_series("e40-pp", stage_list(), collect, exclusive=False, who="e40-pp")


# --------------------------------------------------------------------------------------------------------------------------------
def selftest():
    # the destination: 245 km due south of the equator is 23.4 degrees of latitude on Kerbin
    la, lo = destination(0.0, -74.5, 180.0, 245.0)
    assert abs(la + 245.0 / 600.0 * 180.0 / math.pi) < 1e-6 and abs(lo + 74.5) < 1e-9
    # the sun: start 62's two scenes, 195 010 s apart, give the fair scene's elevation from the clock scene's to 0.05 degrees
    clock = dict(sun_elev=54.2694)
    lon_s = subsolar_lon(clock, 1686012.6, 1881023.0)
    site = unit(c1.SITE_LAT, c1.SITE_LON)
    el = math.degrees(math.asin(float(sun_dir(lon_s) @ site)))
    assert abs(el - 44.07) < 0.1, el
    assert lon_s % 360.0 < (c1.SITE_LON % 360.0), "west of the site"
    # the clock of the log: piecewise linear
    log = dict(clock=[(0.0, 100.0, "c"), (10.0, 107.0, "c"), (30.0, 121.0, "c")])
    assert abs(ut_at(log, 5.0, "c") - 103.5) < 1e-9 and abs(ut_at(log, 20.0, "c") - 114.0) < 1e-9 and abs(ut_at(log, 40.0, "c") - 128.0) < 1e-9
    # the comparison of a frame with itself is parity, a darker copy is not
    rng = np.random.default_rng(3)
    off = np.array([0.25, 0.4, 0.7]) + 0.02 * rng.random((960, 1707, 3))
    y, x = np.mgrid[0:960, 0:1707]
    blob = np.clip((200.0 - np.hypot(x - 400, y - 300)) / 30.0, 0.0, 1.0)[..., None]
    game = off * (1 - blob) + 0.8 * blob
    row = compare("gfair", game, game.copy(), off, 264.0)
    assert row["parity"] and abs(row["d_cover"]) < 1e-12 and row["d_colour"] < 1e-12, row
    dark = off * (1 - blob) + 0.7 * blob
    row = compare("gfair", game, dark, off, 264.0)
    assert not row["pass_colour"] and row["d_luma"] < -0.05, row
    # the uniform difference names what differs
    pj = dict(uniforms=dict(floats={"_PlanetR": 600000.0}, vectors={"_Sun": [1, 0, 0, 4000]}, arrays={"_LayerA": [[1, 2, 3, 4]]}, matrices={}, textures={"_CovMaps": "512x256x4"}))
    pl_ = dict(floats={"_PlanetR": 600000.0}, vectors={"_Sun": [0.9, 0, 0, 4000]}, arrays={"_LayerA": [[1, 2, 3, 4]]}, matrices={}, textures={"_CovMaps": "512x256x4"})
    d = uniform_diff(pj, pl_)
    assert [k for k, _ in d] == ["_Sun"], d
    # the log parser on a few lines of a start
    import tempfile
    lines = [
        '[LOG 06:46:00.636] [ReDefinition] Atmospheres: T09 soft scene fair: ut 1881000, sun 44.5 degrees; waiting 20 s for the maps',
        '[LOG 06:46:28.808] [ReDefinition] Atmospheres: T09d env frameSun: SunColour (2.8071, 2.7677, 2.8274) luminance 2.7804, TowardsSun (-0.860, 0.000, -0.510), elevation over the local up 44.1074 degrees, x',
        '[LOG 06:46:28.808] [ReDefinition] Atmospheres: T09d env frameAmbient: AmbientTop (0.2408, 0.2486, 0.2524) luminance 0.2472, AmbientBottom (0.1204, 0.1243, 0.1262) luminance 0.1236',
        '[LOG 06:46:28.809] [ReDefinition] Atmospheres: T09d env composition: event x; display stage exposure 1, gamma 2.2, white 0.7244, pass sun colour 2.7863; mode reconstruct',
        '[LOG 06:46:33.361] ATMORESULT {"series":"K-T2","metric":"T09.view.ground.elevation_deg","value":18.28,"variant":"plain"}',
        '[LOG 06:46:35.180] [ReDefinition] Atmospheres: own clouds: dumped the layer maps of 4 layers to C:/x/series-pictures/maps/fw_off-ground-u1881024',
        '[LOG 06:47:14.505] [ReDefinition] Atmospheres: T09 soft grain ground soft: tstd 0.01050',
    ]
    folder = tempfile.mkdtemp()
    path = os.path.join(folder, "k.log")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    log = parse_log(path)
    s = log["scenes"]["fair"]
    assert s["white"] == 0.7244 and s["sun"][0] == 2.8071 and s["ambient_bottom"][2] == 0.1262 and abs(s["sun_elev"] - 44.1074) < 1e-9, s
    assert log["poses"][("fair", "ground")]["elevation_deg"] == 18.28 and log["dumps"]["fw_off-ground-u1881024"]["ut"] == 1881024.0
    assert abs(log["grains"][("fair", "ground")] - (6 * 3600 + 47 * 60 + 14.505)) < 1e-6
    print("selftest passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--prepare", action="store_true")
    parser.add_argument("--log", default=LOG)
    parser.add_argument("--maps", default=MAPS)
    parser.add_argument("--state", choices=("start62", "now"))
    parser.add_argument("--report", action="store_true")
    parser.add_argument("--collect", action="store_true", help="measure the last run's frames again (build/WeatherPlayer/e40-pp-pp-a) without running the player")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.collect:
        class _Ctx(object):
            def folder(self, label):
                return os.path.join(REPO, "build", "WeatherPlayer", "e40-pp-" + label)
        collect(_Ctx())
    elif a.prepare:
        prepare(a.log, a.maps, a.state)
    elif a.report:
        with open(RESULTS, encoding="utf-8") as f:
            data = json.load(f)
        for run, rows in data.items():
            print("run", run)
            print(report(rows))
