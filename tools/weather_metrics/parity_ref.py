"""ParityRef: the offline brute-force reference renderer of the parity harness (package T11).

It marches a density field with high step counts, takes the sun with 64 or more light steps and adds multiple scattering by a
depth-limited recursion (a Monte Carlo continuation of the in-scattered radiance, orders 2 to K). Nothing here is tuned to look
like any product: it solves the radiative transfer equation of a participating medium with a Henyey-Greenstein phase function
(Henyey and Greenstein 1941), Beer-Lambert extinction and a single scattering albedo, on the density field it is given.

The density field interface (`DensityField`, `Layer`) is the one the planet-wide layer pass reads: a planet radius, layers with an
inner and an outer altitude, a peak extinction in 1/m, a vertical profile curve over the layer's height (0..1), a coverage map over
the unit sphere and an optional 3D shape factor. Layers add their extinction (transmittance multiplies: stacking). A layer may carry
a bounding sphere (a single cloud). The player scenes (tests/WeatherPlayer Runner/Parity*.cs) build the same fields from the same
numbers (tests/data/parity/scenes.json), so the reference and the pass see one density field.

Units: metres, radians in functions, degrees in camera and sun descriptions. The planet's centre is the origin; the camera's local
frame is up = position/|position|, east, north. The sun's irradiance is SUN_E (pi: a white Lambertian surface lit head-on returns
radiance equal to its albedo). The sky is a smooth environment radiance (`env`), the ground a Lambertian sphere of radius R.

Run `python tools/weather_metrics/parity_run.py selftest` for the analytic self-test, the convergence test and the first reference
images. Stdlib + numpy (Pillow for images).
"""
import math
import numpy as np

trapz = getattr(np, "trapezoid", None) or getattr(np, "trapz")
SUN_E = math.pi
# The sun's angular diameter: 0.5334 degrees mean (9.31 mrad), the figure the plan's shadow geometry uses (9.3 mrad).
SUN_ANGULAR_DIAMETER = 9.3e-3
TWO_PI = 2.0 * math.pi
FLAT_RADIUS = 2.0e9  # a planet so large that its curvature is invisible on the scales of a slab test


# --------------------------------------------------------------------------------------------------------------------------------
# Phase function
# --------------------------------------------------------------------------------------------------------------------------------
class Phase(object):
    """A dual-lobe Henyey-Greenstein phase function, normalised over the sphere (integral 1): w*HG(g1) + (1-w)*HG(g2).
    The reference's default is the forward lobe g = 0.85 (water cloud droplets in the visible, Hansen and Travis 1974) with a weak
    back lobe g = -0.30 (a stand-in for the Mie back-scatter); the lobes are parameters of the reference, not a measurement."""

    def __init__(self, g1=0.85, g2=-0.30, w1=0.92):
        self.g1, self.g2, self.w1 = g1, g2, w1

    @staticmethod
    def hg(mu, g):
        mu = np.asarray(mu, dtype=np.float64)
        return (1.0 - g * g) / (4.0 * math.pi * np.power(np.maximum(1.0 + g * g - 2.0 * g * mu, 1e-12), 1.5))

    def __call__(self, mu):
        return self.w1 * self.hg(mu, self.g1) + (1.0 - self.w1) * self.hg(mu, self.g2)

    def integral(self, n=200001):
        """Numeric integral over the sphere: 2 pi * integral of p(mu) dmu on [-1, 1] (trapezoid)."""
        mu = np.linspace(-1.0, 1.0, n)
        return float(TWO_PI * trapz(self(mu), mu))

    def sample_cos(self, rng, n):
        """Cosines of the angle to the forward direction, drawn from the phase function."""
        u = rng.random(n)
        pick = rng.random(n) < self.w1
        g = np.where(pick, self.g1, self.g2)
        small = np.abs(g) < 1e-4
        gs = np.where(small, 1.0, g)
        mu = (1.0 + gs * gs - np.square((1.0 - gs * gs) / (1.0 - gs + 2.0 * gs * u))) / (2.0 * gs)
        mu = np.where(small, 1.0 - 2.0 * u, mu)
        return np.clip(mu, -1.0, 1.0)


class WrongPhase(Phase):
    """The planted defect of the negative control: a forward lobe with the wrong sign (the silver lining moves to the back)."""

    def __init__(self):
        Phase.__init__(self, g1=-0.85, g2=0.30, w1=0.92)


def _basis(d):
    """Two unit vectors perpendicular to each unit vector d (N x 3), branch-free (Duff et al. 2017)."""
    sign = np.where(d[:, 2] >= 0.0, 1.0, -1.0)
    a = -1.0 / (sign + d[:, 2])
    b = d[:, 0] * d[:, 1] * a
    t1 = np.stack([1.0 + sign * d[:, 0] * d[:, 0] * a, sign * b, -sign * d[:, 0]], axis=1)
    t2 = np.stack([b, sign + d[:, 1] * d[:, 1] * a, -d[:, 1]], axis=1)
    return t1, t2


def scatter_directions(d, mu, rng):
    """New unit directions at angle acos(mu) from the unit directions d, uniform in azimuth."""
    t1, t2 = _basis(d)
    phi = rng.random(len(d)) * TWO_PI
    s = np.sqrt(np.maximum(1.0 - mu * mu, 0.0))
    out = (d * mu[:, None]) + (t1 * (s * np.cos(phi))[:, None]) + (t2 * (s * np.sin(phi))[:, None])
    return out / np.linalg.norm(out, axis=1, keepdims=True)


