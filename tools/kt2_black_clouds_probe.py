#!/usr/bin/env python3
"""Offline diagnosis of K-T2 start 42 (own clouds drawn black and opaque in the game): the real Kerbin layer set of
GameData/ReDefinition/Atmospheres/Bodies/Clouds-Kerbin.cfg (rain deck, cumulus, cumulonimbus, cirrus as the game's stack holds
them) is marched by the weather player's layer scene with the frame numbers of the module (medium step quality, product cost,
early-out 0.01, jitter 1), on uniform maps, at the K-T2 ground pose (264 m, camera elevation 18 deg, heading east), the
zenith, and the 8.3 km 'above' pose.

  python tools/kt2_black_clouds_probe.py write  <folder>             writes jobs.json and the raw R8 maps
  python tools/kt2_black_clouds_probe.py report <folder> <outfolder> reads the player's arrays and prints the tables
  python tools/gpu_lock.py run --who <id> --max-minutes 10 -- <WeatherPlayer.exe> -logFile <log> -screen-width 1280
        -screen-height 720 -screen-fullscreen 0 -mode scene -scene layers -in <folder> -out <outfolder>

The question each table answers is named in .local/plans/atmospheres/K-T2-black-clouds-diagnosis.md. Stdlib and numpy only.
"""
import json
import math
import os
import re
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
CFG = os.path.join(REPO, "GameData", "ReDefinition", "Atmospheres", "Bodies", "Clouds-Kerbin.cfg")
R = 600000.0
MAP_W, MAP_H = 512, 256
SUN_DIR_AZ, SUN_DIR_EL = 120.0, 45.5     # the K-T2 clock's sun elevation (45.5 deg)


# ---- the cfg ---------------------------------------------------------------------------------------------------------------
def parse_nodes(text):
    """A tiny ConfigNode reader: returns the tree as (name, {key: value}, [children])."""
    text = re.sub(r"//[^\n]*", "", text)
    tokens = re.findall(r"[{}]|[^\s{}=]+(?:\s*=\s*[^\n{}]*)?", text)
    root = ("root", {}, [])
    stack = [root]
    pending = None
    for tok in tokens:
        if tok == "{":
            node = (pending, {}, [])
            stack[-1][2].append(node)
            stack.append(node)
            pending = None
        elif tok == "}":
            stack.pop()
        elif "=" in tok:
            k, v = tok.split("=", 1)
            stack[-1][1][k.strip()] = v.strip()
        else:
            pending = tok.strip()
    return root


def find(node, name):
    out = []
    for c in node[2]:
        if c[0] == name:
            out.append(c)
        out.extend(find(c, name))
    return out


def kerbin_layers():
    tree = parse_nodes(open(CFG, encoding="utf-8").read())
    layers = []
    for lay in find(tree, "LAYER"):
        p = lay[1]
        types = []
        for ty in [c for c in lay[2] if c[0] == "TYPE"]:
            keys = []
            for pr_ in [c for c in ty[2] if c[0] == "PROFILE"]:
                for k, v in sorted(pr_[1].items()):
                    a, b = v.split()
                    keys.append((float(a), float(b)))
            xs = np.linspace(0.0, 1.0, 128)
            ys = np.interp(xs, [k[0] for k in keys], [k[1] for k in keys])
            types.append(dict(name=ty[1]["name"], baseShare=float(ty[1]["baseShare"]), topShare=float(ty[1]["topShare"]),
                              erosion=float(ty[1]["erosion"]), profile=[float(y) for y in ys]))
        layers.append(dict(name=p["name"], group=p.get("group", ""), inner=float(p["innerAltitudeM"]), outer=float(p["outerAltitudeM"]),
                           sigma=float(p["sigmaPerM"]), coverage=float(p["coverage"]), softness=float(p["edgeSoftness"]),
                           kind=1 if p["kind"] == "Cheap" else 0, noiseAmount=float(p["noiseAmount"]), noiseScale=float(p["noiseScaleM"]),
                           types=types))
    # CloudLayerSet.Select with pick 0: the first member of each group, lowest inner altitude first.
    seen, chosen = set(), []
    for l in layers:
        if l["group"]:
            if l["group"] in seen:
                continue
            seen.add(l["group"])
        chosen.append(l)
    chosen.sort(key=lambda l: (l["inner"], l["outer"]))
    return chosen[:4]


