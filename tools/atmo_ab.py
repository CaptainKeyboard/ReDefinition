#!/usr/bin/env python3
"""The judging tools of the Atmospheres verification concept (master plan 9.1): A/B of two image sets from identical scenes, the
contribution mask, and the interaction matrix. Needs numpy and Pillow.

  python tools/atmo_ab.py compare <A> <B> [--threshold 0.02] [--out <folder>] [--region x0,y0,x1,y1] [--expect-area F --tol 0.05]
  python tools/atmo_ab.py contrib <image> [--threshold 0.01] [--region x0,y0,x1,y1 | --region-mask <mask image>]
  python tools/atmo_ab.py interaction expand <matrix.json> [--cover]
  python tools/atmo_ab.py interaction judge <matrix.json> <measured.json>
  python tools/atmo_ab.py selftest

compare takes two image files or two folders (images paired by file stem). Every image has a sidecar <stem>.json that states the scene:
pose, seed, ut, state, resolution and upscaler. Only the feature may differ between A and B; if any of these differs, or a sidecar
lacks one, the tool refuses and prints why (exit code 2), because a difference between two different scenes says nothing about the
feature. For each pair it writes a difference image (luminance and chroma), a mask at the stated threshold and these metrics on the
difference region: area_fraction, mean_delta and peak_delta (luma, absolute), luma_sign (+1 where B is brighter, -1 where darker),
mean_signed_delta and edge_contrast (the mean luma of B on the inner border of the mask against the ring outside it).

contrib reads a contribution image (the capture with atmoDebug=contrib, see unity/Assets/ReDefinitionAtmospheres/Include/
AtmoContribution.cginc: black where the feature contributes nothing, the feature's colour times its contribution elsewhere) and
prints the pixels contributed, their share of the frame and the overlap with an expected region.

interaction works on tests/series/interaction-matrix.json: expand prints the rows in the order they run (the baseline first, then the
rows with a non-zero expected effect, grouped by mod set so that the mod set changes as seldom as possible; with --cover also rows
that cover every remaining pair of axis values, without an expectation); judge compares a measured table (row id -> metric -> value)
with the expectations written first and prints every deviation (exit code 1 if there is one).
"""
import argparse
import itertools
import json
import os
import sys

import numpy as np
from PIL import Image

IDENTITY_KEYS = ("pose", "seed", "ut", "state", "resolution", "upscaler")
POSE_TOLERANCE = 1e-6
EDGE_RING = 3


class Refusal(Exception):
    """The two sets are not from identical scenes (or a sidecar is missing)."""


# ---- images and sidecars --------------------------------------------------------------------------------------------------------

def load_image(path):
    """An image as a float32 array (h, w, 3) in 0..1: a PNG or other Pillow format, or a .npy array."""
    if path.endswith(".npy"):
        a = np.load(path).astype(np.float32)
        return a if a.ndim == 3 else np.repeat(a[..., None], 3, axis=2)
    return np.asarray(Image.open(path).convert("RGB")).astype(np.float32) / 255.0


def sidecar_path(path):
    return os.path.splitext(path)[0] + ".json"


def load_sidecar(path):
    """The scene description beside an image; raises Refusal where there is none."""
    side = sidecar_path(path)
    if not os.path.exists(side):
        raise Refusal("%s has no sidecar %s" % (path, os.path.basename(side)))
    with open(side, encoding="utf-8") as f:
        return json.load(f)


def _same(a, b):
    if isinstance(a, (list, tuple)) and isinstance(b, (list, tuple)):
        return len(a) == len(b) and all(_same(x, y) for x, y in zip(a, b))
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool):
        return abs(a - b) <= POSE_TOLERANCE * max(1.0, abs(a), abs(b))
    return a == b


def check_identical(side_a, side_b, name):
    """Raises Refusal unless pose, seed, ut, state, resolution and upscaler agree."""
    for key in IDENTITY_KEYS:
        if key not in side_a or key not in side_b:
            raise Refusal("%s: the sidecar lacks %s" % (name, key))
        if not _same(side_a[key], side_b[key]):
            raise Refusal("%s: %s differs (A %s, B %s); the scenes are not identical" % (name, key, side_a[key], side_b[key]))


