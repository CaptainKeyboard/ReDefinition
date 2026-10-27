"""What DLSS frame generation does to KSP's HUD, measured in the game.

KSP runs on the launch pad with the HUD showing and the camera turning on the scene probe's
sweep (src/Probe/FlipProbe.cs, -redefinitionProbeShowHud). It runs once per case of a DLSS-G
configuration (the package's preset, the proxy's keys). The proxy's screen recording takes
the frames the monitor shows, generated frames included. Then the HUD's regions of each
generated frame are compared with the rendered frames either side of it.

The KSP install's settings files are copied before the run and put back after it
(tools/benchmark_matrix.py's Install). The package's files and the experimental DLLs this
tool is told to use (--dxgi, --mod) are removed and put back too. Python 3 with numpy and
Pillow.

Usage: python tools/hud_probe.py <command>. The command run <out folder> [--cases auto,c,...]
starts KSP once per case. The command analyze <case folder>... [--regions ...] gives the
numbers, from the recordings. The command truth <reference> <folder>... [--json f] compares each
recording against a reference one. The command crops <case folder> <out folder> writes
side-by-side crops of the HUD.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import benchmark_matrix as bm  # noqa: E402
import imaging  # noqa: E402

DEFAULT_PACKAGE = os.path.join(os.path.dirname(HERE), ".local", "dlssg_x")
MOD_REL = os.path.join("GameData", "ReDefinition", "Plugins", "ReDefinition.dll")
ROOT_SETTINGS = "settings.cfg"

# The cases: the package's ini values (section -> key -> value) and the proxy's keys.
# Level 2 in the package's own log: its configuration and capability lines, which say what
# the preset pinned (preset_pinned) and which planes the game handed over.
LOG2 = {"Logging": {"Level": "2", "FrameTiming": "1"}}


def case(preset, proxy=None, ini=None, arguments=None, launch=None):
    """One case: the package's ini with the DLSS-G preset and level 2 logging plus the sections given, and the
    proxy's keys. KSP's extra arguments for the flip sweep are in arguments, or in launch the whole command line
    in place of the sweep's."""
    ini_values = {"DLSSG": {"Preset": preset}}
    ini_values.update(LOG2)
    for section, keys in (ini or {}).items():
        ini_values.setdefault(section, {}).update(keys)
    return {"dlssg_ini": ini_values, "proxy": dict(proxy or {}), "arguments": list(arguments or []),
            "launch": launch}