# --------------------------------------------------------------------------------------------------------------------------------
# The density field
# --------------------------------------------------------------------------------------------------------------------------------
def _unit(v):
    v = np.asarray(v, dtype=np.float64)
    return v / np.linalg.norm(v)


class Layer(object):
    """One cloud layer: altitudes above sea level, the peak extinction in 1/m, the profile over the layer's height (a callable of
    h in 0..1, default 1), the coverage over the unit sphere (a callable of unit vectors N x 3, default 1) and a shape factor (a
    callable of positions N x 3, default 1). `bound` is (centre, radius) of a bounding sphere for a single cloud."""

    def __init__(self, name, inner_m, outer_m, sigma, profile=None, coverage=None, shape=None, bound=None):
        self.name = name
        self.inner_m, self.outer_m, self.sigma = float(inner_m), float(outer_m), float(sigma)
        self.profile, self.coverage, self.shape, self.bound = profile, coverage, shape, bound

    def optical_depth_vertical(self, n=2001):
        """Integral of the extinction over the layer's height for coverage 1 and shape 1 (the table integral of the profile)."""
        h = np.linspace(0.0, 1.0, n)
        prof = np.ones(n) if self.profile is None else self.profile(h)
        return float(self.sigma * (self.outer_m - self.inner_m) * trapz(prof, h))


class DensityField(object):
    """The planet and its layers; `density(p)` is the extinction in 1/m at positions p (N x 3)."""

    def __init__(self, radius, layers, albedo_ground=0.3):
        self.radius = float(radius)
        self.layers = list(layers)
        self.ground_albedo = albedo_ground

    def top(self):
        return max(l.outer_m for l in self.layers) if self.layers else 0.0

    def density(self, p):
        r = np.linalg.norm(p, axis=-1)
        alt = r - self.radius
        total = np.zeros(r.shape)
        for l in self.layers:
            inside = (alt >= l.inner_m) & (alt <= l.outer_m)
            if not inside.any():
                continue
            h = np.clip((alt - l.inner_m) / max(l.outer_m - l.inner_m, 1e-9), 0.0, 1.0)
            val = np.full(r.shape, l.sigma)
            if l.profile is not None:
                val = val * l.profile(h)
            if l.coverage is not None:
                u = p / np.maximum(r, 1e-9)[..., None]
                val = val * l.coverage(u.reshape(-1, 3)).reshape(r.shape)
            if l.shape is not None:
                val = val * l.shape(p.reshape(-1, 3)).reshape(r.shape)
            if l.bound is not None:
                c, br = l.bound
                inside = inside & (np.linalg.norm(p - c, axis=-1) <= br)
            total = total + np.where(inside, val, 0.0)
        return total

    # ---- ray / sphere helpers -------------------------------------------------------------------------------------------------
    @staticmethod
    def hit_sphere(o, d, centre, radius):
        """Entry and exit distances of rays against a sphere; (nan, nan) where a ray misses."""
        oc = o - centre
        b = np.sum(oc * d, axis=-1)
        c = np.sum(oc * oc, axis=-1) - radius * radius
        disc = b * b - c
        ok = disc > 0.0
        root = np.sqrt(np.where(ok, disc, 0.0))
        t0 = np.where(ok, -b - root, np.nan)
        t1 = np.where(ok, -b + root, np.nan)
        return t0, t1

    def ground_hit(self, o, d):
        """Distance to the ground sphere, inf where the ray does not reach it."""
        t0, t1 = self.hit_sphere(o, d, np.zeros(3), self.radius)
        t = np.where(np.isnan(t0), np.inf, np.where(t0 > 0.0, t0, np.where(t1 > 0.0, t1, np.inf)))
        return t

    def slots(self, o, d, tmax):
        """Marching intervals of every layer, two per layer (the shell is hollow): arrays (N, 2L) of t0 and t1, empty = t1 <= t0."""
        n = len(o)
        t0s, t1s = [], []
        zero = np.zeros(3)
        for l in self.layers:
            to0, to1 = self.hit_sphere(o, d, zero, self.radius + l.outer_m)
            ti0, ti1 = self.hit_sphere(o, d, zero, self.radius + max(l.inner_m, 0.0)) if l.inner_m > 0.0 else (np.full(n, np.nan), np.full(n, np.nan))
            hit_o = ~np.isnan(to0)
            hit_i = ~np.isnan(ti0)
            a0 = np.where(hit_o, to0, 0.0)
            a1 = np.where(hit_o, np.where(hit_i, ti0, to1), 0.0)
            b0 = np.where(hit_o & hit_i, ti1, 0.0)
            b1 = np.where(hit_o & hit_i, to1, 0.0)
            if l.bound is not None:
                bc, br = l.bound
                tb0, tb1 = self.hit_sphere(o, d, bc, br)
                miss = np.isnan(tb0)
                tb0 = np.where(miss, 0.0, tb0)
                tb1 = np.where(miss, 0.0, tb1)
                a0, a1 = np.maximum(a0, tb0), np.minimum(a1, tb1)
                b0, b1 = np.maximum(b0, tb0), np.minimum(b1, tb1)
            for lo, hi in ((a0, a1), (b0, b1)):
                lo = np.maximum(lo, 0.0)
                hi = np.minimum(hi, tmax)
                t0s.append(lo)
                t1s.append(np.maximum(hi, lo))
        if not t0s:
            return np.zeros((n, 0)), np.zeros((n, 0))
        return np.stack(t0s, axis=1), np.stack(t1s, axis=1)

    def exact_tau(self, o, d):
        """The optical depth of rays through homogeneous layers (no profile, coverage or shape factor): the sum of extinction times the
        exact chord length of the shells, the ground cutting the ray. The analytic reference of the geometry checks."""
        o = np.atleast_2d(o)
        d = np.atleast_2d(d)
        tg = self.ground_hit(o, d)
        t0, t1 = self.slots(o, d, tg)
        sig = np.repeat(np.array([l.sigma for l in self.layers]), 2)
        return np.sum((t1 - t0) * sig[None, :], axis=1)

    def samples(self, o, d, tmax, steps):
        """Midpoint samples along the rays: t and dt (N, M) sorted by t per ray; dt is 0 where a slot is empty."""
        t0, t1 = self.slots(o, d, tmax)
        n, k = t0.shape
        if k == 0:
            return np.zeros((n, 0)), np.zeros((n, 0))
        length = (t1 - t0)
        dt = length / steps
        frac = (np.arange(steps) + 0.5)[None, None, :]
        t = (t0[:, :, None] + dt[:, :, None] * frac).reshape(n, k * steps)
        dts = np.repeat(dt, steps, axis=1)
        order = np.argsort(t, axis=1, kind="stable")
        return np.take_along_axis(t, order, axis=1), np.take_along_axis(dts, order, axis=1)


