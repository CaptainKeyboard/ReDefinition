"""E40 PUFF: where the small scales of the cloud's surface are lost, one factor at a time on the parity-checked views of game start 64 (series e40-puff; package PUFF, 2026-11-15).

    python tools/weather_metrics/e40pp.py --prepare --state now --start 64 --log <K-T2 soft60 log>      (the views' jobs and maps, once)
    python tools/weather_metrics/e40puff.py --prepare [--views tyhumg,tycong,tycumg,gclock,sclock] [--variants base,light,...]
    python tools/build_weather_player.py --series e40-puff --no-build
    python tools/weather_metrics/e40puff.py --report [--pictures <folder>]                              (the table of the last run, deltas against the base)

Same method as e40surf.py (each variant changes ONE factor of the base view's job: the same camera, sun, maps, haze, display stage and march; every frame is laid over the game's
own frame without clouds and measured on the same mask), with the puffiness measures of puffiness.py instead of the surface-visibility ones. The variants that need no change of the
job but another march (the render target's size) are separate stages with the base views. The keys cloud.billow.* are the product's tuning keys (CloudLookTuning).
"""
import argparse
import json
import os
import shutil
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import puffiness as pf  # noqa: E402

REPO = e40pp.REPO
BUILD = e40pp.BUILD
INPUT = os.path.join(BUILD, "e40puff-in")
LABEL = "puff-a"
LABEL_RES = "puff-r"
DEFAULT_VIEWS = ("tyhumg", "tycong", "tycumg", "gclock", "sclock")


def each_layer(job, f):
    for layer in job.get("layers", []):
        f(layer)


def each_type(job, f):
    for layer in job.get("layers", []):
        for t in layer.get("types", []):
            f(t)


def tune_dotted(pairs):
    """A variant that sets tuning keys (the product's own, e.g. cloud.billow.light)."""
    def f(job):
        for k, v in pairs:
            job.setdefault("tuning", []).append({"key": k, "value": float(v)})
    return f


def v_ero0(job):
    each_type(job, lambda t: t.__setitem__("erosion", 0.0))


def v_ero2(job):
    each_type(job, lambda t: t.__setitem__("erosion", t.get("erosion", 0.0) * 2.0))


def v_nosmooth(job):
    each_layer(job, lambda l: l.__setitem__("shapeSmoothing", 0.0))
    each_type(job, lambda t: t.__setitem__("shapeSmoothing", 0.0))


def cumulus_layer(f):
    """A change of the cumulus layer only."""
    def g(job):
        for layer in job.get("layers", []):
            if layer.get("name", "").endswith("cumulus"):
                f(layer)
    return g


def compose(*fs):
    def g(job):
        for f in fs:
            f(job)
    return g


def repose(pitch_deg):
    """The camera of the job turned about its right axis to this pitch over the local horizon (-90: straight down), same position, heading and field of view (the ridge diagnosis)."""
    def g(job):
        p = np.array(job["position"], dtype=np.float64)
        up_local = p / np.linalg.norm(p)
        f = np.array(job["forward"], dtype=np.float64)
        h = f - up_local * float(f @ up_local)
        norm = np.linalg.norm(h)
        h = h / norm if norm > 1e-9 else np.array(job["right"], dtype=np.float64)
        a = np.radians(pitch_deg)
        nf = np.cos(a) * h + np.sin(a) * up_local
        nu = -np.sin(a) * h + np.cos(a) * up_local
        job["forward"] = [float(x) for x in nf]
        job["up"] = [float(x) for x in nu]
    return g


def camera_at(alt_m, pitch_deg):
    """The job's camera moved along the local up to this altitude over the ground and turned to this pitch (same place, same heading): a ground or a side view of the same cloud field."""
    def g(job):
        p = np.array(job["position"], dtype=np.float64)
        f = np.array(job["forward"], dtype=np.float64)
        up_local = p / np.linalg.norm(p)
        h = f - up_local * float(f @ up_local)
        job["position"] = [float(x) for x in up_local * (float(job["planetRadius"]) + alt_m)]
        a = np.radians(pitch_deg)
        h = h / np.linalg.norm(h)
        job["forward"] = [float(x) for x in np.cos(a) * h + np.sin(a) * up_local]
        job["up"] = [float(x) for x in -np.sin(a) * h + np.cos(a) * up_local]
    return g




def sun_behind(elev_deg):
    """The sun moved behind the camera (opposite its heading) at this elevation over the camera's horizon: the front light of the owner's photograph."""
    def g(job):
        p = np.array(job["position"], dtype=np.float64)
        up_local = p / np.linalg.norm(p)
        f = np.array(job["forward"], dtype=np.float64)
        h = f - up_local * float(f @ up_local)
        h = h / np.linalg.norm(h)
        a = np.radians(elev_deg)
        job["sun"] = [float(x) for x in -np.cos(a) * h + np.sin(a) * up_local]
    return g


def v_soft03(job):
    job["soft"] = 0.3




def v_nolod(job):
    job.setdefault("noise", {})["noLod"] = 1


# The ceiling march (SoftTable case, tests/WeatherPlayer/Assets/Runner/SoftDensityScene.cs): 256 grid points at 6 m at least, no step or transmittance growth, early-out 0.001, every lit step to
# the sun, no light cutoff, 16 sun taps over 12 km (ceil); the full-size target with a jittered history of
# eight percent and no clip (r100+jit+acc8+clip1000), the 40 frames of the replay accumulating 12 or so jittered frames.
# The light taps read the shape only: a light path that reads the erosion crashes the Unity 2019.4 shader compiler, so the ceiling's light is the shape-only light with 16 taps.
CEIL = "seq1+lq2+ceil+r100+jit+acc8+clip1000"
# The ceiling's single changes on top of the product's march (seq1+lq2+r33+lp100): the march's group (steps), the full-size target with the accumulation (res), the light's group (light).
STEPS = "seq1+lq2+cstep+r33+lp100"
RES = "seq1+lq2+r100+jit+acc8+clip1000+lp100"
LIGHT = "seq1+lq2+clight+r33+lp100"
MARCH_LABELS = {CEIL: "puff-ceil", STEPS: "puff-steps", RES: "puff-res", LIGHT: "puff-light"}

# CLOUDR1: the heap, rim exponent and cavity on the cumulus layer only (the per-layer keys cloud.heap.layer.<name>, cloud.edgePower.layer.<name>, cloud.cavity.layer.<name>): the weather
# decks and the cirrus keep their look, so a ground view inside a deck is not fogged. A (prefix, value) pair; the first hit is a march key and global.
HEAP1 = [("cloud.heap.layer.", 1.0)]
EDGE = [("cloud.edgePower.layer.", 0.5)]
CAV = [("cloud.cavity.layer.", 0.7)]
FIRSTHIT = [("cloud.firstHit", 3.0)]
CAV35 = [("cloud.cavity.layer.", 0.35)]
NG96 = "seq1+lq2+ng96+r33+lp100"


def shell(n, near=0.0):
    """The first hit with n thin steps (a quarter of the grid step) after the entry, optionally only up to `near` m."""
    return [("cloud.firstHit", 3.0), ("cloud.firstHit.thin", 0.25), ("cloud.firstHit.shell", float(n)), ("cloud.firstHit.near", float(near))]


def tune_cumulus(pairs):
    """Sets tuning keys: a key ending in a dot is a per-layer prefix and is set for the cumulus layer of the job (the layer whose name ends with cumulus), any other key globally."""
    def f(job):
        for k, v in pairs:
            if k.endswith("."):
                for layer in job.get("layers", []):
                    if layer.get("name", "").endswith("cumulus"):
                        job.setdefault("tuning", []).append({"key": k + layer["name"], "value": float(v)})
            else:
                job.setdefault("tuning", []).append({"key": k, "value": float(v)})
    return f