# ---- the difference -------------------------------------------------------------------------------------------------------------

def luma(a):
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def _shift(m, dy, dx):
    out = np.zeros_like(m)
    h, w = m.shape
    ys, yd = (slice(dy, h), slice(0, h - dy)) if dy >= 0 else (slice(0, h + dy), slice(-dy, h))
    xs, xd = (slice(dx, w), slice(0, w - dx)) if dx >= 0 else (slice(0, w + dx), slice(-dx, w))
    out[ys, xs] = m[yd, xd]
    return out


def erode(m, n=1):
    for _ in range(n):
        m = m & _shift(m, 1, 0) & _shift(m, -1, 0) & _shift(m, 0, 1) & _shift(m, 0, -1)
    return m


def dilate(m, n=1):
    for _ in range(n):
        m = m | _shift(m, 1, 0) | _shift(m, -1, 0) | _shift(m, 0, 1) | _shift(m, 0, -1)
    return m


def difference(a, b, threshold, region=None):
    """The difference of two images and its metrics. region (x0, y0, x1, y1) limits the mask to that rectangle."""
    if a.shape != b.shape:
        raise Refusal("the images differ in size (%s against %s)" % (a.shape, b.shape))
    ya, yb = luma(a), luma(b)
    dl = yb - ya
    chroma = np.sqrt(((b[..., 0] - yb) - (a[..., 0] - ya)) ** 2 + ((b[..., 2] - yb) - (a[..., 2] - ya)) ** 2)
    mask = (np.abs(dl) > threshold) | (chroma > threshold)
    if region is not None:
        x0, y0, x1, y1 = region
        keep = np.zeros_like(mask)
        keep[y0:y1, x0:x1] = True
        mask &= keep
    count = int(mask.sum())
    m = {"area_fraction": count / mask.size, "pixels": count, "mean_delta": 0.0, "peak_delta": 0.0, "luma_sign": 0.0,
         "mean_signed_delta": 0.0, "edge_contrast": 0.0, "chroma_mean": 0.0}
    if count:
        d = dl[mask]
        m["mean_delta"] = float(np.abs(d).mean())
        m["peak_delta"] = float(np.abs(d).max())
        m["mean_signed_delta"] = float(d.mean())
        m["luma_sign"] = float((d > 0).sum() - (d < 0).sum()) / count
        m["chroma_mean"] = float(chroma[mask].mean())
        inner = mask & ~erode(mask, EDGE_RING)
        ring = dilate(mask, EDGE_RING) & ~mask
        if inner.any() and ring.any():
            outside = float(yb[ring].mean())
            m["edge_contrast"] = abs(float(yb[inner].mean()) - outside) / outside if outside > 1e-6 else 0.0
    return m, mask, dl


def write_outputs(folder, stem, mask, dl):
    """The mask and the signed difference (red where B is brighter, blue where darker, scaled so 0.25 is full)."""
    os.makedirs(folder, exist_ok=True)
    Image.fromarray((mask * 255).astype(np.uint8)).save(os.path.join(folder, stem + "_mask.png"))
    s = np.clip(np.abs(dl) * 4.0, 0, 1)
    heat = np.zeros(mask.shape + (3,), np.float32)
    heat[..., 0] = np.where(dl > 0, s, 0)
    heat[..., 2] = np.where(dl < 0, s, 0)
    Image.fromarray((heat * 255).astype(np.uint8)).save(os.path.join(folder, stem + "_diff.png"))