# --------------------------------------------------------------------------------------------------------------------------------
# Camera, sun, environment
# --------------------------------------------------------------------------------------------------------------------------------
def local_frame(pos):
    """up, east, north at a position."""
    up = _unit(pos)
    east = np.cross(np.array([0.0, 0.0, 1.0]), up)
    if np.linalg.norm(east) < 1e-9:
        east = np.array([1.0, 0.0, 0.0])
    east = _unit(east)
    north = np.cross(up, east)
    return up, east, north


def direction_from_angles(frame, azimuth_deg, elevation_deg):
    """A unit vector at an azimuth (from north towards east) and an elevation above the local horizon."""
    up, east, north = frame
    az, el = math.radians(azimuth_deg), math.radians(elevation_deg)
    return math.cos(el) * (math.cos(az) * north + math.sin(az) * east) + math.sin(el) * up


class Camera(object):
    """A pinhole camera: position (planet frame), yaw (from north towards east), pitch (up positive), vertical field of view."""

    def __init__(self, pos, yaw_deg, pitch_deg, width, height, fov_v_deg=50.0):
        self.pos = np.asarray(pos, dtype=np.float64)
        self.yaw, self.pitch = yaw_deg, pitch_deg
        self.width, self.height, self.fov_v = int(width), int(height), fov_v_deg
        self.frame = local_frame(self.pos)
        self.forward = direction_from_angles(self.frame, yaw_deg, pitch_deg)
        up, east, north = self.frame
        right = np.cross(self.forward, up)
        if np.linalg.norm(right) < 1e-9:
            right = east
        self.right = _unit(right)
        self.cam_up = np.cross(self.right, self.forward)

    def ray_directions(self):
        """Unit directions of all pixel centres, row-major from the top row (N x 3)."""
        half = math.tan(math.radians(self.fov_v) * 0.5)
        aspect = self.width / float(self.height)
        xs = ((np.arange(self.width) + 0.5) / self.width * 2.0 - 1.0) * half * aspect
        ys = (1.0 - (np.arange(self.height) + 0.5) / self.height * 2.0) * half
        gx, gy = np.meshgrid(xs, ys)
        d = self.forward[None, :] + gx.reshape(-1, 1) * self.right[None, :] + gy.reshape(-1, 1) * self.cam_up[None, :]
        return d / np.linalg.norm(d, axis=1, keepdims=True)

    def pixel_row_of_elevation(self, elevation_deg):
        """The (fractional) pixel row at which the camera's central column sees a direction at the given elevation angle above the
        local horizon, for a camera with zero roll looking along its yaw: row 0 is the top edge of the image."""
        half = math.tan(math.radians(self.fov_v) * 0.5)
        rel = math.radians(elevation_deg - self.pitch)
        y = math.tan(rel)
        return (1.0 - y / half) * 0.5 * self.height - 0.5 + 0.0


class Sun(object):
    def __init__(self, direction):
        self.direction = _unit(direction)  # towards the sun

    @staticmethod
    def at(pos, azimuth_deg, elevation_deg):
        return Sun(direction_from_angles(local_frame(pos), azimuth_deg, elevation_deg))

    def disc_directions(self, n):
        """n directions spread over the sun's disc (a golden-angle spiral; the centre included)."""
        t1, t2 = _basis(self.direction[None, :])
        t1, t2 = t1[0], t2[0]
        out = [self.direction]
        rad = SUN_ANGULAR_DIAMETER * 0.5
        for i in range(1, n):
            r = rad * math.sqrt(i / float(n - 1 if n > 1 else 1))
            a = i * 2.399963229728653
            v = self.direction + r * (math.cos(a) * t1 + math.sin(a) * t2)
            out.append(_unit(v))
        return np.array(out)


