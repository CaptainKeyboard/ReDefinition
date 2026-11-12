"""The scenes of the early look sheet (task LOOK) and the player series that renders them: python tools/atmo_looksheet.py runs it.

A fixed set of scenes of the own cloud system (the layer scene of the weather player, tests/WeatherPlayer LayersScene) with a fixed camera,
sun, seed and weather map each. The scene scene renders every job twice, as the natural picture (<id>.png) and as the aid picture
(<id>-aid.png, optical depth in false colour on the left half, layer colours on the right half). The scenes are grouped for the contact
sheets: ground, side, above, light.

The camera, the maps and the light are scene inputs, not product settings. The march settings are the product's (LayerFrame defaults:
48 steps, sun march 4000 m, jitter on); the maps are layered fractal noise on a 4096 x 2048 equirectangular grid, ranked to a uniform
value distribution, so that a layer's coverage is the share of the sky it clouds on average. The cloud types and the extinction are chosen
for real cloud: cumulus 0.03 per m, stratus 0.02 per m, cumulonimbus 0.035 per m.
"""
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T-look-in")
R = pr.KERBIN_RADIUS
MAP_W, MAP_H = 4096, 2048
KM_PER_DEG = R * math.pi / 180.0 / 1000.0
TEXEL_KM = 2.0 * math.pi * R / 1000.0 / MAP_W
SIZE = (800, 450)
YAW_EAST = 90.0

CUMULUS = L.CUMULUS
STRATUS = L.STRATUS
ANVIL = L.ANVIL
CIRRUS = [0.2, 0.8, 1.0, 0.7, 0.2, 0.0]


# --------------------------------------------------------------------------------------------------------------------------------
# Maps
# --------------------------------------------------------------------------------------------------------------------------------
def fbm(seed, first=2, last=7, persistence=0.6):
    """Layered noise on the map grid (octave k has 8 * 2^k rows), ranked to a uniform distribution on [0, 1]."""
    from PIL import Image
    rng = np.random.default_rng(seed)
    total = np.zeros((MAP_H, MAP_W), dtype=np.float32)
    amp = 1.0
    for k in range(first, last + 1):
        grid = rng.random((8 * 2 ** k, 16 * 2 ** k)).astype(np.float32)
        total += amp * np.asarray(Image.fromarray(grid).resize((MAP_W, MAP_H), Image.Resampling.BICUBIC), dtype=np.float32)
        amp *= persistence
    order = np.argsort(total.ravel(), kind="stable")
    ranks = np.empty(total.size, dtype=np.float32)
    ranks[order] = np.arange(total.size, dtype=np.float32) / (total.size - 1)
    return ranks.reshape(MAP_H, MAP_W)


def texel(lat, lon):
    """The (row, column) of a place on the map (row 0 = north, column 0 = longitude -180)."""
    return (90.0 - lat) / 180.0 * MAP_H, (lon + 180.0) / 360.0 * MAP_W


def write_map(name, values):
    data = np.clip(np.rint(np.asarray(values, dtype=np.float64) * 255.0), 0, 255).astype(np.uint8)
    os.makedirs(INPUT, exist_ok=True)
    data.tofile(os.path.join(INPUT, name))
    return name


def box_share(integral, row, col, half_rows, half_cols):
    """The mean of the mask in boxes (arrays of centres, in texels) from an integral image with a zero first row and column."""
    r0 = np.clip(np.rint(row - half_rows).astype(int), 0, MAP_H - 1)
    r1 = np.clip(np.rint(row + half_rows).astype(int), 1, MAP_H)
    c0 = np.clip(np.rint(col - half_cols).astype(int), 0, MAP_W - 1)
    c1 = np.clip(np.rint(col + half_cols).astype(int), 1, MAP_W)
    area = np.maximum((r1 - r0) * (c1 - c0), 1)
    return (integral[r1, c1] - integral[r0, c1] - integral[r1, c0] + integral[r0, c0]) / area


def find_site(values, coverage, ahead_km, half_km, want_ahead, near_km=None, want_near=None, yaw=YAW_EAST):
    """The place (lat, lon, east-looking) where the share of cloud (value above 1 - coverage) in a box centred ahead_km east of the camera
    (half-width half_km) is nearest to want_ahead and, where near_km is given, the share within near_km of the camera is nearest to want_near."""
    mask = (values > 1.0 - coverage).astype(np.float64)
    integral = np.zeros((MAP_H + 1, MAP_W + 1))
    integral[1:, 1:] = mask.cumsum(axis=0).cumsum(axis=1)
    lats = np.arange(-6.0, 6.01, 0.25)
    lons = np.arange(-150.0, 150.01, 0.25)
    la, lo = np.meshgrid(lats, lons, indexing="ij")
    row, col = texel(la, lo)
    half = half_km / TEXEL_KM
    yr = math.radians(yaw)
    ahead = box_share(integral, row - math.cos(yr) * ahead_km / TEXEL_KM, col + math.sin(yr) * ahead_km / TEXEL_KM, half, half)
    err = np.abs(ahead - want_ahead)
    if near_km is not None:
        nh = near_km / TEXEL_KM
        near = box_share(integral, row, col, nh, nh)
        err = err + np.abs(near - want_near)
    i = np.unravel_index(int(np.argmin(err)), err.shape)
    return float(la[i]), float(lo[i]), float(err[i])


