#!/usr/bin/env python3
"""The parity harness runner (package T11).

  python tools/weather_metrics/parity_run.py selftest [--quick]   analytic self-test, convergence, negative controls (exit 0 = all pass)
  python tools/weather_metrics/parity_run.py reference <folder>   the reference column: renders the synthetic scenes with parity_ref.py
                                                                  and writes one capture folder per check (npy + meta.json)
  python tools/weather_metrics/parity_run.py table --reference <folder> [--tvc <folder>] [--ours <folder>]
                                                                  the fixed result table: check / reference / TVC / ours / threshold / PASS
  python tools/weather_metrics/parity_run.py numbers              the first reference run: the figures the thresholds of checks.json cite
  python tools/weather_metrics/parity_run.py images <folder>      natural and aid views of the synthetic scenes (png)

A capture folder holds, per check id, a folder <check id>/ with meta.json and arrays (parity_checks.load_capture). A column that has no
folder for a check shows "-". Stdlib + numpy (+ Pillow for images).
"""
import argparse
import json
import math
import os
import sys
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_checks as pc  # noqa: E402

R = pr.KERBIN_RADIUS
F = pr.FLAT_RADIUS

QUALITY = {
    "quick": dict(w=24, h=18, order=6, steps=48, light=32, paths=1, stride=2),
    "full": dict(w=64, h=48, order=128, steps=128, light=64, paths=2, stride=1),
}


def renderer(field, sun, q, phase=None, **kw):
    args = dict(view_steps=q["steps"], light_steps=q["light"], order=q["order"], ms_stride=q["stride"], ms_paths=q["paths"], phase=phase)
    args.update(kw)
    return pr.Renderer(field, sun, **args)


def column_tau(field, xs, ys, h0=3000.0, steps=64):
    """Optical depth of vertical columns through a flat-planet field at the ground positions (xs, ys)."""
    o = np.stack([xs, ys, np.full_like(xs, F + h0)], axis=1)
    r = pr.Renderer(field, pr.Sun([0, 0, 1]), light_steps=steps, order=1)
    d = np.broadcast_to(np.array([0.0, 0.0, -1.0]), o.shape)
    return r.optical_depth(o, d, steps=steps)


def high_alpha(field, cam, steps=2048):
    """Alpha of every pixel with a fine march (a geometry test needs the shell resolved)."""
    r = pr.Renderer(field, pr.Sun([0, 0, 1]), view_steps=steps, light_steps=4, order=1)
    d = cam.ray_directions()
    o = np.broadcast_to(cam.pos, d.shape).copy()
    tg = field.ground_hit(o, d)
    t, dt = field.samples(o, d, tg, steps)
    tau = np.sum(r._sigma(o, d, t) * dt, axis=1) if t.shape[1] else np.zeros(len(d))
    return (1.0 - np.exp(-tau)).reshape(cam.height, cam.width)


# --------------------------------------------------------------------------------------------------------------------------------
# The synthetic scenes and the reference captures
# --------------------------------------------------------------------------------------------------------------------------------
def cell_scene(q, az, el, phase=None, order=None, light=None, seed=5, w=None):
    """The single cloud cell seen from the side at 8 km: returns the Result and the renderer's parameters."""
    field = pr.scene_single_cloud(radius_m=800.0, centre_alt=2000.0, sigma=0.02)
    cam = pr.Camera([0, -8000.0, F + 2000.0], 0, 0, w or q["w"], int((w or q["w"]) * 0.75), 16)
    sun = pr.Sun.at(cam.pos, az, el)
    qq = dict(q)
    if order is not None:
        qq["order"] = order
    if light is not None:
        qq["light"] = light
    return renderer(field, sun, qq, phase=phase, seed=seed).render(cam)


def edge_source(res, lo=0.12, hi=0.7):
    """The mean source function over the cloud's edge band (alpha in lo..hi): cloud radiance over alpha."""
    m = (res.alpha > lo) & (res.alpha < hi)
    return float(res.cloud[m].sum() / max(res.alpha[m].sum(), 1e-12)), int(m.sum())


def base_top(res):
    """Mean cloud radiance of the lowest and the highest fifth of the cell's rows (alpha > 0.5)."""
    m = res.alpha > 0.5
    rows = np.nonzero(m.any(axis=1))[0]
    top, bot = rows[0], rows[-1]
    band = max(int(round((bot - top + 1) * 0.2)), 1)
    base = res.cloud[bot - band + 1:bot + 1][m[bot - band + 1:bot + 1]].mean()
    tp = res.cloud[top:top + band][m[top:top + band]].mean()
    return float(base), float(tp)


def powder_pair(res):
    a = res.alpha
    j = res.cloud / np.maximum(a, 1e-6)
    edge, inter = (a > 0.2) & (a < 0.6), a > 0.97
    return float(j[edge].mean()), float(j[inter].mean())


def cap_horizon(case):
    # The inside deck starts 5 m under the camera and is thin in density, so that the rays below the horizon leave it with alpha < 0.05
    # at a steep enough angle: the row is asin(sigma * 5 m / -ln(0.95)) = 11.2 deg below the horizon.
    layers = [(1995.0, 2500.0, 0.002)] if case == "inside" else [(1700.0, 2500.0, 0.01)]
    h_cam, pitch, fov, from_bottom = {"below": (2.0, 0.0, 50.0, True), "inside": (2000.0, 0.0, 50.0, True), "above": (8000.0, -8.0, 40.0, False)}[case]
    field = pr.DensityField(R, [pr.Layer("deck", *layers[0])])
    cam = pr.Camera([0, 0, R + h_cam], 0, pitch, 3, 480, fov)
    return {"alpha": high_alpha(field, cam), "camera": {"radius": R, "h_cam": h_cam, "layers": layers, "pitch": pitch, "fov_v": fov, "from_bottom": from_bottom}}


def cap_noise_spectrum():
    xs = np.arange(0, 40000, 50.0)
    ys = np.arange(0, 12000, 1000.0)
    X, Y = np.meshgrid(xs, ys)
    f = pr.DensityField(F, pr.scene_noise_deck(pr.FbmShape(seed=1, tile_m=1e7), tau_vertical=1.0).layers)
    tau = column_tau(f, X.ravel(), Y.ravel()).reshape(X.shape)
    return {"alpha_stripes": 1.0 - np.exp(-tau), "dx": 50.0}


def cap_noise_repetition(tile=1e7):
    xs, ys = np.arange(0, 40000, 200.0), np.arange(0, 20000, 200.0)
    X, Y = np.meshgrid(xs, ys)
    f = pr.DensityField(F, pr.scene_noise_deck(pr.FbmShape(seed=1, tile_m=tile), tau_vertical=6.0).layers)
    return {"tau_map": column_tau(f, X.ravel(), Y.ravel()).reshape(X.shape), "tile_lag": 100}