class Environment(object):
    """The sky and the ground as seen from inside the medium: a smooth radiance by the elevation of the direction above the local
    horizon (zenith radiance above, horizon radiance at 0, the ground's radiance below)."""

    def __init__(self, zenith=0.30, horizon=0.55, ground=0.12):
        self.zenith, self.horizon, self.ground = zenith, horizon, ground

    def radiance(self, d, up):
        s = np.sum(d * up, axis=-1)
        sky = self.horizon + (self.zenith - self.horizon) * np.clip(s, 0.0, 1.0)
        below = self.ground
        return np.where(s >= 0.0, sky, below)


class Result(object):
    pass


# --------------------------------------------------------------------------------------------------------------------------------
# The renderer
# --------------------------------------------------------------------------------------------------------------------------------
class Renderer(object):
    """The brute-force renderer. Parameters (the reference's defaults are the high setting):
    view_steps   steps of every marching slot of the view ray (default 128)
    light_steps  steps of every marching slot of the sun and of the continuation rays (default 64)
    order        highest scattering order, 1 = single scattering only (recursion depth order - 1); thick forward-scattering clouds
                 need tens of orders: the order table of the self-test shows the convergence
    ms_paths     continuations per evaluated sample (their mean is taken; noise falls with the square root)
    ms_stride    the multiple-scattering term is evaluated at every ms_stride-th sample and held between (1 = every sample)
    sun_disc     directions over the sun's disc for the ground's direct light (default 9)
    albedo       the single scattering albedo (default 0.999)
    seed         the RNG seed: the same inputs give the same image
    """

    def __init__(self, field, sun, env=None, phase=None, view_steps=128, light_steps=64, order=4, ms_stride=1, sun_disc=9,
                 albedo=0.999, seed=1234, ground_full_samples=0, ms_paths=1):
        self.ms_paths = int(ms_paths)
        self.field = field
        self.sun = sun
        self.env = env or Environment()
        self.phase = phase or Phase()
        self.view_steps, self.light_steps, self.order = int(view_steps), int(light_steps), int(order)
        self.ms_stride, self.sun_disc, self.albedo = int(ms_stride), int(sun_disc), float(albedo)
        self.rng = np.random.default_rng(seed)
        self.ground_full_samples = int(ground_full_samples)
        self.density_evals = 0

    # ---- transmittance --------------------------------------------------------------------------------------------------------
    def _sigma(self, o, d, t):
        p = o[:, None, :] + d[:, None, :] * t[:, :, None]
        self.density_evals += int(t.size)
        return self.field.density(p.reshape(-1, 3)).reshape(t.shape)

    def optical_depth(self, o, d, tmax=None, steps=None):
        """Optical depth from o along d up to tmax (default: out of the medium) by the midpoint rule."""
        o = np.atleast_2d(o)
        d = np.atleast_2d(d)
        if d.shape[0] != o.shape[0]:
            d = np.broadcast_to(d, o.shape)
        if tmax is None:
            tmax = np.full(len(o), np.inf)
        t, dt = self.field.samples(o, d, tmax, steps or self.light_steps)
        if t.shape[1] == 0:
            return np.zeros(len(o))
        return np.sum(self._sigma(o, d, t) * dt, axis=1)

    def sun_transmittance(self, p, disc=1):
        """Transmittance from points to the sun; with disc > 1 the mean over the sun's disc (a soft shadow edge)."""
        dirs = self.sun.disc_directions(disc) if disc > 1 else self.sun.direction[None, :]
        acc = np.zeros(len(p))
        for s in dirs:
            acc += np.exp(-self.optical_depth(p, np.broadcast_to(s, p.shape)))
        return acc / len(dirs)

    # ---- the in-scattered radiance --------------------------------------------------------------------------------------------
    def inscatter(self, p, d, k, paths=1):
        """In-scattered radiance at points p towards -d (d: the direction of the ray that reached p), orders 1..k: the sun term and,
        for k > 1, the mean of `paths` Monte Carlo continuations along directions drawn from the phase function."""
        n = len(p)
        mu_sun = np.sum(d * self.sun.direction[None, :], axis=1)
        sun_term = SUN_E * self.sun_transmittance(p) * self.phase(mu_sun)
        if paths > 1 and k > 1 and n > 0:
            rest = self.inscatter(np.repeat(p, paths, axis=0), np.repeat(d, paths, axis=0), k, 1) - np.repeat(sun_term * self.albedo, paths)
            return self.albedo * sun_term + rest.reshape(n, paths).mean(axis=1)
        out = sun_term
        if k > 1 and n > 0:
            mu = self.phase.sample_cos(self.rng, n)
            dn = scatter_directions(d, mu, self.rng)
            t, dt = self.field.samples(p, dn, np.full(n, np.inf), self.light_steps)
            up = p / np.linalg.norm(p, axis=1, keepdims=True)
            env = self.env.radiance(dn, up)
            if t.shape[1] == 0:
                return self.albedo * (out + env)  # no medium on the continuation: the environment alone
            sig = self._sigma(p, dn, t)
            cum = np.cumsum(sig * dt, axis=1)
            tau_tot = cum[:, -1]
            t_esc = np.exp(-tau_tot)
            cont = np.zeros(n)
            live = (1.0 - t_esc) > 1e-7
            if live.any():
                u = self.rng.random(int(live.sum()))
                target = -np.log(1.0 - u * (1.0 - t_esc[live]))
                cl = cum[live]
                idx = np.minimum((cl < target[:, None]).sum(axis=1), cl.shape[1] - 1)
                rows = np.arange(len(idx))
                prev = np.where(idx > 0, cl[rows, np.maximum(idx - 1, 0)], 0.0)
                step_tau = np.maximum(cl[rows, idx] - prev, 1e-30)
                frac = np.clip((target - prev) / step_tau, 0.0, 1.0)
                tl, dtl = t[live], dt[live]
                tq = tl[rows, idx] + (frac - 0.5) * dtl[rows, idx]
                q = p[live] + dn[live] * tq[:, None]
                cont[live] = (1.0 - t_esc[live]) * self.inscatter(q, dn[live], k - 1)
            out = out + cont + t_esc * env
        return self.albedo * out

    # ---- the ground -----------------------------------------------------------------------------------------------------------
    def ground_radiance(self, g, disc=None):
        """Radiance of the Lambertian ground at points g (direct sun through the cloud + the environment's ambient; or the
        cosine-weighted radiance through the medium with `ground_full_samples` directions)."""
        up = g / np.linalg.norm(g, axis=1, keepdims=True)
        cos_s = np.clip(np.sum(up * self.sun.direction[None, :], axis=1), 0.0, None)
        direct = SUN_E * cos_s * self.sun_transmittance(g + up * 0.01, disc or self.sun_disc)
        if self.ground_full_samples > 0:
            amb = self.diffuse_irradiance(g, up) / math.pi
        else:
            amb = np.full(len(g), float(self.env.zenith + self.env.horizon) * 0.5)
        return self.field.ground_albedo * (direct / math.pi + amb)

    def diffuse_irradiance(self, g, up):
        """Irradiance (per pi) of the sky hemisphere at ground points through the medium: the mean over cosine-weighted directions of
        the radiance arriving along them (each with the sun and the continuation, no ground bounce)."""
        n = len(g)
        acc = np.zeros(n)
        m = self.ground_full_samples
        for _ in range(m):
            mu = np.sqrt(self.rng.random(n))
            d = scatter_directions(up, mu, self.rng)
            acc += self.radiance(g + up * 0.01, d)[0]
        return acc / m * math.pi

    # ---- radiance of rays -----------------------------------------------------------------------------------------------------
    def radiance(self, o, d, occluder_t=None):
        """Radiance along rays (o: N x 3, d: N x 3). Returns (L, tau_to_hit, depth, active_steps)."""
        n = len(d)
        o = np.broadcast_to(o, (n, 3)).copy() if o.ndim == 1 else o
        tg = self.field.ground_hit(o, d)
        if occluder_t is not None:
            tg = np.minimum(tg, occluder_t)
        t, dt = self.field.samples(o, d, tg, self.view_steps)
        L = np.zeros(n)
        tau_hit = np.zeros(n)
        depth_num = np.zeros(n)
        depth_den = np.zeros(n)
        active = np.zeros(n)
        trans = np.ones(n)
        if t.shape[1] > 0:
            sig = self._sigma(o, d, t)
            dtau = sig * dt
            cum_before = np.cumsum(dtau, axis=1) - dtau
            T_before = np.exp(-cum_before)
            w = T_before * (1.0 - np.exp(-dtau))
            active = (sig > 0.0).sum(axis=1).astype(np.float64)
            tau_hit = dtau.sum(axis=1)
            trans = np.exp(-tau_hit)
            cols = np.arange(0, t.shape[1], max(self.ms_stride, 1))
            J = np.zeros(t.shape)
            for c0 in cols:
                c1 = min(c0 + max(self.ms_stride, 1), t.shape[1])
                rowsel = np.nonzero(w[:, c0:c1].max(axis=1) > 1e-6)[0] if c1 > c0 else np.array([], dtype=int)
                if len(rowsel) == 0:
                    continue
                cm = c0 + (c1 - c0) // 2
                p = o[rowsel] + d[rowsel] * t[rowsel, cm][:, None]
                j = self.inscatter(p, d[rowsel], self.order, self.ms_paths)
                J[rowsel, c0:c1] = j[:, None]
            L = np.sum(w * J, axis=1)
            depth_num = np.sum(w * t, axis=1)
            depth_den = np.sum(w, axis=1)
        # what lies behind the medium: the ground or the sky
        up_c = o / np.linalg.norm(o, axis=1, keepdims=True)
        sky = self.env.radiance(d, up_c)
        behind = sky
        gmask = np.isfinite(tg) & (self.field.ground_hit(o, d) <= tg + 1e-6)
        if gmask.any():
            g = o[gmask] + d[gmask] * tg[gmask][:, None]
            behind = behind.copy()
            behind[gmask] = self.ground_radiance(g)
        if occluder_t is not None:
            omask = np.isfinite(occluder_t) & (occluder_t < self.field.ground_hit(o, d))
            behind = np.where(omask, 0.05, behind)
        cloud = L
        L = L + trans * behind
        depth = np.where(depth_den > 1e-9, depth_num / np.maximum(depth_den, 1e-12), np.nan)
        return L, tau_hit, depth, active, cloud, gmask

    def render(self, camera, occluders=None, chunk=384):
        """Renders an image. Returns a Result with lum, alpha, tau, depth, steps (all height x width)."""
        d = camera.ray_directions()
        n = len(d)
        o = np.broadcast_to(camera.pos, (n, 3)).copy()
        occ = None
        if occluders:
            occ = np.full(n, np.inf)
            for centre, radius in occluders:
                t0, t1 = DensityField.hit_sphere(o, d, np.asarray(centre, dtype=np.float64), radius)
                occ = np.minimum(occ, np.where(np.isnan(t0), np.inf, np.where(t0 > 0, t0, np.inf)))
        L = np.zeros(n)
        tau = np.zeros(n)
        depth = np.zeros(n)
        steps = np.zeros(n)
        cloud = np.zeros(n)
        ground = np.zeros(n, dtype=bool)
        for a in range(0, n, chunk):
            b = min(a + chunk, n)
            r = self.radiance(o[a:b], d[a:b], None if occ is None else occ[a:b])
            L[a:b], tau[a:b], depth[a:b], steps[a:b], cloud[a:b], ground[a:b] = r
        res = Result()
        shape = (camera.height, camera.width)
        res.lum = L.reshape(shape)
        res.tau = tau.reshape(shape)
        res.alpha = (1.0 - np.exp(-tau)).reshape(shape)
        res.depth = depth.reshape(shape)
        res.steps = steps.reshape(shape)
        # The in-scattered radiance of the medium alone (without what lies behind it): the cloud's own light.
        res.cloud = cloud.reshape(shape)
        res.ground = ground.reshape(shape)
        res.density_evals = self.density_evals
        return res