CASES = {
    "auto": case("Auto"),
    "c": case("C"),
    "auto-nohud": case("Auto", {"hudLessColour": "0"}),
    "c-nohud": case("C", {"hudLessColour": "0"}),
    # DLSS-G's UI plane and user interface recomposition (dlssgUiRecomposition): the UI as opaque, or at its least alpha.
    "auto-ui": case("Auto", {"dlssgUiRecomposition": "1"}),
    "auto-uimin": case("Auto", {"dlssgUiRecomposition": "1", "dlssgUiAlpha": "minimal"}),
    "c-ui": case("C", {"dlssgUiRecomposition": "1"}),
    # The truth: no frame generation, the sweep at half the step, so that a rendered frame
    # stands at every pose a generated frame stands at.
    "ref": case("Auto", {"frameGeneration": "0"}, arguments=["-redefinitionProbeHalfStep"]),
    "ref2": case("Auto", {"frameGeneration": "0"}, arguments=["-redefinitionProbeHalfStep"]),
    # The multiplier chosen while the game runs (OwnSettings.FrameGenerationMultiplier, as the settings window sets
    # it), each for 12 s with the settings window open on Display in the shot (-redefinitionProbeSettingsWindow).
    "choice": case("Auto", launch=["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeShot", "choice",
                                   "-redefinitionProbeShotVariant", "none", "-redefinitionProbeSettingsWindow", "Display",
                                   "-redefinitionProbeShotIni",
                                   ";".join("mod.frameGenerationMultiplier=" + m for m in ("2x", "3x", "4x", "6x", "Dynamic"))
                                   + ";mod.frameGeneration=False"]),
    # The same with a package that allows 2x only (--max-generated-frames 1): what is not offered, and why.
    "choice-limited": case("Auto", launch=["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeShot", "choice",
                                           "-redefinitionProbeShotVariant", "none", "-redefinitionProbeSettingsWindow",
                                           "Display", "-redefinitionProbeShotIni",
                                           "mod.frameGenerationMultiplier=4x;mod.frameGenerationMultiplier=Dynamic"]),
    # The diagnostics window's Overview and Frame generation pages, the multiplier set to 3x between them.
    "diagnostics": case("Auto", launch=["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeShot", "choice",
                                        "-redefinitionProbeShotVariant", "none", "-redefinitionProbeDiagnostics",
                                        "overview@category=Overview|fg@category=FrameGeneration,set=frameGenerationMultiplier:1"
                                        "|fg3@category=FrameGeneration"]),
    # DLSS-G's dynamic mode with and without the latency brake, and a fixed 3x as the reference, 12 s each: the
    # proxy's log holds the presented/rendered and latency lines of each.
    "dynamic-brake": case("Auto", {"reportSeconds": "1"}, launch=[
        "-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeShot", "dyn", "-redefinitionProbeShotVariant",
        "none", "-redefinitionProbeShotIni",
        "mod.frameGenerationMultiplier=Dynamic,latencyBrake=1,latencyBrakeWithDlssg=1;"
        "mod.frameGenerationMultiplier=Dynamic,latencyBrake=0;"
        "mod.frameGenerationMultiplier=3x,latencyBrake=1,latencyBrakeWithDlssg=1;"
        "mod.frameGenerationMultiplier=3x,latencyBrake=0"]),
    # The default multiplier (Dynamic) where the package does not support dynamic generation (--max-generated-frames 1):
    # it runs as 2x, and the settings window says so.
    "default": case("Auto", launch=["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeShot", "choice",
                                    "-redefinitionProbeShotVariant", "none", "-redefinitionProbeSettingsWindow", "Display",
                                    "-redefinitionProbeShotIni", "latencyBrake=1"]),
    # Frame generation's inputs dumped (HUD-less image and backbuffer), for which pixels the HUD covers.
    "dump": case("Auto", arguments=["-redefinitionProbeDumps"]),
}


def sha(path):
    """The SHA-256 of the file as hex, to tell which build of a DLL is in place."""
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def set_root_display(text, width, height, fullscreen):
    """KSP's settings.cfg text with the screen size and full screen set; raises where a key is missing."""
    for key, value in (("SCREEN_RESOLUTION_WIDTH", width), ("SCREEN_RESOLUTION_HEIGHT", height),
                       ("FULLSCREEN", "True" if fullscreen else "False")):
        text, n = re.subn(r"(?m)^(\s*%s\s*=\s*).*$" % key, lambda m: m.group(1) + str(value), text)
        if n == 0:
            raise RuntimeError("no %s in KSP's settings.cfg" % key)
    return text


def run(args):
    """Start KSP once per case with its package ini, proxy keys and settings, and copy each run's logs, shots,
    recording and dumps into <out>/<case>; the install's files and DLLs are put back after."""
    install = bm.Install(args.ksp)
    if install.running():
        sys.exit("KSP is running; close it first.")
    os.makedirs(args.out, exist_ok=True)
    wanted = [c.strip() for c in args.cases.split(",") if c.strip()]
    for c in wanted:
        if c not in CASES:
            sys.exit("unknown case %s (known: %s)" % (c, ", ".join(CASES)))
    if install.package_in_place():
        sys.exit("The DLSS-G package is already next to KSP_x64.exe; the run would remove it.")

    snapshot = os.path.join(args.out, "snapshot")
    shutil.rmtree(snapshot, ignore_errors=True)
    print("snapshot:", install.snap(snapshot), "files")
    kept = _keep_dlls(install, args)
    try:
        for rel, new in (("dxgi.dll", args.dxgi), (MOD_REL, args.mod)):
            if new:
                shutil.copy2(new, install.path(rel))
        with open(install.path(bm.SETTINGS_REL), encoding="utf-8") as f:
            mod_settings = f.read()
        with open(install.path(bm.PROXY_INI), encoding="utf-8", errors="replace") as f:
            ini_template = f.read()
        with open(install.path(ROOT_SETTINGS), encoding="utf-8") as f:
            root_settings = f.read()
        if not args.no_profile:
            # Medium on this machine, never the player's own settings.
            lines, mod_settings = bm.apply_profile(install, args.profile, mod_settings, args.out)
            print("\n".join(lines))
        mod_settings = bm.set_cfg_values(mod_settings, {"enabled": "True", "frameGeneration": "True", "quality": "Quality"})
        root_settings = set_root_display(root_settings, args.width, args.height, False)
        for case in wanted:
            _run_case(install, case, args, mod_settings, ini_template, root_settings)
    finally:
        _put_back(install, kept, snapshot)