# --------------------------------------------------------------------------------------------------------------------------------
# Scenes
# --------------------------------------------------------------------------------------------------------------------------------
GROUPS = [("ground", "Ground views"), ("side", "Side views at 8 km"), ("above", "From above"), ("light", "Low sun, sunset, night")]

DAY = dict(sunColour=[3.0, 2.9, 2.7], ambientTop=[0.35, 0.42, 0.55], ambientBottom=[0.14, 0.15, 0.16])


def cu_layer(name="cumulus", cov_map="cu.r8", coverage=0.45, inner=1400.0, outer=3600.0):
    return L.layer(inner, outer, 0.03, [L.slab_type("cumulus", 0.0, 0.8, CUMULUS, 0.5)], coverage=coverage, softness=0.2, cov_map=cov_map,
                   noise=0.5, noise_scale=700.0, name=name)


def deck_layer(cov_map="deck.r8", coverage=0.93, inner=900.0, outer=2300.0):
    return L.layer(inner, outer, 0.02, [L.slab_type("stratus", 0.0, 0.9, STRATUS, 0.3)], coverage=coverage, softness=0.18, cov_map=cov_map,
                   noise=0.4, noise_scale=900.0, name="deck")


def cirrus_layer(cov_map="cirrus.r8"):
    return L.layer(8000.0, 8400.0, 0.0025, [L.slab_type("cirrus", 0.0, 1.0, CIRRUS, 0.2)], coverage=0.7, softness=0.25, kind=1, cov_map=cov_map,
                   noise=0.6, noise_scale=1500.0, name="cirrus")


def storm_layer():
    types = [L.slab_type("cumulus", 0.0, 0.22, CUMULUS, 0.5), L.slab_type("cumulonimbus", 0.0, 1.0, ANVIL, 0.6)]
    return L.layer(800.0, 11500.0, 0.035, types, coverage=0.5, softness=0.12, cov_map="cell.r8", type_map="cell-type.r8", noise=0.5,
                   noise_scale=800.0, name="cell")


def scene_table():
    """The scenes: a list of dicts with the id, the group, the title, the camera (site name, altitude, pitch, yaw, fov), the sun (azimuth,
    elevation), the layers (functions of nothing) and the light overrides."""
    return [
        dict(id="G1", group="ground", title="Ground at 2 m, broken cumulus field", site="cu-ground", alt=2.0, pitch=18.0, fov=60.0, sun=(120.0, 40.0),
             layers=[cu_layer()], light=dict(DAY)),
        dict(id="G2", group="ground", title="Ground at 2 m, thick stratus deck", site="deck", alt=2.0, pitch=25.0, fov=60.0, sun=(120.0, 40.0),
             layers=[deck_layer()], light=dict(DAY)),
        dict(id="S1", group="side", title="8 km view of a cumulus field", site="cu-side", alt=500.0, pitch=3.0, fov=60.0, sun=(180.0, 35.0),
             layers=[cu_layer()], light=dict(DAY)),
        dict(id="S2", group="side", title="8 km view of a storm cell", site="cell", alt=300.0, pitch=18.0, fov=60.0, sun=(190.0, 30.0),
             layers=[storm_layer()], light=dict(DAY)),
        dict(id="A1", group="above", title="From 10 km altitude looking down on a deck", site="above", alt=10000.0, pitch=-30.0, fov=60.0,
             sun=(120.0, 45.0), layers=[deck_layer("above.r8", 0.8, 1000.0, 2400.0)], light=dict(DAY)),
        dict(id="O1", group="above", title="From orbit (300 km) at the limb", site="orbit", alt=300000.0, pitch=-44.0, fov=55.0, sun=(120.0, 35.0),
             layers=[deck_layer("orbit.r8", 0.6, 1000.0, 2400.0), cirrus_layer()], light=dict(DAY), space=1),
        dict(id="L1", group="light", title="Low sun (7 deg), looking towards it", site="cu-ground", alt=2.0, pitch=10.0, fov=60.0, sun=(90.0, 7.0),
             layers=[cu_layer()], light=dict(sunColour=[2.9, 2.2, 1.5], ambientTop=[0.30, 0.34, 0.45], ambientBottom=[0.12, 0.12, 0.13],
                                             skyHorizon=[0.80, 0.68, 0.55], skyZenith=[0.22, 0.36, 0.68])),
        dict(id="L2", group="light", title="Sunset (sun at 1.5 deg), looking towards it", site="cu-west", alt=2.0, pitch=8.0, fov=60.0, yaw=270.0,
             sun=(270.0, 1.5), layers=[cu_layer()],
             light=dict(sunColour=[2.6, 1.0, 0.35], ambientTop=[0.20, 0.18, 0.26], ambientBottom=[0.10, 0.07, 0.06],
                        skyHorizon=[0.75, 0.30, 0.15], skyZenith=[0.10, 0.14, 0.28], groundColour=[0.12, 0.10, 0.08])),
        dict(id="L3", group="light", title="Night, moon at 30 deg", site="cu-moon", alt=2.0, pitch=14.0, fov=60.0, yaw=120.0, sun=(120.0, 30.0),
             layers=[cu_layer()],
             light=dict(sunColour=[0.0100, 0.0108, 0.0125], ambientTop=[0.0012, 0.0016, 0.0028], ambientBottom=[0.0003, 0.0004, 0.0006],
                        skyHorizon=[0.0006, 0.0007, 0.0011], skyZenith=[0.00015, 0.0002, 0.0004], groundColour=[0.0008, 0.0009, 0.0006], exposure=150.0)),
    ]


