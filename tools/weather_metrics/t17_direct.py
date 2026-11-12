"""T17: why is the single scattering ("direct only") of the deep storm cell lower than the T11 reference's order 1? An offline CPU model of the cell's
single scattering (the view march of the layer pass and LightTau/powder of ReDefinitionAtmospheresCloudLight.cginc, grey light), with every term switchable
one at a time. Needs the GPU outputs of the T15-split series (the 'do' jobs) and the reference renders (build/parity-ref-holdout/<cell>[-direct].npz).

    python tools/weather_metrics/t17_direct.py decompose [--gpu <T15-split-scene folder>] [--ref <parity-ref-holdout folder>]
    python tools/weather_metrics/t17_direct.py modes <ref folders>   # powder applied to everything / all but the single scattering / nowhere, all cells
    python tools/weather_metrics/t17_direct.py sensitivity <ref folders>
    python tools/weather_metrics/t17_direct.py gpu-jobs <folder>     # the player input of the one GPU sweep; gpu-read <folder with the -r.npy planes>
"""
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import light as Lt  # noqa: E402
from weather_metrics import light_cpu as C  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import t14  # noqa: E402

R = pr.KERBIN_RADIUS
SUN_E = math.pi
W = 48
# The shipped single-scattering model, as the T15 'do' jobs run it (octaves 1, no diffusion).
SHIPPED = dict(steps=4, ratio=2.0, cap=4000.0, powder=0.8075, powder_len=400.0, albedo=0.999, g1=0.85, g2=-0.30, w1=0.92, view_steps=128, sun_col=SUN_E,
               offset=0.5, tau_mode="taps", tau_scale=1.0)


def storm_cell(spec):
    name, az, el, radius, sigma = spec
    alt = t14.centre_alt(spec)
    rv = 0.7 * radius
    return C.Cell(centre_alt=alt, radius=radius, vertical=rv, edge=0.35, sigma=sigma, inner=alt - 1.05 * rv, outer=alt + 1.05 * rv)


def hg(mu, g):
    return (1.0 - g * g) / (4.0 * math.pi * np.maximum(1.0 + g * g - 2.0 * g * mu, 1e-12) ** 1.5)


def sun_tau(cell, pc, sun, o):
    """The optical depth from the points towards the sun for the option set o."""
    if o["tau_mode"] == "exact":
        # A uniform midpoint rule with `steps` steps over the whole chord of both slots, no cap: the reference's light_steps rule.
        return C.light_tau(cell, pc, sun, int(o["steps"]), 1.0, 1e12) * o["tau_scale"]
    return C.light_tau(cell, pc, sun, int(o["steps"]), o["ratio"], o["cap"]) * o["tau_scale"]


def render_direct(cell, cam, sun, **over):
    """Per pixel: (alpha, single scattering radiance). The view march is the layer pass's (count = ceil(len / 1 m) capped at view_steps, samples at the
    centre of the equal steps), the source function is albedo * sun * Phase * exp(-tau) * powder."""
    o = dict(SHIPPED)
    o.update(over)
    dirs = cam.ray_directions()
    n = dirs.shape[0]
    org = np.broadcast_to(cam.pos, (n, 3)).copy()
    a0, a1, b0, b1 = cell.spans(org, dirs)
    sun = np.asarray(sun, dtype=np.float64)
    mu = dirs @ sun
    phase = o["w1"] * hg(mu, o["g1"]) + (1.0 - o["w1"]) * hg(mu, o["g2"])
    rad = np.zeros(n)
    T = np.ones(n)
    tau_sum = np.zeros(n)
    for (s0, s1) in ((a0, a1), (b0, b1)):
        ln = s1 - s0
        valid = ln > 0
        count = np.clip(np.ceil(ln / 1.0), 1, o["view_steps"])
        dt = np.where(valid, ln / count, 0.0)
        for i in range(int(o["view_steps"])):
            active = valid & (i < count)
            if not active.any():
                break
            idx = np.nonzero(active)[0]
            t = s0[idx] + dt[idx] * (i + o["offset"])
            pc = org[idx] + dirs[idx] * t[:, None]
            dens = cell.density(pc)
            dtau = dens * dt[idx]
            ext = 1.0 - np.exp(-dtau)
            lit = ext > 1e-9
            j = np.zeros(len(idx))
            if lit.any():
                tau = sun_tau(cell, pc[lit], sun, o)
                powder = 1.0 + (1.0 - np.exp(-2.0 * dens[lit] * o["powder_len"]) - 1.0) * min(max(o["powder"], 0.0), 1.0)
                j[lit] = o["sun_col"] * phase[idx][lit] * np.exp(-tau) * powder * o["albedo"]
            rad[idx] += T[idx] * j * ext
            T[idx] *= 1.0 - ext
            tau_sum[idx] += dtau
    alpha = 1.0 - np.exp(-tau_sum)
    return alpha.reshape(cam.height, cam.width), rad.reshape(cam.height, cam.width)