# --------------------------------------------------------------------------------------------------------------------------------
# Synthetic scenes (the same numbers are in tests/data/parity/scenes.json for the player scenes)
# --------------------------------------------------------------------------------------------------------------------------------
KERBIN_RADIUS = 600000.0


def scene_uniform_deck(base=2200.0, top=2500.0, tau_vertical=8.0, radius=KERBIN_RADIUS):
    """A homogeneous cloud shell between two altitudes with the given vertical optical depth."""
    sigma = tau_vertical / (top - base)
    return DensityField(radius, [Layer("deck", base, top, sigma)])


def slab_sun_transmission(tau, elevation_deg, phase=None, albedo=0.999, photons=400000, seed=4321, chunk=200000):
    """The sun's irradiance on the ground under a plane-parallel homogeneous slab of vertical optical depth tau, as a share of the
    open ground's: beam plus all scattered light, no sky, no ground bounce (the slab-flux reference of check R12).

    An analog photon walk: free paths from -ln(u) in optical-depth units, absorption with probability 1 - albedo at each
    collision, scattering directions drawn from the phase function. Every photon carries the same flux through the horizontal
    plane, so the share is the fraction that crosses the ground plane. It conserves energy at any tau (no truncation of the
    scattering order, which loses the multiply scattered light of a thick forward-scattering slab) and its sampling error is
    sqrt(T (1 - T) / photons) (0.2 % at 400000 photons for T = 0.18), the same for every tau and every seed. Returns (share, error)."""
    phase = phase or Phase()
    rng = np.random.default_rng(seed)
    hits = 0
    total = 0
    c0 = math.sin(math.radians(elevation_deg))
    while total < photons:
        n = min(chunk, photons - total)
        total += n
        depth = np.zeros(n)
        # The direction as a 3-vector with the downward cosine in z (+z = towards the ground).
        d = np.zeros((n, 3))
        d[:, 0] = math.sqrt(max(1.0 - c0 * c0, 0.0))
        d[:, 2] = c0
        alive = np.arange(n)
        while len(alive):
            s = -np.log(1.0 - rng.random(len(alive)))
            depth[alive] += d[alive, 2] * s
            reached = depth[alive] >= tau
            escaped = depth[alive] < 0.0
            hits += int(reached.sum())
            keep = ~(reached | escaped)
            keep &= rng.random(len(alive)) < albedo
            alive = alive[keep]
            if len(alive):
                mu = phase.sample_cos(rng, len(alive))
                d[alive] = scatter_directions(d[alive], mu, rng)
    share = hits / float(total)
    return share, math.sqrt(max(share * (1.0 - share), 1e-12) / total)