# ---- poses -----------------------------------------------------------------------------------------------------------------
def frame_at(lat, lon):
    la, lo = math.radians(lat), math.radians(lon)
    up = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)])
    east = np.array([-math.sin(lo), math.cos(lo), 0.0])
    north = np.cross(up, east)
    return up, east, north


def camera(h, yaw, pitch, fov=60.0, aspect=16 / 9.0):
    up, east, north = frame_at(0.0, 0.0)
    pos = up * (R + h)
    y, p = math.radians(yaw), math.radians(pitch)
    fwd = math.cos(p) * (math.cos(y) * north + math.sin(y) * east) + math.sin(p) * up
    right = np.cross(fwd, up)
    right /= np.linalg.norm(right)
    cam_up = np.cross(right, fwd)
    return pos, fwd, right, cam_up, fov


def sun_dir(pos, az, el):
    up = pos / np.linalg.norm(pos)
    east = np.cross([0.0, 0.0, 1.0], up)
    east /= np.linalg.norm(east)
    north = np.cross(up, east)
    a, e = math.radians(az), math.radians(el)
    return math.cos(e) * (math.cos(a) * north + math.sin(a) * east) + math.sin(e) * up


POSES = {
    "ground": (264.0, 90.0, 18.0),
    "zenith": (264.0, 90.0, 89.0),
    "above": (8304.0, 90.0, -26.8),
}


def write_map(folder, name, value):
    data = np.full((MAP_H, MAP_W), int(round(min(max(value, 0.0), 1.0) * 255.0)), dtype=np.uint8)
    data.tofile(os.path.join(folder, name))
    return name


def make_job(job_id, mode, pose, cov, typ, sun_scale, ambient, layers, size=(64, 36), extra=None):
    h, yaw, pitch = POSES[pose]
    pos, fwd, right, up, fov = camera(h, yaw, pitch)
    sun = sun_dir(pos, SUN_DIR_AZ, SUN_DIR_EL)
    jl = []
    for l in layers:
        d = dict(l)
        d.update(fadeNear=0.0, fadeFar=0.0, shift=0.0, emission=[0.0, 0.0, 0.0],
                 coverageMap="cov-%03d.r8" % int(round(cov * 100)), typeMap="typ-%03d.r8" % int(round(typ * 100)))
        jl.append(d)
    j = dict(id=job_id, mode=mode, width=size[0], height=size[1], planetRadius=R, hasSurface=1, sun=[float(x) for x in sun],
             rayLimit=0.0, maxSteps=48, sunSteps=3, earlyOut=0.01, minStep=25.0, stepGrowth=0.0002, jitter=1.0, sunMarch=4000.0, resolution=1.0,
             layerMask=0, mapWidth=MAP_W, mapHeight=MAP_H, layers=jl, occluder=[0.0, 0.0, 0.0, 0.0], productCost=1,
             position=[float(x) for x in pos], forward=[float(x) for x in fwd], right=[float(x) for x in right], up=[float(x) for x in up], fovV=fov,
             sunColour=[float(sun_scale)] * 3 if np.isscalar(sun_scale) else [float(x) for x in sun_scale],
             ambientTop=[float(x) for x in ambient[0]], ambientBottom=[float(x) for x in ambient[1]])
    if extra:
        j.update(extra)
    return j


COVS = (0.0, 0.2, 0.4, 0.5, 0.514, 0.6, 0.8, 1.0)
TYPES = (0.0, 0.5, 1.0)
PLAYER_AMB = ([0.35, 0.42, 0.55], [0.14, 0.15, 0.16])
ZERO_AMB = ([0.0, 0.0, 0.0], [0.0, 0.0, 0.0])
SUN_SCALES = (0.0, 0.1, 0.3, 1.0, 3.0)