def pairs(path_a, path_b):
    """(name, file A, file B) of two files or of the images of two folders with the same stem."""
    if os.path.isfile(path_a) and os.path.isfile(path_b):
        return [(os.path.splitext(os.path.basename(path_a))[0], path_a, path_b)]
    exts = (".png", ".npy", ".jpg", ".bmp")
    names_a = {os.path.splitext(f)[0]: f for f in os.listdir(path_a) if f.lower().endswith(exts)}
    names_b = {os.path.splitext(f)[0]: f for f in os.listdir(path_b) if f.lower().endswith(exts)}
    missing = sorted(set(names_a) ^ set(names_b))
    if missing:
        raise Refusal("the sets do not hold the same images: %s" % ", ".join(missing))
    return [(n, os.path.join(path_a, names_a[n]), os.path.join(path_b, names_b[n])) for n in sorted(names_a)]


def compare(path_a, path_b, threshold, out=None, region=None):
    """A/B of two sets; returns {name: metrics}. Raises Refusal where the scenes are not identical."""
    results = {}
    for name, fa, fb in pairs(path_a, path_b):
        check_identical(load_sidecar(fa), load_sidecar(fb), name)
        metrics, mask, dl = difference(load_image(fa), load_image(fb), threshold, region)
        results[name] = metrics
        if out:
            write_outputs(out, name, mask, dl)
    return results


# ---- the contribution mask ------------------------------------------------------------------------------------------------------

def contribution(image, threshold, region=None, region_mask=None):
    """The figures of a contribution image: pixels contributed (any channel above the threshold), their share of the frame, and the
    overlap with an expected region (a rectangle or a mask): inside = the share of the contributed pixels that lie in it, coverage =
    the share of the region that is contributed."""
    contributed = image.max(axis=2) > threshold
    out = {"pixels": int(contributed.sum()), "share": float(contributed.mean())}
    expected = None
    if region_mask is not None:
        expected = region_mask > 0.5
    elif region is not None:
        expected = np.zeros_like(contributed)
        x0, y0, x1, y1 = region
        expected[y0:y1, x0:x1] = True
    if expected is not None:
        both = int((contributed & expected).sum())
        out["inside"] = both / out["pixels"] if out["pixels"] else 0.0
        out["coverage"] = both / int(expected.sum()) if expected.any() else 0.0
        out["expected_pixels"] = int(expected.sum())
    return out


# ---- the interaction matrix -----------------------------------------------------------------------------------------------------

MODSET_AXES = ("eve", "scatterer", "deferred", "tufx")


def pairwise(axes, forbidden=()):
    """Rows (dicts axis -> value) that together contain every pair of values of two different axes at least once (greedy), in the
    order of the axes' values. forbidden is a list of dicts that no row may contain as a subset."""
    names = list(axes)

    def allowed(row):
        return not any(all(row.get(k) == v for k, v in f.items()) for f in forbidden)

    todo = set()
    for a, b in itertools.combinations(names, 2):
        for va in axes[a]:
            for vb in axes[b]:
                if allowed({a: va, b: vb}):
                    todo.add(((a, va), (b, vb)))
    rows = []
    while todo:
        row = {}
        # Seed with the first uncovered pair, then add per axis the value that covers the most uncovered pairs and keeps the row allowed.
        (ka, va), (kb, vb) = min(todo, key=lambda p: (names.index(p[0][0]), str(p)))
        row[ka], row[kb] = va, vb
        for n in names:
            if n in row:
                continue
            best, gain = None, -1
            for v in axes[n]:
                trial = dict(row)
                trial[n] = v
                if not allowed(trial):
                    continue
                g = sum(1 for k, w in row.items() if ((k, w), (n, v)) in todo or ((n, v), (k, w)) in todo)
                if g > gain:
                    best, gain = v, g
            if best is None:
                best = axes[n][0]
            row[n] = best
        for a, b in itertools.combinations(names, 2):
            todo.discard(((a, row[a]), (b, row[b])))
            todo.discard(((b, row[b]), (a, row[a])))
        rows.append(row)
    return rows


def _modset_key(row):
    return tuple(row.get("set", {}).get(a, "") for a in MODSET_AXES)