REFERENCE = {
    "G1": "Cumulus humilis/mediocris from the ground: flat, grey bases at one level (the condensation level), white cauliflower tops, the "
          "sun-facing flanks brightest with a thin bright rim. Blue sky between the clouds is clean; bases darken towards the horizon and the "
          "clouds shrink with distance (perspective).",
    "G2": "Stratus/stratocumulus or nimbostratus from below: a nearly uniform grey ceiling, darker and bluer than any cumulus base, with "
          "soft low-contrast mottling and no visible edges. The sun is not visible, the light is diffuse and shadows are absent; brightness "
          "falls with thickness.",
    "S1": "Cumulus field at 8 km: a row of towers with flat bases at one height, crisp bright tops, dark modelling on the flanks away from "
          "the sun and cauliflower detail at every edge. Distance shows as haze (bluer, lower contrast) and as depth ordering of the towers; "
          "gaps show sky and ground.",
    "S2": "Cumulonimbus at 8 km: a tower 8 to 12 km tall with a hard cauliflower edge on the updraft side, a flat anvil spreading at the top, a "
          "dark, almost black base and a grey rain shaft below it. The sunlit flank is white with strong shadowing, the shadow side blue-grey.",
    "A1": "Stratocumulus from 10 km: a bright white surface (albedo 0.6 to 0.9) with cellular mottling, soft shadows of taller cells on the "
          "cloud top, thin patches showing the dark ground, a bright glint towards the sun. Never black; the deck looks lighter than the ground.",
    "O1": "Orbit limb: a thin blue-white atmosphere band along the limb against black space; cloud decks as bright patches that compress "
          "towards the horizon; thin, semi-transparent cirrus; the surface darkens towards the limb by the air column; long shadows near "
          "the terminator.",
    "L1": "Low sun: against the sun cumulus have dark bodies with a thin, bright silver lining along the edges facing the sun; thin parts "
          "glow white-yellow; the bases are orange-grey. The light is warm yellow, the shaded sides take the blue ambient of the sky.",
    "L2": "Sunset: cloud undersides lit red-orange-pink by light that crossed a long air path, upper parts still yellow-white or lilac; the "
          "sky near the sun orange to deep red, opposite sky blue-purple. Sides towards the viewer are deep grey-purple; the ground is in "
          "shadow.",
    "L3": "Moonlit night: clouds silver-grey, low contrast and almost colourless (cool blue-grey); thin edges towards the moon glow with a "
          "silver lining, dense bodies stay dark. The sky is slightly lighter around the moon; stars show in the gaps.",
}

# What the picture is expected to show, written before the run (checks of the sheet, not parity checks).
EXPECT = {
    "G1": "separate cumulus with flat grey bases and a lit side, blue gaps, clouds shrinking towards the horizon",
    "G2": "uniform grey ceiling without edges, low contrast, no sun disc, soft mottling",
    "S1": "towers with flat bases at one level, bright sunlit top and flank, darker shaded flank, depth ordering",
    "S2": "a tall tower with a cauliflower edge and a flat anvil, a dark base, a white lit flank",
    "A1": "a bright white deck with mottling and thin patches, no black",
    "O1": "decks as bright patches compressing towards a thin blue limb against black",
    "L1": "dark bodies with a thin bright rim towards the sun, warm light",
    "L2": "warm red-orange clouds and sky near the sun, dark ground, undersides darker than tops",
    "L3": "cool, dim, low-contrast grey clouds with a brighter rim towards the moon",
}