def _keep_dlls(install, args):
    """Copies the install's dxgi.dll and mod DLL aside where the run is told to use others (--dxgi, --mod), so
    that they can be put back; returns {path in the install: the copy}."""
    # The DLLs put in place are put back as they were.
    kept = {}
    for rel, new in (("dxgi.dll", args.dxgi), (MOD_REL, args.mod)):
        if new:
            kept[rel] = os.path.join(args.out, "kept-" + os.path.basename(rel))
            shutil.copy2(install.path(rel), kept[rel])
            print("installed %s: %s -> %s" % (rel, sha(kept[rel])[:12], sha(new)[:12]))
    return kept


def _run_case(install, case, args, mod_settings, ini_template, root_settings):
    """One case: writes the settings and the proxy's keys, puts the package in place, runs KSP with the case's
    arguments, and copies what the run wrote into <out>/<case>."""
    spec = CASES[case]
    print("case", case)
    proxy = {"frameGeneration": "1", "frameGenerationTechnique": "dlss", "fgMinimumFps": "0", "reportSeconds": "1",
             "dlssFrameGeneration": "1"}
    proxy.update(spec["proxy"])
    proxy.update(dict(kv.split("=", 1) for kv in args.proxy_key))
    with open(install.path(bm.SETTINGS_REL), "w", encoding="utf-8") as f:
        f.write(mod_settings)
    with open(install.path(ROOT_SETTINGS), "w", encoding="utf-8") as f:
        f.write(root_settings)
    with open(install.path(bm.PROXY_INI), "w", encoding="utf-8", newline="") as f:
        f.write(bm.set_ini_values(ini_template, proxy))
    launched = time.time()
    try:
        install.put_package(args.package, args.max_generated_frames, spec["dlssg_ini"])
        if spec["launch"]:
            outcome = install.launch(spec["launch"] + ["-popupwindow"] + args.arg, 420)
        else:
            outcome = install.launch(["-redefinitionProbe", "-redefinitionProbeFlip", "-redefinitionProbeShowHud",
                                      "-redefinitionProbeFpsCap", str(args.fps_cap), "-popupwindow"]
                                     + spec["arguments"] + args.arg, 420)
    finally:
        logs = install.path(bm.PACKAGE_DIR, "logs")
        out_case = os.path.join(args.out, case)
        os.makedirs(out_case, exist_ok=True)
        if os.path.isdir(logs):
            shutil.copytree(logs, os.path.join(out_case, "dlssg-logs"), dirs_exist_ok=True)
        install.remove_package()
    recordings = _collect_run(install, out_case, launched)
    with open(os.path.join(out_case, "outcome.txt"), "w") as f:
        f.write(outcome + "\n")
    print("  ", outcome, "->", out_case, "(%d recording)" % len(recordings))


def _collect_run(install, out_case, launched):
    """Copies what a run that began at `launched` wrote into out_case: the logs, the probe's shots, the screen
    recording and the frame generation inputs' dump. Returns the recordings found."""
    fresh = lambda p: os.path.exists(p) and os.path.getmtime(p) >= launched
    for name in ("KSP.log", "ReDefinitionProxy.log"):
        if fresh(install.path(name)):
            shutil.copy2(install.path(name), os.path.join(out_case, name))
    runs = install.path("ReDefinitionCaptures")
    shots = [os.path.join(runs, d) for d in os.listdir(runs)
             if d.endswith("-run") and os.path.getmtime(os.path.join(runs, d)) >= launched]
    if shots:
        shutil.copytree(max(shots, key=os.path.getmtime), os.path.join(out_case, "shots"), dirs_exist_ok=True)
    recordings = [os.path.join(runs, d) for d in os.listdir(runs)
                  if re.fullmatch(r"\d{8}-\d{6}", d) and os.path.getmtime(os.path.join(runs, d)) >= launched]
    if recordings:
        shutil.copytree(max(recordings, key=os.path.getmtime), os.path.join(out_case, "frames"), dirs_exist_ok=True)
    dumps = [os.path.join(runs, d) for d in os.listdir(runs)
             if d.endswith("-inputs") and os.path.getmtime(os.path.join(runs, d)) >= launched]
    if dumps:
        shutil.copytree(max(dumps, key=os.path.getmtime), os.path.join(out_case, "inputs"), dirs_exist_ok=True)
    return recordings