def base_top(alpha, cloud, mask):
    rows = np.nonzero(mask.any(axis=1))[0]
    top, bot = rows[0], rows[-1]
    band = max(int(round((bot - top + 1) * 0.2)), 1)
    base = cloud[bot - band + 1:bot + 1][mask[bot - band + 1:bot + 1]].mean()
    tp = cloud[top:top + band][mask[top:top + band]].mean()
    return float(base), float(tp), float(cloud[mask].mean())


def figures(img, ref, mask):
    b, t, m = base_top(None, img, mask)
    rb, rt, rm = base_top(None, ref, mask)
    return b / rb, t / rt, m / rm, float(img.sum() / ref.sum())


def row(label, img, ref, mask):
    b, t, m, s = figures(img, ref, mask)
    return "%-44s base %.3f  top %.3f  mean %.3f  sum %.3f" % (label, b, t, m, s)


def decompose(gpu, refdir):
    # (label, overrides) -- each line changes the shipped model by one term
    steps = [
        ("shipped model (mirror of the T15 'do' job)", {}),
        ("powder off", dict(powder=0.0)),
        ("sun march cap off (4 taps, ratio 2)", dict(cap=1e12)),
        ("sun march cap 9000 m", dict(cap=9000.0)),
        ("4 uniform taps (ratio 1), cap 4000", dict(ratio=1.0)),
        ("8 taps ratio 2", dict(steps=8)),
        ("16 taps ratio 2", dict(steps=16)),
        ("exact tau (64 uniform), powder on", dict(tau_mode="exact", steps=64)),
        ("exact tau (64 uniform), powder off  = reference", dict(tau_mode="exact", steps=64, powder=0.0)),
        ("sun colour 3.0 (instead of pi)", dict(sun_col=3.0)),
        ("view sample offset 0 (step start)", dict(offset=0.0)),
        ("view steps 256", dict(view_steps=256)),
    ]
    for spec in t14.LOOK_CELLS:
        if not spec[0].startswith("storm"):
            continue
        name, az, el, radius, sigma = spec
        z = np.load(os.path.join(refdir, name + ".npz"))
        d = np.load(os.path.join(refdir, name + "-direct.npz"))
        mask = z["alpha"] > 0.5
        ref = d["cloud"]
        cam = t14.look_camera(spec, W)
        sun = Lt.flat_sun(az, el)
        cell = storm_cell(spec)
        print("\n== %s (sun az %.0f el %.0f)  ratios to the reference's order 1" % (name, az, el))
        if gpu:
            g = np.load(os.path.join(gpu, "%s_default_do-r.npy" % name)).astype(np.float64)
            print(row("GPU 'do' job (T15, measured)", g, ref, mask))
        for label, over in steps:
            a, c = render_direct(storm_cell(spec), cam, sun, **over)
            line = row(label, c, ref, mask)
            if gpu:
                line += "   vs GPU %.3f" % (c[mask].sum() / g[mask].sum())
            print(line, flush=True)