# name -> (what, the change of the job or None, the case of the march: None = the base's)
VARIANTS = {
    "base": ("the replay as e40pp makes it", None, None),
    # --- CLOUDR1 (2026-11-16): the model packages P2 (heap volume), P1 (edge exponent), P3 (cavity), P6 (first hit) as the global tuning keys of CloudLookTuning; each also at the ceiling. ---
    "p2": ("(P2) the heap volume, preset billow, for every type (cloud.heap 1)", tune_cumulus(HEAP1), None),
    "p2r": ("(P2) the heap volume, preset rough (cloud.heap 2)", tune_cumulus([("cloud.heap.layer.", 2.0)]), None),
    "p2p1": ("(P2 + P1) heap volume + the rim exponent 0.5 (cloud.edgePower)", tune_cumulus(HEAP1 + EDGE), None),
    "p2p1p3": ("(P2 + P1 + P3) + cavity 0.7", tune_cumulus(HEAP1 + EDGE + CAV), None),
    "p1": ("(P1) the rim exponent 0.5 alone", tune_cumulus(EDGE), None),
    "p2p1p3d": ("(P2 + P1 + P3 + detail cascade) the erosion detail's octaves 2^(-0.65 k) (cloud.heap.detail)", tune_cumulus(HEAP1 + EDGE + CAV + [("cloud.heap.detail", 0.65)]), None),
    "p6": ("(P6) first hit by bisection (3 evaluations) and thin steps inside", tune_cumulus(FIRSTHIT), None),
    "p2p1p3p6": ("(P2 + P1 + P3 + P6)", tune_cumulus(HEAP1 + EDGE + CAV + FIRSTHIT), None),
    # --- CLOUDR1 round 3: P2 + P1 + P3 (cavity 0.35) at the production march, with the outer shell of thin steps after the bisected entry (N of them), and at the Phase 0 grid ---
    "r_a": ("(round 3) P2 + P1 + P3 (cavity 0.35) at the production march", tune_cumulus(HEAP1 + EDGE + CAV35), None),
    "r_s16": ("(round 3) r_a + first hit, 16 thin steps after the entry", tune_cumulus(HEAP1 + EDGE + CAV35 + shell(16)), None),
    "r_s32": ("(round 3) r_a + first hit, 32 thin steps", tune_cumulus(HEAP1 + EDGE + CAV35 + shell(32)), None),
    "r_s64": ("(round 3) r_a + first hit, 64 thin steps", tune_cumulus(HEAP1 + EDGE + CAV35 + shell(64)), None),
    "r_s32n": ("(round 3) r_a + first hit, 32 thin steps up to 8 km", tune_cumulus(HEAP1 + EDGE + CAV35 + shell(32, 8000.0)), None),
    "r_ls": ("(ablation) P6 (32 thin steps) with every lit step marching to the sun (no light reuse)", tune_cumulus(HEAP1 + EDGE + CAV35 + shell(32)), "seq1+lq2+cls+r33+lp100"),
    "r_ng96": ("(curve) r_a with 96 grid points", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+ng96+r33+lp100"),
    "r_ng128": ("(curve) r_a with 128 grid points", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+ng128+r33+lp100"),
    "r_ng192": ("(curve) r_a with 192 grid points", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+ng192+r33+lp100"),
    # --- CLOUDR1 diagnosis of the pulled ("fun-house") look: the lookup's untiling displacement, the vertical scale, the flow map, straight down and from the side, all at the product's march with 96 grid points ---
    # --- CLOUDR1: the tiling hidden by blending (cloud.noise.blend 1: random offset and cube rotation per lattice cell, variance-preserving blend of the Gaussianised ranks) instead of the displacement ---
    "bl_a": ("(blend) P2 + P1 + P3 with the blended lookup at the production grid, 96 grid points", tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.noise.blend", 1.0)]), NG96),
    "bl_base": ("(blend) the base model (no heap) with the shape volume blended instead of displaced", tune_dotted([("cloud.noise.blend", 1.0)]), None),
    "bl_dn": ("(blend) straight down, the blended lookup", compose(repose(-90.0), tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.noise.blend", 1.0)])), NG96),
    "bl_sd": ("(blend) from the side, the blended lookup", compose(camera_at(3600.0, -4.0), tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.noise.blend", 1.0)])), NG96),
    "bl_c": ("(blend) the ceiling", compose(v_nosmooth, tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.noise.blend", 1.0)])), CEIL),
    "bl_g": ("(blend) the Phase 0 grid", tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.noise.blend", 1.0)]), STEPS),
    "fh_legacy": ("(diagnosis) the heap read at the fully displaced point like the shape volume (cloud.heap.warp -1)", tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.heap.warp", -1.0)]), NG96),
    "fh_w0": ("(diagnosis) no untiling displacement at all", compose(tune_cumulus(HEAP1 + EDGE + CAV35), lambda job: job.setdefault("noise", {}).__setitem__("noUntile", 1)), NG96),
    "fh_wa2": ("(diagnosis) the displacement's amplitude 2 scales (6.5)", compose(tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.heap.warp", -1.0)]), lambda job: job.setdefault("noise", {}).__setitem__("warpAmplitude", 2.0)), NG96),
    "fh_vs1": ("(diagnosis) the cumulus layer's vertical scale 1 (0.35), heap read as in the legacy lookup", compose(tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.heap.warp", -1.0), ("cloud.heap.iso", 0.0)]), cumulus_layer(lambda l: l.__setitem__("verticalScale", 1.0))), NG96),
    "fh_fl0": ("(diagnosis) the flow map off (every layer's flow amplitude 0)", compose(tune_cumulus(HEAP1 + EDGE + CAV35), lambda job: each_layer(job, lambda l: l.__setitem__("flowAmplitude", 0.0))), NG96),
    "fh_dn": ("(diagnosis) straight down (pitch -90), the keys now", compose(repose(-90.0), tune_cumulus(HEAP1 + EDGE + CAV35)), NG96),
    "fh_dnL": ("(diagnosis) straight down, the heap read at the fully displaced point", compose(repose(-90.0), tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.heap.warp", -1.0)])), NG96),
    "fh_dnB": ("(diagnosis) straight down, production base", repose(-90.0), None),
    "fh_sd": ("(diagnosis) from the side (3600 m, pitch -4), the keys now", compose(camera_at(3600.0, -4.0), tune_cumulus(HEAP1 + EDGE + CAV35)), NG96),
    "fh_sdL": ("(diagnosis) from the side, the heap read at the fully displaced point", compose(camera_at(3600.0, -4.0), tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.heap.warp", -1.0)])), NG96),
    "fh_c": ("(diagnosis) the ceiling, the keys now", compose(v_nosmooth, tune_cumulus(HEAP1 + EDGE + CAV35)), CEIL),
    "fh_cL": ("(diagnosis) the ceiling, the heap read at the fully displaced point", compose(v_nosmooth, tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.heap.warp", -1.0)])), CEIL),
    "r_c256": ("(ablation) r_a with 256 grid points", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+c256+r33+lp100"),
    "r_cmin6": ("(ablation) r_a with a minimum step of 6 m", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+cmin6+r33+lp100"),
    "r_cng": ("(ablation) r_a without step and transmittance growth", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+cng+r33+lp100"),
    "r_c256m": ("(ablation) r_a with 256 grid points and a 6 m minimum step", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+c256+cmin6+r33+lp100"),
    "r_c256mg": ("(ablation) r_a with 256 grid points, 6 m minimum, no growth", tune_cumulus(HEAP1 + EDGE + CAV35), "seq1+lq2+c256+cmin6+cng+r33+lp100"),
    "r_g": ("(round 3) r_a at the Phase 0 grid (the reference look)", tune_cumulus(HEAP1 + EDGE + CAV35), STEPS),
    # --- OPT4 (2026-11-17): the march target at a quarter of the full size (recon.resolution 0.25) against today's third, on today's look (heap, rim 0.5, cavity 0.35, blend, 96 points) ---
    "o4_now": ("(OPT4) today's look, the march target a third of the full size", tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.noise.blend", 1.0)]), NG96),
    "o4_r25": ("(OPT4) today's look, the march target a quarter of the full size", tune_cumulus(HEAP1 + EDGE + CAV35 + [("cloud.noise.blend", 1.0)]), "seq1+lq2+ng96+r25+lp100"),
    # --- CELLS (2026-11-17): the jobs as e40pp --prepare --state now makes them carry today's cfg (the types' heap, rim and cavity, the cumulus layer's cellPitch); the product march of Medium ---
    "c_off": ("(CELLS) the game today: the map's texels as the clouds (cloud.cells 0)", tune_dotted([("cloud.cells", 0.0)]), NG96),
    "c_on": ("(CELLS) single clouds from the map's share on the cumulus layer's 3 km lattice (cloud.cells 1)", tune_dotted([("cloud.cells", 1.0)]), NG96),
    # the soft rim seen from below: the erosion in the rim band (types' erosion halved), the edge band (the cumulus layer's edge softness 0.08 instead of 0.15), both
    # the soft water content's rim width is softness (the owner's setting, 0.5) x the layer's softnessScale (cumulus 1) in units of the noise's swing: 0.5 of it fades every cloud over hundreds of metres
    "c_ss03": ("(CELLS) c_on, the cumulus layer's softnessScale 0.3 (rim width 0.15)", compose(tune_dotted([("cloud.cells", 1.0)]), cumulus_layer(lambda l: l.__setitem__("softnessScale", 0.3))), NG96),
    "c_ss05": ("(CELLS) c_on, the cumulus layer's softnessScale 0.5 (rim width 0.25)", compose(tune_dotted([("cloud.cells", 1.0)]), cumulus_layer(lambda l: l.__setitem__("softnessScale", 0.5))), NG96),
    # the owner's photograph's framing (one cumulus seen from the side over the sea): the camera 30 m over the sea looking east from the K-T2 site, 8 degrees up, the sun behind it
    "c_side": ("(CELLS) the photograph's framing: 30 m over the sea, 8 degrees up, cells on", compose(camera_at(30.0, 8.0), tune_dotted([("cloud.cells", 1.0)])), NG96),
    "c_side_off": ("(CELLS) the photograph's framing, cells off (the game before CELLS)", compose(camera_at(30.0, 8.0), tune_dotted([("cloud.cells", 0.0)])), NG96),
    # the photograph's light too: the sun 25 degrees up behind the camera, the camera 4 degrees up (the far clouds in the frame)
    "c_photo": ("(CELLS) the photograph's framing and light: 30 m, 4 degrees up, the sun 25 degrees up behind the camera, cells on", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cells", 1.0)])), NG96),
    "c_photo_off": ("(CELLS) as c_photo, cells off", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cells", 0.0)])), NG96),
    # the look without a cost limit (owner 2026-11-17: first the photograph's look whatever it costs, then the performance): c_photo at the ceiling march
    "c_photo_ceil": ("(CELLS) c_photo at the ceiling march (256 grid points, 6 m minimum step, every lit step to the sun with 16 taps, full size)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cells", 1.0)])), CEIL),
    # The single cloud's body (CloudCells.cginc CellBody) as the cfg sets it (the cumulus layer's cellBillow 0.5, cellBand 10 m, cellFine 1: the look the owner chose on 2026-11-17),
    # in the photograph's framing and light. b_ceil at the ceiling march is the chosen look; b_prod is what the product's march of Medium draws of it; b_fh adds the first hit by
    # bisection with 32 thin steps after it (the surface found to the band's width), b_fh50 that at half the size; b_f0 and b_f3 the finer billows off and three times as strong.
    "b_ceil": ("(BODY) the cfg's single clouds at the ceiling march", compose(camera_at(30.0, 4.0), sun_behind(25.0)), CEIL),
    "b_f0": ("(BODY) b_ceil without the finer billows (cloud.cellFine 0)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellFine", 0.0)])), CEIL),
    "b_f3": ("(BODY) b_ceil with the finer billows three times as strong (cloud.cellFine 3)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellFine", 3.0)])), CEIL),
    "b_prod": ("(BODY) the cfg's single clouds at the product march (Medium)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), NG96),
    "b_fh": ("(BODY) b_prod with the first hit and 32 thin steps", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted(shell(32))), NG96),
    "b_fh50": ("(BODY) b_fh at half the size (r50)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted(shell(32))), "seq1+lq2+ng96+r50+lp100"),
    # which group of the ceiling makes its look: the product's march with only the ceiling's steps, only its full-size target, only its light
    "b_steps": ("(BODY) the product's march with the ceiling's steps", compose(camera_at(30.0, 4.0), sun_behind(25.0)), STEPS),
    "b_res": ("(BODY) the product's march with the ceiling's full-size target", compose(camera_at(30.0, 4.0), sun_behind(25.0)), RES),
    "b_light": ("(BODY) the product's march with the ceiling's light", compose(camera_at(30.0, 4.0), sun_behind(25.0)), LIGHT),
    # b_steps makes the look: its levers one at a time on the product's march (256 grid points, a 6 m minimum step, no step or transmittance growth, early-out 0.001, no leap, every lit step to the sun)
    "b_c256": ("(BODY) the product's march with 256 grid points", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+r33+lp100"),
    # the cause check (2026-11-17): 96 grid points with the heap field's spacing LOD off (cloud.heap.footLod 0: the billows' mip no longer follows the march spacing)
    # which part completes the ceiling's shading (the owner saw b_c256/b_steps blotchy against b_ceil): the steps with the light, the steps with the full size
    "b_steps_light": ("(BODY) the ceiling's steps and light at a third of the size", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+cstep+clight+r33+lp100"),
    "b_steps_res": ("(BODY) the ceiling's steps and full-size target, the product's light", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+cstep+r100+jit+acc8+clip1000+lp100"),
    # the sun taps jittered in their segments (CloudLight.cginc LightTau, 2026-11-17): b_steps again with the product's light, to set against b_ceil and b_steps_light
    "b_steps_jit": ("(BODY) the ceiling's steps, the product's light with jittered sun taps", compose(camera_at(30.0, 4.0), sun_behind(25.0)), STEPS),
    # the sun taps read filtered to their segments with a near point tap (gLightFoot, 2026-11-17): at the ceiling's steps and at the product's march
    "b_steps_filt": ("(BODY) the ceiling's steps, the product's light with filtered jittered sun taps", compose(camera_at(30.0, 4.0), sun_behind(25.0)), STEPS),
    "b_prod_filt": ("(BODY) the product's march (96 grid points) with filtered jittered sun taps", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.heap.footLod", 1.0)])), "seq1+lq2+ng96+r33+lp100"),
    "b_ng96_nofoot": ("(BODY) the product's march with 96 grid points, the heap's spacing LOD off", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.heap.footLod", 0.0)])), "seq1+lq2+ng96+r33+lp100"),
    # A4 (owner audit 2026-11-17): cloud.cellTrace leaps the grid points beyond the reach of a body's billows (the picture should be the grid march's: cover and look equal on and off);
    # cloud.cellFoot filters the body by the sample's footprint (finer billows fade once the sample is wider than them, the band at least a pixel): the product's march with fewer grid points
    # should come close to the chosen look (t_ceil_off) where the grid alone needed 256.
    "t_ceil": ("(TRACE) b_ceil with the shell leap", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0)])), CEIL),
    "t_ceil_off": ("(TRACE) b_ceil without the shell leap (the chosen look as it was)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 0.0)])), CEIL),
    "t_prod": ("(TRACE) the product's march (96 grid points) with the shell leap", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0)])), NG96),
    "t_prod_off": ("(TRACE) the product's march (96 grid points) without the shell leap", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 0.0)])), NG96),
    "f_ceil": ("(FOOT) b_ceil with the shell leap and the footprint filter", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0), ("cloud.cellFoot", 1.0)])), CEIL),
    "f_ng96": ("(FOOT) 96 grid points with the shell leap and the footprint filter", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0), ("cloud.cellFoot", 1.0)])), NG96),
    # cloud.cellShell: fine steps inside a body's shell (the pixel's size, at least 6 m, at most the grid step), with the leap outside; fewer grid points should reach the ceiling's look.
    "s_ng64": ("(SHELL) 64 grid points with the shell leap and fine steps in the shell", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0), ("cloud.cellShell", 1.0)])), "seq1+lq2+ng64+r33+lp100"),
    "s_ng96": ("(SHELL) 96 grid points with the shell leap and fine steps in the shell", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0), ("cloud.cellShell", 1.0)])), NG96),
    "s_ng128": ("(SHELL) 128 grid points with the shell leap and fine steps in the shell", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0), ("cloud.cellShell", 1.0)])), "seq1+lq2+ng128+r33+lp100"),
    # A4 2026-11-18: the owner's verdict: t_ceil_off is the finest picture, every cheaper one loses visibly. Which of the ceiling's factors makes it, one at a time from the ceiling:
    # the march target's size (r33, the product's), the temporal stage (the product's reconstruction instead of the jittered accumulation), the light taps (b_steps_res), the step (the
    # product's grid of 256 and 96 points at the ceiling's full size and accumulation).
    "k_ceil_r33": ("(SPLIT) the ceiling march at the product's third of the size and its reconstruction", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ceil+r33+lp100"),
    "k_ceil_rec": ("(SPLIT) the ceiling march at full size with the product's reconstruction (no accumulation)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ceil+r100+lp100"),
    # Round 1 of the cheaper way (owner 2026-11-18: the look costs ten times the frame, the size of the march target four of those): the ceiling march at a half and two thirds of the size.
    "k_ceil_r50": ("(SEARCH 1) the ceiling march at half the size with the product's reconstruction", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ceil+r50+lp100"),
    "k_ceil_r66": ("(SEARCH 1) the ceiling march at two thirds of the size with the product's reconstruction", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ceil+r66+lp100"),
    # Round 2 (owner 2026-11-18: r66 passable but visibly different; first save where clouds are hidden or contribute little): at full size, the ceiling's march and light built from
    # their parts (k_vis_parts must equal k_ceil_rec), then one visibility saving of the product back at a time, then all three.
    "k_vis_parts": ("(SEARCH 2) the ceiling built from its parts (control: equal to k_ceil_rec)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cng+ceo+cel+ctaps+cls+r100+lp100"),
    "k_vis_eo": ("(SEARCH 2) the ceiling with the product's early out (a ray ends at 1 % of its light left)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cng+cel+ctaps+cls+r100+lp100"),
    "k_vis_tg": ("(SEARCH 2) the ceiling with the product's step growth with the lost transmittance", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+ceo+cel+ctaps+cls+r100+lp100"),
    "k_vis_cut": ("(SEARCH 2) the ceiling with the product's light cutoff (a step that weighs little reuses the last sun march)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cng+ceo+cel+ctaps+cst1+r100+lp100"),
    # Round 3 (owner 2026-11-18: the three visibility savings are equal; in the game they gave 1.5x, the look still ~6x the product): from k_vis_all, one of the remaining big
    # levers back at a time: the product's leap over empty samples, a 12 m shortest step, 8 sun taps; then all three.
    "k3_leap": ("(SEARCH 3) k_vis_all with the product's leap over empty samples", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+ctaps+cst1+r100+lp100"),
    "k3_min12": ("(SEARCH 3) k_vis_all with a 12 m shortest step", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cms12+cnsg+cel+ctaps+cst1+r100+lp100"),
    "k3_taps8": ("(SEARCH 3) k_vis_all with 8 sun taps over the 12 km", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+cst1+r100+lp100"),
    "k3_all": ("(SEARCH 3) k_vis_all with the leap, a 12 m shortest step and 8 sun taps", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cms12+cnsg+ctap8+cst1+r100+lp100"),
    # Round 4 (owner 2026-11-18: 8 sun taps are equal; the leap and a 12 m shortest step changed no pixel, the 256 grid points set the step): from k3_taps8, one more light or
    # step lever back to the product's at a time: 4 sun taps, a 9 km sun march, the step growth along the ray, the light march every second lit step.
    "k4_taps4": ("(SEARCH 4) k3_taps8 with 4 sun taps", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap4+cst1+r100+lp100"),
    "k4_sm9": ("(SEARCH 4) k3_taps8 with a 9 km sun march", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+csm9+cst1+r100+lp100"),
    "k4_grow": ("(SEARCH 4) k3_taps8 with the product's step growth along the ray", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cel+ctap8+cst1+r100+lp100"),
    "k4_stride": ("(SEARCH 4) k3_taps8 with the light march every second lit step (the product's stride)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+r100+lp100"),
    # Round 5 (owner 2026-11-18: the light march every second lit step is equal; 4 taps and a 9 km march are not): the accepted look (k4_stride) at a smaller march size, the
    # missing pixels taken from the jittered history (the target t_ceil_off accumulated jittered frames too). Static camera: one that passes is checked in motion next.
    "k5_r66acc": ("(SEARCH 5) k4_stride at two thirds of the size with jitter and accumulation", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+r66+jit+acc8+clip1000+lp100"),
    "k5_r50acc": ("(SEARCH 5) k4_stride at half the size with jitter and accumulation", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+r50+jit+acc8+clip1000+lp100"),
    "k5_r33acc": ("(SEARCH 5) k4_stride at a third of the size with jitter and accumulation", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+r33+jit+acc8+clip1000+lp100"),
    # The owner's perf program (2026-11-18): point 2, the tile pass that starts no ray where the maps prove the tile empty (cloudSkipEmpty; must leave the picture as it is),
    # and point 6, the noise detail dropped where a pixel cannot resolve it (cloudDetailLod with the far shape mean; the owner judges).
    "p2_skip": ("(PERF 2) the accepted look with the tile pass's skip of empty tiles", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+skip+r100+lp100"),
    "p6_dlod": ("(PERF 6) the accepted look with the detail dropped below a pixel", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+dlod+r100+lp100"),
    # Timing split of the accepted look in the player's soft-cost scene (2026-11-18, the GPU timestamps of the march, a few minutes a run; the cost debug bits make the picture
    # wrong on purpose): no light march (dbg1), no shape noise (dbg2), neither and no erosion (dbg7), the map alone without the single clouds, no finer billows.
    "tc_base": ("(TIME) the accepted look", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+r100+lp100"),
    "tc_d1": ("(TIME) the accepted look without the light march", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+dbg1+r100+lp100"),
    "tc_d2": ("(TIME) the accepted look without the shape noise", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+dbg2+r100+lp100"),
    "tc_d7": ("(TIME) the accepted look without light, noise and erosion", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+dbg7+r100+lp100"),
    "tc_nocells": ("(TIME) the accepted look with the map alone (no single clouds)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cells", 0.0)])), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+r100+lp100"),
    "tc_nofine": ("(TIME) the accepted look without the finer billows", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellFine", 0.0)])), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+r100+lp100"),
    # The light cache (2026-11-19): the accepted look with its first three sun taps marched (47/94/188 m) and the rest of the way to the sun from the cache (40 km box,
    # 24 fill taps unless named). lc_g128 the default 128 x 128 x 32 voxels (owner: equal to k4_stride; player 86 against 97 ms); the others at 256 x 256 x 64:
    # lc_3 (125 ms, the fill costs more than it saves), lc_box80 a box of 80 km, lc_near4 four near taps, lc_fill12 12 fill taps.
    "lc_3": ("(CACHE) the accepted look, the sun's light beyond 330 m from the light cache (256 voxels across)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+lcache3+lcgrid256+r100+lp100"),
    "lc_box80": ("(CACHE) lc_3 with an 80 km box", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+lcache3+lcgrid256+lcbox80+r100+lp100"),
    "lc_near4": ("(CACHE) lc_3 with four near taps (to 700 m)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+lcache4+lcgrid256+r100+lp100"),
    "lc_fill12": ("(CACHE) lc_3 with 12 fill taps", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+lcache3+lcgrid256+lcfill12+r100+lp100"),
    "lc_g128": ("(CACHE) the light cache's default, 128 x 128 x 32 voxels", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+lcache3+lcgrid128+r100+lp100"),
    "lc_g128f12": ("(CACHE) lc_g128 with 12 fill taps", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+lcache3+lcgrid128+lcfill12+r100+lp100"),
    # The owner's perf program point 5 (2026-11-19): the accepted look as a pixel-exact rotation, every full-size pixel marched every 4 (rot2) or 9 (rot3) frames and replaced whole,
    # the others the reprojected history alone (bilinear; rot2c Catmull-Rom). Judged in motion (ATMO_PUFF_MOTION=1, scenarios fly and turnfast) against k4_stride.
    "rot2": ("(ROTATION) k4_stride, each pixel marched every 4th frame", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bexact+lp100"),
    "rot2c": ("(ROTATION) rot2 with the Catmull-Rom history", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bexact+chm+lp100"),
    "rot3": ("(ROTATION) k4_stride, each pixel marched every 9th frame", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk3+bexact+lp100"),
    # rot2/rot3 replaced each pixel by one raw sample: grainy even with a still camera (k4_stride smooths its march noise over frames, 20 % a frame). rot2a/rot3a keep that
    # smoothing (the fresh sample blended in at 0.2, the others the clipped history). far5/far10 (the owner's question): only pixels whose ray enters the clouds beyond
    # 5 or 10 km rotate (every 4th frame), the nearer ones are marched every frame as in k4_stride.
    "rot2a": ("(ROTATION) rot2 with the look's smoothing", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+lp100"),
    "rot3a": ("(ROTATION) rot3 with the look's smoothing", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk3+bacc+lp100"),
    # Owner 2026-11-19: rot2a is a little softer in motion (turn, fly): the kept pixels' history is re-read bilinearly every frame for three frames; rot2ac reads it with Catmull-Rom.
    # Timing split of the accepted look WITH the rotation (2026-11-19, after 9e44a2c9): where the remaining time goes (the cost debug bits make the picture wrong on purpose).
    "tr_base": ("(TIME) the accepted look with the rotation", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100"),
    "tr_d1": ("(TIME) tr_base without the light march", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+dbg1+lp100"),
    "tr_d2": ("(TIME) tr_base without the shape noise", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+dbg2+lp100"),
    "tr_d7": ("(TIME) tr_base without light, noise and erosion", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+dbg7+lp100"),
    "tr_nocells": ("(TIME) tr_base with the map alone (no single clouds)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cells", 0.0)])), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100"),
    # The light stride with the rotation (2026-11-19, owner: continue the cloud optimisation): ls3/ls4 the sun march every 3rd/4th lit step, m the reused depth
    # moved by the ray's progress (lrm); against rot2ac (stride 2, the accepted look with the rotation) and tr_s1 (every lit step).
    "tr_s1": ("(STRIDE) rot2ac with the sun march at every lit step", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+cst1+lp100"),
    "tr_s2m": ("(STRIDE) rot2ac with the reuse moved", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lrm+lp100"),
    "tr_s3": ("(STRIDE) rot2ac, the sun march every 3rd lit step", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+ls3+lp100"),
    "tr_s3m": ("(STRIDE) tr_s3 with the reuse moved", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+ls3+lrm+lp100"),
    "tr_s4m": ("(STRIDE) the sun march every 4th lit step, the reuse moved", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+ls4+lrm+lp100"),
    "rot2ac": ("(ROTATION) rot2a with the Catmull-Rom history", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100"),
    # M2a P0 cost prototype (cloud.bodies, CloudCells.cginc): the march of tr_base with the bodies of several size classes, each a compiled variant of the march pass; p0_b0 is tr_base
    # (no keyword: the accepted lattice). p0dn_* look straight down from 15 km over the same place (the arrangement of the bodies).
    **{"p0_b%d" % n: ("(P0) tr_base, cloud.bodies %d (%s)" % (n, what), compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.bodies", float(n))])),
                      "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for n, what in ((0, "the accepted lattice"), (1, "G2 as designed"), (2, "F2"), (3, "two classes"), (4, "G2 with arrays"), (5, "G2, loops over the cells too"), (6, "neighbourhood check"),
                                     (7, "G2 with arrays, two hashes per body"), (8, "leap check"), (9, "leap check's control"))},
    **{"p0dn_b%d" % n: ("(P0) straight down from 15 km, cloud.bodies %d" % n, compose(camera_at(15000.0, -90.0), sun_behind(45.0), tune_dotted([("cloud.bodies", float(n))])),
                        "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for n in (0, 1, 5, 6, 8, 9)},
    # M2a P0b-0: the views of the series p0b-cost (the bodies, the forced share and the light are march tokens there, so one view load measures every case) and the
    # pictures of the pruned form (12) against 7 (soft-temporal, cloud.bodies as a job key).
    "p0b_g": ("(P0b-0) tr_base's ground view", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100"),
    "p0b_dn": ("(P0b-0) straight down from 15 km", compose(camera_at(15000.0, -90.0), sun_behind(45.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100"),
    **{"p0b_%s%d" % (v, n): ("(P0b-0) %s, cloud.bodies %d" % (what, n), compose(pose, tune_dotted([("cloud.bodies", float(n))])), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for v, what, pose in (("g", "ground", compose(camera_at(30.0, 4.0), sun_behind(25.0))), ("dn", "straight down from 15 km", compose(camera_at(15000.0, -90.0), sun_behind(45.0))))
       for n in (0, 7, 12)},
    # P0b-1: the K slots (14 .. 16) against 7 and today, at the map's share and at forced shares (s<NN>: cloud.bodies.share NN / 100), for the pictures and the alphas of
    # the matched-cover search.
    **{"p0b_%s%ds%02d" % (v, n, s): ("(P0b-1) %s, cloud.bodies %d, share %s" % (what, n, "the map's" if s == 0 else "%.2f" % (s / 100.0)),
                                     compose(pose, tune_dotted([("cloud.bodies", float(n)), ("cloud.bodies.share", s / 100.0)])),
                                     "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for v, what, pose in (("g", "ground", compose(camera_at(30.0, 4.0), sun_behind(25.0))), ("dn", "straight down from 15 km", compose(camera_at(15000.0, -90.0), sun_behind(45.0))))
       for n in (0, 7, 13, 14, 15, 16) for s in (0, 10, 15, 20, 25, 30, 35, 40, 50, 60, 75)},
    # P0c: with the light cache (march token lcache3: three near sun taps, the rest from the cache) and a low sun, so that the shadows between bodies show: today and
    # the bodies over four slots (16), each with the cache off (c0) and on (c1).
    **{"p0c_%s%d_c%d" % (v, n, c): ("(P0c) %s, cloud.bodies %d, light cache %s" % (what, n, "on" if c else "off"),
                                    compose(pose, tune_dotted([("cloud.bodies", float(n))])),
                                    "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm%s+lp100" % ("+lcache3" if c else ""))
       for v, what, pose in (("g", "ground, sun 10 degrees behind", compose(camera_at(30.0, 4.0), sun_behind(10.0))),
                             ("dn", "straight down from 15 km, sun 15 degrees", compose(camera_at(15000.0, -90.0), sun_behind(15.0))))
       for n in (0, 16) for c in (0, 1)},
    # P0c diagnosis of the streaks in the P0b ground view (cloud.bodies.debug: 1 no reach leap, 2 no shell leap, 4 the nearest body alone).
    **{"p0c_dbg%d" % b: ("(P0c) ground, cloud.bodies 16, debug %d" % b,
                         compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.bodies", 16.0), ("cloud.bodies.debug", float(b))])),
                         "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for b in (0, 1, 2, 3, 4, 8, 16)},
    **{"p0c_trace%d_%d" % (n, t): ("(P0c) ground, cloud.bodies %d, cloud.cellTrace %d" % (n, t),
                                   compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.bodies", float(n)), ("cloud.cellTrace", float(t))])),
                                   "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for n in (0, 16) for t in (0, 1)},
    # P0c: the streaks with the height variation off (the cumulus layer's heightVariation 0) and with one type everywhere (cloud.typeOverride 0.5), the shell leap on.
    **{"p0c_field%d_%s" % (n, k): ("(P0c) ground, cloud.bodies %d, %s" % (n, k),
                                   compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.bodies", float(n))]),
                                           cumulus_layer(lambda l: l.__setitem__("heightVariation", 0.0)) if k in ("hv0", "hv0nt", "both", "bothnt") else (lambda job: None),
                                           tune_cumulus([("cloud.typeOverride.", 0.5)]) if k in ("ty", "tynt", "both", "bothnt") else (lambda job: None),
                                           tune_dotted([("cloud.cellTrace", 0.0)]) if k.endswith("nt") else (lambda job: None)),
                                   "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for n in (0, 16) for k in ("hv0", "hv0nt", "ty", "tynt", "both", "bothnt")},
    **{"p0c_fixf%d_%d" % (s, t): ("(P0c) ground, cloud.bodies 16, share forced %.2f, cloud.cellTrace %d" % (s / 100.0, t),
                                  compose(camera_at(30.0, 4.0), sun_behind(25.0),
                                          tune_dotted([("cloud.bodies", 16.0), ("cloud.bodies.share", s / 100.0), ("cloud.cellTrace", float(t))])),
                                  "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for s in (25, 40) for t in (0, 1)},
    # P0d: the local leap bound (the largest share near the ray): its validity (shell leap on / off at the map's share and at forced shares), and the diagnosis of the
    # straight-edged body (17 the 4 x 4 search, debug 64 no second body in the largest class, a forced share against the share map's texel edges).
    **{"p0d_val_s%02d_t%d" % (s, t): ("(P0d) ground, K4 local bound, share %s, cloud.cellTrace %d" % ("the map's" if s == 0 else "%.2f" % (s / 100.0), t),
                                      compose(camera_at(30.0, 4.0), sun_behind(25.0),
                                              tune_dotted([("cloud.bodies", 16.0), ("cloud.bodies.share", s / 100.0), ("cloud.cellTrace", float(t))])),
                                      "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for s in (0, 25, 50) for t in (0, 1)},
    **{"p0d_body_%s" % k: ("(P0d) ground, %s" % what, compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted(keys)),
                           "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for k, what, keys in (("k4", "K4", [("cloud.bodies", 16.0)]), ("k4w", "K4 with a 4 x 4 search", [("cloud.bodies", 17.0)]),
                             ("nb1", "K4 without the second body of the largest class", [("cloud.bodies", 16.0), ("cloud.bodies.debug", 64.0)]),
                             ("f35", "K4 at a forced share 0.35", [("cloud.bodies", 16.0), ("cloud.bodies.share", 0.35)]),
                             ("today", "today's lattice", [("cloud.bodies", 0.0)]))},
    **{"p0d_dn_%s" % k: ("(P0d) straight down from 15 km, sun 45, %s" % what, compose(camera_at(15000.0, -90.0), sun_behind(45.0), tune_dotted(keys)),
                         "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100")
       for k, what, keys in (("k4", "K4 local bound", [("cloud.bodies", 16.0)]), ("today", "today's lattice", [("cloud.bodies", 0.0)]))},
    "far5": ("(ROTATION) k4_stride, pixels beyond 5 km marched every 4th frame", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+far5+r100+lp100"),
    "far10": ("(ROTATION) k4_stride, pixels beyond 10 km marched every 4th frame", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+far10+r100+lp100"),
    "k_vis_all": ("(SEARCH 2) the ceiling with all three visibility savings of the product", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+c256+cmin6+cnsg+cel+ctaps+cst1+r100+lp100"),
    "k_ng256_full":("(SPLIT) 256 grid points at the ceiling's full size and accumulation", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng256+r100+jit+acc8+clip1000+lp100"),
    "k_ng96_full": ("(SPLIT) 96 grid points at the ceiling's full size and accumulation", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng96+r100+jit+acc8+clip1000+lp100"),
    "f_ng96_only":("(FOOT) 96 grid points with the footprint filter alone (no shell leap)", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 0.0), ("cloud.cellFoot", 1.0)])), NG96),
    "f_ng128":("(FOOT) 128 grid points with the shell leap and the footprint filter", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0), ("cloud.cellFoot", 1.0)])), "seq1+lq2+ng128+r33+lp100"),
    "f_ng256": ("(FOOT) 256 grid points with the shell leap and the footprint filter", compose(camera_at(30.0, 4.0), sun_behind(25.0), tune_dotted([("cloud.cellTrace", 1.0), ("cloud.cellFoot", 1.0)])), "seq1+lq2+ng256+r33+lp100"),
    "b_ng256": ("(BODY) the product's march with 256 grid points (the product since ffd10f37)", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng256+r33+lp100"),
    "b_ng128": ("(BODY) the product's march with 128 grid points", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng128+r33+lp100"),
    "b_ng160": ("(BODY) the product's march with 160 grid points", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng160+r33+lp100"),
    "b_ng192": ("(BODY) the product's march with 192 grid points", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng192+r33+lp100"),
    "b_min6": ("(BODY) the product's march with a 6 m minimum step", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng96+cmin6+r33+lp100"),
    "b_cng": ("(BODY) the product's march without step and transmittance growth", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng96+cng+r33+lp100"),
    "b_ceo": ("(BODY) the product's march with early-out 0.001", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng96+ceo+r33+lp100"),
    "b_cel": ("(BODY) the product's march without the leap over empty grid points", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng96+cel+r33+lp100"),
    "b_cls": ("(BODY) the product's march with every lit step to the sun", compose(camera_at(30.0, 4.0), sun_behind(25.0)), "seq1+lq2+ng96+cls+r33+lp100"),
    "c_ero50": ("(CELLS) c_on, the types' erosion halved", compose(tune_dotted([("cloud.cells", 1.0)]), lambda job: each_type(job, lambda t: t.__setitem__("erosion", t.get("erosion", 0.0) * 0.5))), NG96),
    "c_edge08": ("(CELLS) c_on, the cumulus layer's edge softness 0.08", compose(tune_dotted([("cloud.cells", 1.0)]), cumulus_layer(lambda l: l.__setitem__("softness", 0.08))), NG96),
    "c_both": ("(CELLS) c_on, erosion halved and edge softness 0.08", compose(tune_dotted([("cloud.cells", 1.0)]), lambda job: each_type(job, lambda t: t.__setitem__("erosion", t.get("erosion", 0.0) * 0.5)),
                                                                               cumulus_layer(lambda l: l.__setitem__("softness", 0.08))), NG96),
    "steps": ("(Phase 0) the ceiling's grid, minimum step, no growth, early-out", None, STEPS),
    "s_p2p1p3": ("(Phase 0 grid) P2 + P1 + P3 at the ceiling's grid, minimum step, no growth", tune_cumulus(HEAP1 + EDGE + CAV), STEPS),
    "s_p2p1p3p6": ("(Phase 0 grid) P2 + P1 + P3 + P6 at the ceiling's grid", tune_cumulus(HEAP1 + EDGE + CAV + FIRSTHIT), STEPS),
    "ceil": ("(ceiling) the model without the real-time compromises", v_nosmooth, CEIL),
    "c_p2": ("(ceiling) P2", compose(v_nosmooth, tune_cumulus(HEAP1)), CEIL),
    "c_p2p1": ("(ceiling) P2 + P1", compose(v_nosmooth, tune_cumulus(HEAP1 + EDGE)), CEIL),
    "c_p2p1p3": ("(ceiling) P2 + P1 + P3", compose(v_nosmooth, tune_cumulus(HEAP1 + EDGE + CAV)), CEIL),
    "ero0": ("(a) no erosion (every type's erosion 0): what the erosion contributes now", v_ero0, None),
    "ero2": ("(a) the erosion of every type doubled", v_ero2, None),
    "contrast2": ("(a) the erosion detail's contrast about its mean doubled (cloud.billow.contrast 2)", tune_dotted([("cloud.billow.contrast", 2.0)]), None),
    "contrast3": ("(a) the erosion detail's contrast x 3 (the volume's d has a spread of 0.09: x 3.2 makes it uniform like the shape volume; cloud.billow.contrast 3)",
                  tune_dotted([("cloud.billow.contrast", 3.0)]), None),
    "top1": ("(a) the erosion's gain toward the top 1 (cloud.billow.top: erosion x (1 + h))", tune_dotted([("cloud.billow.top", 1.0)]), None),
    "fine08": ("(a) the fine octave at weight 0.8 (0.4 now)", tune_dotted([("cloud.billow.fine", 0.8)]), None),
    "fine0": ("(a) no fine octave (weight 0)", tune_dotted([("cloud.billow.fine", 0.0)]), None),
    "fine4": ("(a) the fine octave at 4 times the frequency (2.37 now)", tune_dotted([("cloud.billow.fineFreq", 4.0)]), None),
    "nolod": ("(a) no distance LOD of the detail", v_nolod, None),
    "light": ("(b) billow shading: the direct light of a sample follows the erosion detail there (cloud.billow.light 1)", tune_dotted([("cloud.billow.light", 1.0)]), None),
    "nosmooth": ("(c) no shape smoothing", v_nosmooth, None),
    "soft03": ("(c) the soft density's width 0.3 (0.5 now)", v_soft03, None),
    "sharp": ("(d) the composite upsamples with a Catmull-Rom spline (cloud.billow.sharpen 1)", tune_dotted([("cloud.billow.sharpen", 1.0)]), None),
    "res50": ("(d) the march target half the size (r50; a third now)", None, "r50"),
    "res100": ("(d) the march target the full size (r100)", None, "r100"),
    "puff1": ("(e) contrast 3 + top 0.5", tune_dotted([("cloud.billow.contrast", 3.0), ("cloud.billow.top", 0.5)]), None),
    "puff2": ("(e) contrast 3 + top 0.5 + light tap + sharpen 1", tune_dotted([("cloud.billow.contrast", 3.0), ("cloud.billow.top", 0.5), ("cloud.billow.light", 1.0),
                                                                                  ("cloud.billow.sharpen", 1.0)]), None),
}


def march_of(case):
    """The march string of a variant's case: the base's with the resolution case replaced."""
    if case is None:
        return e40pp.MARCH
    if case.startswith("seq"):
        return case   # a whole march (the ceiling's)
    return e40pp.MARCH.replace("+r33", "+" + case)


def tag_of(view, variant):
    return view if variant == "base" else "%s-%s" % (view, variant)


def prepare(views, variants, only=None):
    """only: a set of (view, variant) pairs to prepare out of views x variants (None: all of them)."""
    if not os.path.isfile(os.path.join(e40pp.INPUT, "jobs.json")):
        raise SystemExit("run e40pp.py --prepare first")
    os.makedirs(INPUT, exist_ok=True)
    with open(os.path.join(e40pp.INPUT, "jobs.json"), encoding="utf-8") as f:
        jobs = {j["id"]: j for j in json.load(f)["jobs"]}
    with open(os.path.join(e40pp.INPUT, "heights.json"), encoding="utf-8") as f:
        specs = {s["id"]: s for s in json.load(f)["specs"]}
    with open(os.path.join(e40pp.INPUT, "stages.json"), encoding="utf-8") as f:
        stages = json.load(f)
    out_jobs, out_specs, out_stages = [], [], {}
    for view in views:
        base_tag = e40pp.view_tag(view)
        base_id = "gn-%s-prod-f0" % base_tag
        for variant in variants:
            if only is not None and (view, variant) not in only:
                continue
            name = tag_of(view, variant)
            new_tag = e40pp.view_tag(name)
            for fn in os.listdir(e40pp.INPUT):
                if fn.startswith(base_tag + "-"):
                    shutil.copyfile(os.path.join(e40pp.INPUT, fn), os.path.join(INPUT, new_tag + fn[len(base_tag):]))
            if variant.startswith("med"):
                # mediocris: the cumulus layer's type map is the type value 0.5 everywhere (the type axis is stratocumulus, humilis, mediocris, congestus, altocumulus)
                path = os.path.join(INPUT, new_tag + "-Kerbin-cumulus-type.r8")
                size = os.path.getsize(path)
                with open(path, "wb") as f:
                    f.write(bytes([128]) * size)
            job = json.loads(json.dumps(jobs[base_id]).replace(base_tag + "-", new_tag + "-"))
            job["id"] = "gn-%s-prod-f0" % new_tag
            job["width"], job["height"] = SIZE
            change, case = VARIANTS[variant][1], VARIANTS[variant][2]
            # The single-cloud lattice is the shape test's tool (owner 2026-11-19): on in every puff job unless the variant sets cloud.cells itself (its entry comes later and wins);
            # the game's default is off, the weather map decides the clouds.
            job.setdefault("tuning", []).insert(0, {"key": "cloud.cells", "value": 1.0})
            if change:
                change(job)
            spec = json.loads(json.dumps(specs[base_id]).replace(base_tag + "-", new_tag + "-"))
            spec["id"] = job["id"]
            out_jobs.append(job)
            out_specs.append(spec)
            out_stages[name] = dict(stages[view], view=view, variant=variant, march=march_of(case))
            e40pp.VIEWS[name] = dict(e40pp.VIEWS[view])
            print("%s <- %s" % (name, VARIANTS[variant][0]))
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": out_jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": out_specs}, f)
    with open(os.path.join(INPUT, "stages.json"), "w", encoding="utf-8") as f:
        json.dump(out_stages, f, indent=1)


def prepared():
    path = os.path.join(INPUT, "stages.json")
    if not os.path.isfile(path):
        return {}
    with open(path, encoding="utf-8") as f:
        stages = json.load(f)
    for name, st in stages.items():
        if name not in e40pp.VIEWS:
            e40pp.VIEWS[name] = dict(e40pp.VIEWS[st["view"]])
    return stages


# The size of the player's frames: ATMO_PUFF_SIZE=2560x1440 (1920x1080) renders the pictures at that size; the default is the game's 1707 x 960 that the parity series and the real-photo ranges are
# stated for. Frames of another size are composed over the game's frame resized to it ("player, not parity checked at this size") and MEASURED after an area downsample to 1707 x 960, so that
# the pixel-scale measures (2-16 px bands, bumps per 1000 px) keep the px per radian of the real-photo ranges (831 px/rad at a 60 degree vertical field of view).
REF_W, REF_H = 1707, 960


def frame_size():
    text = os.environ.get("ATMO_PUFF_SIZE", "")
    if "x" in text:
        w, h = text.lower().split("x")
        return int(w), int(h)
    return REF_W, REF_H


SIZE = frame_size()
SUFFIX = "" if SIZE == (REF_W, REF_H) else "-%d" % SIZE[0]
# ATMO_PUFF_MOTION=1: every march also in the player's moving scenarios (fly, turnfast, flyfast), for the reconstructions that need motion to be judged; --scenario and
# --frame of --native pick such a frame (the clouds are composed over the game's still frame, so the ground behind a turned camera is the still view's).
MOTION = os.environ.get("ATMO_PUFF_MOTION", "") == "1"


def light_args():
    """The player's light arguments of e40pp with this size's width and the height."""
    out = list(e40pp.LIGHT)
    i = out.index("-width")
    out[i + 1] = str(SIZE[0])
    # -dump last keeps the player to the still scenario; the moving ones need it gone (the still frames stay those of -dumpframes).
    if MOTION and "-dump" in out:
        j = out.index("-dump")
        del out[j:j + 2]
    return out + ["-height", str(SIZE[1])]


def at_size(img, w, h):
    """An RGB float picture resized to w x h (area filter when it gets smaller, bilinear when it grows)."""
    from PIL import Image
    if img.shape[1] == w and img.shape[0] == h:
        return img
    pic = Image.fromarray((np.clip(img, 0, 1) * 255.0 + 0.5).astype("uint8"))
    return np.asarray(pic.resize((w, h), Image.BOX if w < img.shape[1] else Image.BILINEAR), dtype=np.float64) / 255.0


def stage_label(march):
    """The label of the stage that draws a march: the base march's stage is puff-a, another is puff-r followed by the resolution case (r50 or r100); another size adds -<width>."""
    if march in MARCH_LABELS:
        return MARCH_LABELS[march] + SUFFIX
    if march == e40pp.MARCH:
        return LABEL + SUFFIX
    # a march of its own (the ablations): the label carries a short checksum of the march string, so two marches of one resolution case do not share a stage
    import zlib
    return "puff-m%08x" % (zlib.crc32(march.encode("ascii")) & 0xFFFFFFFF) + SUFFIX


def stage_list():
    """One stage per distinct march: the views of the job variants with the base march, the resolution variants (the base job) with theirs."""
    stages = prepared()
    out = []
    by_march = {}
    for name, st in stages.items():
        by_march.setdefault(st["march"], []).append(name)
    for march, names in sorted(by_march.items()):
        views = ",".join(e40pp.view_tag(n) for n in names)
        out.append(Stage(stage_label(march), ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", march] + light_args()
                         + (["-motion", "all"] if MOTION else []),
                         size=SIZE, proxy=False, timeout=3400 + (3000 if SIZE != (REF_W, REF_H) else 0)))
    return out


def measure_all(folder_of):
    """folder_of: stage label -> the player's output folder of that stage; the frames are found by the view's march."""
    stages = prepared()
    rows = {}
    for name, st in stages.items():
        spec = e40pp.VIEWS[name]
        off = e40pp.game_frame(spec, "off_a", st.get("start", e40pp.GAME_START))
        folder = folder_of(stage_label(st["march"]))
        path = os.path.join(folder, e40pp.case_file(name, st["march"]) + ".npy")
        if off is None or not os.path.isfile(path):
            continue
        m = e40ty.load_lum(folder, e40pp.case_file(name, st["march"]))
        mh, mw = m.shape[0], m.shape[1]
        # the game's frame without clouds at the frame's size (bilinear where it is another size than the game's)
        off_native = at_size(off, mw, mh)
        if st["variant"] in MOVED_CAMERA:
            # the game's frame is another pose: a clear gradient sky behind the clouds instead (the cloud's own look is what these views are for)
            ys = np.linspace(0.0, 1.0, mh)[:, None, None]
            sky = (1.0 - ys) * np.array([0.22, 0.40, 0.76]) + ys * np.array([0.62, 0.76, 0.92])
            off_native = np.repeat(sky, mw, axis=1)
        img = e40pp.compose(m, off_native, st)
        ok = e40pp.measure_mask(st["view"], st["alt"], off.shape)
        # the measures run on the 1707 x 960 scale (area downsample of the native frame), the pictures stay native
        img_ref = at_size(img, off.shape[1], off.shape[0])
        r = pf.measure(img_ref, off, None, ok)
        rows[name] = dict(view=st["view"], variant=st["variant"], full=r, alpha=np.asarray(m[:, :mw, 3], dtype=np.float32), **pf.summary(r))
        rows[name]["image"] = img
        rows[name]["ok_ref"] = ok
    return rows


REAL_ROOT = os.path.join(e40pp.MAIN, ".local", "work", "real-clouds")
# the real-photo type each view is compared with (the cumulus layer's mixed views with humilis)
REAL_TYPE = {"tyhuma": "humilis", "tyhuma_a": "humilis", "tycona": "congestus", "gclock": "humilis", "sclock": "humilis", "ty_humilis_a": "humilis", "ty_congestus_a": "congestus"}


def real_ranges(height):
    """The REALREF ranges re-derived for the frame height (run_real_size.py: the same photographs resized to the px per radian of that height); None where the file is missing."""
    path = os.path.join(REAL_ROOT, "results", "ranges_%d.json" % height)
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)["ranges"]


def real_table(rows):
    """The real-photo method (REALREF's blueness mask, realref.measure_img) on each frame: at its NATIVE size, compared with the ranges derived at that size's px per radian, and, as a labelled
    cross-check column set, on the frame area-downsampled to 1707 x 960 compared with the 960 ranges. Returns the text."""
    sys.path.insert(0, os.path.join(REAL_ROOT, "scripts"))
    try:
        import realref as rr
    except ImportError:
        return "(REALREF scripts not found under %s)" % REAL_ROOT
    keys = [("D8_64", "box"), ("L4", "edge-len"), ("fine", "2-16px"), ("bumps", "bumps"), ("S4", "S(4)")]
    lines = ["%-18s %-6s %-11s " % ("view-variant", "size", "mode") + " ".join("%9s" % label for _, label in keys) + "   range (median, min-max) of the real photos at this size"]
    for name, r in sorted(rows.items(), key=lambda kv: (kv[1]["view"], kv[1]["variant"] != "base", kv[1]["variant"])):
        img = r["image"]
        h, w = img.shape[:2]
        ok_ref = r["ok_ref"]
        for mode, pic, ok, height in (("native", img, np.asarray(at_size(ok_ref.astype(np.float64)[..., None].repeat(3, axis=2), w, h)[..., 0] > 0.5), h),
                                      ("downsampled", at_size(img, REF_W, REF_H), ok_ref, REF_H)):
            if mode == "downsampled" and (w, h) == (REF_W, REF_H):
                continue
            res, _, _ = rr.measure_img(pic, ok_in=ok)
            x = rr.row(res)
            rng = real_ranges(height)
            t = REAL_TYPE.get(r["view"])
            text = ""
            if rng and t in rng:
                text = "  ".join("%s %.3f (%.3f-%.3f)" % (lab, rng[t][k][0], rng[t][k][1], rng[t][k][2]) if rng[t].get(k) else "%s -" % lab for k, lab in keys[:4])
            lines.append("%-18s %-6s %-11s " % (name, "%dx%d" % (w, h) if mode == "native" else "%dx%d" % (REF_W, REF_H), mode)
                         + " ".join("%9.3f" % x.get(k, float("nan")) for k, _ in keys) + ("   " + text if text else ""))
    return "\n".join(lines)


def scaleinv_table(rows):
    """CLOUDR1: the multi-band scale-invariance measures (scaleinv.py: outline box-counting dimension D and power-spectrum slope S over 2-8, 8-32, 32-128, 128-512 px) of each frame at its
    NATIVE size, next to the real photographs' ranges at the same px per radian (results/scaleinv_<height>.json, made by `scaleinv.py real <height>`). Ours is the march's alpha, the photographs'
    the blueness distance; the luma spectrum is left out for ours (the frame holds the game's ground and sky)."""
    from weather_metrics import scaleinv as si
    lines = ["%-18s %-8s " % ("view-variant", "measure") + " ".join("%6s" % ("%d-%d" % b) for b in si.BANDS) + "   real photographs of the type (median, min-max) per band"]
    for name, r in sorted(rows.items(), key=lambda kv: (kv[1]["view"], kv[1]["variant"] != "base", kv[1]["variant"])):
        alpha = r["alpha"]
        res = si.measure(alpha, None, 0.5)
        ref = None
        path = os.path.join(REAL_ROOT, "results", "scaleinv_%d.json" % alpha.shape[0])
        t = REAL_TYPE.get(r["view"])
        if os.path.isfile(path) and t:
            with open(path, encoding="utf-8") as f:
                ref = json.load(f)["ranges"].get(t)
        lines += si.table_lines(name, res, ref)
        lines.append("%-18s %-8s %6.3f   (share of the frame with alpha above 0.5)" % (name, "cover", float((np.asarray(alpha) > 0.5).mean())))
    return "\n".join(lines)


KEYS = ["cover", "fine", "D8_64", "L4", "bumps", "S4", "S16"]


def table(rows):
    lines = ["%-18s " % "view-variant" + " ".join("%9s" % k for k in KEYS)]
    for name, r in sorted(rows.items(), key=lambda kv: (kv[1]["view"], kv[1]["variant"] != "base", kv[1]["variant"])):
        lines.append("%-18s " % name + " ".join("%9.4f" % r.get(k, float("nan")) for k in KEYS))
    return "\n".join(lines)


MOVED_CAMERA = ("G0", "GN4", "T0", "TN4", "C0", "CN4", "dn", "dnS5", "d45", "d45S5", "fh_dn", "fh_dnL", "fh_dnB", "fh_sd", "fh_sdL", "bl_dn", "bl_sd")
CROP_W, CROP_H = 900, 600


def crop_windows(alpha):
    """Three native 900 x 600 windows of a frame, chosen on the BASE frame's alpha and used for every variant of the view: 'rim' (the most pixels of partial opacity, the cloud's edge),
    'core' (the highest mean opacity), 'near' (the most partial-opacity pixels in the lower half of the frame, where the nearest clouds and the ground are). Returns {name: (x0, y0)}."""
    a = np.clip(np.asarray(alpha, dtype=np.float32), 0.0, 1.0)
    h, w = a.shape
    edge = ((a > 0.2) & (a < 0.8)).astype(np.float32)
    step = 30
    best = {"rim": (-1.0, (0, 0)), "core": (-1.0, (0, 0)), "near": (-1.0, (0, h // 2 - 0))}
    for y in range(0, h - CROP_H + 1, step):
        for x in range(0, w - CROP_W + 1, step):
            e = float(edge[y:y + CROP_H:6, x:x + CROP_W:6].sum())
            c = float(a[y:y + CROP_H:6, x:x + CROP_W:6].mean())
            if e > best["rim"][0]:
                best["rim"] = (e, (x, y))
            if c > best["core"][0]:
                best["core"] = (c, (x, y))
            if y >= h - CROP_H - 2 * step and e > best["near"][0]:
                best["near"] = (e, (x, y))
    return {k: v[1] for k, v in best.items()}


def write_native(out, view, variant, img, windows):
    """The frame at native size (full/<view>-<variant>.png) and the windows at 1:1 (crops/<view>-<variant>-<rim|core|near>.png), no resize and no label in the pixels; returns the paths."""
    from PIL import Image
    os.makedirs(os.path.join(out, "full"), exist_ok=True)
    os.makedirs(os.path.join(out, "crops"), exist_ok=True)
    pic = (np.clip(img, 0, 1) * 255).astype("uint8")
    paths = []
    p = os.path.join(out, "full", "%s-%s.png" % (view, variant))
    Image.fromarray(pic).save(p)
    paths.append(p)
    for name, (x, y) in windows.items():
        q = os.path.join(out, "crops", "%s-%s-%s.png" % (view, variant, name))
        Image.fromarray(pic[y:y + CROP_H, x:x + CROP_W]).save(q)
        paths.append(q)
    return paths


def native_pictures(items, out, folder_of, scenario="static", frame=39):
    """Full and crop pictures for [(view, variant)] straight from the player's frames (no prepared variant inputs needed): the windows come from the view's base frame.
    scenario and frame pick a moving scenario's frame (ATMO_PUFF_MOTION)."""
    stages = prepared()
    done = []
    windows = {}
    base_m = {}
    for view, variant in sorted(items, key=lambda t: (t[0], t[1] != "base", t[1])):
        name = view if variant == "base" else "%s-%s" % (view, variant)
        st = stages[name]
        spec = e40pp.VIEWS[name]
        off = e40pp.game_frame(spec, "off_a", st.get("start", e40pp.GAME_START))
        # each variant's own march decides its stage folder and its file name; the windows come from the view's base (production) frame
        folder = folder_of(stage_label(st["march"]))
        case = e40pp.case_file(name, st["march"], frame).replace("-static-", "-%s-" % scenario)
        if off is None or not os.path.isfile(os.path.join(folder, case + ".npy")):
            print("missing frame: %s (%s)" % (name, case))
            continue
        m = oriented(e40ty.load_lum(folder, case), base_m.get(view))
        if variant == "base":
            base_m[view] = m
        img = e40pp.compose(m, at_size(off, m.shape[1], m.shape[0]), st)
        if view not in windows:
            windows[view] = crop_windows(np.asarray(m[:, :m.shape[1], 3], dtype=np.float32))
        done += write_native(out, view, variant, img, windows[view])
    return done


def oriented(m, base):
    """The frame the right way up. The player's dump of the ceiling stage came out upside down on every second call (the pixels are the same, 1.0000 correlation after the flip); the
    production frame of the same view decides which way the frame is: the one whose colour channels follow the base frame's better."""
    if base is None:
        return m

    def small(x):
        h, w = x.shape[0] // 20 * 20, x.shape[1] // 20 * 20
        return x[:h, :w, :3].sum(axis=2).reshape(h // 20, 20, w // 20, 20).mean(axis=(1, 3)).ravel()
    same = np.corrcoef(small(m), small(base))[0, 1]
    flipped = np.corrcoef(small(m[::-1]), small(base))[0, 1]
    return m[::-1] if flipped > same else m


def pictures(rows, out):
    from PIL import Image, ImageDraw
    os.makedirs(out, exist_ok=True)
    windows = {}
    for name, r in sorted(rows.items(), key=lambda kv: (kv[1]["view"], kv[1]["variant"] != "base", kv[1]["variant"])):
        view = r["view"]
        if view not in windows:
            windows[view] = crop_windows(r["alpha"])
        write_native(out, view, r["variant"], r["image"], windows[view])
    by_view = {}
    for name, r in rows.items():
        by_view.setdefault(r["view"], []).append((r["variant"], r["image"]))
    for view, items in by_view.items():
        items.sort(key=lambda t: (t[0] != "base", t[0]))
        cols = 3
        rows_n = (len(items) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * 574, rows_n * 400), (20, 20, 20))
        d = ImageDraw.Draw(sheet)
        for i, (variant, img) in enumerate(items):
            tile = Image.fromarray((np.clip(img, 0, 1) * 255).astype("uint8")).resize((570, 380))
            x, y = (i % cols) * 574, (i // cols) * 400
            sheet.paste(tile, (x, y + 18))
            d.text((x + 4, y + 3), "working view (parity checked on the base only): %s %s" % (view, variant), fill=(255, 255, 255))
        sheet.save(os.path.join(out, "e40-puff-%s.png" % view))


def collect(ctx):
    rows = measure_all(ctx.folder)
    print(table(rows))
    metrics = []
    for name, r in rows.items():
        for k in KEYS:
            v = r.get(k)
            if v is not None and v == v:
                metrics.append(Metric.info("e40puff.%s.%s" % (name, k), v))
    return metrics


register_series("e40-puff", stage_list(), collect, exclusive=False, who="e40-puff")

COST_VARIANTS = ("base", "ssz", "steps", "res", "light", "ceil", "r_a", "r_s16", "r_s32", "r_s64", "r_s32n", "r_ls", "r_c256", "r_cmin6", "r_cng", "r_c256m", "r_c256mg", "r_g", "r_ng96", "r_ng128", "r_ng192", "bl_a", "bl_base", "bl_g", "p2p1p3", "s_p2p1p3", "tc_base", "tc_d1", "tc_d2", "tc_d7", "tc_nocells", "tc_nofine", "lc_3", "lc_box80", "lc_near4", "lc_fill12", "lc_g128", "lc_g128f12", "rot2", "rot3", "rot2a", "rot3a", "far5", "far10", "tr_base", "tr_d1", "tr_d2", "tr_d7", "tr_nocells", "tr_s1", "tr_s2m", "tr_s3", "tr_s3m", "tr_s4m", "rot2ac",
                 "p0_b0", "p0_b1", "p0_b2", "p0_b3", "p0_b4", "p0_b5", "p0_b7")


def cost_label(march):
    return "puffc-" + stage_label(march)[5:]


def cost_stage_list():
    """The GPU time of the pass (soft-cost scene: march | resolve | composite, median of 40 frames, proxy timestamps) for the variants named in COST_VARIANTS: one stage per march (a stage
    runs one march over its views, and a view of a job variant carries that variant's job)."""
    stages = prepared()
    by_march = {}
    for n, st in stages.items():
        if st["variant"] in COST_VARIANTS:
            by_march.setdefault(st["march"], []).append(n)
    out = []
    # ATMO_COST_ROUNDS=N: N rounds in alternating order (the scene's -rounds), each round's march median reported (the M2a P0 repeats).
    rounds = int(os.environ.get("ATMO_COST_ROUNDS", "2") or 2)
    for march, names in sorted(by_march.items()):
        views = ",".join(e40pp.view_tag(n) for n in names)
        out.append(Stage(cost_label(march), ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", march, "-width", str(SIZE[0]), "-height", str(SIZE[1])]
                         + (["-rounds", str(rounds)] if rounds > 2 else []),
                         size=SIZE, proxy=True, timeout=2400 + (1500 if SIZE != (REF_W, REF_H) else 0) + 400 * max(0, rounds - 2)))
    return out


def collect_cost(ctx):
    rows = {}
    for label in sorted({cost_label(st["march"]) for st in prepared().values()}):
        for k, v in ctx.player_metrics(label).items():
            if not k.startswith("softcost."):
                continue
            _, part, rest = k.split(".", 2)
            view = rest.split(".", 1)[0]
            rows.setdefault(view, {})[part] = v[0]
    metrics, lines = [], ["E40 PUFF: GPU ms of the pass at %d x %d (march | resolve | composite | total; with ATMO_COST_ROUNDS each round's march)" % SIZE]
    for view in sorted(rows):
        r = rows[view]
        per = [r[p] for p in sorted((p for p in r if p.startswith("marchr")), key=lambda p: int(p[6:]))]
        lines.append("   %-34s %7.3f | %7.3f | %7.3f | %7.3f%s" % (view, r.get("march", float("nan")), r.get("resolve", float("nan")), r.get("composite", float("nan")), r.get("total", float("nan")),
                                                         ("   rounds " + " ".join("%.3f" % x for x in per)) if per else ""))
        metrics.append(Metric.info("e40puff.cost." + view, r.get("total", float("nan"))))
    print("\n".join(lines))
    return metrics


register_series("e40-puff-cost", cost_stage_list(), collect_cost, exclusive=True, who="e40-puff")

# M2a P0b-0: the cost of the bodies at fixed shares, light on and off (ATMO_P0B=share), the split of V7's cost (split), the density call counters (count). One stage
# over the prepared views p0b_g and p0b_dn; every case is a march (tokens bv, bs, bcnt, dbg1), so a view is loaded once a round.
P0B_BASE = "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm"


def p0b_marches(kind):
    """P0b-0: split, share, count. P0b-1: kcount (the K slots' correctness counters, light on), kcost / kcostoff (today, V7 and the K variant ATMO_P0B_K at the map's share and
    the four fixed shares, light on / off), list (the cases of ATMO_P0B_MARCHES, tokens joined by '+', cases by ',', e.g. bv13+bs25,bv15+bs21)."""
    if kind == "split":
        return [P0B_BASE + "+bv%d+bs0+lp100" % b for b in (0, 7, 10, 11, 12)]
    if kind == "kcount":
        return [P0B_BASE + "+bv%d+bs%d+bcnt+lp100" % (b, s) for s in (10, 25, 50, 75) for b in (14, 15, 16)]
    if kind in ("kcost", "kcostoff"):
        k = int(os.environ.get("ATMO_P0B_K", "15"))
        dbg = "+dbg1" if kind == "kcostoff" else ""
        return ([P0B_BASE + "+bv%d+bs0%s+lp100" % (b, dbg) for b in (0, 7, k)]
                + [P0B_BASE + "+bv%d+bs%d%s+lp100" % (b, s, dbg) for s in (10, 25, 50, 75) for b in (13, 7, k)])
    if kind == "list":
        return [P0B_BASE + "+" + c + "+lp100" for c in os.environ.get("ATMO_P0B_MARCHES", "").split(",") if c]
    tail = "+bcnt" if kind == "count" else ""
    return [P0B_BASE + "+bv%d+bs%d%s%s+lp100" % (b, s, dbg, tail) for s in (10, 25, 50, 75) for b in (13, 7, 12) for dbg in ("", "+dbg1")]


def p0b_stage_list():
    kind = os.environ.get("ATMO_P0B", "split")
    names = [n for n, st in prepared().items() if st["variant"] in ("p0b_g", "p0b_dn")]
    if not names:
        return []
    rounds = int(os.environ.get("ATMO_COST_ROUNDS", "2") or 2)
    views = ",".join(e40pp.view_tag(n) for n in sorted(names))
    return [Stage("p0b-" + kind, ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", ",".join(p0b_marches(kind)),
                                  "-width", str(SIZE[0]), "-height", str(SIZE[1])] + (["-rounds", str(rounds)] if rounds > 2 else []),
                  size=SIZE, proxy=True, timeout=1150)]


def collect_p0b(ctx):
    kind = os.environ.get("ATMO_P0B", "split")
    rows = {}
    for k, v in ctx.player_metrics("p0b-" + kind).items():
        if not k.startswith("softcost."):
            continue
        _, part, rest = k.split(".", 2)
        view, _medium, march = rest.split(".", 2)
        rows.setdefault((view, march), {})[part] = v[0]
    lines = ["P0b-0 %s: march ms (median of the round medians, min-max of the rounds) and the density call counters per frame" % kind]
    for (view, march) in sorted(rows):
        r = rows[(view, march)]
        per = sorted(r[p] for p in r if p.startswith("marchr"))
        cnt = [r.get("bodycount%d" % i) for i in range(8)]
        text = "   %-14s %-26s %7.3f" % (view, march.replace(P0B_BASE + "+", "").replace("+lp100", ""), r.get("march", float("nan")))
        if per:
            text += "  (%.3f-%.3f, med %.3f, n %d)" % (per[0], per[-1], per[len(per) // 2], len(per))
        if "fill" in r:
            text += "  cache fill %.3f, march + fill %.3f" % (r["fill"], r.get("march", float("nan")) + r["fill"])
        if cnt[0] is not None:
            calls = (cnt[0] or 0) + (cnt[1] or 0)
            text += "  calls view %.0f light %.0f, live/cover call %.2f, body calls %.0f, looped/body %.2f, reaching/body %.2f" % (
                cnt[0], cnt[1], cnt[2] / max(calls, 1), cnt[3], cnt[4] / max(cnt[3], 1), cnt[5] / max(cnt[3], 1))
            if cnt[6] is not None:
                text += ", K: dropped could reach %.1f, share differs %.1f per frame" % (cnt[6], cnt[7])
        lines.append(text)
    print("\n".join(lines))
    return []


register_series("p0b-cost", p0b_stage_list(), collect_p0b, exclusive=True, who="p0b")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--report", action="store_true")
    p.add_argument("--views", default=",".join(DEFAULT_VIEWS))
    p.add_argument("--variants", default=",".join(VARIANTS))
    p.add_argument("--root", help="the player's output folder, default build/WeatherPlayer; a stage's folder is <root>/e40-puff-<label>")
    p.add_argument("--pictures")
    p.add_argument("--pairs", help="only these view:variant pairs out of --views x --variants, e.g. tyhuma:N1,tycona:C0")
    p.add_argument("--native", help="folder for the full-size and 1:1 crop pictures of --items straight from the player's frames, e.g. tyhuma:base,tyhuma:S5")
    p.add_argument("--items", default="")
    p.add_argument("--scenario", default="static", help="with --native: the player's scenario of the frame (static, fly, turnfast, flyfast; the moving ones need ATMO_PUFF_MOTION=1)")
    p.add_argument("--frame", type=int, default=39, help="with --native: the frame number (the moving scenarios dump 37 and 38)")
    a = p.parse_args()
    if a.native:
        root = a.root or BUILD.replace("weather-series", "WeatherPlayer")
        items = [tuple(s.split(":")) for s in a.items.split(",") if s]
        for path in native_pictures(items, a.native, lambda label: os.path.join(root, "e40-puff-" + label), a.scenario, a.frame):
            print(path)
        return 0
    if a.prepare:
        only = set(tuple(s.split(":")) for s in a.pairs.split(",") if s) if a.pairs else None
        prepare(a.views.split(","), a.variants.split(","), only)
    if a.report:
        root = a.root or BUILD.replace("weather-series", "WeatherPlayer")
        rows = measure_all(lambda label: os.path.join(root, "e40-puff-" + label))
        print("puffiness.py (red/green gain against the game's frame without clouds; on the frame area-downsampled to 1707 x 960: cross-check):")
        print(table(rows))
        print()
        print(real_table(rows))
        print()
        print(scaleinv_table(rows))
        if a.pictures:
            pictures(rows, a.pictures)
    return 0


if __name__ == "__main__":
    sys.exit(main())