def scene_slab(tau, thickness=1000.0, base=2000.0):
    """A plane-parallel homogeneous slab of vertical optical depth tau on a planet whose curvature is invisible."""
    return DensityField(FLAT_RADIUS, [Layer("slab", base, base + thickness, tau / thickness)])


def scene_two_slab(tau1, tau2, base1=1500.0, top1=1900.0, base2=3000.0, top2=3400.0):
    return DensityField(FLAT_RADIUS, [Layer("lower", base1, top1, tau1 / (top1 - base1)), Layer("upper", base2, top2, tau2 / (top2 - base2))])


def scene_single_cloud(radius_m=800.0, centre_alt=2000.0, sigma=0.02, flat_base=True, planet=FLAT_RADIUS, hard=False):
    """One cumulus-like cell: an ellipsoid (radius_m wide, 0.7 x as tall) with a smooth edge (a hard edge if hard) and, if
    flat_base, a flat bottom cut at its centre height minus 0.5 of the vertical radius."""
    c = np.array([0.0, 0.0, planet + centre_alt])
    rv = radius_m * 0.7

    def shape(p):
        q = (p - c)
        r2 = (q[:, 0] / radius_m) ** 2 + (q[:, 1] / radius_m) ** 2 + (q[:, 2] / rv) ** 2
        r = np.sqrt(r2)
        if hard:
            val = (r < 1.0).astype(np.float64)
        else:
            x = np.clip((1.0 - r) / 0.35, 0.0, 1.0)
            val = x * x * (3.0 - 2.0 * x)
        if flat_base:
            val = val * (q[:, 2] > -0.5 * rv)
        return val

    inner, outer = centre_alt - rv * 1.05, centre_alt + rv * 1.05
    return DensityField(planet, [Layer("cell", inner, outer, sigma, shape=shape, bound=(c, radius_m * 1.05))])