def _put_back(install, kept, snapshot):
    """Takes the package away, puts the kept DLLs and the snapshot of the settings back, and says what changed."""
    install.remove_package()
    for rel, saved in kept.items():
        shutil.copy2(saved, install.path(rel))
    report = install.restore(snapshot)
    print("restored:", len(report), "item(s)")
    for rel, saved in kept.items():
        print("  %s now %s (was %s)" % (rel, sha(install.path(rel))[:12], sha(saved)[:12]))


# ----------------------------------------------------------------------------- analysis

def load_frames(folder):
    """The recording's frames in order, a frame the monitor showed again dropped, with the
    game frame each carries (frames.txt)."""
    import numpy as np
    from PIL import Image
    table = {}
    path = os.path.join(folder, "frames.txt")
    if os.path.exists(path):
        with open(path) as f:
            next(f)
            for line in f:
                p = line.split()
                if len(p) >= 5:
                    table[p[0]] = (float(p[1]), int(p[4]))
    frames, previous = [], None
    for n in sorted(x for x in os.listdir(folder) if x.endswith(".png")):
        a = np.asarray(Image.open(os.path.join(folder, n)).convert("RGB"), dtype=np.float32)
        if previous is not None and np.abs(a - previous).mean() < 1e-3:
            continue
        previous = a
        t, g = table.get(n[:-4], (0.0, -1))
        frames.append({"name": n[:-4], "image": a, "time": t, "game": g})
    return frames


def classify(frames):
    """Real or generated. DLSS-G presents its generated frame after the Present of the newer
    rendered frame and before that frame, so the frames sharing a game frame number end with
    the rendered one. The number is stamped from the proxy's Present times, so the order is
    settled by the strict alternation of 2x instead. Its phase is voted over a window of frames
    by those labels."""
    groups = []
    for f in frames:
        if groups and groups[-1][0]["game"] == f["game"]:
            groups[-1].append(f)
        else:
            groups.append([f])
    for g in groups:
        for i, f in enumerate(g):
            f["label"] = "real" if i == len(g) - 1 else "generated"
    n = len(frames)
    for i, f in enumerate(frames):
        votes = [0, 0]
        for j in range(max(0, i - 8), min(n, i + 9)):
            votes[(j % 2) if frames[j]["label"] == "real" else 1 - (j % 2)] += 1
        phase = 0 if votes[0] >= votes[1] else 1
        f["kind"] = "real" if i % 2 == phase else "generated"
    return groups


def parse_regions(text, width, height):
    """name:x0,y0,x1,y1 in fractions of the frame, separated by ';'."""
    regions = {}
    for item in text.split(";"):
        name, box = item.split(":")
        x0, y0, x1, y1 = [float(v) for v in box.split(",")]
        regions[name] = (int(x0 * width), int(y0 * height), int(x1 * width), int(y1 * height))
    return regions


def gradient(a):
    """The magnitude of the grey image's forward differences, one pixel smaller each way."""
    import numpy as np
    g = imaging.grey(a)
    gx = np.abs(np.diff(g, axis=1))[:-1, :]
    gy = np.abs(np.diff(g, axis=0))[:, :-1]
    return np.sqrt(gx * gx + gy * gy)


def static_mask(reals, box, threshold):
    """The pixels of the box that stay put through the whole recording while the camera
    turns: the HUD where it is opaque (the scene behind it moves, and a pixel of it does not)."""
    import numpy as np
    x0, y0, x1, y1 = box
    stack = np.stack([imaging.grey(f["image"][y0:y1, x0:x1]) for f in reals])
    return stack.std(axis=0) < threshold