def write(folder):
    os.makedirs(folder, exist_ok=True)
    layers = kerbin_layers()
    for c in COVS:
        write_map(folder, "cov-%03d.r8" % int(round(c * 100)), c)
    for t in TYPES:
        write_map(folder, "typ-%03d.r8" % int(round(t * 100)), t)
    jobs = []
    # 1. tau and alpha by pose, coverage value and type value (light does not matter for the aux pass)
    for pose in POSES:
        for c in COVS:
            for t in TYPES:
                jobs.append(make_job("aux-%s-c%03d-t%03d" % (pose, int(round(c * 100)), int(round(t * 100))), "aux", pose, c, t, 1.0, PLAYER_AMB, layers))
    # 2. radiance by light: the sun scale and the ambient, at coverage 0.514 / type 0.5 and a thin and a thick case
    for pose in ("ground", "above"):
        for (c, t) in ((0.514, 0.5), (0.6, 0.0)):
            for s in SUN_SCALES:
                for an, amb in (("player", PLAYER_AMB), ("zero", ZERO_AMB)):
                    jobs.append(make_job("lum-%s-c%03d-t%03d-s%03d-%s" % (pose, int(round(c * 100)), int(round(t * 100)), int(round(s * 100)), an),
                                         "lum", pose, c, t, s, amb, layers))
    # 3. pictures of the player's own light for the ground and above poses
    for pose in ("ground", "above"):
        jobs.append(make_job("pic-%s-c514-t050" % pose, "color", pose, 0.514, 0.5, 1.0, PLAYER_AMB, layers, size=(640, 360),
                             extra=dict(sunColour=[3.0, 2.9, 2.7], accumulate=1, exposure=0.0)))
    with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    print("jobs", len(jobs), "layers", [l["name"] for l in layers])


# ---- report ----------------------------------------------------------------------------------------------------------------
def load(out, name):
    return np.load(os.path.join(out, name + ".npy"))


def tone(x, k=1.2):
    return (1.0 - np.exp(-np.maximum(x, 0.0) * k)) ** (1.0 / 2.2)


def report(out):
    lines = []
    lines.append("TABLE 1: tau (median) and share of pixels with alpha > 0.99 / tau >= 32 by pose, coverage map value, type map value")
    lines.append("pose    cov   type | tau_median  alpha>0.99  tau>=32")
    for pose in POSES:
        for c in COVS:
            for t in TYPES:
                name = "aux-%s-c%03d-t%03d" % (pose, int(round(c * 100)), int(round(t * 100)))
                try:
                    tau = load(out, name + "-tau")
                    al = load(out, name + "-alpha")
                except OSError:
                    continue
                lines.append("%-7s %.3f %.1f | %10.2f  %9.3f  %7.3f" % (pose, c, t, float(np.median(tau)), float((al > 0.99).mean()), float((tau >= 32).mean())))
    lines.append("")
    lines.append("TABLE 2: radiance of the march (premultiplied) by sun scale and ambient: median luminance, median tone-mapped (exposure 1.2), alpha median")
    lines.append("pose    cov  type  sun   ambient | lum_median  tone_median  alpha_median")
    for pose in ("ground", "above"):
        for (c, t) in ((0.514, 0.5), (0.6, 0.0)):
            for s in SUN_SCALES:
                for an in ("player", "zero"):
                    name = "lum-%s-c%03d-t%03d-s%03d-%s" % (pose, int(round(c * 100)), int(round(t * 100)), int(round(s * 100)), an)
                    try:
                        r, g, b, a = (load(out, name + "-" + k) for k in "rgba")
                    except OSError:
                        continue
                    lum = 0.2126 * r + 0.7152 * g + 0.0722 * b
                    m = a > 0.5
                    lm = float(np.median(lum[m])) if m.any() else float("nan")
                    tm = float(np.median(tone(lum[m]))) if m.any() else float("nan")
                    lines.append("%-7s %.3f %.1f  %.2f  %-7s | %10.4f  %11.4f  %12.3f" % (pose, c, t, s, an, lm, tm, float(np.median(a))))
    text = "\n".join(lines)
    print(text)
    with open(os.path.join(out, "report.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "write":
        write(sys.argv[2])
    elif len(sys.argv) >= 4 and sys.argv[1] == "report":
        report(sys.argv[3])
    else:
        print(__doc__)
        sys.exit(2)