CURVE = np.interp(np.linspace(0, 1, 257), [0.0, 0.3, 0.6, 1.0], [0.0, 1.0, 0.8, 0.0])


def cap_vertical_profile():
    base, top, sigma = 2000.0, 3000.0, 0.02
    layer = pr.Layer("typed", base, top, sigma, profile=lambda h: np.interp(h, np.linspace(0, 1, len(CURVE)), CURVE))
    field = pr.DensityField(F, [layer])
    r = pr.Renderer(field, pr.Sun([0, 0, 1]), light_steps=512, order=1)
    hs = np.linspace(0.05, 1.0, 20)
    tau = []
    for h in hs:
        o = np.array([[0.0, 0.0, F + base]])
        d = np.array([[0.0, 0.0, 1.0]])
        tau.append(float(r.optical_depth(o, d, tmax=np.array([h * (top - base)]), steps=512)[0]))
    return {"heights": hs, "tau_cum": np.array(tau), "curve": CURVE, "sigma_thickness": sigma * (top - base)}


def cap_type_ramp():
    types = (1500.0, 3000.0, 2500.0, 5000.0)  # base A, base B, top A, top B (type A tall at the low x, B at the high x)
    ramp = lambda x: np.clip(x / 10000.0, 0.0, 1.0)

    def shape(p):
        t = ramp(p[:, 0])
        alt = p[:, 2] - F
        lo = types[0] + (types[1] - types[0]) * t
        hi = types[2] + (types[3] - types[2]) * t
        return ((alt >= lo) & (alt <= hi)).astype(np.float64)

    field = pr.DensityField(F, [pr.Layer("ramp", 1400.0, 5100.0, 0.01, shape=shape)])
    r = pr.Renderer(field, pr.Sun([0, 0, 1]), light_steps=2048, order=1)
    xs = np.linspace(0.0, 10000.0, 6)
    base, top = [], []
    for x in xs:
        o = np.array([[x, 0.0, F + 1400.0]])
        d = np.array([[0.0, 0.0, 1.0]])
        hs = np.linspace(1400.0, 5100.0, 741)
        cum = np.array([float(r.optical_depth(o, d, tmax=np.array([h - 1400.0]), steps=max(int((h - 1400.0) / 2.5), 4))[0]) if h > 1400.0 else 0.0 for h in hs])
        tot = cum[-1]
        base.append(float(np.interp(0.001 * tot, cum, hs)))
        top.append(float(np.interp(0.999 * tot, cum, hs)))
    return {"ramp": ramp(xs), "base": np.array(base), "top": np.array(top), "types": list(types)}


def sphere_pixel_to_latlon(cam, field_top, px, py):
    """Latitude and longitude (deg) where the ray through the fractional pixel (px, py) meets the sphere of radius R + field_top."""
    half = math.tan(math.radians(cam.fov_v) * 0.5)
    aspect = cam.width / float(cam.height)
    x = ((px + 0.5) / cam.width * 2.0 - 1.0) * half * aspect
    y = (1.0 - (py + 0.5) / cam.height * 2.0) * half
    d = cam.forward + x * cam.right + y * cam.cam_up
    d = d / np.linalg.norm(d)
    t0, _ = pr.DensityField.hit_sphere(cam.pos[None, :], d[None, :], np.zeros(3), R + field_top)
    p = cam.pos + d * t0[0]
    u = p / np.linalg.norm(p)
    return math.degrees(math.asin(u[2])), math.degrees(math.atan2(u[1], u[0]))


def cap_coverage(coverage=0.4):
    field, grid = pr.scene_coverage_map(coverage, tau_vertical=12.0)
    cam = pr.Camera([0, 0, R + 1500000.0], 0, -90, 96, 96, 30)
    alpha = high_alpha(field, cam, steps=48)
    d = cam.ray_directions()
    o = np.broadcast_to(cam.pos, d.shape)
    t0, _ = pr.DensityField.hit_sphere(o, d, np.zeros(3), R + 2500.0)
    hit = ~np.isnan(t0)
    pts = o[hit] + d[hit] * t0[hit][:, None]
    u = pts / np.linalg.norm(pts, axis=1, keepdims=True)
    expected = float((pr.sphere_map_lookup(grid, u) < coverage).mean())
    inside = alpha.ravel()[hit]
    return {"alpha": inside.reshape(1, -1), "map_mean": expected}


def cap_orientation():
    lat0, lon0 = 20.0, 30.0
    c = np.array([math.cos(math.radians(lat0)) * math.cos(math.radians(lon0)), math.cos(math.radians(lat0)) * math.sin(math.radians(lon0)), math.sin(math.radians(lat0))])
    cov = lambda u: (np.degrees(np.arccos(np.clip(u @ c, -1, 1))) < 3.0).astype(np.float64)
    field = pr.DensityField(R, [pr.Layer("blob", 2200.0, 2500.0, 0.04, coverage=cov)])
    pos = c * (R + 1500000.0)
    cam = pr.Camera(pos, 0, -90, 96, 96, 30)
    alpha = high_alpha(field, cam, steps=48)
    ys, xs = np.nonzero(alpha > 0.5)
    w = alpha[ys, xs]
    px, py = float((xs * w).sum() / w.sum()), float((ys * w).sum() / w.sum())
    lat, lon = sphere_pixel_to_latlon(cam, 2500.0, px, py)
    return {"measured_latlon": [lat, lon], "true_latlon": [lat0, lon0]}


def cap_stack():
    f12 = pr.scene_two_slab(2.0, 3.0)
    f1 = pr.DensityField(F, [f12.layers[0]])
    f2 = pr.DensityField(F, [f12.layers[1]])
    cam = pr.Camera([0, 0, F + 2.0], 0, 40, 16, 16, 30)
    return {"alpha1": high_alpha(f1, cam, 256), "alpha2": high_alpha(f2, cam, 256), "alpha12": high_alpha(f12, cam, 256)}


def cap_boundary(q):
    field = pr.scene_slab(6.0, thickness=300.0, base=2000.0)
    sun = pr.Sun.at(np.array([0, 0, F]), 200, 38)
    el = np.radians(np.linspace(20, 70, 6))
    d = np.stack([np.zeros(6), np.cos(el), np.sin(el)], axis=1)
    lum = []
    for i in range(31):
        h = 1995.0 + i * (20.0 / 60.0) * 1.0  # 20 m/s at 60 frames per second: 0.333 m per frame
        o = np.broadcast_to(np.array([0.0, 0.0, F + h]), d.shape).copy()
        # The same seed every frame: the Monte Carlo noise is one fixed pattern, so that the sequence shows the field's change only.
        r = renderer(field, sun, dict(q, order=min(q["order"], 8), paths=1))
        lum.append(float(r.radiance(o, d)[0].mean()))
    return {"luminance": np.array(lum)}


def cap_phase(phase=None):
    mu = np.linspace(-1.0, 1.0, 4001)
    return {"mu": mu, "p": (phase or pr.Phase())(mu)}