def site_positions():
    """The fixed sites: name -> (lat, lon), found on the maps (the search is deterministic)."""
    cu = fbm(11, 2, 7, 0.6)
    sites = {}
    # ground: the 12 km box ahead clouds about 0.45 of the sky
    sites["cu-ground"] = find_site(cu, 0.45, 8.0, 6.0, 0.45)[:2]
    # side: a cumulus field 12 km ahead (half 6 km, about half cloud), the camera itself in a clear patch (3 km box under 0.25)
    sites["cu-west"] = find_site(cu, 0.45, 8.0, 6.0, 0.45, yaw=270.0)[:2]
    sites["cu-moon"] = find_site(cu, 0.45, 8.0, 6.0, 0.45, yaw=120.0)[:2]
    sites["cu-side"] = find_site(cu, 0.45, 12.0, 6.0, 0.55, 3.0, 0.15)[:2]
    deck = fbm(12, 2, 6, 0.55)
    sites["deck"] = find_site(deck, 0.93, 6.0, 6.0, 0.97)[:2]
    above = fbm(13, 2, 7, 0.6)
    sites["above"] = find_site(above, 0.8, 12.0, 12.0, 0.8)[:2]
    sites["cell"] = (2.0, -120.0)
    sites["orbit"] = (0.0, 40.0)
    return sites, cu, deck, above


def write_maps(cu, deck, above):
    write_map("cu.r8", cu)
    write_map("deck.r8", deck)
    write_map("above.r8", above)
    write_map("orbit.r8", fbm(14, 1, 7, 0.6))
    write_map("cirrus.r8", fbm(15, 2, 6, 0.55))
    # the storm cell: a blob 14 km east of the camera at (2 N, 120 W), radius about 7 km, its edge broken by noise; the type grows from cumulus
    # at the rim to cumulonimbus at the core
    rows = (np.arange(MAP_H) + 0.5)[:, None]
    cols = (np.arange(MAP_W) + 0.5)[None, :]
    row0, col0 = texel(2.0, -120.0 + 14.0 / KM_PER_DEG)
    dy = (rows - row0) * TEXEL_KM
    dx = (cols - col0) * TEXEL_KM * math.cos(math.radians(2.0))
    g = np.exp(-(dx * dx + dy * dy) / (7.0 * 7.0))
    noise = fbm(16, 3, 7, 0.65)
    cell = np.clip(g * (0.55 + 0.9 * noise), 0.0, 1.0)
    write_map("cell.r8", cell)
    write_map("cell-type.r8", np.clip(0.15 + 1.1 * g, 0.0, 1.0))


def jobs():
    sites, cu, deck, above = site_positions()
    write_maps(cu, deck, above)
    out = []
    for s in scene_table():
        lat, lon = sites[s["site"]]
        up = np.array([math.cos(math.radians(lat)) * math.cos(math.radians(lon)), math.cos(math.radians(lat)) * math.sin(math.radians(lon)),
                       math.sin(math.radians(lat))])
        cam = pr.Camera(up * (R + s["alt"]), s.get("yaw", YAW_EAST), s["pitch"], SIZE[0], SIZE[1], s["fov"])
        j = L.job("LS-%s" % s["id"], "color", cam, s["layers"], SIZE[0], SIZE[1], sun=L.sun_vector(cam.pos, s["sun"][0], s["sun"][1]),
                  maxSteps=48, minStep=25.0, stepGrowth=0.0002, jitter=1.0, earlyOut=0.01, sunSteps=3, mapWidth=MAP_W, mapHeight=MAP_H)
        j.update(s["light"])
        if s.get("space"):
            j["space"] = 1
            j["maxSteps"] = 64
        if s["id"] in ("S1", "S2"):
            j["maxSteps"] = 96
        out.append(j)
    return out


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs()}, f)


def collect(ctx):
    folder = ctx.folder("scene")
    out = []
    for s in scene_table():
        ok = all(os.path.isfile(os.path.join(folder, "LS-%s%s.png" % (s["id"], suffix))) for suffix in ("", "-aid"))
        out.append(Metric.flag("look.%s.pictures" % s["id"], ok))
    return out


def stages():
    jobs_file = os.path.join(INPUT, "jobs.json")
    if not os.path.isfile(jobs_file) or os.path.getmtime(jobs_file) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False)]


register_series("T-look", stages(), collect, exclusive=False, who="LOOK")