def psnr(mse):
    """The PSNR in dB of a mean square error on 8-bit levels; 99 for none."""
    import math
    return 99.0 if mse <= 1e-9 else 10.0 * math.log10(255.0 * 255.0 / mse)


def measure(frames, regions, threshold=1.0, bad_levels=12.0):
    """Per region, over every generated frame with a rendered one on each side, it finds the
    HUD's pixels that never move in the recording (the static mask). There it measures the
    generated frame's difference to the mean of the two rendered frames: mean absolute, PSNR,
    and the share of pixels off by more than bad_levels. It also measures the frame's edge
    sharpness against theirs."""
    import numpy as np
    reals = [f for f in frames if f["kind"] == "real"]
    result = {}
    for name, box in regions.items():
        x0, y0, x1, y1 = box
        mask = static_mask(reals, box, threshold)
        rows = []
        for i, f in enumerate(frames):
            if f["kind"] != "generated":
                continue
            before = next((g for g in reversed(frames[:i]) if g["kind"] == "real"), None)
            after = next((g for g in frames[i + 1:] if g["kind"] == "real"), None)
            if before is None or after is None:
                continue
            gen, a, b = (fr["image"][y0:y1, x0:x1] for fr in (f, before, after))
            target = 0.5 * (a + b)
            d = np.abs(gen - target)[mask]
            real_d = np.abs(a - b)[mask]
            ge, ae, be = gradient(gen), gradient(a), gradient(b)
            edge = np.zeros(ge.shape, dtype=bool)
            edge |= mask[:-1, :-1]
            sharp = ge[edge].mean() / max(1e-6, 0.5 * (ae[edge].mean() + be[edge].mean()))
            whole = np.abs(gen - target).mean()
            rows.append({"mad": float(d.mean()), "floor": float(real_d.mean()), "psnr": psnr(float((d * d).mean())),
                         "bad": float((d.max(axis=1) > bad_levels).mean()) if d.ndim == 2 else float((d > bad_levels).mean()),
                         "sharp": float(sharp), "whole": float(whole)})
        if rows:
            result[name] = {"static_px": int(mask.sum()), "n": len(rows),
                            **{k: float(np.mean([r[k] for r in rows])) for k in rows[0]}}
    return result


def analyze(args):
    """Classify each recording's frames, print the HUD regions' measures of the generated frames, and write them to
    --json if given."""
    summary = {}
    for folder in args.folders:
        frames = load_frames(os.path.join(folder, "frames") if os.path.isdir(os.path.join(folder, "frames")) else folder)
        classify(frames)
        h, w = frames[0]["image"].shape[:2]
        regions = parse_regions(args.regions, w, h)
        result = measure(frames, regions, args.threshold, args.bad)
        summary[folder] = result
        print("== %s: %d frames, %d generated, %d rendered, %dx%d" % (
            folder, len(frames), sum(1 for f in frames if f["kind"] == "generated"),
            sum(1 for f in frames if f["kind"] == "real"), w, h))
        print("%-10s %7s %4s %7s %7s %7s %8s %7s %7s" % ("region", "static", "n", "mad", "floor", "psnr", "bad%", "sharp",
                                                          "whole"))
        for name, r in result.items():
            print("%-10s %7d %4d %7.3f %7.3f %7.2f %8.3f %7.3f %7.3f" % (
                name, r["static_px"], r["n"], r["mad"], r["floor"], r["psnr"], 100 * r["bad"], r["sharp"], r["whole"]))
    if args.json:
        with open(args.json, "w") as f:
            json.dump(summary, f, indent=1)