def cap_silver(q, phase=None):
    near = cell_scene(q, 0, 8, phase=phase)
    far = cell_scene(q, 75, 8, phase=phase)
    return {"edge_near_sun": edge_source(near)[0], "edge_far_sun": edge_source(far)[0]}


def cap_beer(q):
    cam = pr.Camera([0, 0, F + 2.0], 0, 90, 3, 3, 10)
    sun = pr.Sun.at(cam.pos, 0, 40)
    out = []
    for tau, thick in ((1.5, 1000.0), (3.0, 2000.0)):
        r = renderer(pr.scene_slab(tau, thickness=thick), sun, q, order=1, view_steps=256)
        out.append(1.0 - float(r.render(cam).alpha[1, 1]))
    return {"T_h": out[0], "T_2h": out[1]}


def cap_light_steps(q, steps=4):
    a = cell_scene(q, 90, 50, order=1, light=steps)
    b = cell_scene(q, 90, 50, order=1, light=64)
    m = b.alpha > 0.5
    return {"sun_term": float(a.cloud[m].sum()), "sun_term_ref64": float(b.cloud[m].sum()), "steps": steps}


def cap_base_top(q):
    base, top = base_top(cell_scene(q, 90, 50))
    return {"base": base, "top": top}


def cap_powder(q):
    e, i = powder_pair(cell_scene(q, 180, 35))
    e1, i1 = powder_pair(cell_scene(q, 180, 35, order=1))
    return {"edge_j": e, "interior_j": i, "ref_dip": e / i, "single_scatter_dip": e1 / i1}


DISC_1090 = pc.DISC_1090


def shadow_scene(tau, e_deg, h=2000.0, half=500.0):
    """A square slab cloud (hard edges, 20 m thick) over flat ground, the sun in the east at elevation e: returns the renderer."""
    layer = pr.Layer("slab", h - 10.0, h + 10.0, tau / 20.0, coverage=lambda u: ((np.abs(u[:, 0] * F) < half) & (np.abs(u[:, 1] * F) < half)).astype(np.float64))
    field = pr.DensityField(F, [layer])
    sun = pr.Sun.at(np.array([0, 0, F]), 90, e_deg)
    return pr.Renderer(field, sun, view_steps=8, light_steps=64, order=1, sun_disc=24)