def expand(matrix, cover=False):
    """The rows to run: the baseline first, then the rows with a non-zero expected effect (grouped by mod set); with cover, the rows
    that complete the pairwise coverage, marked without expectation."""
    baseline = dict(matrix["baseline"])
    rows = [{"id": "baseline", "set": baseline, "expect": None, "needs_modset_change": False}]
    effect_rows = [r for r in matrix["rows"] if r.get("expect") and r["expect"].get("effect", "none") != "none"]
    base_mod = _modset_key({"set": baseline})
    effect_rows.sort(key=lambda r: (_modset_key({"set": {**baseline, **r["set"]}}) != base_mod, _modset_key({"set": {**baseline, **r["set"]}}), r["id"]))
    for r in effect_rows:
        full = {**baseline, **r["set"]}
        rows.append({"id": r["id"], "set": full, "expect": r["expect"], "needs_modset_change": _modset_key({"set": full}) != base_mod})
    if cover:
        have = [x["set"] for x in rows]
        for i, row in enumerate(pairwise(matrix["axes"], matrix.get("forbidden", []))):
            if not any(all(h.get(k) == v for k, v in row.items()) for h in have):
                rows.append({"id": "cover%02d" % (i + 1), "set": row, "expect": None, "needs_modset_change": _modset_key({"set": row}) != base_mod})
    return rows


def judge_row(row, measured, base_measured):
    """The deviation of one row (a string) or None. expect: metric, effect (increase | decrease | change | none), min / max of the
    effect (measured minus baseline unless absolute is true). min and max bound the size of the effect without its sign: the sign is
    judged by increase and decrease, and change accepts either sign. Rows with effect none are not run (see expand)."""
    e = row["expect"]
    metric = e["metric"]
    if metric not in measured or metric not in base_measured:
        return "%s: metric %s was not measured" % (row["id"], metric)
    value, base = measured[metric], base_measured[metric]
    delta = value if e.get("absolute") else value - base
    kind = e.get("effect", "change")
    if kind == "increase" and not delta > 0:
        return "%s: %s expected to increase, measured %+.4g" % (row["id"], metric, delta)
    if kind == "decrease" and not delta < 0:
        return "%s: %s expected to decrease, measured %+.4g" % (row["id"], metric, delta)
    if kind == "change" and delta == 0:
        return "%s: %s expected to change, measured no change" % (row["id"], metric)
    if "min" in e and abs(delta) < e["min"]:
        return "%s: %s effect %.4g is below the expected minimum %.4g" % (row["id"], metric, abs(delta), e["min"])
    if "max" in e and abs(delta) > e["max"]:
        return "%s: %s effect %.4g is above the expected maximum %.4g" % (row["id"], metric, abs(delta), e["max"])
    return None


def judge(matrix, measured):
    """Every deviation of the measured table (row id -> metric -> value) from the expectations; rows without expectation are skipped."""
    deviations = []
    if "baseline" not in measured:
        return ["baseline: the baseline row was not measured"]
    for row in expand(matrix):
        if row["expect"] is None:
            continue
        if row["id"] not in measured:
            deviations.append("%s: the row was not measured" % row["id"])
            continue
        d = judge_row(row, measured[row["id"]], measured["baseline"])
        if d:
            deviations.append(d)
    return deviations


# ---- the self test: positive and negative controls -------------------------------------------------------------------------------