def scene_terrain_shadow(tau, thickness=300.0, base=2000.0, edge_x=None):
    """The shadow scene: a slab over flat ground; with edge_x the slab ends at x = edge_x (a hard-edged shadow)."""
    layer = Layer("slab", base, base + thickness, tau / thickness)
    if edge_x is not None:
        def cov(u):
            x = u[:, 0] * FLAT_RADIUS
            return (x < edge_x).astype(np.float64)
        layer.coverage = cov
    return DensityField(FLAT_RADIUS, [layer])


def value_noise_map(seed, nlat=96, nlon=192, octaves=3, rank_uniform=True):
    """A smooth random map on a (lat, lon) grid, ranked so that its values are uniform on [0, 1]."""
    rng = np.random.default_rng(seed)
    field = np.zeros((nlat, nlon))
    amp, total = 1.0, 0.0
    for o in range(octaves):
        gl, gn = 4 * 2 ** o, 8 * 2 ** o
        grid = rng.random((gl + 1, gn + 1))
        y = np.linspace(0, gl, nlat, endpoint=False)
        x = np.linspace(0, gn, nlon, endpoint=False)
        y0, x0 = y.astype(int), x.astype(int)
        fy, fx = (y - y0)[:, None], (x - x0)[None, :]
        fy, fx = fy * fy * (3 - 2 * fy), fx * fx * (3 - 2 * fx)
        g = lambda yy, xx: grid[yy[:, None], (xx[None, :]) % gn]
        v = (g(y0, x0) * (1 - fx) + g(y0, x0 + 1) * fx) * (1 - fy) + (g(y0 + 1, x0) * (1 - fx) + g(y0 + 1, x0 + 1) * fx) * fy
        field += amp * v
        total += amp
        amp *= 0.5
    field /= total
    if rank_uniform:
        order = np.argsort(field.ravel())
        ranks = np.empty(field.size)
        ranks[order] = (np.arange(field.size) + 0.5) / field.size
        field = ranks.reshape(field.shape)
    return field


def sphere_map_lookup(grid, u):
    """Bilinear lookup of a (lat, lon) grid at unit vectors u (N x 3): north-up, lon 0 at +x, east towards +y."""
    lat = np.arcsin(np.clip(u[:, 2], -1, 1))
    lon = np.arctan2(u[:, 1], u[:, 0])
    nlat, nlon = grid.shape
    fy = (math.pi / 2 - lat) / math.pi * (nlat - 1)
    fx = ((lon + math.pi) / TWO_PI * nlon) % nlon
    y0 = np.clip(fy.astype(int), 0, nlat - 2)
    x0 = fx.astype(int) % nlon
    ty, tx = fy - y0, fx - fx.astype(int)
    x1 = (x0 + 1) % nlon
    return (grid[y0, x0] * (1 - tx) + grid[y0, x1] * tx) * (1 - ty) + (grid[y0 + 1, x0] * (1 - tx) + grid[y0 + 1, x1] * tx) * ty


def scene_coverage_map(coverage, seed=7, base=2200.0, top=2500.0, tau_vertical=12.0, radius=KERBIN_RADIUS):
    """A thick deck whose columns exist where the ranked noise map is below the coverage C (so the covered fraction is C)."""
    grid = value_noise_map(seed)
    layer = Layer("deck", base, top, tau_vertical / (top - base),
                  coverage=lambda u: (sphere_map_lookup(grid, u) < coverage).astype(np.float64))
    return DensityField(radius, [layer]), grid


def _hash3(ix, iy, iz, seed):
    """A 32-bit integer hash of lattice coordinates (uint32 arithmetic), values in [0, 1)."""
    h = (ix.astype(np.uint32) * np.uint32(73856093)) ^ (iy.astype(np.uint32) * np.uint32(19349663)) ^ (iz.astype(np.uint32) * np.uint32(83492791)) ^ np.uint32(seed * 2654435761 % (1 << 32))
    h ^= h >> np.uint32(16)
    h *= np.uint32(0x2545F491)  # own odd multipliers; the avalanche is checked by the noise tests, not claimed
    h ^= h >> np.uint32(15)
    h *= np.uint32(0x4F6CDD1D)
    h ^= h >> np.uint32(16)
    return h.astype(np.float64) / 4294967296.0