def cap_shadow(e_deg, h=2000.0):
    r = shadow_scene(8.0, e_deg, h)
    xs = np.arange(-1500.0 - h / math.tan(math.radians(e_deg)), 1500.0 + 1.0, 8.0)
    ys = np.arange(-800.0, 801.0, 100.0)
    X, Y = np.meshgrid(xs, ys)
    g = np.stack([X.ravel(), Y.ravel(), np.full(X.size, F)], axis=1)
    f = r.sun_transmittance(g + np.array([0, 0, 0.01]), 24).reshape(X.shape)
    dark = 1.0 - f
    cx = float((dark * X).sum() / dark.sum())
    # penumbra: the profile along x on the row y = 0 across the shadow's far (low x) edge, where the factor falls to the shadow
    row = f[len(ys) // 2]
    return {"shift_m": -cx, "height_m": h, "elevation_deg": e_deg, "x_m": xs, "profile": row, "pixel_m": 8.0}


def penumbra_profile(e_deg, h=2000.0):
    """The shadow-edge profile across the cloud's east edge (the sun side of the shadow), sampled finely, and the cloud's own edge softness."""
    r = shadow_scene(8.0, e_deg, h)
    shift = h / math.tan(math.radians(e_deg))
    edge = 500.0 - shift  # the east edge of the cloud (x = +500) falls at 500 - shift on the ground
    span = max(h * pr.SUN_ANGULAR_DIAMETER / math.sin(math.radians(e_deg)) ** 2 * 3.0, 40.0)
    xs = np.linspace(edge - span, edge + span, 257)
    g = np.stack([xs, np.zeros_like(xs), np.full(xs.shape, F)], axis=1)
    f = r.sun_transmittance(g + np.array([0, 0, 0.01]), 64)
    return xs, f


def cap_shadow_penumbra(e_deg):
    xs, f = penumbra_profile(e_deg)
    return {"x_m": xs, "profile": f, "pixel_m": float(xs[1] - xs[0]), "height_m": 2000.0, "elevation_deg": e_deg}


def cap_shadow_darkness(tau=4.0, e_deg=45.0):
    r = shadow_scene(tau, e_deg)
    core = np.array([[-2000.0 / math.tan(math.radians(e_deg)), 0.0, F + 0.01]])
    return {"shadow": float(r.sun_transmittance(core, 1)[0]), "lit": 1.0, "tau_sun": tau / math.sin(math.radians(e_deg))}


TAUS = (0.1, 1.0, 5.0, 20.0)


def cap_shadow_vs_tau(q, ground_samples=12):
    """Ground irradiance under a thick slab over the open ground's, with the diffuse floor: the reference curve."""
    out = []
    open_r = pr.Renderer(pr.DensityField(F, []), pr.Sun.at(np.array([0, 0, F]), 90, 45), light_steps=16, order=1, ground_full_samples=600)
    base = float(open_r.ground_radiance(np.array([[0.0, 0.0, F]]))[0])
    for tau in TAUS:
        layer = pr.Layer("slab", 1900.0, 2100.0, tau / 200.0)
        field = pr.DensityField(F, [layer])
        r = renderer(field, pr.Sun.at(np.array([0, 0, F]), 90, 45), dict(q, order=max(q["order"], 12)), ground_full_samples=ground_samples, sun_disc=1)
        out.append(float(r.ground_radiance(np.array([[0.0, 0.0, F]]), disc=1)[0]) / base)
    return {"taus": list(TAUS), "factors": np.array(out), "ref_factors": np.array(out)}


def reference_captures(quality="quick"):
    """{check id: capture dict} of everything the renderer can produce on the synthetic scenes."""
    q = QUALITY[quality]
    caps = {}
    for case in ("below", "inside", "above"):
        caps["R01-horizon-" + case] = cap_horizon(case)
    caps["R02-noise-spectrum"] = cap_noise_spectrum()
    caps["R02-noise-repetition"] = cap_noise_repetition()
    caps["R03-vertical-profile"] = cap_vertical_profile()
    caps["R03-type-ramp"] = cap_type_ramp()
    caps["R04-coverage-fraction"] = cap_coverage()
    caps["R04-map-orientation"] = cap_orientation()
    caps["R05-stack-alpha"] = cap_stack()
    caps["R05-boundary-crossing"] = cap_boundary(q)
    caps["R06-phase-integral"] = cap_phase()
    caps["R06-silver-lining"] = cap_silver(q)
    caps["R07-beer-lambert"] = cap_beer(q)
    caps["R07-light-steps"] = cap_light_steps(q)
    caps["R08-base-top"] = cap_base_top(q)
    caps["R09-powder"] = cap_powder(q)
    caps["R11-shadow-displacement"] = cap_shadow(45.0)
    caps["R11-shadow-penumbra"] = cap_shadow_penumbra(45.0)
    caps["R11-shadow-darkness"] = cap_shadow_darkness()
    caps["R12-shadow-vs-tau"] = cap_shadow_vs_tau(q)
    return caps


# --------------------------------------------------------------------------------------------------------------------------------
# Captures on disk, the table
# --------------------------------------------------------------------------------------------------------------------------------
def write_capture(folder, data):
    os.makedirs(folder, exist_ok=True)
    meta = {}
    for k, v in data.items():
        if isinstance(v, np.ndarray):
            np.save(os.path.join(folder, k + ".npy"), v)
        else:
            meta[k] = v
    with open(os.path.join(folder, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=1, default=float)


def reference_figures(ref_caps):
    """The figures of the reference column that other columns are judged against: {check id: {key: value}}."""
    out = {}
    c = ref_caps.get("R06-silver-lining")
    if c:
        out["R06-silver-lining"] = {"ref_ratio": float(c["edge_near_sun"]) / float(c["edge_far_sun"])}
    c = ref_caps.get("R08-base-top")
    if c:
        out["R08-base-top"] = {"ref_ratio": float(c["top"]) / float(c["base"])}
    c = ref_caps.get("R09-powder")
    if c:
        out["R09-powder"] = {"ref_dip": float(c["edge_j"]) / float(c["interior_j"])}
    c = ref_caps.get("R12-shadow-vs-tau")
    if c:
        out["R12-shadow-vs-tau"] = {"ref_factors": np.asarray(c["ref_factors"])}
    return out


def run_column(spec, caps, ref_figures=None):
    """{check id: Measure} for the captures of one column; figures of the reference column fill what the capture does not carry."""
    out = {}
    for cid, data in caps.items():
        s = spec[cid]
        data = dict(data)
        for k, v in (ref_figures or {}).get(cid, {}).items():
            data.setdefault(k, v)
        out[cid] = pc.run_check(s["function"], data, s)
    return out


def load_column(folder):
    caps = {}
    if folder and os.path.isdir(folder):
        for name in sorted(os.listdir(folder)):
            full = os.path.join(folder, name)
            if os.path.isdir(full):
                caps[name] = pc.load_capture(full)
    return caps


def fmt(m):
    if m is None:
        return "-"
    return ("%.4g" % m.value if m.value is not None else "nan") + ("" if m.ok else "!")


def threshold_text(s):
    t = s.get("threshold", {})
    unit = s.get("unit", "")
    parts = []
    if "max" in t:
        parts.append("<= %g" % t["max"])
    if "min" in t:
        parts.append(">= %g" % t["min"])
    if "band" in t:
        parts.append("%g..%g" % tuple(t["band"]))
    if "ref_rel" in t:
        parts.append("+-%g%% of ref" % (100 * t["ref_rel"]))
    if "over" in t:
        parts.append("<= ref + %g%%" % (100 * t["over"]))
    if "rel" in t:
        parts.append("+-%g%% + %g px of the analytic width" % (100 * t["rel"], t.get("pixels", 0)))
    if "seconds" in t:
        parts.append("0 errors in %g s" % t["seconds"])
    return (" ".join(parts) + (" " + unit if unit else "")).strip() or "-"


def build_table(spec, columns):
    """The fixed result table: one row per check with the three columns, the threshold and the verdict of the 'ours' column (or of
    the reference column where no 'ours' data exists yet)."""
    figures = reference_figures(columns.get("reference", {}))
    measured = {name: run_column(spec, caps, None if name == "reference" else figures) for name, caps in columns.items()}
    lines = ["%-26s %12s %12s %12s  %-26s %s" % ("check", "reference", "TVC", "ours", "threshold", "PASS")]
    for cid, s in spec.items():
        ref, tvc, ours = (measured.get(n, {}).get(cid) for n in ("reference", "tvc", "ours"))
        verdict = ours if ours is not None else ref
        flag = "-" if verdict is None else ("PASS" if verdict.ok else "FAIL")
        lines.append("%-26s %12s %12s %12s  %-26s %s" % (cid, fmt(ref), fmt(tvc), fmt(ours), threshold_text(s), flag))
    return "\n".join(lines), measured


# --------------------------------------------------------------------------------------------------------------------------------
# Synthetic data for the functions that need a game or a player: a good record (must pass) and a defective one (must fail)
# --------------------------------------------------------------------------------------------------------------------------------
def synthetic_pairs():
    """{check id: (good data, defect data)} built from the analytic expectation of each check."""
    pairs = {}
    # The shadow edge of a disc of 9.3 mrad at h = 2000 m, e = 45 deg: the profile is the disc's occlusion curve.
    h, e = 2000.0, 45.0
    full = h * pr.SUN_ANGULAR_DIAMETER / math.sin(math.radians(e)) ** 2
    u = np.linspace(-1.0, 1.0, 401)
    prof = 1.0 - (np.arccos(u) - u * np.sqrt(1.0 - u * u)) / math.pi
    x = u * full * 0.5
    pairs["R11-shadow-penumbra"] = (dict(x_m=x, profile=prof, pixel_m=1.0, height_m=h, elevation_deg=e), dict(x_m=x * 3.0, profile=prof, pixel_m=1.0, height_m=h, elevation_deg=e))
    pairs["R03-vertical-profile"] = None  # covered by the renderer capture
    frames = np.arange(60)
    pairs["R10-cache-direct"] = (dict(luminance=1.0 - np.exp(-frames / 3.0) * 0.4 + 0 * frames), dict(luminance=1.0 - np.exp(-frames / 30.0) * 0.4))
    amb = np.arange(600)
    pairs["R10-cache-ambient"] = (dict(luminance=1.0 - np.exp(-amb / 40.0) * 0.5), dict(luminance=1.0 - np.exp(-amb / 400.0) * 0.5))
    pairs["R10-cache-jump"] = (dict(luminance=1.0 + 0.002 * np.arange(50)), dict(luminance=np.concatenate([np.ones(25), 1.3 * np.ones(25)])))
    pairs["R11-shadow-receivers"] = (dict(factors={"terrain": 0.30, "vessel": 0.31, "kerbal": 0.295}), dict(factors={"terrain": 0.30, "vessel": 0.52, "kerbal": 0.295}))
    pairs["R13-ocean-land"] = (dict(water=0.31, land=0.30), dict(water=0.45, land=0.30))
    pairs["R13-disc-shadow"] = (dict(measured_latlon=[10.2, 20.1], true_latlon=[10.0, 20.0]), dict(measured_latlon=[13.0, 20.0], true_latlon=[10.0, 20.0]))
    err = 0.5 ** (np.arange(40) / 2.0)
    pairs["R14-convergence"] = (dict(error_to_reference=err, cycle_frames=12), dict(error_to_reference=0.5 ** (np.arange(40) / 12.0), cycle_frames=4))
    pairs["R14-ghost"] = ({"ghost_move.dlss_K": 0.003, "trail_px.dlss_K": 0.05}, {"ghost_move.dlss_K": 0.02, "trail_px.dlss_K": 0.05})
    pairs["R14-origin-shift"] = (dict(before=0.500, after=0.505), dict(before=0.5, after=0.56))
    pairs["R15-cost"] = (dict(ms=1.0, tvc_ms=0.95), dict(ms=1.4, tvc_ms=0.95))
    pairs["R16-mv-match"] = (dict(mv_match=0.99), dict(mv_match=0.9))
    pairs["R16-fg-duplicates"] = (dict(duplicates=11, baseline=10), dict(duplicates=14, baseline=10))
    pairs["R17-depth-edge"] = (dict(cloud_edge_x=np.arange(10) + 0.3, depth_edge_x=np.arange(10)), dict(cloud_edge_x=np.arange(10) + 3.0, depth_edge_x=np.arange(10)))
    alt = np.arange(200000.0, 260001.0, 1000.0)
    pairs["R18-handover"] = (dict(altitude_m=alt, luminance=1.0 - 0.01 * np.arange(len(alt))), dict(altitude_m=alt, luminance=np.concatenate([np.ones(30), 0.6 * np.ones(31)])))
    wind, vessel = [10.0, 0.0], [0.0, 0.0]
    ang = math.degrees(math.atan2(10.0, 12.0))
    pairs["R19-rain-angle"] = (dict(fall_speed=12.0, wind=wind, vessel=vessel, angle_deg=ang + 1.0), dict(fall_speed=12.0, wind=wind, vessel=vessel, angle_deg=ang + 9.0))
    pairs["R19-density-gate"] = (dict(coverage=[0.1, 0.4, 0.7], density=[0.0, 0.3, 0.9]), dict(coverage=[0.1, 0.4, 0.7], density=[0.5, 0.3, 0.9]))
    for cid in ("R20-droplet-build", "R20-droplet-dry"):
        pairs[cid] = (dict(measured_s=21.0, config_s=20.0), dict(measured_s=30.0, config_s=20.0))
    pairs["R21-wet-diffuse"] = (dict(wet_over_dry=0.41, wet_diffuse=0.4), dict(wet_over_dry=0.6, wet_diffuse=0.4))
    pairs["R21-warp-catchup"] = (dict(jump=0.70, stepwise=0.71), dict(jump=0.5, stepwise=0.71))
    pairs["R22-lightning-delay"] = (dict(distance_m=3432.0, delay_s=10.2), dict(distance_m=3432.0, delay_s=4.0))
    pairs["R22-lightning-flash"] = (dict(flash_lum=3.0, base_lum=1.0), dict(flash_lum=1.2, base_lum=1.0))
    pairs["R22-lightning-gate"] = (dict(bolts_above_warp=0, bolts_below_coverage=0), dict(bolts_above_warp=2, bolts_below_coverage=0))
    d = np.array([10000.0, 30000.0, 60000.0])
    ref = pc.aerial_perspective_reference(1.0, 0.55, d, 40000.0)
    pairs["R23-aerial"] = (dict(l_near=1.0, l_sky=0.55, d_m=d, visual_range_m=40000.0, luminance=ref * 1.05), dict(l_near=1.0, l_sky=0.55, d_m=d, visual_range_m=40000.0, luminance=np.ones(3)))
    pairs["R24-exposure"] = (dict(ratio_fixed=2.0, ratio_auto=2.03), dict(ratio_fixed=2.0, ratio_auto=2.4))
    pairs["R25-rt-ratio"] = (dict(ray_factor=[0.5, 0.4, 0.3], screen_factor=[0.52, 0.41, 0.3]), dict(ray_factor=[0.5, 0.4, 0.3], screen_factor=[0.7, 0.41, 0.3]))
    pairs["R26-robust-warp"] = (dict(errors=0, seconds=60), dict(errors=3, seconds=60))
    pairs["R26-robust-revert"] = (dict(first_frame_valid=1), dict(first_frame_valid=0))
    pairs["R26-robust-leaks"] = (dict(leaked_objects=0), dict(leaked_objects=4))
    pairs["R26-robust-precision"] = (dict(jitter_px=0.1), dict(jitter_px=0.6))
    pairs["R08-ambient-floor"] = (dict(ambient=0.08, sun_share=1.0), dict(ambient=0.01, sun_share=1.0))
    pairs["R05-boundary-crossing"] = (dict(luminance=1.0 - 0.004 * np.arange(30)), dict(luminance=np.concatenate([np.ones(15), 0.5 * np.ones(15)])))
    return {k: v for k, v in pairs.items() if v is not None}


class Table(object):
    def __init__(self):
        self.rows = []
        self.ok = True

    def add(self, name, value, limit, passed, note=""):
        self.rows.append((name, value, limit, passed, note))
        self.ok = self.ok and passed

    def text(self):
        lines = ["%-46s %14s  %-22s %s" % ("self-test", "value", "limit", "verdict")]
        for name, value, limit, passed, note in self.rows:
            lines.append("%-46s %14s  %-22s %s %s" % (name, value, limit, "PASS" if passed else "FAIL", note))
        return "\n".join(lines)


def selftest(quick=True, fast=False):
    t = Table()
    q = QUALITY["quick" if quick else "full"]
    spec = pc.load_spec()

    # Beer-Lambert on the rendered slab: T(2h) = T(h)^2 and T = exp(-tau).
    cap = cap_beer(q)
    m = pc.run_check("beer_lambert_slab", cap, dict(spec["R07-beer-lambert"], threshold={"max": 0.01}))
    t.add("Beer-Lambert T(2h) = T(h)^2", "%.5f" % m.value, "<= 0.01", m.ok, "(T(h) %.5f, exp(-1.5) %.5f)" % (cap["T_h"], math.exp(-1.5)))

    # HG normalisation: the integral of the reference's phase function and its lobes; the sampler reproduces the mean cosine.
    ph = pr.Phase()
    integ = ph.integral()
    t.add("HG normalisation (dual lobe)", "%.6f" % integ, "1 +- 0.01", abs(integ - 1.0) <= 0.01)
    for g in (0.85, -0.3, 0.0):
        v = float(2 * math.pi * pr.trapz(pr.Phase.hg(np.linspace(-1, 1, 100001), g), np.linspace(-1, 1, 100001)))
        t.add("HG normalisation g = %g" % g, "%.6f" % v, "1 +- 0.01", abs(v - 1.0) <= 0.01)
    mu = ph.sample_cos(np.random.default_rng(1), 400000)
    expect = ph.w1 * ph.g1 + (1 - ph.w1) * ph.g2
    t.add("phase sampler mean cosine = w1 g1 + w2 g2", "%.4f" % mu.mean(), "%.4f +- 0.01" % expect, abs(mu.mean() - expect) <= 0.01)

    # Horizon: the visible horizon row against the analytic dip angle acos(R / (R + h)) (distance sqrt(2Rh + h^2)).
    for h in (2000.0, 8000.0):
        field = pr.DensityField(R, [])
        cam = pr.Camera([0, 0, R + h], 0, -5.0, 8, 240, 40.0)
        res = pr.Renderer(field, pr.Sun.at(cam.pos, 0, 40), view_steps=4, light_steps=4, order=1).render(cam)
        col = res.ground[:, 4]
        first_ground = int(np.nonzero(col)[0][0])  # the first row from the top that sees the ground
        dip = -math.degrees(math.acos(R / (R + h)))
        analytic_row = cam.pixel_row_of_elevation(dip)
        # the horizon lies between the last sky row and the first ground row, at the coordinate first_ground - 0.5
        err = abs((first_ground - 0.5) - analytic_row)
        dist = math.sqrt(2 * R * h + h * h)
        t.add("horizon row at h = %d m (distance %.0f km)" % (h, dist / 1000.0), "%.2f px" % err, "<= 1 px", err <= 1.0)

    # The horizon check on the cloud shell itself (the three camera heights of the table).
    for case in ("below", "inside", "above"):
        cap = cap_horizon(case)
        m = pc.run_check("horizon_row", cap, dict(spec["R01-horizon-" + case], threshold={"max": 1.0}))
        t.add("shell horizon row, camera %s" % case, "%.3f px" % m.value, "<= 1 px", m.ok, "(row %.2f, analytic %.2f)" % (m.detail["row"], m.detail["analytic_row"]))

    # Light march: the sun term of a homogeneous slab follows exp(-tau / sin e).
    m = pc.run_check("shadow_darkness", cap_shadow_darkness(4.0, 45.0), dict(spec["R11-shadow-darkness"], threshold={"max": 0.01}))
    t.add("sun transmittance = exp(-tau / sin e)", "%.5f" % m.value, "<= 0.01", m.ok, "(factor %.5f, exp %.5f)" % (m.detail["factor"], m.reference))

    if not fast:
        # Convergence: the reference against a 4x longer one (view and light steps) on the cell.
        # The reference's settings: 128 view steps (a step of 13 m for a mean free path of 50 m), 64 light steps, stride 1.
        rq = dict(q, steps=128, light=64, stride=1, paths=2, w=32)
        lq = dict(rq, steps=512, light=256)
        base = cell_scene(dict(rq, order=1), 90, 50)
        longer = cell_scene(dict(lq, order=1), 90, 50)
        mask = base.alpha > 0.5
        d2 = abs(base.cloud[mask].sum() - longer.cloud[mask].sum()) / longer.cloud[mask].sum()
        t.add("convergence, 4x longer reference, single scattering", "%.5f" % d2, "<= 0.01", d2 <= 0.01)
        order = 16 if quick else 32
        base = cell_scene(dict(rq, order=order), 90, 50)
        longer = cell_scene(dict(lq, order=order), 90, 50)
        d1 = abs(base.cloud[mask].sum() - longer.cloud[mask].sum()) / longer.cloud[mask].sum()
        t.add("convergence, 4x longer reference, order %d" % order, "%.5f" % d1, "<= 0.01", d1 <= 0.01)

    # The horizon check against the closed form: the dip acos(R / (R + h)) of the ground (below), asin(sigma dh / -ln 0.95) (inside)
    # or the dip of the deck's outer shell (above).
    for case, h, bound, pitch, fov in (("below", 2.0, 0.0, 0.0, 50.0), ("inside", 2000.0, 0.0, 0.0, 50.0), ("above", 8000.0, 2500.0, -8.0, 40.0)):
        cap = cap_horizon(case)
        cam = pr.Camera([0, 0, R + h], 0, pitch, 3, 480, fov)
        if case == "inside":
            closed = cam.pixel_row_of_elevation(-math.degrees(math.asin(0.002 * 5.0 / -math.log(0.95))))
        else:
            closed = cam.pixel_row_of_elevation(-math.degrees(math.acos((R + bound) / (R + h))))
        got = pc.crossing_row(cap["alpha"][:, 1], 0.05, case != "above")
        t.add("shell horizon row, camera %s, against the closed form" % case, "%.2f px" % abs(got - closed), "<= 1 px", abs(got - closed) <= 1.0, "(row %.2f, closed %.2f)" % (got, closed))

    # Negative control: a wrong phase function (forward lobe with the wrong sign) is found by the phase checks.
    if not fast:
        good = pc.run_check("silver_lining", cap_silver(q), spec["R06-silver-lining"])
        capbad = cap_silver(q, phase=pr.WrongPhase())
        capbad["ref_ratio"] = good.value
        bad = pc.run_check("silver_lining", capbad, spec["R06-silver-lining"])
        t.add("planted wrong phase: silver lining check on the good copy", "%.2f" % good.value, ">= 2.0", good.ok)
        t.add("planted wrong phase: silver lining check FAILS on the defect", "%.2f vs ref %.2f" % (bad.value, good.value), "+-15 % of ref (fail)", not bad.ok)
    scaled = cap_phase()
    scaled["p"] = scaled["p"] * 1.3
    mi = pc.run_check("phase_integral", scaled, spec["R06-phase-integral"])
    t.add("planted un-normalised phase: integral check FAILS", "%.4f" % mi.detail["integral"], "1 +- 0.01 (fail)", not mi.ok)
    mi0 = pc.run_check("phase_integral", cap_phase(), spec["R06-phase-integral"])
    t.add("phase integral check on the reference phase", "%.5f" % mi0.detail["integral"], "1 +- 0.01", mi0.ok)
    # Every check function without a renderer capture: a good record passes and a defective one fails.
    for cid, (good_data, bad_data) in sorted(synthetic_pairs().items()):
        s = spec[cid]
        g = pc.run_check(s["function"], good_data, s)
        b = pc.run_check(s["function"], bad_data, s)
        t.add("function %s: good record / defect" % cid, "%.4g / %.4g" % (g.value, b.value), "pass / fail", g.ok and not b.ok)
    # A light march with one step (a coarse pass) is found by the light-step check.
    if not fast:
        coarse = pc.run_check("light_step_error", cap_light_steps(q, 1), spec["R07-light-steps"])
        fine = pc.run_check("light_step_error", cap_light_steps(q, 8), spec["R07-light-steps"])
        t.add("planted coarse light march (1 step): check FAILS", "%.3f" % coarse.value, "<= 0.10 (fail)", not coarse.ok)
        t.add("8 light steps: check", "%.4f" % fine.value, "<= 0.10", fine.ok)
    # A repeating noise tile is found by the repetition check.
    rep = pc.run_check("noise_repetition", cap_noise_repetition(tile=2.0e4), spec["R02-noise-repetition"])
    norep = pc.run_check("noise_repetition", cap_noise_repetition(), spec["R02-noise-repetition"])
    t.add("planted repeating noise tile (20 km): check FAILS", "%.3f" % rep.value, "> 0.15 (fail)", not rep.ok)
    t.add("non-repeating noise: repetition check", "%.3f" % norep.value, "<= 0.15", norep.ok)
    return t


# --------------------------------------------------------------------------------------------------------------------------------
# The completeness test of checks.json
# --------------------------------------------------------------------------------------------------------------------------------
# The rows of the parity table (tvc-parity.md 4.3), with the P-ids each row names.
PLAN_ROWS = {
    "P01,P25 shell and horizon": ["P01", "P25"],
    "P02,P04,P11,P12 noise and erosion": ["P02", "P04", "P11", "P12"],
    "P06-P09 type table and curves": ["P06", "P07", "P08", "P09"],
    "P13 coverage map": ["P13"],
    "P20 stacking": ["P20"],
    "P26-P27 phase": ["P26", "P27"],
    "P28-P29 light march and step": ["P28", "P29"],
    "P30,P32,P34 sky light and dark base": ["P30", "P32", "P34"],
    "P33 powder": ["P33"],
    "P40-P42 light cache": ["P40", "P41", "P42"],
    "P44-P47 shadows": ["P44", "P45", "P46", "P47"],
    "P46 darkness law": ["P46"],
    "P48-P49 ocean and scaled": ["P48", "P49"],
    "P52-P53,P55 temporal": ["P52", "P53", "P55"],
    "P52 cost": ["P52"],
    "P59-P60 upscaler": ["P59", "P60"],
    "P62 depth": ["P62"],
    "P64,P67,P68 hand-over": ["P64", "P67", "P68"],
    "P79-P81 rain, snow": ["P79", "P80", "P81"],
    "P82 droplets": ["P82"],
    "P83 wet surfaces": ["P83"],
    "P84 lightning": ["P84"],
    "P87 Scatterer": ["P87"],
    "P89 exposure": ["P89"],
    "P93 RT pack": ["P93"],
    "P95-P99 settings and robustness": ["P95", "P96", "P97", "P98", "P99"],
}
SCENES = {"uniform-deck", "slab", "single-cloud", "plane-terrain", "two-slab", "mountain-silhouette", "altitude-sweep"}
PACKAGES = {"T%02d" % i for i in range(1, 12)}
KINDS = {"analytic", "renderer", "band", "gate"}


def verify():
    """Completeness of checks.json: returns the list of problems (empty = complete)."""
    with open(pc.CHECKS_PATH, encoding="utf-8") as f:
        doc = json.load(f)
    problems = []
    numbers = {n["id"]: n for n in doc["numbers"]}
    for n in numbers.values():
        for key in ("name", "value", "kind", "source"):
            if key not in n:
                problems.append("number %s lacks %s" % (n.get("id"), key))
        if n.get("kind") not in ("analytic", "published", "measured", "gate"):
            problems.append("number %s has the kind %s" % (n.get("id"), n.get("kind")))
    tvc_text = open(os.path.join(os.path.dirname(pc.CHECKS_PATH), "tvc-capture.md"), encoding="utf-8").read()
    seen, rows, pids = set(), set(), set()
    for c in doc["checks"]:
        cid = c.get("id", "?")
        if cid in seen:
            problems.append("duplicate check id " + cid)
        seen.add(cid)
        for key in ("row", "function", "title", "pids", "reference", "scene", "capture", "package", "threshold", "thresholdSource", "tvc"):
            if key not in c:
                problems.append("%s lacks %s" % (cid, key))
        if c.get("function") not in pc.CHECKS:
            problems.append("%s names the function %s, which does not exist in parity_checks.py" % (cid, c.get("function")))
        if c.get("reference") not in KINDS:
            problems.append("%s has the reference kind %s" % (cid, c.get("reference")))
        if c.get("scene") not in SCENES:
            problems.append("%s has the scene %s" % (cid, c.get("scene")))
        if c.get("package") not in PACKAGES:
            problems.append("%s has the owner %s" % (cid, c.get("package")))
        if "proposed" in json.dumps(c).lower().replace("[proposed] kept", ""):
            problems.append("%s still carries a [proposed] marker" % cid)
        source = str(c.get("thresholdSource", ""))
        if source.startswith("N") and source[:3] in numbers or source.startswith("plan") or source.startswith("analytic") or source.startswith("same") or source.startswith("C21"):
            pass
        else:
            problems.append("%s names no source for its threshold: %s" % (cid, source))
        tvc = c.get("tvc")
        if tvc and tvc != "none" and tvc not in tvc_text:
            problems.append("%s names the TVC view %s, which tvc-capture.md does not list" % (cid, tvc))
        rows.add(c.get("row"))
        pids.update(c.get("pids", []))
    for row, ids in PLAN_ROWS.items():
        if row not in rows:
            problems.append("the plan row '%s' has no check" % row)
    for row in rows:
        if row not in PLAN_ROWS:
            problems.append("the check row '%s' is not a row of the plan" % row)
    for row, ids in PLAN_ROWS.items():
        for pid in ids:
            if pid not in pids:
                problems.append("the P-id %s of the row '%s' is in no check" % (pid, row))
    for fn in pc.CHECKS:
        if fn not in {c.get("function") for c in doc["checks"]}:
            problems.append("the function %s has no check" % fn)
    return problems


# --------------------------------------------------------------------------------------------------------------------------------
# The first reference run: the figures behind checks.json
# --------------------------------------------------------------------------------------------------------------------------------
def numbers():
    q = QUALITY["full"]
    print("reference run, quality", q)
    t0 = time.time()
    near = cell_scene(q, 0, 8)
    far = cell_scene(q, 75, 8)
    en, nn = edge_source(near)
    ef, nf = edge_source(far)
    print("silver lining: edge source near the sun (8 deg) %.4f (%d px), 75 deg %.4f (%d px), ratio %.2f; phase ratio %.1f"
          % (en, nn, ef, nf, en / ef, pr.Phase()(math.cos(math.radians(8))) / pr.Phase()(math.cos(math.radians(75)))))
    b, tp = base_top(cell_scene(q, 90, 50))
    print("cumulus cell, sun 50 deg from the side: base %.4f top %.4f top/base %.2f" % (b, tp, tp / b))
    b1, t1 = base_top(cell_scene(q, 90, 50, order=1))
    print("  single scattering only: top/base %.2f" % (t1 / b1))
    e, i = powder_pair(cell_scene(q, 180, 35))
    e1, i1 = powder_pair(cell_scene(q, 180, 35, order=1))
    print("powder: edge/interior source %.3f (multiple), %.3f (single scattering)" % (e / i, e1 / i1))
    for steps in (1, 2, 4, 8, 16, 32):
        c = cap_light_steps(q, steps)
        print("light steps %2d: sun term error against 64 steps %.4f" % (steps, abs(c["sun_term"] - c["sun_term_ref64"]) / c["sun_term_ref64"]))
    ref = cell_scene(q, 90, 50, order=1, light=64)
    r256 = cell_scene(q, 90, 50, order=1, light=256)
    m = ref.alpha > 0.5
    print("64 vs 256 light steps: %.5f" % (abs(ref.cloud[m].sum() - r256.cloud[m].sum()) / r256.cloud[m].sum()))
    prev = None
    for order in (1, 2, 4, 8, 16, 32, 64, 128):
        res = cell_scene(dict(q, order=order), 90, 50, order=order)
        mm = res.alpha > 0.5
        v = float(res.cloud[mm].mean())
        print("order %3d: mean cloud radiance %.4f%s" % (order, v, "" if prev is None else "  (change %.3f)" % ((v - prev) / prev)))
        prev = v
    for tile in (1e7, 2e4):
        vals = []
        for seed in range(1, 9):
            xs, ys = np.arange(0, 40000, 200.0), np.arange(0, 20000, 200.0)
            X, Y = np.meshgrid(xs, ys)
            f = pr.DensityField(F, pr.scene_noise_deck(pr.FbmShape(seed=seed, tile_m=tile), tau_vertical=6.0).layers)
            vals.append(pc.autocorr(column_tau(f, X.ravel(), Y.ravel()).reshape(X.shape), 100))
        print("autocorrelation at 20 km, tile %g m, seeds 1-8: min %.3f max %.3f" % (tile, min(vals), max(vals)))
    sl = []
    for seed in (1, 2, 3):
        xs, ys = np.arange(0, 40000, 50.0), np.arange(0, 12000, 1000.0)
        X, Y = np.meshgrid(xs, ys)
        f = pr.DensityField(F, pr.scene_noise_deck(pr.FbmShape(seed=seed, tile_m=1e7), tau_vertical=1.0).layers)
        a = 1 - np.exp(-column_tau(f, X.ravel(), Y.ravel()).reshape(X.shape))
        e = [1 / 2400.0, 1 / 1200.0, 1 / 600.0, 1 / 300.0]
        sl.append([pc.spectrum_slope(a, 50.0, e[k], e[k + 1]) for k in range(3)])
    print("noise spectrum slopes (3 octave bands 2.4 km to 300 m), seeds 1-3: min %s max %s" % (np.round(np.min(sl, axis=0), 2), np.round(np.max(sl, axis=0), 2)))
    print("disc edge 10-90 share of the full width: %.4f" % DISC_1090)
    print("done in %.0f s" % (time.time() - t0))


def images(folder):
    """Natural and aid views of the synthetic scenes."""
    os.makedirs(folder, exist_ok=True)
    q = dict(QUALITY["full"], w=96)
    scenes = {}
    cam = pr.Camera([0, -8000.0, F + 2000.0], 0, 0, 96, 72, 16)
    scenes["single-cloud"] = (pr.scene_single_cloud(), cam, pr.Sun.at(cam.pos, 90, 50))
    cam = pr.Camera([0, 0, R + 2.0], 0, 25, 96, 72, 60)
    scenes["uniform-deck"] = (pr.scene_uniform_deck(1700.0, 2500.0, 8.0), cam, pr.Sun.at(cam.pos, 200, 38))
    cam = pr.Camera([0, 0, F + 2.0], 0, 40, 96, 72, 60)
    scenes["two-slab"] = (pr.scene_two_slab(2.0, 3.0), cam, pr.Sun.at(cam.pos, 200, 45))
    cam = pr.Camera([0, 0, F + 600.0], 0, -45, 96, 72, 50)
    scenes["plane-terrain"] = (pr.scene_terrain_shadow(4.0, 200.0, 2000.0, edge_x=300.0), cam, pr.Sun.at(cam.pos, 90, 40))
    cam = pr.Camera([0, 0, R + 8000.0], 0, -8, 96, 72, 40)
    scenes["altitude-8km"] = (pr.scene_uniform_deck(2200.0, 2500.0, 6.0), cam, pr.Sun.at(cam.pos, 200, 30))
    cam = pr.Camera([0, 0, F + 100.0], 0, 4, 96, 72, 50)
    mount = [(np.array([0.0, 30000.0, F - 30000.0 + 900.0]), 30000.0)]
    scenes["mountain-silhouette"] = (pr.scene_slab(5.0, 600.0, 1500.0), cam, pr.Sun.at(cam.pos, 160, 35), mount)
    for name, sc in scenes.items():
        field, camera, sun = sc[0], sc[1], sc[2]
        occ = sc[3] if len(sc) > 3 else None
        res = renderer(field, sun, dict(q, order=24, paths=2, steps=96)).render(camera, occluders=occ)
        pr.save_natural(os.path.join(folder, name + "-natural.png"), res)
        pr.save_aid(os.path.join(folder, name + "-aid.png"), res)
        print(name, "mean lum %.3f mean alpha %.3f" % (res.lum.mean(), res.alpha.mean()))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("selftest")
    p.add_argument("--quick", action="store_true")
    p.add_argument("--fast", action="store_true", help="skip the renderings of the cloud cell (convergence, silver lining, light steps)")
    p = sub.add_parser("reference")
    p.add_argument("folder")
    p.add_argument("--quality", default="quick", choices=list(QUALITY))
    p = sub.add_parser("table")
    p.add_argument("--reference")
    p.add_argument("--tvc")
    p.add_argument("--ours")
    sub.add_parser("numbers")
    sub.add_parser("verify")
    p = sub.add_parser("images")
    p.add_argument("folder")
    a = ap.parse_args(argv)
    if a.cmd == "selftest":
        t = selftest(a.quick or a.fast, a.fast)
        print(t.text())
        print("self-test: %s" % ("PASSED" if t.ok else "FAILED"))
        return 0 if t.ok else 1
    if a.cmd == "reference":
        caps = reference_captures(a.quality)
        for cid, data in caps.items():
            write_capture(os.path.join(a.folder, cid), data)
        print("wrote %d captures to %s" % (len(caps), a.folder))
        return 0
    if a.cmd == "table":
        spec = pc.load_spec()
        text, measured = build_table(spec, {"reference": load_column(a.reference), "tvc": load_column(a.tvc), "ours": load_column(a.ours)})
        print(text)
        shown = measured.get("ours") or measured.get("reference") or {}
        return 0 if all(m.ok for m in shown.values()) else 1
    if a.cmd == "numbers":
        numbers()
        return 0
    if a.cmd == "verify":
        problems = verify()
        for p in problems:
            print("PROBLEM  " + p)
        print("checks.json: %s" % ("COMPLETE" if not problems else "%d problems" % len(problems)))
        return 0 if not problems else 1
    if a.cmd == "images":
        images(a.folder)
        return 0
    return 2


if __name__ == "__main__":
    sys.exit(main())