def small(a, factor=8):
    """The RGB image averaged over factor x factor blocks, for the cheap pose match."""
    h, w = a.shape[0] // factor * factor, a.shape[1] // factor * factor
    return a[:h, :w].reshape(h // factor, factor, w // factor, factor, 3).mean(axis=(1, 3))


def scene_window(frame_shape):
    """Where the scene alone shows (no HUD): the camera pose is matched on it."""
    h, w = frame_shape[:2]
    return slice(int(0.18 * h), int(0.55 * h)), slice(int(0.12 * w), int(0.40 * w))


def dilate(mask, radius):
    """The mask grown by a square of the radius (wrapping at the image's edges)."""
    import numpy as np
    out = mask.copy()
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            out |= np.roll(np.roll(mask, dy, axis=0), dx, axis=1)
    return out


def edge_band(mask, radius=2):
    """The ring of pixels within radius of the edge of the static mask, on either side:
    where the HUD's opaque parts meet what shows through or around them."""
    return dilate(mask, radius) & dilate(~mask, radius)


def against_truth(reference, frames, regions):
    """Each frame against the reference frame showing the same camera pose (the closest in
    the scene window), per region, as PSNR by kind of frame. It takes the region's whole
    box, its pixels that never move (the HUD where it is opaque), and the ring along the
    edge of those (where the HUD meets the scene). It also takes the edge sharpness of the
    frame against the reference's there. The reference is a rendered frame at every pose, no frame
    generation, so what it shows is what a generated frame should show."""
    import numpy as np
    ys, xs = scene_window(frames[0]["image"].shape)
    ref_small = np.stack([small(r["image"][ys, xs]) for r in reference])
    masks = {}
    for name, box in regions.items():
        mask = static_mask(frames_of_kind(frames, "real"), box, 1.0)
        masks[name] = (mask, edge_band(mask))
    stats = {name: {"generated": [], "real": []} for name in regions}
    for f in frames:
        d = np.abs(ref_small - small(f["image"][ys, xs])[None]).mean(axis=(1, 2, 3))
        j = int(np.argmin(d))
        for name, (x0, y0, x1, y1) in regions.items():
            mask, band = masks[name]
            got = f["image"][y0:y1, x0:x1]
            want = reference[j]["image"][y0:y1, x0:x1]
            diff = got - want
            ge, we = gradient(got), gradient(want)
            ring = band[:-1, :-1]
            stats[name][f["kind"]].append((
                float((diff * diff).mean()),
                float((diff[mask] ** 2).mean()) if mask.any() else 0.0,
                float((diff[band] ** 2).mean()) if band.any() else 0.0,
                float(ge[ring].mean() / max(1e-6, we[ring].mean())) if ring.any() else 1.0,
                float(d[j])))
    out = {}
    for name in regions:
        out[name] = {}
        for kind in ("generated", "real"):
            rows = stats[name][kind]
            if rows:
                out[name][kind] = {"n": len(rows), "psnr_box": psnr(float(np.mean([r[0] for r in rows]))),
                                   "psnr_static": psnr(float(np.mean([r[1] for r in rows]))),
                                   "psnr_edge": psnr(float(np.mean([r[2] for r in rows]))),
                                   "sharp_edge": float(np.mean([r[3] for r in rows])),
                                   "pose_err": float(np.mean([r[4] for r in rows]))}
    return out, None


def frames_of_kind(frames, kind):
    """The frames classified as that kind (real or generated)."""
    return [f for f in frames if f["kind"] == kind]


def truth(args):
    """Measure each recording against the reference recording, per region and kind of frame, print the table and
    write it to --json if given."""
    reference = load_frames(os.path.join(args.reference, "frames") if os.path.isdir(os.path.join(args.reference, "frames"))
                            else args.reference)
    for f in reference:
        f["kind"] = "real"
    h, w = reference[0]["image"].shape[:2]
    regions = parse_regions(args.regions, w, h)
    print("reference %s: %d frames" % (args.reference, len(reference)))
    summary = {}
    for folder in args.folders:
        frames = load_frames(os.path.join(folder, "frames") if os.path.isdir(os.path.join(folder, "frames")) else folder)
        classify(frames)
        if args.all_real:
            for f in frames:
                f["kind"] = "real"
        result, matches = against_truth(reference, frames, regions)
        summary[folder] = result
        print("== %s (%d frames)" % (folder, len(frames)))
        print("%-10s %-10s %4s %9s %11s %10s %10s %9s" % ("region", "kind", "n", "psnr box", "psnr static", "psnr edge",
                                                         "sharp edge", "pose err"))
        for name, kinds in result.items():
            for kind, r in kinds.items():
                print("%-10s %-10s %4d %9.2f %11.2f %10.2f %10.3f %9.2f" % (
                    name, kind, r["n"], r["psnr_box"], r["psnr_static"], r["psnr_edge"], r["sharp_edge"], r["pose_err"]))
    if args.json:
        with open(args.json, "w") as f:
            json.dump(summary, f, indent=1)


def crops(args):
    """Save, for three generated frames of the sweep, each region beside the rendered frames before and after it,
    scaled up by --scale."""
    import numpy as np
    from PIL import Image
    folder = os.path.join(args.folder, "frames") if os.path.isdir(os.path.join(args.folder, "frames")) else args.folder
    frames = load_frames(folder)
    classify(frames)
    h, w = frames[0]["image"].shape[:2]
    regions = parse_regions(args.regions, w, h)
    os.makedirs(args.out, exist_ok=True)
    # Three consecutive frames in the middle of the sweep that hold a generated frame: before, generated, after.
    picks = [i for i, f in enumerate(frames) if f["kind"] == "generated" and 0 < i < len(frames) - 1]
    chosen = [picks[len(picks) // 3], picks[len(picks) // 2], picks[2 * len(picks) // 3]]
    for n, i in enumerate(chosen):
        for name, (x0, y0, x1, y1) in regions.items():
            tiles = [frames[i - 1], frames[i], frames[i + 1]]
            strip = np.concatenate([t["image"][y0:y1, x0:x1] for t in tiles], axis=1)
            img = Image.fromarray(np.clip(strip, 0, 255).astype(np.uint8))
            img = img.resize((img.width * args.scale, img.height * args.scale), Image.NEAREST)
            img.save(os.path.join(args.out, "%s-%d-%s.png" % (os.path.basename(os.path.normpath(args.folder)), n, name)))


def main():
    """Parse the command line and run the subcommand: run, analyze, truth or crops."""
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    r = sub.add_parser("run")
    r.add_argument("out")
    r.add_argument("--cases", default="auto,c")
    r.add_argument("--ksp", default=bm.DEFAULT_KSP)
    r.add_argument("--package", default=DEFAULT_PACKAGE)
    r.add_argument("--dxgi", help="a proxy DLL to install for the run")
    r.add_argument("--mod", help="a ReDefinition.dll to install for the run")
    r.add_argument("--profile", default="medium")
    r.add_argument("--no-profile", action="store_true")
    r.add_argument("--width", type=int, default=1600)
    r.add_argument("--height", type=int, default=900)
    r.add_argument("--fps-cap", type=int, default=55)
    r.add_argument("--max-generated-frames", type=int, default=1)
    r.add_argument("--proxy-key", action="append", default=[], help="key=value for the proxy's ini, every case")
    r.add_argument("--arg", action="append", default=[], help="a command line argument for KSP, every case")
    a = sub.add_parser("analyze")
    a.add_argument("folders", nargs="+")
    a.add_argument("--regions", default=DEFAULT_REGIONS)
    a.add_argument("--json")
    a.add_argument("--threshold", type=float, default=1.0, help="a pixel is static below this standard deviation")
    a.add_argument("--bad", type=float, default=12.0, help="levels off that count a pixel as wrong")
    t = sub.add_parser("truth")
    t.add_argument("reference")
    t.add_argument("folders", nargs="+")
    t.add_argument("--regions", default=DEFAULT_REGIONS)
    t.add_argument("--json")
    t.add_argument("--all-real", action="store_true", help="call every frame rendered (a reference against a reference)")
    c = sub.add_parser("crops")
    c.add_argument("folder")
    c.add_argument("out")
    c.add_argument("--regions", default=DEFAULT_REGIONS)
    c.add_argument("--scale", type=int, default=2)
    args = p.parse_args()
    if args.command == "run":
        run(args)
    elif args.command == "analyze":
        analyze(args)
    elif args.command == "truth":
        truth(args)
    else:
        crops(args)


DEFAULT_REGIONS = ("stage:0.0,0.05,0.05,0.77;altimeter:0.44,0.0,0.77,0.14;navball:0.44,0.60,0.70,1.0;"
                   "apps:0.96,0.0,1.0,0.75;portraits:0.73,0.74,0.98,1.0;orbit:0.0,0.80,0.34,1.0")

if __name__ == "__main__":
    main()