def _scene(h=180, w=320, seed=7):
    """A synthetic scene: a smooth gradient with fixed noise (the same for every call with the same seed)."""
    rng = np.random.RandomState(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    base = 0.35 + 0.25 * (y / h) + 0.1 * (x / w)
    img = np.repeat(base[..., None], 3, axis=2) * np.array([1.0, 0.97, 0.92], np.float32)
    return np.clip(img + rng.normal(0, 0.002, img.shape).astype(np.float32), 0, 1)


def _write(folder, name, image, side):
    os.makedirs(folder, exist_ok=True)
    Image.fromarray((np.clip(image, 0, 1) * 255 + 0.5).astype(np.uint8)).save(os.path.join(folder, name + ".png"))
    with open(os.path.join(folder, name + ".json"), "w") as f:
        json.dump(side, f)


def selftest():
    import shutil
    import tempfile
    root = tempfile.mkdtemp(prefix="atmo-ab-selftest-")
    side = {"pose": [10.0, 5.0, -3.0, 40.0, 5.0], "seed": 1234, "ut": 100.5, "state": "s1", "resolution": [320, 180], "upscaler": "native"}
    try:
        a = _scene()
        # Positive control: a planted square of known area at three sizes; the mask area must be within 5 % of the truth.
        for size in (12, 30, 60):
            b = a.copy()
            b[40:40 + size, 100:100 + size] *= 0.7
            da, db = os.path.join(root, "A%d" % size), os.path.join(root, "B%d" % size)
            _write(da, "view", a, side)
            _write(db, "view", b, side)
            result = compare(da, db, 0.02, os.path.join(root, "out%d" % size))["view"]
            truth = size * size / a[..., 0].size
            assert abs(result["area_fraction"] - truth) <= 0.05 * truth, (size, result["area_fraction"], truth)
            assert result["luma_sign"] == -1.0 and result["peak_delta"] > 0.05 and result["edge_contrast"] > 0.1, result
            assert os.path.exists(os.path.join(root, "out%d" % size, "view_mask.png"))
            print("planted %2dx%-2d: area %.5f truth %.5f (%.2f %%), sign %+.0f, peak %.3f, edge %.3f"
                  % (size, size, result["area_fraction"], truth, 100 * abs(result["area_fraction"] - truth) / truth, result["luma_sign"],
                     result["peak_delta"], result["edge_contrast"]))
        # Negative control: nothing planted gives an empty difference.
        _write(os.path.join(root, "N1"), "view", a, side)
        _write(os.path.join(root, "N2"), "view", _scene(), side)
        empty = compare(os.path.join(root, "N1"), os.path.join(root, "N2"), 0.02)["view"]
        assert empty["area_fraction"] == 0.0 and empty["pixels"] == 0, empty
        print("nothing planted: area %.1f" % empty["area_fraction"])
        # Refusal where the pose or the seed differ, or a sidecar is missing.
        for key, value in (("pose", [10.0, 5.0, -3.0, 41.0, 5.0]), ("seed", 99), ("upscaler", "dlss")):
            other = dict(side)
            other[key] = value
            _write(os.path.join(root, "R_" + key), "view", a, other)
            try:
                compare(os.path.join(root, "N1"), os.path.join(root, "R_" + key), 0.02)
                raise AssertionError("no refusal for a different " + key)
            except Refusal as e:
                print("refused (%s): %s" % (key, e))
        os.remove(os.path.join(root, "N2", "view.json"))
        try:
            compare(os.path.join(root, "N1"), os.path.join(root, "N2"), 0.02)
            raise AssertionError("no refusal for a missing sidecar")
        except Refusal:
            pass
        # The contribution mask equals the planted pixels exactly, and its overlap with the expected region.
        aov = np.zeros((180, 320, 3), np.float32)
        aov[50:90, 120:180] = (1.0, 0.5, 0.0)
        got = contribution(aov, 0.01, region=(110, 40, 190, 100))
        assert got["pixels"] == 40 * 60 and got["inside"] == 1.0 and abs(got["coverage"] - 2400 / 4800) < 1e-9, got
        off = contribution(aov, 0.01, region=(0, 0, 50, 50))
        assert off["inside"] == 0.0 and contribution(np.zeros_like(aov), 0.01)["pixels"] == 0
        print("contribution: %d pixels of %d planted, inside %.2f, coverage %.2f" % (got["pixels"], 2400, got["inside"], got["coverage"]))
        # The pairwise sampling covers every pair with fewer rows than the product.
        axes = {"a": [0, 1, 2], "b": [0, 1, 2], "c": [0, 1, 2], "d": [0, 1]}
        rows = pairwise(axes)
        for (x, y) in itertools.combinations(axes, 2):
            for vx in axes[x]:
                for vy in axes[y]:
                    assert any(r[x] == vx and r[y] == vy for r in rows), (x, vx, y, vy)
        assert len(rows) < 3 * 3 * 3 * 2
        print("pairwise: %d rows cover all pairs of 4 axes (product %d)" % (len(rows), 3 * 3 * 3 * 2))
        # The interaction runner: a right expectation passes, a deliberately wrong one is reported as a deviation.
        matrix = {"axes": {"feature": ["off", "fog"], "eve": ["on", "off"]}, "baseline": {"feature": "off", "eve": "on"},
                  "rows": [{"id": "R1", "set": {"feature": "fog"}, "expect": {"metric": "m", "effect": "increase", "min": 0.05, "max": 0.5}},
                           {"id": "R2", "set": {"feature": "fog", "eve": "off"}, "expect": {"metric": "m", "effect": "decrease"}}]}
        measured = {"baseline": {"m": 0.2}, "R1": {"m": 0.4}, "R2": {"m": 0.5}}
        found = judge(matrix, measured)
        assert len(found) == 1 and found[0].startswith("R2"), found
        assert [r["id"] for r in expand(matrix)][0] == "baseline"
        print("interaction: baseline first, wrong expectation reported: " + found[0])
        print("selftest passed")
    finally:
        shutil.rmtree(root, ignore_errors=True)


# ---- command line ---------------------------------------------------------------------------------------------------------------

def _region(text):
    return tuple(int(v) for v in text.split(",")) if text else None


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("compare")
    c.add_argument("a")
    c.add_argument("b")
    c.add_argument("--threshold", type=float, default=0.02)
    c.add_argument("--out")
    c.add_argument("--region")
    c.add_argument("--expect-area", type=float, help="the expected area fraction; the exit code is 1 when it is missed")
    c.add_argument("--tol", type=float, default=0.05, help="relative tolerance of --expect-area")
    k = sub.add_parser("contrib")
    k.add_argument("image")
    k.add_argument("--threshold", type=float, default=0.01)
    k.add_argument("--region")
    k.add_argument("--region-mask")
    i = sub.add_parser("interaction")
    i.add_argument("action", choices=["expand", "judge"])
    i.add_argument("matrix")
    i.add_argument("measured", nargs="?")
    i.add_argument("--cover", action="store_true")
    sub.add_parser("selftest")
    args = p.parse_args(argv)
    if args.cmd == "selftest":
        selftest()
        return 0
    if args.cmd == "compare":
        try:
            results = compare(args.a, args.b, args.threshold, args.out, _region(args.region))
        except Refusal as e:
            print("REFUSED: %s" % e)
            return 2
        code = 0
        for name, m in results.items():
            print("%-24s " % name + " ".join("%s=%.5g" % (k_, v) for k_, v in m.items()))
            if args.expect_area is not None and abs(m["area_fraction"] - args.expect_area) > args.tol * args.expect_area:
                print("  area %.5g misses the expected %.5g by more than %.0f %%" % (m["area_fraction"], args.expect_area, 100 * args.tol))
                code = 1
        return code
    if args.cmd == "contrib":
        mask = np.asarray(Image.open(args.region_mask).convert("L")).astype(np.float32) / 255.0 if args.region_mask else None
        print(json.dumps(contribution(load_image(args.image), args.threshold, _region(args.region), mask)))
        return 0
    with open(args.matrix, encoding="utf-8") as f:
        matrix = json.load(f)
    if args.action == "expand":
        for r in expand(matrix, args.cover):
            print("%-12s %s%s%s" % (r["id"], json.dumps(r["set"], sort_keys=True), "  [mod set change]" if r["needs_modset_change"] else "",
                                    "  expect " + json.dumps(r["expect"]) if r["expect"] else ""))
        return 0
    with open(args.measured, encoding="utf-8") as f:
        measured = json.load(f)
    found = judge(matrix, measured)
    for d in found:
        print("DEVIATION " + d)
    print("%d deviation(s)" % len(found))
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