class FbmShape(object):
    """Fractal value noise in 3D with a tile period: the shape factor of a layer (smoothstep(lo, hi, fbm), 0..1). `tile_m` is the
    distance after which the pattern repeats exactly (use a huge tile for a field without visible repetition), `cell_m` the lattice
    spacing of the first octave; each next octave halves the cell and scales the amplitude by `persistence` (0.8 gives a 1D power
    spectrum slope near 1.7 to 2.3 in the bands of the noise check, the published range of cloud fields)."""

    def __init__(self, seed=3, tile_m=1.0e7, cell_m=1500.0, octaves=5, lo=0.38, hi=0.62, persistence=0.8):
        self.seed, self.tile_m, self.cell_m, self.octaves, self.lo, self.hi = seed, float(tile_m), float(cell_m), octaves, lo, hi
        self.persistence = persistence

    def fbm(self, p):
        total = np.zeros(len(p))
        amp, norm = 1.0, 0.0
        cell = self.cell_m
        for o in range(self.octaves):
            period = max(int(round(self.tile_m / cell)), 2)
            q = p / cell
            i0 = np.floor(q)
            f = q - i0
            f = f * f * (3.0 - 2.0 * f)
            i0 = i0.astype(np.int64) % period
            i1 = (i0 + 1) % period
            acc = 0.0
            for dx in (0, 1):
                for dy in (0, 1):
                    for dz in (0, 1):
                        ix = i1[:, 0] if dx else i0[:, 0]
                        iy = i1[:, 1] if dy else i0[:, 1]
                        iz = i1[:, 2] if dz else i0[:, 2]
                        w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                        acc = acc + w * _hash3(ix, iy, iz, self.seed + 17 * o)
            total += amp * acc
            norm += amp
            amp *= self.persistence
            cell *= 0.5
        return total / norm

    def __call__(self, p):
        v = self.fbm(p)
        x = np.clip((v - self.lo) / (self.hi - self.lo), 0.0, 1.0)
        return x * x * (3.0 - 2.0 * x)


def scene_noise_deck(shape, base=2200.0, top=2500.0, tau_vertical=6.0, radius=KERBIN_RADIUS):
    """A deck whose extinction is modulated by a noise shape factor (the noise and repetition checks)."""
    return DensityField(radius, [Layer("deck", base, top, tau_vertical / (top - base), shape=shape)])


def alpha_image(field, camera, steps=96):
    """The alpha (1 - transmittance) and the optical depth of every pixel, without lighting (a fast path for geometry and noise)."""
    r = Renderer(field, Sun([0, 0, 1]), view_steps=steps, light_steps=8, order=1)
    d = camera.ray_directions()
    o = np.broadcast_to(camera.pos, d.shape).copy()
    tau = np.zeros(len(d))
    for a in range(0, len(d), 2048):
        b = min(a + 2048, len(d))
        tg = field.ground_hit(o[a:b], d[a:b])
        t, dt = field.samples(o[a:b], d[a:b], tg, steps)
        if t.shape[1]:
            tau[a:b] = np.sum(r._sigma(o[a:b], d[a:b], t) * dt, axis=1)
    shape = (camera.height, camera.width)
    return (1.0 - np.exp(-tau)).reshape(shape), tau.reshape(shape)


# --------------------------------------------------------------------------------------------------------------------------------
# Images
# --------------------------------------------------------------------------------------------------------------------------------
def _lut(t, stops):
    t = np.clip(t, 0.0, 1.0)
    xs = np.linspace(0.0, 1.0, len(stops))
    out = np.zeros(t.shape + (3,))
    for c in range(3):
        out[..., c] = np.interp(t, xs, [s[c] for s in stops])
    return out


def tonemap(lum, exposure=1.0):
    v = 1.0 - np.exp(-lum * exposure * 1.3)
    return (np.clip(v, 0, 1) ** (1 / 2.2))


def save_natural(path, res, exposure=1.0):
    from PIL import Image
    g = (tonemap(res.lum, exposure) * 255).astype(np.uint8)
    Image.fromarray(np.stack([g, g, g], -1)).resize((res.lum.shape[1] * 4, res.lum.shape[0] * 4), Image.NEAREST).save(path)


def save_aid(path, res):
    """The aid view: false-colour optical depth (log, left) and the heat map of the marched steps with density (right)."""
    from PIL import Image
    tau = np.log10(np.maximum(res.tau, 1e-4))
    t = (tau + 3.0) / 4.5
    left = _lut(t, [(0.05, 0.05, 0.3), (0.1, 0.4, 0.8), (0.2, 0.8, 0.5), (0.95, 0.9, 0.2), (0.9, 0.2, 0.1)])
    mx = max(res.steps.max(), 1.0)
    right = _lut(res.steps / mx, [(0.0, 0.0, 0.0), (0.4, 0.1, 0.5), (0.9, 0.4, 0.1), (1.0, 0.95, 0.5)])
    img = np.concatenate([left, np.ones((left.shape[0], 2, 3)), right], axis=1)
    Image.fromarray((img * 255).astype(np.uint8)).resize((img.shape[1] * 4, img.shape[0] * 4), Image.NEAREST).save(path)
