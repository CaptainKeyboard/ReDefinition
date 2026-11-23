"""E40 PP: the weather player reproduces what the game shows (series e40-pp; the definition is .local/plans/atmospheres/E40-player-parity.md).

    python tools/weather_metrics/e40pp.py --prepare [--log LOG] [--maps MAPS] [--state start62|now]   the views of a K-T2 soft60 start as player jobs
    python tools/build_weather_player.py --series e40-pp [--no-build]
    python tools/weather_metrics/e40pp.py --report                                                  the table of the last run
    python tools/weather_metrics/e40pp.py --selftest

A view of the game is replayed with what the game ran: the pose and the field of view, the clock and the sun's direction (the K-T2 log), the pass's sun colour and the
display stage's white (the log's frameSun and composition lines), the ambient light (frameAmbient), the layer maps the probe dumped, and the aerial perspective. Where the dump
of the view holds pass.json (the probe's E40 PP dump, taken in the frame of the picture) the replay takes every number from it instead: the camera relative to the
body's centre in the game's world axes and the world-to-map rotation (the noise is looked up in world axes), the noise's drift, wind and seed, the height maps, the
anvils already drawn into the maps, the aerial perspective uniforms, the display stage; and the player's own readout of the uniforms is compared with the game's, name by name.
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
# The game's frames are 1707 x 960 (pass.json: width 1707, the march target 563 x 317); the replay renders the same size (E40 PP64: 1708 put every pixel grid one column off).
W, H = 1707, 960
SOLAR_DAY = 21600.0
# The tolerance of parity (E40 PP64; the definition, section 3, with the quantities that do not depend on the game's exposure kept apart from the one that does):
#   cover share (points), outline width after smoothing both signals (the game's per-pixel grain is left out), mean chroma of the cloud pixels (rgb / luminance, largest channel),
#   sunlit over shaded luminance (90th / 10th percentile of the cloud pixels, smoothed): none of these depends on the exposure of the game's frame.
#   Cloud-pixel mean colour against the game's luminance (after the frame gain): TOL_COLOUR; it depends on the game's exposure (frame_gain) and is "unclear" where it fails.
TOL_COLOUR, TOL_COVER, TOL_EDGE, TOL_CHROMA, TOL_LIT = 0.05, 0.03, 0.15, 0.03, 0.15
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


def job_layers(name, state, pass_json, input_dir=None):
    input_dir = input_dir or INPUT
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
        if state == "start62":
            jl["anvilType"] = -1.0           # start 62 drew no anvils
        elif pass_json:
            # a dump of a start with pass.json has the maps as the GPU held them, anvils included: none is drawn again, but the layer keeps its cfg's anvilType (the cover threshold
            # leaves the anvil's type out of the water content: with anvilType -1 the cumulonimbus layer's coverage came out 0.024 higher than the game's)
            jl["anvilDrawn"] = 1
        elif lk.get("anvilType", -1.0) >= 0.0:
            # a dump from before E40 CB holds the precipitation level as the cumulonimbus type (e40c1.old_dump_type_scale)
            rain = c1.layer_rain_type(layer)
            path = os.path.join(input_dir, map_name(name, layer, "type"))
            if rain < 1.0 and os.path.isfile(path) and int(np.fromfile(path, dtype=np.uint8).max()) > rain * 255.0 + 2.0:
                jl["typeScale"] = rain
        d = dumped.get(layer)
        if d and d.get("height") and os.path.isfile(os.path.join(input_dir, "%s-%s-height.u8" % (view_tag(name), layer))):
            h = d["height"]
            jl.update(heightMap="%s-%s-height.u8" % (view_tag(name), layer), heightWidth=h["width"], heightHeight=h["height"], heightRoot=h["root"], heightTop=h["top"],
                      heightVariation=h["variation"], heightSeed=int(d.get("heightSeed", -1)))
        if index < len(noise_layers):
            n = noise_layers[index]
            jl.update(drift=[float(x) for x in n["drift"]], rise=float(n["rise"]), wind=[float(x) for x in n["wind"]])
            if n.get("axis"):
                jl["axis"] = [float(x) for x in n["axis"]]
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


AERIAL_PERSPECTIVE_TINT_START62 = np.array([1.35, 1.48, 1.17])      # the cfg's aerialPerspectiveTintR / G / B (then hazeTint*) at start 62 (E40 FL far; replaced by E40 H1's horizon sky after it)


def far_block(light, state, relative_az):
    """The far block of the state: start 62's aerial perspective (the sun's in-scatter over the horizon's 0.35 blue, times the tint and aerialPerspectiveTint), or today's (e40v1.aerial_perspective_block, E40 H1's
    colours from the air along the view ray at the sun's azimuth relative to the view)."""
    spec = dict(sun=light["sun_grey"], elev=light["elev"], white=light["white"])
    if state != "start62":
        return v1.aerial_perspective_block(spec, v1.HEADING + relative_az)
    ray = np.array(v1.SC_RAYLEIGH)
    tint = v1.tint_of(spec)

    def inscatter(exponent):
        return np.maximum(spec["sun"] * 0.23 * (ray / ray.mean()) ** exponent, np.array(gn.AMBIENT_TOP))

    return dict(aerialPerspective=1, shapeMean=0, policy=0, minSamples=0, rayleigh=list(v1.SC_RAYLEIGH), mie=v1.SC_MIE, rayleighHeight=v1.SC_HR, mieHeight=v1.SC_HM, aerialPerspectiveStrength=1.0,
                inscatter=[float(x) for x in inscatter(0.35) * tint * AERIAL_PERSPECTIVE_TINT_START62], inscatterNear=[float(x) for x in inscatter(0.0) * tint])


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
    return dict(aerialPerspective=int(a[0] > 0.5), shapeMean=int(u["vectors"]["_FarD"][0] > 0.5), policy=0, minSamples=0, aerialPerspectiveStrength=a[1], rayleighHeight=a[2], mieHeight=a[3],
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


def prepare(log_path=LOG, maps=MAPS, state=None, start=GAME_START):
    """Copies each view's maps into the inputs and writes the jobs (state None: start62 where the dump has no pass.json, else now); start: the game start whose frames
    the views are compared with (series-pictures/K-T2/start<N>, else the frames in K-T2 itself, the last start's)."""
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
        stage.update(state=st, dump=os.path.basename(dump), start=int(start))
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
def game_frame(spec, kind, start=GAME_START):
    path = os.path.join(GAME, "start%d" % start,"t09-soft60-%s-%s-1707x960-natural.png" % (spec["picture"], kind))
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
    also differs from its baseline where no cloud is (the cloud shadows on the sea, the aerial perspective over clear sky, a few hundredths everywhere in the views from above),
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


def lit_ratio(img, cloud):
    """The sunlit against the shaded side of the clouds: the 90th over the 10th percentile of the cloud pixels' luminance after a light smoothing (the game's per-pixel grain would
    widen the spread; the exposure of the frame cancels in the ratio). NaN where the cloud has under 200 pixels."""
    if int(cloud.sum()) < 200:
        return float("nan")
    from scipy.ndimage import gaussian_filter
    lum = gaussian_filter(img @ v1.LUMA, LIT_SMOOTH_PX, mode="nearest")[cloud]
    return float(np.percentile(lum, 90.0) / max(np.percentile(lum, 10.0), 1e-4))


GAIN_MIN_PIXELS = 20000
EDGE_DENOISE_PX = 4.0
LIT_SMOOTH_PX = 2.0
EDGE_FULL_COVER = 0.98


def frame_gain(game, off, alpha, ok):
    """The gain of the game's frame against its baseline where the replay has no cloud (alpha below 0.01): the game's camera runs TUFX (hdr, auto exposure, bloom), which scales the
    whole frame by an exposure that depends on what the frame holds, so the frame with clouds and the baseline without them differ by a factor even where there is no cloud.
    MEASURED on start 64: on cloud-free pixels game against baseline is a power law with exponent 0.97 to 1.02 (a pure gain), 0.89 to 1.06 over the views, the same at 20 and 100 pixels from
    the nearest cloud (not bloom). The replay has no post stage: its composite is compared with the game's frame divided by this gain. None where the frame has too few cloud-free
    pixels (a view inside a closed deck): then the exposure is not known."""
    clear = ok & (alpha < 0.01)
    if int(clear.sum()) < GAIN_MIN_PIXELS:
        return None
    return float((game[clear] @ v1.LUMA).mean() / max((off[clear] @ v1.LUMA).mean(), 1e-6))


def opaque_gain(game, replay, alpha, ok):
    """The game's luminance over the replay's on the pixels the replay covers (alpha above 0.9): the exposure of the game's frame times the replay's radiance error. Next to
    frame_gain (the exposure read from the cloud-free pixels against the baseline frame) it shows how much of a brightness residual the exposure can account for: equal to the
    frame gain means the replay's cloud radiance is the game's. None where under 5000 pixels are covered."""
    sel = ok & (alpha > 0.9)
    if int(sel.sum()) < 5000:
        return None
    return float((game[sel] @ v1.LUMA).mean() / max((replay[sel] @ v1.LUMA).mean(), 1e-6))


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
        row["lit_game"], row["lit_replay"] = lit_ratio(game, gc), lit_ratio(replay, rc)
        row["d_lit"] = (row["lit_replay"] - row["lit_game"]) / row["lit_game"]
    ge, re_ = v1.edge_stats(gsig, ok), v1.edge_stats(rsig, ok)
    row["edge_game"], row["edge_replay"] = ge["edge"], re_["edge"]
    row["d_edge"] = (re_["edge"] - ge["edge"]) / ge["edge"] if ge["edge"] == ge["edge"] and ge["edge"] > 0 else float("nan")
    # The same outline width after both signal maps are smoothed by EDGE_DENOISE_PX: the width is the median gradient of the signal, which the game's per-pixel grain (a short
    # history under a moving camera, precipitation specks, bloom) raises and the replay's converged still frame does not have. Reported next to the raw width, never instead of it.
    from scipy.ndimage import gaussian_filter
    gs_, rs_ = v1.edge_stats(gaussian_filter(gsig, EDGE_DENOISE_PX, mode="nearest"), ok), v1.edge_stats(gaussian_filter(rsig, EDGE_DENOISE_PX, mode="nearest"), ok)
    row["edge_game_dn"], row["edge_replay_dn"] = gs_["edge"], rs_["edge"]
    row["d_edge_dn"] = (rs_["edge"] - gs_["edge"]) / gs_["edge"] if gs_["edge"] == gs_["edge"] and gs_["edge"] > 0 else float("nan")
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
    # a frame the cloud covers all over (cover from EDGE_FULL_COVER) has no outline to measure: the outline is not judged there (reported as is, never a pass or a fail)
    row["edge_judged"] = g["cover"] < EDGE_FULL_COVER
    row["pass_edge"] = (abs(row["d_edge_dn"]) <= TOL_EDGE if row["d_edge_dn"] == row["d_edge_dn"] else False) if row["edge_judged"] else True
    row["pass_chroma"] = row.get("d_chroma", float("inf")) <= TOL_CHROMA
    row["pass_lit"] = abs(row["d_lit"]) <= TOL_LIT if row.get("d_lit", float("nan")) == row.get("d_lit", float("nan")) else False
    row["parity"] = all(row[k] for k in ("pass_colour", "pass_cover", "pass_edge", "pass_chroma", "pass_lit"))
    return row


def residual_words(row):
    def pct(key):
        return "%+.0f %%" % (100 * row[key]) if row.get(key, float("nan")) == row.get(key, float("nan")) else "n/a"

    return "bright %s, chroma %.3f, cover %+.3f, lit/shaded %s, outline %s" % (
        "%.1f %%" % (100 * row["d_colour"]) if "d_colour" in row else "n/a", row.get("d_chroma", float("nan")), row["d_cover"], pct("d_lit"), pct("d_edge_dn"))


EXPOSURE_FREE = ("pass_cover", "pass_edge", "pass_chroma", "pass_lit")


def verdict_of(row):
    """in tolerance / out of tolerance / unclear. A residual in a quantity the game's exposure does not touch (cover, outline, chroma, sunlit over shaded) is out of tolerance.
    The brightness of the cloud pixels does depend on it: the frame gain (frame_gain, read from the cloud-free pixels) takes part of it out, but the gain measured on the
    clouds themselves can differ from it (the baseline frame has its own exposure), so a brightness residual outside the tolerance is unclear, never out of tolerance."""
    if not all(row[k] for k in EXPOSURE_FREE):
        return "out of tolerance"
    return "in tolerance" if row["pass_colour"] else "unclear"


def pair_picture(name, game, replay, row, state, start=GAME_START):
    """The pair game | replay. The replay carries the label 'parity checked' only where the view is in tolerance; every other replay is a working view (the label rule)."""
    from PIL import Image
    sys.path.insert(0, os.path.join(REPO, "tools"))
    import picture_label as pl
    shown = pl.text_for(parity=residual_words(row)) if row["verdict"] == "in tolerance" else pl.text_for(working=True)
    halves = []
    for img, text in ((game, pl.text_for(game=start)), (replay, shown)):
        p = Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8), "RGB").resize((img.shape[1] // 2, img.shape[0] // 2), Image.BILINEAR)
        halves.append(pl.label_image(p, text))
    out = Image.new("RGB", (halves[0].width + halves[1].width + 6, max(h.height for h in halves)), (0, 0, 0))
    out.paste(halves[0], (0, 0))
    out.paste(halves[1], (halves[0].width + 6, 0))
    from PIL import PngImagePlugin
    meta = PngImagePlugin.PngInfo()
    meta.add_text(pl.KEY, "%s | %s" % (pl.text_for(game=start), shown))
    meta.add_text("ReDefinition-residual", "%s; state %s; verdict %s; frame gain %s" % (residual_words(row), state, row["verdict"], "%.3f" % row["gain"] if row.get("gain") is not None else "unknown"))
    os.makedirs(PICTURES, exist_ok=True)
    path = os.path.join(PICTURES, "e40-pp-s%d-%s-%s.png" % (start, state, name))
    out.save(path, pnginfo=meta)
    return path


def uniform_diff(pass_json, player_json, rel=1e-3):
    """The uniforms whose values differ between the game's pass.json and the player's readout (vectors, arrays, matrices, floats; a texture by its size)."""
    out = []
    g, p = pass_json["uniforms"], player_json
    # What is left out and why (E40 PP64): the camera and the planet's centre are two ways to write the same camera relative to the centre (compared as the difference); the
    # fourth element of the sun colour and the ambient colours is not read by the shader; the frame number and the jitter seed of the frame change from frame to frame; the display
    # stage (_OutP, _OutW) is the replay's compose step, which takes the same white, tint and gamma from pass.json.
    ignore = {"_PlanetCenter": None, "_CamPos": None, "_OutP": None, "_OutW": None, "_StepP": [2], "_DitherP": [0], "_SunColor": [3], "_AmbientTop": [3], "_AmbientBottom": [3]}
    gv_cam = np.array(g["vectors"].get("_CamPos", [0, 0, 0, 0])[:3], dtype=float) - np.array(g["vectors"].get("_PlanetCenter", [0, 0, 0, 0])[:3], dtype=float)
    pv_cam = np.array(p["vectors"].get("_CamPos", [0, 0, 0, 0])[:3], dtype=float) - np.array(p["vectors"].get("_PlanetCenter", [0, 0, 0, 0])[:3], dtype=float)
    if not np.allclose(gv_cam, pv_cam, rtol=0.0, atol=1.0):
        out.append(("_CamPos - _PlanetCenter", "game %s player %s" % (np.round(gv_cam, 1).tolist(), np.round(pv_cam, 1).tolist())))
    for group in ("floats", "vectors", "arrays", "matrices"):
        for k, gv in g[group].items():
            pv = p[group].get(k)
            a, b = np.array(gv if gv is not None else [], dtype=float).ravel(), np.array(pv if pv is not None else [], dtype=float).ravel()
            if a.shape != b.shape:
                out.append((k, "shape %s / %s" % (a.shape, b.shape)))
                continue
            if k in ignore:
                if ignore[k] is None:
                    continue
                for i in ignore[k]:
                    a[i::4] = 0.0
                    b[i::4] = 0.0
            if a.size and not np.allclose(a, b, rtol=rel, atol=1e-6, equal_nan=True):
                i = int(np.argmax(np.abs(a - b)))
                out.append((k, "game %.6g player %.6g (element %d)" % (a[i], b[i], i)))
    for k, gv in g["textures"].items():
        if gv != p["textures"].get(k):
            out.append((k, "texture %s / %s" % (gv, p["textures"].get(k))))
    return out


# Frame 39 of the still scenario is the picture; the earlier frames (the history after a cut) are written too, to see how much grain the game's frame carries (collect: row["frames"]).
EXTRA_FRAMES = (1, 2, 3, 5, 8, 12, 20)
LIGHT = ["-light", "product", "-dump", "last", "-width", str(W), "-dumpframes", ",".join(str(i) for i in EXTRA_FRAMES + (39,))]
# The product's march: the r33 case plus the leap over empty grid points the game's pass runs with (layers.leapScale 1: _SpacingP.z 1 in every pass.json; the r33 case alone has none).
MARCH = c1.R33 + "+lp100"
LABEL = "pp-b"


def stage_list():
    stages = prepared_stages()
    if not stages:
        return []
    views = ",".join(view_tag(n) for n in stages)
    return [Stage(LABEL, ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", MARCH] + LIGHT, size=(W, H), proxy=False,
                  timeout=3400)]


def case_file(name, march, frame=39):
    tag = view_tag(name)
    return "gs-%s-pr50-static-%s-acc20-f%d" % (tag, march, frame) if march != "ref" else "gs-%s-pr50-static-ref-f%d" % (tag, frame)


def grain_by_frame(name, folder, stage, game, off, gain, ok):
    """The grain of the replay's earlier frames against the game's frame: the share of the cloud signal's power above 0.1 cycles per pixel (e40v1.edge_stats 'hf') and the outline width,
    for the frames after a history cut. The replay's frame 39 is converged; the game's frame carries pixel-scale energy the converged replay lacks."""
    out = {}
    _, gsig, _ = cloud_stats(game / (gain or 1.0), off, ok)
    ge = v1.edge_stats(gsig, ok)
    out["game"] = dict(hf=ge["hf"], edge=ge["edge"])
    for frame in EXTRA_FRAMES + (39,):
        path = os.path.join(folder, case_file(name, MARCH, frame) + ".npy")
        if not os.path.isfile(path):
            continue
        replay = compose(e40ty.load_lum(folder, case_file(name, MARCH, frame)), off, stage)
        _, rsig, _ = cloud_stats(replay, off, ok)
        re_ = v1.edge_stats(rsig, ok)
        out[frame] = dict(hf=re_["hf"], edge=re_["edge"])
    return out


def collect(ctx):
    rows, metrics = {}, []
    folder = ctx.folder(LABEL)
    stages = prepared_stages()
    for name, stage in stages.items():
        spec = VIEWS[name]
        start = stage.get("start", GAME_START)
        off, game = game_frame(spec, "off_a", start), game_frame(spec, "soft_a", start)
        path = os.path.join(folder, case_file(name, MARCH) + ".npy")
        if off is None or game is None or not os.path.isfile(path):
            rows[name] = dict(missing="baseline" if off is None else "game frame" if game is None else "replay")
            continue
        m = e40ty.load_lum(folder, case_file(name, MARCH))
        replay = compose(m, off, stage)
        gain = frame_gain(game, off, np.clip(m[:, :off.shape[1], 3], 0.0, 1.0), measure_mask(name, stage["alt"], game.shape))
        raw = compare(name, game, replay, off, stage["alt"])
        row = compare(name, game / gain, replay, off, stage["alt"]) if gain else raw
        row.update(state=stage["state"], source=stage["source"], dump=stage["dump"], ut=stage["ut"], white=stage["white"], gain=gain,
                   raw=dict(d_luma=raw.get("d_luma"), d_colour=raw.get("d_colour"), d_cover=raw["d_cover"], d_edge=raw["d_edge"]))
        row["verdict"] = verdict_of(row)
        row["k_opaque"] = opaque_gain(game, replay, np.clip(m[:, :off.shape[1], 3], 0.0, 1.0), measure_mask(name, stage["alt"], game.shape))
        row["frames"] = grain_by_frame(name, folder, stage, game, off, gain, measure_mask(name, stage["alt"], game.shape))
        row["picture"] = pair_picture(name, game, np.clip(replay * (gain or 1.0), 0.0, 1.0), row, stage["state"], start)
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
    lines = ["E40 PP parity (tolerance: cover %.2f, outline %.0f %% after smoothing, chroma %.2f, sunlit/shaded %.0f %%, brightness %.0f %% after the frame gain)" % (
                 TOL_COVER, 100 * TOL_EDGE, TOL_CHROMA, 100 * TOL_LIT, 100 * TOL_COLOUR),
             "   %-8s %-7s %5s %5s | %6s %6s | %7s %7s %7s | %7s | %6s | %s" % ("view", "state", "gain", "kOpq", "cov g", "cov r", "bright", "dLuma", "dChroma", "dSunSh", "dEdgeDn", "verdict")]
    for name, r in rows.items():
        if "missing" in r:
            lines.append("   %-8s no %s" % (name, r["missing"]))
            continue
        gain = r.get("gain")
        raw = r.get("raw", {})
        k = r.get("k_opaque")
        failed = ", ".join(key[5:] for key in ("pass_colour", "pass_cover", "pass_edge", "pass_chroma", "pass_lit") if key in r and not r[key])
        lines.append("   %-8s %-7s %5s %5s | %6.3f %6.3f | %7.3f %+7.3f %7.3f | %+7.2f | %+6.2f | %s%s" % (
            name, r["state"], "%.3f" % gain if gain else "none", "%.3f" % k if k else "none", r["game"]["cover"], r["replay"]["cover"], r.get("d_colour", float("nan")), r.get("d_luma", float("nan")),
            r.get("d_chroma", float("nan")), r.get("d_lit", float("nan")), r.get("d_edge_dn", float("nan")), r.get("verdict", "?"), " (%s)" % failed if failed else ""))
        if gain and raw.get("d_colour") is not None:
            lines.append("        before the frame gain: dLuma %+.3f dColour %.3f; raw outline game %.1f replay %.1f" % (raw["d_luma"], raw["d_colour"], r["edge_game"], r["edge_replay"]))
        if "lit_game" in r:
            lines.append("        sunlit over shaded (90th / 10th percentile of the cloud luminance): game %.2f replay %.2f" % (r["lit_game"], r["lit_replay"]))
        if "edge_game_dn" in r:
            lines.append("        outline after smoothing both signals by %.0f px: game %.1f replay %.1f%s" % (
                EDGE_DENOISE_PX, r["edge_game_dn"], r["edge_replay_dn"], "" if r.get("edge_judged", True) else " (not judged: the cloud covers the frame)"))
        if r.get("frames"):
            fr = r["frames"]
            lines.append("        grain (cloud signal power above 0.1 cycles/px, outline px): game %.4f / %.1f; replay frame %s" % (
                fr["game"]["hf"], fr["game"]["edge"], ", ".join("%s: %.4f / %.1f" % (k, v["hf"], v["edge"]) for k, v in fr.items() if k != "game")))
        for k, v in r.get("uniform_diff", []):
            lines.append("        uniform %s: %s" % (k, v))
    return "\n".join(lines)


register_series("e40-pp", stage_list(), collect, exclusive=False, who="e40-pp")


# --------------------------------------------------------------------------------------------------------------------------------
# M0 parity: the replay of the game's pinned capture (master plan phase 0 item 3; T09ProbePin.cs, scene pin-replay)
# --------------------------------------------------------------------------------------------------------------------------------
#   python tools/weather_metrics/e40pp.py --pin-prepare CAPTURE [CAPTURE ...]     the captures (series-pictures/K-T2/maps/pin-*) as replay jobs
#   python tools/build_weather_player.py --series m0-pin                          the replay, twice per job (-repeat 2), and the player's own run-to-run difference
#   python tools/weather_metrics/m0parity.py CAPTURE build/WeatherPlayer/m0-pin-pin/<job>-r0-cloud.npy --base CAPTURE1 CAPTURE2 --out DIR
# Everything the replay marches with comes from the capture's pass.json: the settings (every field of the game's frame, reconstruction and noise switches), every tuning key
# in force, the camera and the planet's centre in the game's world space, the frame numbers from the history's cut; nothing from the march table of e40gn.py. The layer stack
# is still the tree's cfg (job_layers): pass.json's typeTableHash against the replay's <job>-r<k>-replay.json says whether the tables are the same.
PIN_INPUT = os.path.join(BUILD, "m0pin-in")
PIN_LABEL = "pin"
PIN_REPEAT = int(os.environ.get("M0PIN_REPEAT", "2"))


def _quaternion(right, up, forward):
    """The rotation whose columns are right, up and forward (Unity's axes x, y, z), as (x, y, z, w)."""
    m = np.array([right, up, forward], dtype=np.float64).T
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0.0:
        s = math.sqrt(t + 1.0) * 2.0
        return [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    i = int(np.argmax([m[0, 0], m[1, 1], m[2, 2]]))
    j, k = (i + 1) % 3, (i + 2) % 3
    s = math.sqrt(1.0 + m[i, i] - m[j, j] - m[k, k]) * 2.0
    q = [0.0, 0.0, 0.0, (m[k, j] - m[j, k]) / s]
    q[i] = 0.25 * s
    q[j] = (m[j, i] + m[i, j]) / s
    q[k] = (m[k, i] + m[i, k]) / s
    return q


def selftest_standin(pass_json):
    """The pin block and a few settings for a dump from before the pinned capture (no "pin", no "settings"): ONLY for the player's self-parity (the same job replayed twice must
    give the same buffer), which tests the replay's determinism, not the game's numbers. Each value is read from the old pass.json (recon, uniforms) where it has one."""
    u = pass_json["uniforms"]
    centre = [float(x) for x in u["vectors"]["_PlanetCenter"][:3]]
    cam = pass_json["camera"]
    world_cam = [centre[i] + float(cam["position"][i]) for i in range(3)]
    recon = pass_json.get("recon", {})
    rotation = recon.get("setting") == "Block" and float(recon.get("resolution", 0.0)) > 0.99
    acc = float(recon.get("accumulate", 0.2))

    def s(path, value):
        return dict(path=path, values=[repr(float(value)) if isinstance(value, float) else str(value)])

    settings = [s("Frame.UseSceneDepth", 1), s("Frame.MaxSteps", int(recon.get("maxSteps", 48))), s("Frame.DitherSequence", int(recon.get("dither", 3))),
                s("Frame.MinStepM", float(u["vectors"]["_StepP"][0])), s("Frame.PlanetCenter.x", centre[0]), s("Frame.PlanetCenter.y", centre[1]), s("Frame.PlanetCenter.z", centre[2])]
    if rotation:
        # ReconMode.Block is 1 (LayerReconstructor.cs: Full, Block, Low); the rotation's weights as OwnCloudOptions.Apply sets them.
        settings += [s("Recon.Mode", 1), s("Recon.Block", 2), s("Recon.Rotation", 1), s("Recon.FreshWeight", acc), s("Recon.StaleWeight", 0.0), s("Recon.CatmullHistory", 1),
                     s("Recon.Resolution", 1.0), s("Recon.Accumulate", acc)]
    pin = dict(frameOrigin=0, frameNumber=PIN_FRAMES - 1, framesSinceCut=PIN_FRAMES, firstFrameNumber=0, standin=True,
               world=dict(camera=world_cam, rotation=_quaternion(cam["right"], cam["up"], cam["forward"]), planetCenter=centre, fov=cam["fov"], width=cam["width"], height=cam["height"]))
    return pin, settings


# T09Probe.PinFrames: the records from the cut to the capture.
PIN_FRAMES = 64


def _halton32(index, base):
    """Fsr3Upscaler.Halton in float32 (src/Fsr3/Fsr3Upscaler.cs)."""
    f, result, i = np.float32(1.0), np.float32(0.0), index
    while i > 0:
        f = np.float32(f / np.float32(base))
        result = np.float32(result + f * np.float32(i % base))
        i = i // base
    return result


def pin_poses(cap, frames):
    """The game's camera position, rotation and planet centre of each record from the capture's frames.json (OwnCloudModule.PinLogFrame), flattened; None without it."""
    path = os.path.join(cap, "frames.json")
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as f:
        recs = json.load(f)
    if len(recs) != frames:
        return None
    return ([float(x) for r in recs for x in r["cameraWorld"][:3]], [float(x) for r in recs for x in r["rotation"][:4]],
            [float(x) for r in recs for x in r["planetCenter"][:3]])


def pin_jitter(cap, pass_json, pin, frames):
    """The host's jitter of each record from the cut (render pixels), or (None, why). frames.json of the capture (OwnCloudModule.PinLogFrame, the global _ReDefinition_Jitter
    of each record) where it exists; else the upscaler's own sequence (UpscalerRig.ChooseJitter: Fsr3Upscaler.GetJitterOffset at Time.frameCount with GetJitterPhaseCount)
    from the frame numbers of pass.json, the render width and the display width the start's tag names (perfNpic<width>...), for captures from before the frame log."""
    path = os.path.join(cap, "frames.json")
    if os.path.isfile(path):
        with open(path, encoding="utf-8") as f:
            recs = json.load(f)
        if len(recs) != frames:
            return None, "frames.json holds %d records, the capture %d" % (len(recs), frames)
        return [(float(r["jitter"][0]), float(r["jitter"][1])) for r in recs], "frames.json"
    m = re.search(r"pic(\d+)dlss", os.path.basename(os.path.normpath(cap)))
    if not m or "frameOrigin" not in pin or pin.get("standin"):
        return None, "no frames.json and no display width in the tag"
    render, display = int(pass_json["camera"]["width"]), int(m.group(1))
    phases = int(np.float32(8.0) * np.float32(np.float32(display) / np.float32(render)) ** 2)
    out = []
    for k in range(frames):
        i = (int(pin["frameOrigin"]) + k) % phases + 1
        out.append((float(_halton32(i, 2) - np.float32(0.5)), float(_halton32(i, 3) - np.float32(0.5))))
    return out, "the upscaler's sequence: %d phases for %d -> %d, from frame %d" % (phases, render, display, int(pin["frameOrigin"]))


def prepare_pin(captures, out=PIN_INPUT, standin=False):
    """Writes the replay jobs of the captures (pin-<n>) into out: the maps and heights copied, the game's depth, jobs.json and heights.json. standin: a dump from before the
    pinned capture is accepted with selftest_standin's block (the self-parity only)."""
    os.makedirs(out, exist_ok=True)
    jobs, specs, index = [], [], []
    for n, cap in enumerate(captures):
        with open(os.path.join(cap, "pass.json"), encoding="utf-8") as f:
            pj = json.load(f)
        if "settings" in pj and "pin" in pj:
            pin, settings = pj["pin"], pj["settings"]
        elif standin:
            pin, settings = selftest_standin(pj)
        else:
            raise SystemExit("%s: pass.json has no pin block and no settings (not a pinned capture); --pin-standin is for the player's self-parity only" % cap)
        name = "pin%d" % n
        tag = view_tag(name)
        for layer in c1.STACK:
            for kind in ("cover", "type"):
                shutil.copyfile(os.path.join(cap, "%s-%s.u8" % (layer, kind)), os.path.join(out, map_name(name, layer, kind)))
            hsrc = os.path.join(cap, "%s-height.u8" % layer)
            if os.path.isfile(hsrc):
                shutil.copyfile(hsrc, os.path.join(out, "%s-%s-height.u8" % (tag, layer)))
        shutil.copyfile(os.path.join(cap, "humidity.u8"), os.path.join(out, "%s-humidity.r8" % tag))
        depth = None
        if os.path.isfile(os.path.join(cap, "depth.npy")):
            depth = "%s-depth.npy" % tag
            shutil.copyfile(os.path.join(cap, "depth.npy"), os.path.join(out, depth))
        layers = job_layers(name, "now", pj, out)
        # What the replay cannot take from the capture is named, never filled in: a layer without the game's height map (the player would build one from heights.json's
        # stand-in numbers), and a setting the walk left out that is not at its default (a light cache that is on).
        unsupported = ["layer %s: the capture has no height map (%s-height.u8); the replay would build its own" % (jl["name"], jl["name"])
                       for jl in layers if not jl.get("heightMap")]
        unsupported += ["the game's %s is set; the replay cannot set it" % p for p in pj.get("settingsNotDefault", [])]
        cam, u, world = pj["camera"], pj["uniforms"], pin["world"]
        w, h = int(world["width"]), int(world["height"])
        jid = "pin-" + name
        job = lay.job(jid, "lum", pr.Camera(np.array(cam["position"]), 0.0, 0.0, w, h, world["fov"]), layers, w, h, planet=pj["planetRadius"], mapWidth=c1.MAP_W, mapHeight=c1.MAP_H,
                      sun=pj["sun"]["towards"], sunColour=pj["sun"]["colour"], ambientTop=pj["ambientTop"], ambientBottom=pj["ambientBottom"], far=far_from_uniforms(u))
        job.update(position=cam["position"], forward=cam["forward"], right=cam["right"], up=cam["up"], fovV=world["fov"],
                   worldToMap=[float(x) for row in pj["worldToMap"] for x in row])
        noise = pj.get("noise", {})
        job["noise"] = dict(seed=int(noise.get("seed", 1)), flowTime=float(noise.get("flowTime", 0.0)))
        # The noise switches as the game had them (the settings' Noise.*), so that the replay's load builds the same volumes and its later loads do not build them again
        # (a new volume sets the soft density's detail mean after the stack was uploaded).
        sw = {s_["path"][len("Noise."):]: s_["values"][0] for s_ in settings if s_["path"].startswith("Noise.")}
        if sw:
            job["noise"].update(seed=int(sw.get("Seed", job["noise"]["seed"])), sizeLevel=int(sw.get("SizeLevel", 1)), disabled=1 - int(sw.get("Enabled", "1")),
                                noUntile=1 - int(sw.get("Untile", "1")), noCurl=1 - int(sw.get("Curl", "1")), noLod=1 - int(sw.get("DistanceLod", "1")),
                                noFine=1 - int(sw.get("FineDetail", "1")), warpAmplitude=float(sw.get("WarpAmplitude", 0.0)), warpCell=float(sw.get("WarpCell", 0.0)),
                                sphere=float(sw.get("SphereScale", 1.0)))
        job["tuning"] = [dict(key=k, value=float(v)) for k, v in sorted(pj.get("tuning", {}).items()) if v is not None]
        job["settings"] = settings
        job["unsupported"] = unsupported
        for text in unsupported:
            print("%s: NOT REPLAYABLE: %s" % (cap, text))
        job.update(depthFile=depth or "", worldCamera=[float(x) for x in world["camera"]], worldRotation=[float(x) for x in world["rotation"]],
                   firstFrame=int(pin["firstFrameNumber"]), frames=int(pin["framesSinceCut"]))
        vec = u.get("vectors", {})
        if "_MapReachCov" in vec and "_MapReachType" in vec:
            job.update(reachCov=[float(x) for x in vec["_MapReachCov"][:4]], reachType=[float(x) for x in vec["_MapReachType"][:4]])
        job["typeTableHash"] = pj.get("typeTableHash") or ""
        jitter, source = pin_jitter(cap, pj, pin, job["frames"])
        if jitter is not None:
            job["hostJitter"] = [float(x) for xy in jitter for x in xy]
            print("%s: the depth follows the host's jitter per record (%s)" % (jid, source))
        elif not pin.get("standin"):
            print("%s: no per-record jitter (%s); the depth of the last record is used for every record" % (jid, source))
        poses = pin_poses(cap, job["frames"])
        if poses is not None:
            job.update(recordCamera=poses[0], recordRotation=poses[1], recordPlanetCenter=poses[2])
            print("%s: the camera follows the game's pose per record (frames.json)" % jid)
        elif not pin.get("standin"):
            print("%s: no per-record pose (no frames.json); the capture frame's camera is used for every record" % jid)
        jobs.append(job)
        specs.append(dict(id=jid, variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-humidity.r8" % tag, updraftMap="", seed=664216370 & 0xFFFFFF))
        index.append(dict(job=jid, capture=os.path.abspath(cap), standin=bool(pin.get("standin")), typeTableHash=pj.get("typeTableHash"), width=w, height=h,
                          firstFrame=job["firstFrame"], frames=job["frames"], settings=len(settings)))
        print("%s <- %s (%d settings, frames %d from %d, %dx%d%s)" % (jid, cap, len(settings), job["frames"], job["firstFrame"], w, h, ", self-parity stand-in" if pin.get("standin") else ""))
    with open(os.path.join(out, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(out, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)
    with open(os.path.join(out, "pin-index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, indent=1)
    return jobs


def pin_index():
    path = os.path.join(PIN_INPUT, "pin-index.json")
    if not os.path.isfile(path):
        return []
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def pin_stage_list():
    index = pin_index()
    if not index:
        return []
    return [Stage(PIN_LABEL, ["-mode", "scene", "-scene", "pin-replay", "-in", PIN_INPUT, "-repeat", str(PIN_REPEAT), "-freshRecon", os.environ.get("M0PIN_FRESH_RECON", "0")] + os.environ.get("M0PIN_EXTRA", "").split(), size=(index[0]["width"], index[0]["height"]), proxy=False,
                  timeout=1800)]


def pin_collect(ctx):
    """Per job: the player's own run-to-run difference (r0 against r1, m0parity) and, where the capture has the game's cloud.npy, game against player (r0); the type table's
    hash of both; the uniforms that differ (uniform_diff)."""
    from weather_metrics import m0parity
    folder = ctx.folder(PIN_LABEL)
    metrics = []
    for item in pin_index():
        jid = item["job"]
        r0, r1 = os.path.join(folder, jid + "-r0-cloud.npy"), os.path.join(folder, jid + "-r1-cloud.npy")
        if not os.path.isfile(r0):
            metrics.append(Metric.flag("m0pin.%s.replayed" % jid, False))
            continue
        with open(os.path.join(folder, jid + "-r0-replay.json"), encoding="utf-8") as f:
            replay = json.load(f)
        metrics.append(Metric.flag("m0pin.%s.settings_all_set" % jid, not replay["problems"]))
        if item.get("typeTableHash"):
            metrics.append(Metric.flag("m0pin.%s.type_table_equal" % jid, replay["typeTableHash"] == item["typeTableHash"]))
        if os.path.isfile(r1):
            s, text = m0parity.compare(r0, r1, os.path.join(BUILD, "m0pin-self-" + jid), label_a="player r0", label_b="player r1")
            print(text)
            metrics.append(Metric.info("m0pin.%s.self_max_colour" % jid, s["whole"]["max_colour"]))
            metrics.append(Metric.info("m0pin.%s.self_max_alpha" % jid, s["whole"]["max_alpha"]))
            metrics.append(Metric.info("m0pin.%s.self_beyond_half_step" % jid, s["whole"]["beyond_half_step"]))
            u0, u1 = os.path.join(folder, jid + "-r0-uniforms.json"), os.path.join(folder, jid + "-r1-uniforms.json")
            if os.path.isfile(u0) and os.path.isfile(u1):
                with open(u0, encoding="utf-8") as f:
                    a0 = json.load(f)
                with open(u1, encoding="utf-8") as f:
                    a1 = json.load(f)
                differ = ["%s.%s" % (g, k) for g in a0 for k in a0[g] if a0[g][k] != a1.get(g, {}).get(k)]
                print("  %s uniforms r0 against r1: %s" % (jid, ", ".join(differ) if differ else "all equal"))
                metrics.append(Metric.info("m0pin.%s.self_uniforms_differing" % jid, len(differ)))
            # Every further replay against the one before it (-repeat 3: whether r1 equals r2 while r0 differs, i.e. state the first replay carries over).
            k = 2
            while os.path.isfile(os.path.join(folder, "%s-r%d-cloud.npy" % (jid, k))):
                prev, cur = os.path.join(folder, "%s-r%d-cloud.npy" % (jid, k - 1)), os.path.join(folder, "%s-r%d-cloud.npy" % (jid, k))
                s, text = m0parity.compare(prev, cur, os.path.join(BUILD, "m0pin-self%d-%s" % (k, jid)), label_a="player r%d" % (k - 1), label_b="player r%d" % k)
                print(text)
                metrics.append(Metric.info("m0pin.%s.self%d_max_colour" % (jid, k), s["whole"]["max_colour"]))
                metrics.append(Metric.info("m0pin.%s.self%d_beyond_half_step" % (jid, k), s["whole"]["beyond_half_step"]))
                k += 1
            # The replay's own repeats must be bit-identical: any difference is a named problem with its numbers (the stage fails on it).
            first = np.load(r0)
            problems = []
            k = 1
            while os.path.isfile(os.path.join(folder, "%s-r%d-cloud.npy" % (jid, k))):
                other = np.load(os.path.join(folder, "%s-r%d-cloud.npy" % (jid, k)))
                d = np.abs(other.astype(np.float64) - first)
                if d.max() > 0.0:
                    problems.append("r%d differs from r0 in %.4f %% of the pixels, max %.6f" % (k, 100.0 * float((d > 0).any(axis=-1).mean()), float(d.max())))
                k += 1
            for text in problems:
                print("PROBLEM %s: the replay is not repeatable: %s" % (jid, text))
            metrics.append(Metric.flag("m0pin.%s.self_identical" % jid, not problems))
        game = os.path.join(item["capture"], "cloud.npy")
        if os.path.isfile(game) and not item.get("standin"):
            try:
                s, text = m0parity.compare(game, r0, os.path.join(BUILD, "m0pin-game-" + jid), label_a="game", label_b="player")
            except m0parity.OrientationError as e:
                print("ORIENTATION FAILED: %s" % e)
                metrics.append(Metric.flag("m0pin.%s.orientation" % jid, False))
                continue
            print(text)
            metrics.append(Metric.info("m0pin.%s.game_max_colour" % jid, s["whole"]["max_colour"]))
            metrics.append(Metric.info("m0pin.%s.game_mean_colour" % jid, s["whole"]["mean_colour"]))
        uni = os.path.join(folder, jid + "-r0-uniforms.json")
        with open(os.path.join(item["capture"], "pass.json"), encoding="utf-8") as f:
            pj = json.load(f)
        if os.path.isfile(uni):
            with open(uni, encoding="utf-8") as f:
                diff = uniform_diff(pj, json.load(f))
            for k, v in diff:
                print("  %s uniform %s: %s" % (jid, k, v))
            metrics.append(Metric.info("m0pin.%s.uniforms_differing" % jid, len(diff)))
    return metrics


register_series("m0-pin", pin_stage_list(), pin_collect, exclusive=False, who="m0-pin")


def pin_run(no_build=False, who="m0-pin"):
    """The series m0-pin without the registry of every series (build_weather_player.py --series imports all modules of weather_metrics, and one of them needs files a fresh
    work tree does not have): the build in the Unity lock, the replay and the collection in the GPU lock, as run_series does."""
    import build_weather_player as bwp
    if not no_build:
        code = bwp.under_lock("unity", who, 20, False, [sys.executable, bwp.__file__, "--stage", "build"])
        if code != 0:
            return code
    return bwp.under_lock("gpu", who, 20, False, [sys.executable, os.path.abspath(__file__), "--pin-run-inner"])


def pin_run_inner():
    import build_weather_player as bwp
    from weather_metrics import table
    stages = pin_stage_list()
    if not stages:
        raise SystemExit("no prepared pin jobs (%s); run --pin-prepare first" % PIN_INPUT)
    exe = os.path.join(bwp.OUTPUT, "WeatherPlayer.exe")
    if not os.path.exists(exe):
        raise SystemExit("the player is not built (%s)" % exe)
    bwp.write_ini()
    beside = os.path.join(bwp.OUTPUT, "dxgi.dll")
    if os.path.exists(beside):
        os.remove(beside)
    st = stages[0]
    label = "m0-pin-" + st.label
    folder = bwp.fresh(label)
    lines = bwp.run_lines(exe, label, ["-out", folder] + st.extra, st.size, st.timeout)
    print("\n".join(lines or ["(the player wrote no lines)"]))

    class _Ctx(object):
        def folder(self, _label):
            return folder

    metrics = pin_collect(_Ctx())
    metrics.append(Metric.flag("stage." + st.label, lines is not None and any(line == "all checks passed" for line in lines)))
    print(table(metrics))
    return 0 if all(m.passed() for m in metrics) else 1


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
    # the frame gain: a game frame exposed 0.9 times is read from the cloud-free pixels, and divided out it is parity again; a frame with no cloud-free pixels has none (unclear)
    alpha = blob[..., 0]
    ok_all = np.ones((960, 1707), dtype=bool)
    gain = frame_gain(game * 0.9, off, alpha, ok_all)
    assert gain is not None and abs(gain - 0.9) < 1e-6, gain
    assert compare("gfair", game * 0.9 / gain, game, off, 264.0)["parity"]
    assert frame_gain(game, off, np.ones_like(alpha), ok_all) is None
    # the verdict: a residual in a quantity the exposure does not touch is out of tolerance; only the brightness can be unclear
    free = dict(pass_cover=True, pass_edge=True, pass_chroma=True, pass_lit=True)
    assert verdict_of(dict(free, pass_colour=True)) == "in tolerance" and verdict_of(dict(free, pass_colour=False)) == "unclear"
    assert verdict_of(dict(free, pass_colour=True, pass_cover=False)) == "out of tolerance" and verdict_of(dict(free, pass_colour=False, pass_lit=False)) == "out of tolerance"
    # the sunlit over shaded ratio does not change with the exposure
    cloud = np.zeros((960, 1707), dtype=bool)
    cloud[100:500, 100:900] = True
    ramp = np.tile(np.linspace(0.2, 0.8, 1707), (960, 1))[..., None] * np.ones(3)
    assert abs(lit_ratio(ramp, cloud) - lit_ratio(ramp * 0.7, cloud)) < 1e-6
    assert abs(opaque_gain(ramp * 0.9, ramp, np.ones((960, 1707)), np.ones((960, 1707), dtype=bool)) - 0.9) < 1e-9
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
    parser.add_argument("--start", type=int, default=GAME_START, help="the game start the frames are of")
    parser.add_argument("--report", action="store_true")
    parser.add_argument("--collect", action="store_true", help="measure the last run's frames again (build/WeatherPlayer/e40-pp-pp-b) without running the player")
    parser.add_argument("--pin-prepare", nargs="+", metavar="CAPTURE", help="the pinned captures (folders with pass.json, maps, depth.npy) as replay jobs for the series m0-pin")
    parser.add_argument("--pin-standin", action="store_true", help="accept a dump from before the pinned capture (the player's self-parity only)")
    parser.add_argument("--pin-run", action="store_true", help="build the player and replay the prepared pin jobs under the locks (the series m0-pin)")
    parser.add_argument("--no-build", action="store_true", help="with --pin-run: the player as it is built")
    parser.add_argument("--pin-run-inner", action="store_true", help=argparse.SUPPRESS)
    a = parser.parse_args()
    if a.pin_prepare:
        prepare_pin(a.pin_prepare, standin=a.pin_standin)
    elif a.pin_run:
        sys.exit(pin_run(a.no_build))
    elif a.pin_run_inner:
        sys.exit(pin_run_inner())
    elif a.selftest:
        selftest()
    elif a.collect:
        class _Ctx(object):
            def folder(self, label):
                return os.path.join(REPO, "build", "WeatherPlayer", "e40-pp-" + label)
        collect(_Ctx())
    elif a.prepare:
        prepare(a.log, a.maps, a.state, a.start)
    elif a.report:
        with open(RESULTS, encoding="utf-8") as f:
            data = json.load(f)
        for run, rows in data.items():
            print("run", run)
            print(report(rows))