def render_full(cell, cam, sun, powder_mode="all", p=None, steps=128, sun_col=SUN_E):
    """The whole source function of the shipped model (light_cpu.render), with the powder applied as the mode says:
    all  the shipped model: powder multiplies the whole octave sum and the diffusion term
    ms   powder multiplies everything but the first octave (the single scattering stays physical)
    none powder off
    one  powder on the first octave only
    Returns (alpha, cloud, single scattering part of the cloud)."""
    p = p or C.Params()
    p1 = C.Params(**dict(p.__dict__, octaves=1))
    dirs = cam.ray_directions()
    n = dirs.shape[0]
    org = np.broadcast_to(cam.pos, (n, 3)).copy()
    a0, a1, b0, b1 = cell.spans(org, dirs)
    sun = np.asarray(sun, dtype=np.float64)
    mu = dirs @ sun
    rad = np.zeros(n)
    single = np.zeros(n)
    T = np.ones(n)
    tau_sum = np.zeros(n)
    for (s0, s1) in ((a0, a1), (b0, b1)):
        ln = s1 - s0
        valid = ln > 0
        count = np.clip(np.ceil(ln / 1.0), 1, steps)
        dt = np.where(valid, ln / count, 0.0)
        for i in range(steps):
            active = valid & (i < count)
            if not active.any():
                break
            idx = np.nonzero(active)[0]
            t = s0[idx] + dt[idx] * (i + 0.5)
            pc = org[idx] + dirs[idx] * t[:, None]
            dens = cell.density(pc)
            dtau = dens * dt[idx]
            ext = 1.0 - np.exp(-dtau)
            lit = ext > 1e-9
            j = np.zeros(len(idx))
            js = np.zeros(len(idx))
            if lit.any():
                tau = C.light_tau(cell, pc[lit], sun, int(p.sunSteps), p.tapRatio)
                m = mu[idx][lit]
                diffuse = p.diffusion * C.INV4PI * (1.0 - np.exp(-tau / max(p.diffusionTau, 1e-3))) / (1.0 + max(p.diffusionDecay, 0.0) * tau)
                o_all = C.octave_sum(p, m, tau) + diffuse
                o_one = C.octave_sum(p1, m, tau)
                o_rest = o_all - o_one
                d_ = dens[lit]
                pw = 1.0 + (1.0 - np.exp(-2.0 * d_ * p.powderLength) - 1.0) * min(max(p.powder, 0.0), 1.0)
                if powder_mode == "all":
                    src_dir = o_all * pw
                    src_one = o_one * pw
                elif powder_mode == "ms":
                    src_dir = o_one + o_rest * pw
                    src_one = o_one
                elif powder_mode == "one":
                    src_dir = o_one * pw + o_rest
                    src_one = o_one * pw
                else:
                    src_dir = o_all
                    src_one = o_one
                alt = np.sqrt(np.sum(pc[lit] * pc[lit], axis=-1)) - R
                share = np.clip((alt - cell.inner) / (cell.outer - cell.inner), 0.0, 1.0)
                sky = (Lt.AMBIENT_BOTTOM + (Lt.AMBIENT_TOP - Lt.AMBIENT_BOTTOM) * share) * p.ambientGain
                tu = d_ * np.maximum(cell.outer - alt, 0.0)
                amb = np.maximum(sky * np.exp(-tu * p.ambientOcclusion) * p.albedo, max(p.ambientFloor, 0.0301) * sun_col * C.INV4PI)
                j[lit] = sun_col * src_dir * p.albedo + amb
                js[lit] = sun_col * src_one * p.albedo
            rad[idx] += T[idx] * j * ext
            single[idx] += T[idx] * js * ext
            T[idx] *= 1.0 - ext
            tau_sum[idx] += dtau
    shape = (cam.height, cam.width)
    return (1.0 - np.exp(-tau_sum)).reshape(shape), rad.reshape(shape), single.reshape(shape)


def all_cells(refroots):
    """(name, spec, camera, cell, reference total) of the storm, deep and hold-out cells whose references are found in refroots."""
    from weather_metrics import light_holdout as H
    out = []
    for spec in t14.LOOK_CELLS:
        out.append((spec[0], spec, t14.look_camera(spec, W), storm_cell(spec), "judged-T15" if spec[0].startswith("storm") else "T14 deep"))
    for spec in H.HOLDOUT:
        name, az, el, radius, sigma = spec
        rv = 0.7 * radius
        cell = C.Cell(centre_alt=2000.0, radius=radius, vertical=rv, edge=0.35, sigma=sigma, inner=2000.0 - 1.05 * rv, outer=2000.0 + 1.05 * rv)
        out.append((name, spec, H.hold_camera(radius, W), cell, "fit" if H.is_fit(spec) else "hold-out"))
    res = []
    for name, spec, cam, cell, kind in out:
        for root in refroots:
            f = os.path.join(root, name + ".npz")
            if os.path.isfile(f):
                z = np.load(f)
                res.append((name, spec, cam, cell, kind, z["alpha"], z["cloud"]))
                break
    return res


def powder_modes(refroots):
    """Total and single-scattering accuracy of the powder modes on every cell with a reference (CPU model only, nothing fitted)."""
    print("%-10s %-10s | %s" % ("cell", "kind", "mode: total base/top/mean over the reference's total"))
    for name, spec, cam, cell, kind, ra, rc in all_cells(refroots):
        az, el = spec[1], spec[2]
        sun = Lt.flat_sun(az, el)
        mask = ra > 0.5
        rb, rt, rm = base_top(None, rc, mask)
        cols = []
        for mode in ("all", "ms", "none"):
            a, c, s = render_full(cell, cam, sun, mode)
            b, t, m = base_top(None, c, mask)
            edge = (ra > 0.2) & (ra < 0.6)
            e = c[edge].mean() / rc[edge].mean() if edge.any() else float("nan")
            cols.append("%s %.3f/%.3f/%.3f rim %.3f" % (mode, b / rb, t / rt, m / rm, e))
        print("%-10s %-10s | %s" % (name, kind, "   ".join(cols)), flush=True)


GPU_VARIANTS = (
    # id suffix, light job overrides, sun march (m): single scattering only (the T15 'do' job) with one term changed
    ("do", {}, 4000.0),                                         # control: the T15 job
    ("do0", dict(powder=0.0), 4000.0),                          # powder off
    ("dot", dict(sunSteps=64, tapRatio=1.0), 20000.0),          # the sun's optical depth with 64 uniform taps over the whole chord, powder on
    ("do0t", dict(powder=0.0, sunSteps=64, tapRatio=1.0), 20000.0),   # both: the reference's single scattering
)


def write_gpu_jobs(folder):
    """The player input of the one GPU sweep of T17: single scattering of the three storm cells with the powder and the sun march quadrature varied."""
    import json
    jobs = []
    for spec in t14.LOOK_CELLS:
        if not spec[0].startswith("storm"):
            continue
        for suffix, over, march in GPU_VARIANTS:
            o = dict(over)
            o.update(octaves=1, diffusion=0.0)
            jobs.append(t14.look_job("%s_%s" % (spec[0], suffix), spec, aid=4, layer_kw=dict(sunMarch=march), **o))
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    print("wrote", len(jobs), "jobs to", folder)


def read_gpu(gpu, refdir):
    """Ratios of the GPU single scattering variants to the reference's order 1."""
    for spec in t14.LOOK_CELLS:
        if not spec[0].startswith("storm"):
            continue
        name = spec[0]
        z = np.load(os.path.join(refdir, name + ".npz"))
        ref = np.load(os.path.join(refdir, name + "-direct.npz"))["cloud"]
        mask = z["alpha"] > 0.5
        print("\n== %s" % name)
        for suffix, over, march in GPU_VARIANTS:
            f = os.path.join(gpu, "%s_%s-r.npy" % (name, suffix))
            if os.path.isfile(f):
                print(row("GPU %s %s sunMarch %.0f" % (suffix, over, march), np.load(f).astype(np.float64), ref, mask))


def sensitivity(refroots):
    """One-at-a-time sensitivities of the multiple scattering part (not a fit, nothing is chosen from these): the base total over the reference's
    for the storm cells when one constant of the octaves moves."""
    cells = [c for c in all_cells(refroots) if c[0].startswith("storm") or c[0] in ("deepback", "deepfront", "H1-el12", "H8-el5")]
    print("%-24s | %s" % ("variant", "  ".join("%-10s" % c[0] for c in cells)))
    for label, kw in (("shipped", {}), ("msIsotropy 1.0", dict(msIsotropy=1.0)), ("msIsotropy 0.5", dict(msIsotropy=0.5)), ("octaveC 0.5", dict(octaveC=0.5)),
                      ("msBrightness x0.8", dict(msBrightness=2.9358 * 0.8)), ("diffusion 0", dict(diffusion=0.0))):
        vals = []
        for name, spec, cam, cell, kind, ra, rc in cells:
            a, c, s = render_full(cell, cam, Lt.flat_sun(spec[1], spec[2]), "all", C.Params(**kw))
            b, t, m = base_top(None, c, ra > 0.5)
            rb, rt, rm = base_top(None, rc, ra > 0.5)
            vals.append("%.3f/%.3f" % (b / rb, t / rt))
        print("%-24s | %s" % (label, "  ".join("%-10s" % v for v in vals)), flush=True)


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "decompose"
    arg = lambda k, d: sys.argv[sys.argv.index(k) + 1] if k in sys.argv else d
    if cmd == "decompose":
        base = os.path.join(os.path.dirname(os.path.dirname(HERE)), "build")
        decompose(arg("--gpu", os.path.join(base, "WeatherPlayer", "T15-split-scene")), arg("--ref", os.path.join(base, "parity-ref-holdout")))
    elif cmd == "modes":
        base = os.path.join(os.path.dirname(os.path.dirname(HERE)), "build")
        roots = [r for r in sys.argv[2:]] or [os.path.join(base, "parity-ref-holdout")]
        powder_modes(roots)
    elif cmd == "gpu-jobs":
        write_gpu_jobs(sys.argv[2])
    elif cmd == "gpu-read":
        base = os.path.join(os.path.dirname(os.path.dirname(HERE)), "build")
        read_gpu(sys.argv[2], arg("--ref", os.path.join(base, "parity-ref-holdout")))
    elif cmd == "sensitivity":
        sensitivity(sys.argv[2:])


if __name__ == "__main__":
    main()
