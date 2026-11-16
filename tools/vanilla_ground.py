#!/usr/bin/env python3
"""The vanilla-ground test: pictures of the KSC ground and the pad's far and ground-level views, once WITHOUT any ReDefinition file in the game and
once with ours in place, taken by the same small plugin (tools/ProbeLite) at the same poses. Stdlib only (plus tools/atmo_series.py for helpers).

  python tools/vanilla_ground.py plan    --variant vanilla|vanilla-original|ours [--ksp <KSP folder>]
  python tools/vanilla_ground.py run     --variant vanilla|vanilla-original|ours [--ksp <KSP folder>] [--profile medium|none] [--keep-eve] [--timeout-min N] [--extra-arg A ...]
  python tools/vanilla_ground.py restore [--ksp <KSP folder>]
  python tools/vanilla_ground.py compare [--vanilla vanilla|vanilla-original]

Variants: vanilla = no ReDefinition file, the render settings ReDefinition wrote into settings.cfg left as they are; vanilla-original = the same with those settings
pinned back to the originals ReDefinition remembered (bundled.cfg BEFORE_REDEFINITION: TEXTURE_QUALITY, TERRAIN_SHADER_QUALITY, LIGHT_QUALITY); ours = ReDefinition in place.
Exit codes of run: 0 clean, 3 pictures taken but the plugin or a pose reported warnings, 1 failed, 2 refused.
  python -m unittest tools.tests.test_vanilla_ground

`run` needs KSP closed, no GPU lock held and 12 GB of free memory. Variant vanilla moves everything of ReDefinition out of the install (GameData/ReDefinition,
the proxy dxgi.dll with its ini, the Streamline, DLSS and FidelityFX DLLs, the Ampere package) into .local/archive/vanilla-aside/<time>/; variant ours
leaves ReDefinition in place. Both move EnvironmentalVisualEnhancements aside as K-S2's starts do (--keep-eve leaves it). Both: the Medium profile's files (unless --profile none) and K-S2's pins (2560x1440 windowed, UI_SCALE 1.5, vsync off) are applied,
the plugin ProbeLite (no reference to any ReDefinition assembly) is put into GameData/ProbeLite, KSP is started with the series' window arguments, and the
script blocks on the game's exit (the plugin quits the game when its last picture is written, or when it fails) with an upper limit. The pictures, the results
table (the ground's mean colour per pose), the plugin's log, the assemblies the game loaded and KSP.log go to .local/work/series-pictures/VANILLA/<variant>/.

The restore is the first-class path: a finally block always runs it, and `restore` runs it after a crash of this script. It removes ProbeLite, puts the moved
files back, restores the side files the run writes (settings.cfg, the ModuleManager caches, the probe save's own files, every mod's PluginData folder including
KSPCF's texture cache, Kopernicus' cache, the profile's files; not Logs/, not Parsek's folder in the save, not the Unity PlayerPrefs) and then compares SHA-256 hashes of everything it moved with the hashes taken before it moved. The journal stays and the pointer file
<KSP>/_vanilla_aside_active.json stays until every hash matches.
"""
import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import atmo_series as series  # noqa: E402
import gpu_lock  # noqa: E402

DEFAULT_REPO = os.path.dirname(HERE)
DEFAULT_KSP = series.DEFAULT_KSP
MAX_MINUTES = 20
FREE_GB = 12
SAVE_FOLDER = "ReDefinitionProbe"
POINTER = "_vanilla_aside_active.json"
PICTURES_REL = (".local", "work", "series-pictures", "VANILLA")
ASIDE_REL = (".local", "archive", "vanilla-aside")
LITE_DLL_REL = ("build", "ProbeLite", "ProbeLite.dll")
LITE_FOLDER = "GameData/ProbeLite"

# Everything of ours outside GameData/ReDefinition that the build, the proxy installer or the player's setup puts into the KSP folder (read from
# src/ReDefinition.csproj, tools/benchmark_matrix_lib/constants.py and src/Vendor/AmperePackage.cs): the proxy dxgi.dll, its ini, defaults and log, the
# Streamline interposer and plugins, the NVIDIA and AMD runtimes the player supplies, and the Ampere package (version.dll, dlssg_sm86.ini, the folder).
ASIDE_PATTERNS = ["dxgi.dll", "version.dll", "dlssg_sm86.ini", "ReDefinitionProxy.*", "sl.*.dll", "nvngx_*.dll", "amd_fidelityfx_*.dll"]
ASIDE_DIRS = ["GameData/ReDefinition", "dlssg_sm86"]
# Not moved: UnityPlayer.dll is the game's own, equal to its .release copy unless a profiling build is in (the plan says so then); the licence texts are inert.

# The caches the game's mod loader rewrites when GameData changed; restored byte for byte, so the next normal start finds its cache valid.
SIDE_FILES = ["settings.cfg", "GameData/ModuleManager.ConfigCache", "GameData/ModuleManager.ConfigSHA", "GameData/ModuleManager.Physics",
              "GameData/ModuleManager.TechTree", "PartDatabase.cfg"]
SIDE_DIRS = ["PluginData", "saves/%s/Backup" % SAVE_FOLDER]

# K-S2's pins (tests/series/K-S2.json: the series' settings and the start's), the same for both variants. The second list is the main mod's own settings
# file, which only the variant with ours has.
PINS_KSP = {"SCREEN_RESOLUTION_WIDTH": 2560, "SCREEN_RESOLUTION_HEIGHT": 1440, "FULLSCREEN": False, "UI_SCALE": 1.5, "SYNC_VBL": 0, "FRAMERATE_LIMIT": 1000}
PINS_MAIN = {"raytracing": "Off", "frameGeneration": False, "qualityAuto": False, "quality": "NativeAA"}
MAIN_SETTINGS = "GameData/ReDefinition/Main/PluginData/settings.cfg"
# The series' window arguments (tools/atmo_series.py plan_args with K-S2's probeArgs).
WINDOW_ARGS = ["-popupwindow", "-screen-width", "2560", "-screen-height", "1440", "-screen-fullscreen", "0"]


class Refusal(Exception):
    """The run does not start (or does not go on) for a stated reason."""


def say(message):
    print(message, flush=True)


def norm(rel):
    return os.path.normpath(rel.replace("/", os.sep))


def join(root, rel):
    return os.path.join(root, *rel.replace("\\", "/").split("/"))


# ---------------------------------------------------------------- the plan

VARIANTS = ("vanilla", "vanilla-original", "ours")
# The render settings ReDefinition's profile changed in the game's settings.cfg; bundled.cfg keeps their originals in BEFORE_REDEFINITION. Variant vanilla-original
# pins them back, so that nothing ReDefinition wrote stays behind.
ORIGINAL_KEYS = ("TEXTURE_QUALITY", "TERRAIN_SHADER_QUALITY", "LIGHT_QUALITY")


def is_vanilla(variant):
    return variant.startswith("vanilla")


def original_ksp_settings(ksp):
    """{key: value} of the render settings ReDefinition remembered in bundled.cfg's BEFORE_REDEFINITION block ('ksp.KEY = value'); empty where there is none."""
    path = join(ksp, "GameData/ReDefinition/Main/PluginData/bundled.cfg")
    out = {}
    if not os.path.isfile(path):
        return out
    inside, depth = False, 0
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.strip()
            if line == "BEFORE_REDEFINITION":
                inside = True
                continue
            if inside and line == "{":
                depth += 1
            elif inside and line == "}":
                depth -= 1
                if depth <= 0:
                    break
            elif inside and depth == 1 and line.startswith("ksp.") and "=" in line:
                key, value = [s.strip() for s in line[4:].split("=", 1)]
                if key in ORIGINAL_KEYS:
                    out[key] = value
    return out


def current_ksp_settings(ksp):
    out = {}
    path = join(ksp, "settings.cfg")
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                key, _eq, value = line.partition("=")
                if key.strip() in ORIGINAL_KEYS and _eq:
                    out[key.strip()] = value.strip()
    return out


def aside_items(ksp, variant="vanilla", keep_eve=False):
    """The things a run moves, as (relative path with forward slashes, kind), in the order they are moved. Only what exists. Variant vanilla moves everything of
    ReDefinition; both variants move EnvironmentalVisualEnhancements and its cloud configs as K-S2's starts do ("disableMods": ["EVE"]), unless keep_eve."""
    items = []
    if is_vanilla(variant):
        for pattern in ASIDE_PATTERNS:
            for path in sorted(glob.glob(os.path.join(ksp, pattern))):
                if os.path.isfile(path):
                    items.append((os.path.basename(path), "file"))
        for rel in ASIDE_DIRS:
            if os.path.isdir(join(ksp, rel)):
                items.append((rel, "dir"))
    if not keep_eve:
        for folder in series.MOD_FOLDERS["EVE"]:
            if os.path.isdir(join(ksp, "GameData/" + folder)):
                items.append(("GameData/" + folder, "dir"))
    seen, out = set(), []
    for rel, kind in items:
        if rel.lower() not in seen:
            seen.add(rel.lower())
            out.append((rel, kind))
    return out


def tree_hashes(root):
    """{relative path with forward slashes: SHA-256} of every file under a file or folder."""
    if os.path.isfile(root):
        return {"": series.sha256(root)}
    out = {}
    for rel in series.tree_files(root):
        out[rel] = series.sha256(os.path.join(root, *rel.split("/")))
    return out


def tree_stat(root):
    """{relative path: (size, mtime)} of every file under a folder (a manifest only; nothing is copied)."""
    out = {}
    if not os.path.isdir(root):
        return out
    for rel in series.tree_files(root):
        st = os.stat(os.path.join(root, *rel.split("/")))
        out[rel] = (st.st_size, int(st.st_mtime))
    return out


EXTRA_FILES = ["GameData/toolbar-settings.dat", "GameData/AtmosphereAutopilot/Global_settings.txt", "GameData/RocketSoundEnhancement/Settings.cfg"]


def side_plan(ksp, profile_cache, variant, use_profile, keep_eve=False):
    """What the run snapshots: (files, dirs). The save folder's own files (not Parsek, not Ships: those are 280 MB and the run does not write them) are listed
    one by one; every other mod's PluginData folder (KSPCF's texture cache, Scatterer's cache) and Kopernicus' cache are copied; ReDefinition's PluginData folders
    only for variant ours (the vanilla variants move the whole folder away and it comes back as it was). Not covered: Logs/, Screenshots/, Parsek's folder in the save
    (changes are counted and reported), the Unity PlayerPrefs of the window size."""
    files = list(SIDE_FILES) + [f for f in EXTRA_FILES if os.path.isfile(join(ksp, f))]
    dirs = list(SIDE_DIRS)
    skip = {"redefinition"} | (set() if keep_eve else {f.lower() for f in series.MOD_FOLDERS["EVE"]})
    gamedata = join(ksp, "GameData")
    if os.path.isdir(gamedata):
        for name in sorted(os.listdir(gamedata)):
            if name.lower() in skip:
                continue
            if os.path.isdir(os.path.join(gamedata, name, "PluginData")):
                dirs.append("GameData/%s/PluginData" % name)
        if os.path.isdir(os.path.join(gamedata, "Kopernicus", "Cache")):
            dirs.append("GameData/Kopernicus/Cache")
    if variant == "ours":
        for rel in ("ReDefinitionProxy.log",):
            if os.path.isfile(join(ksp, rel)):
                files.append(rel)
        if os.path.isdir(join(ksp, "dlssg_sm86/logs")):
            dirs.append("dlssg_sm86/logs")
    save = join(ksp, "saves/" + SAVE_FOLDER)
    if os.path.isdir(save):
        for name in sorted(os.listdir(save)):
            if os.path.isfile(os.path.join(save, name)):
                files.append("saves/%s/%s" % (SAVE_FOLDER, name))
    if variant == "ours":
        gd = join(ksp, "GameData/ReDefinition")
        if os.path.isdir(gd):
            for name in sorted(os.listdir(gd)):
                if os.path.isdir(os.path.join(gd, name, "PluginData")):
                    dirs.append("GameData/ReDefinition/%s/PluginData" % name)
    if use_profile and os.path.isdir(profile_cache):
        for rel in series.tree_files(profile_cache):
            if series.is_texture_cache_file(rel):
                continue
            if is_vanilla(variant) and rel.lower().startswith("gamedata/redefinition/"):
                continue
            if rel not in files:
                files.append(rel)
    return files, dirs


def pins_for(ksp, variant):
    """The pins of a run as [{file, set}]: K-S2's, the main mod's own for ours, ReDefinition's remembered original render settings for vanilla-original.
    Read before anything is moved (bundled.cfg goes away with GameData/ReDefinition)."""
    ksp_pins = dict(PINS_KSP)
    if variant == "vanilla-original":
        ksp_pins.update(original_ksp_settings(ksp))
    pins = [{"file": "settings.cfg", "set": ksp_pins}]
    if variant == "ours":
        pins.append({"file": MAIN_SETTINGS, "set": dict(PINS_MAIN)})
    return pins


def build_plan(ksp, variant, profile_cache, use_profile, keep_eve=False):
    """The whole plan as plain data (also what `plan` prints and what the tests read)."""
    plan = {"variant": variant, "ksp": ksp, "moves": [], "pins": [], "launch": None, "notes": []}
    for rel, kind in aside_items(ksp, variant, keep_eve):
        path = join(ksp, rel)
        size = os.path.getsize(path) if kind == "file" else sum(os.path.getsize(join(path, f)) for f in series.tree_files(path))
        plan["moves"].append({"rel": rel, "kind": kind, "bytes": size})
    plan["pins"] = pins_for(ksp, variant)
    original, current = original_ksp_settings(ksp), current_ksp_settings(ksp)
    changed = ["%s %s (ReDefinition's profile; originally %s)" % (k, current[k], original[k]) for k in ORIGINAL_KEYS if k in original and k in current and original[k] != current[k]]
    if changed and variant != "vanilla-original":
        plan["notes"].append("settings.cfg holds values ReDefinition wrote: %s. They stay in this variant (vanilla-original pins the originals)." % "; ".join(changed))
    if is_vanilla(variant):
        plan["notes"].append("what ReDefinition saved into other mods' own files (Scatterer, Parallax, TUFX, Deferred, DistantObject configs) stays in place")
    plan["profile"] = "Medium files from %s" % profile_cache if use_profile else "none"
    unity, release = join(ksp, "UnityPlayer.dll"), join(ksp, "UnityPlayer.dll.release")
    if os.path.isfile(unity) and os.path.isfile(release) and series.sha256(unity) != series.sha256(release):
        plan["notes"].append("UnityPlayer.dll differs from UnityPlayer.dll.release (a profiling build is in); the pictures are of that build")
    return plan


def format_plan(plan):
    lines = ["variant %s, KSP %s" % (plan["variant"], plan["ksp"])]
    total = sum(m["bytes"] for m in plan["moves"])
    lines.append("moved aside (%d items, %.1f MB): %s" % (len(plan["moves"]), total / 1048576.0, ", ".join(m["rel"] for m in plan["moves"]) or "nothing"))
    if plan["variant"] == "ours":
        lines.append("ReDefinition stays in place")
    for pin in plan["pins"]:
        lines.append("pinned in %s: %s" % (pin["file"], ", ".join("%s = %s" % kv for kv in pin["set"].items())))
    lines.append("profile: %s" % plan["profile"])
    lines.append("plugin: %s in %s, no ReDefinition assembly referenced" % ("ProbeLite.dll", LITE_FOLDER))
    lines.append("launch: KSP_x64.exe %s" % " ".join(WINDOW_ARGS))
    lines += ["note: " + n for n in plan["notes"]]
    return "\n".join(lines)


# ---------------------------------------------------------------- the journal

class Journal:
    """What a crash needs to put everything back: written before each change, atomically."""

    def __init__(self, path):
        self.path = path
        self.data = {}

    def write(self):
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(self.data, f, indent=1)
        os.replace(tmp, self.path)

    @staticmethod
    def read(path):
        j = Journal(path)
        with open(path, "r", encoding="utf-8") as f:
            j.data = json.load(f)
        return j


def pointer_path(ksp):
    return os.path.join(ksp, POINTER)


def snapshot_from(journal, ksp):
    snap = series.Snapshot(ksp, journal.data["snapshot"]["store"])
    snap.dirs = list(journal.data["snapshot"]["dirs"])
    snap.files = list(journal.data["snapshot"]["files"])
    snap.manifest = dict(journal.data["snapshot"]["manifest"])
    return snap


def move(src, dst):
    """A rename on one volume; a copy and delete across volumes. The destination must not exist."""
    if os.path.exists(dst):
        raise Refusal("cannot move %s to %s: the target exists" % (src, dst))
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    try:
        os.rename(src, dst)
    except OSError:
        # A copy and delete only where the two places are on different volumes; on one volume a failed rename (a locked file) must not leave half a copy.
        if os.stat(os.path.dirname(src)).st_dev == os.stat(os.path.dirname(dst)).st_dev:
            raise
        shutil.move(src, dst)


# ---------------------------------------------------------------- restore

def collect_pictures(ksp, mainrepo, variant, salvage=False):
    """Copies what the plugin wrote (pictures, results, logs) and KSP.log to .local/work/series-pictures/VANILLA/<variant>/. Returns the folder or None."""
    data = join(ksp, LITE_FOLDER + "/PluginData")
    if not os.path.isdir(data):
        return None
    dst = os.path.join(mainrepo, *PICTURES_REL, variant if not salvage else "%s-salvage-%s" % (variant, time.strftime("%Y%m%d-%H%M%S")))
    if os.path.isdir(dst):
        previous = dst + "-previous"
        if os.path.isdir(previous):
            shutil.rmtree(previous, ignore_errors=True)
        os.rename(dst, previous)
    os.makedirs(dst, exist_ok=True)
    for rel in series.tree_files(data):
        target = os.path.join(dst, *rel.split("/"))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copy2(os.path.join(data, *rel.split("/")), target)
    log = os.path.join(ksp, "KSP.log")
    if os.path.isfile(log):
        shutil.copy2(log, os.path.join(dst, "KSP.log"))
    return dst


def restore(ksp, mainrepo, running, journal_path=None, log=say):
    """Puts the install back as the journal says. Returns the list of problems (empty: everything verified, journal closed, pointer removed)."""
    pointer = pointer_path(ksp)
    if journal_path is None:
        if not os.path.isfile(pointer):
            log("restore: nothing to put back (no %s)" % POINTER)
            return []
        with open(pointer, "r", encoding="utf-8") as f:
            journal_path = json.load(f)["journal"]
    if not os.path.isfile(journal_path):
        return ["the journal %s is missing" % journal_path]
    if running():
        return ["KSP_x64 runs; close it, then run: python tools/vanilla_ground.py restore"]
    journal = Journal.read(journal_path)
    d = journal.data
    problems = []
    aside = os.path.join(os.path.dirname(journal_path), "aside")

    # 1. the plugin's own folder: its pictures are salvaged once (the run collects them itself before), then it goes.
    lite = join(ksp, LITE_FOLDER)
    if os.path.isdir(lite):
        if not d.get("collected"):
            try:
                folder = collect_pictures(ksp, mainrepo, d.get("variant", "unknown"), salvage=True)
                if folder:
                    log("restore: the plugin's files were kept in %s" % folder)
            except OSError as e:
                problems.append("the plugin's pictures could not be kept: %s" % e)
        try:
            shutil.rmtree(lite)
            log("restore: %s removed" % LITE_FOLDER)
        except OSError as e:
            problems.append("%s could not be removed (%s); remove it by hand" % (LITE_FOLDER, e))

    # 2. the moved files, last moved first.
    for item in reversed(d.get("moves", [])):
        src, dst = join(aside, item["rel"]), join(ksp, item["rel"])
        if not os.path.exists(src):
            continue
        try:
            move(src, dst)
            log("restore: %s back" % item["rel"])
        except (Refusal, OSError) as e:
            problems.append("%s: %s" % (item["rel"], e))

    # 3. the side files and folders.
    if d.get("snapshot"):
        bad = snapshot_from(journal, ksp).restore()
        for rel in bad:
            problems.append("side file not equal to its original: %s" % rel)

    # 4. the hashes of everything that was moved.
    for item in d.get("moves", []):
        live = join(ksp, item["rel"])
        if not os.path.exists(live):
            problems.append("%s is missing after the restore" % item["rel"])
            continue
        now = tree_hashes(live)
        if now != item["hashes"]:
            wrong = sorted(set(now) ^ set(item["hashes"]) | {k for k in now if k in item["hashes"] and now[k] != item["hashes"][k]})
            problems.append("%s differs from the original in %d file(s): %s" % (item["rel"], len(wrong), ", ".join(wrong[:5])))
    if problems:
        log("restore: PROBLEMS, the journal %s stays" % journal_path)
        for p in problems:
            log("   " + p)
        return problems

    d["state"] = "restored"
    d["restored"] = time.strftime("%Y-%m-%d %H:%M:%S")
    journal.write()
    shutil.rmtree(aside, ignore_errors=True)
    store = (d.get("snapshot") or {}).get("store")
    if store:
        shutil.rmtree(store, ignore_errors=True)
    if os.path.isfile(pointer):
        os.remove(pointer)
    log("restore: verified; %d moved item(s) match their hashes, %d side file(s) match" % (len(d.get("moves", [])), len((d.get("snapshot") or {}).get("manifest", {}))))
    return []


# ---------------------------------------------------------------- the run

def apply_profile_filtered(cache, ksp, variant, log=say):
    """The Medium profile's files over the KSP folder, as tools/atmo_series.py does, except KSPCF's texture cache and, for vanilla, every ReDefinition path."""
    n = 0
    for rel in series.tree_files(cache):
        if series.is_texture_cache_file(rel):
            continue
        if is_vanilla(variant) and rel.lower().startswith("gamedata/redefinition/"):
            continue
        dst = join(ksp, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(os.path.join(cache, *rel.split("/")), dst)
        n += 1
    log("profile: %d file(s) applied" % n)


def done_warnings(done):
    """The warnings count on the second line of the plugin's done file (warnings=N); 0 where there is none."""
    for line in (done or "").splitlines()[1:3]:
        if line.startswith("warnings="):
            try:
                return int(line.split("=", 1)[1])
            except ValueError:
                return 0
    return 0


def read_done(ksp):
    path = join(ksp, LITE_FOLDER + "/PluginData/done")
    if not os.path.isfile(path):
        return None
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return f.read()


def launch_blocking(ksp, args, timeout_s, log=say):
    """Starts the game and blocks on its exit, at most timeout_s. Returns (how it ended, exit code). The window is kept in front by the helper the series uses."""
    proc = subprocess.Popen([os.path.join(ksp, "KSP_x64.exe")] + args, cwd=ksp, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    stop = threading.Event()
    try:
        from benchmark_matrix_lib.install import Foreground
        foreground = Foreground(proc.pid)

        def keep():
            while not stop.wait(2.0):
                try:
                    foreground.keep()
                except Exception:
                    pass
        threading.Thread(target=keep, daemon=True).start()
    except Exception:
        pass
    try:
        code = proc.wait(timeout=timeout_s)
        return "quit", code
    except subprocess.TimeoutExpired:
        log("launch: %d s are up; the game is ended" % timeout_s)
        gpu_lock.kill_tree(proc.pid)
        try:
            proc.wait(timeout=60)
        except subprocess.TimeoutExpired:
            pass
        return "timeout after %d s" % timeout_s, None
    finally:
        stop.set()
        if proc.poll() is None:
            gpu_lock.kill_tree(proc.pid)


def build_probelite(repo, ksp, dotnet):
    """Builds tools/ProbeLite (net48, the game's assemblies only). Returns the path of ProbeLite.dll; raises Refusal where the build fails."""
    csproj = os.path.join(repo, "tools", "ProbeLite", "ProbeLite.csproj")
    code = subprocess.call([dotnet, "build", csproj, "-c", "Release", "-nologo", "-v:minimal", "-p:KspRoot=" + ksp], cwd=repo)
    dll = os.path.join(repo, *LITE_DLL_REL)
    if code != 0 or not os.path.isfile(dll):
        raise Refusal("the ProbeLite build failed (exit %s)" % code)
    return dll


class Run:
    """One run of one variant. The hooks stand in for the game, the build, the lock and the memory in the tests."""

    def __init__(self, ksp, variant, repo=DEFAULT_REPO, mainrepo=None, hooks=None, log=say, use_profile=True, timeout_min=MAX_MINUTES, extra_args=None, keep_eve=False):
        self.ksp = ksp
        self.variant = variant
        self.repo = repo
        self.mainrepo = mainrepo or gpu_lock.main_repo()
        self.log = log
        h = hooks or {}
        self.running = h.get("running", series.ksp_running_tasklist)
        self.free_gb = h.get("free_gb", series.free_memory_gb)
        self.build = h.get("build", lambda: build_probelite(repo, ksp, series.DEFAULT_DOTNET if os.path.exists(series.DEFAULT_DOTNET) else "dotnet"))
        self.launch = h.get("launch", lambda args, timeout: launch_blocking(ksp, args, timeout, log))
        self.lock = h.get("lock", True)
        self.vm = h.get("vm") or series.VmControl(log=log)
        self.use_profile = use_profile
        self.keep_eve = keep_eve
        self.profile_cache = h.get("profile_cache", os.path.join(self.mainrepo, *series.PROFILE_CACHE_REL))
        self.timeout_s = min(timeout_min, MAX_MINUTES) * 60
        self.extra_args = list(extra_args or [])
        self.stamp = time.strftime("%Y%m%d-%H%M%S")

    def refuse_checks(self):
        if self.variant not in VARIANTS:
            raise Refusal("the variant is one of: " + ", ".join(VARIANTS))
        if self.running():
            raise Refusal("KSP_x64 runs: close the game first")
        if os.path.isfile(os.path.join(self.ksp, series.DISABLED_DIR, "journal.json")):
            raise Refusal("an earlier series run left mod folders in %s: run `python tools/atmo_series.py modset-restore` first" % series.DISABLED_DIR)
        if os.path.isfile(pointer_path(self.ksp)):
            raise Refusal("an earlier run left its pointer %s in the KSP folder: run `python tools/vanilla_ground.py restore` first" % POINTER)
        if os.path.exists(join(self.ksp, LITE_FOLDER)):
            raise Refusal("%s exists already: run `python tools/vanilla_ground.py restore`, or remove it if it is not from this tool" % LITE_FOLDER)
        if not os.path.isfile(os.path.join(self.ksp, "KSP_x64.exe")):
            raise Refusal("%s has no KSP_x64.exe" % self.ksp)
        if not os.path.isfile(join(self.ksp, "saves/%s/probe.sfs" % SAVE_FOLDER)) and not os.path.isfile(join(self.ksp, "saves/%s/persistent.sfs" % SAVE_FOLDER)):
            raise Refusal("the save %s has neither probe.sfs nor persistent.sfs" % SAVE_FOLDER)
        if self.use_profile and not os.path.isdir(self.profile_cache):
            raise Refusal("no profile cache %s (use --profile none)" % self.profile_cache)

    def execute(self):
        """Runs it. Returns a dict: ok, how, done (the plugin's last file), pictures folder, restore problems. The restore always runs. The game-server VM is
        stopped before the memory is checked and started again after the restore."""
        self.refuse_checks()
        dll = self.build()
        self.vm.begin()
        try:
            free = self.free_gb()
            if free < FREE_GB:
                raise Refusal("only %.1f GB of memory are free (%d needed); close Rider, OneDrive, Toolbox, Edge" % (free, FREE_GB))
            return self._execute(dll)
        finally:
            self.vm.end()

    def _execute(self, dll):
        plan = build_plan(self.ksp, self.variant, self.profile_cache, self.use_profile, self.keep_eve)
        self.log(format_plan(plan))
        archive = os.path.join(self.mainrepo, *ASIDE_REL, self.stamp)
        os.makedirs(archive, exist_ok=True)
        journal = Journal(os.path.join(archive, "journal.json"))
        files, dirs = side_plan(self.ksp, self.profile_cache, self.variant, self.use_profile, self.keep_eve)
        pins = pins_for(self.ksp, self.variant)
        store = os.path.join(archive, "snapshot")
        snap = series.Snapshot(self.ksp, store)
        for rel in files:
            snap.add_file(rel)
        for rel in dirs:
            snap.add_dir(rel)
        parsek = join(self.ksp, "saves/%s/Parsek" % SAVE_FOLDER)
        try:
            parsek_before = tree_stat(parsek)
        except OSError:
            parsek_before = {}
        result = {"ok": False, "how": None, "done": None, "pictures": None, "problems": [], "parsek_changed": 0, "warnings": 0}
        lock_path = lock_info = None
        if self.lock:
            lock_path = gpu_lock.lock_path("gpu")
            info, holder = gpu_lock.try_acquire(lock_path, "vanilla-" + self.variant, self.timeout_s / 60 + 15, False, "gpu", os.getpid(), "vanilla_ground run " + self.variant)
            if info is None:
                shutil.rmtree(archive, ignore_errors=True)
                raise Refusal("the GPU lock is held: %s" % (gpu_lock.describe(holder) if holder else lock_path))
            lock_info = info
        journal.data = {"version": 1, "state": "active", "variant": self.variant, "ksp": self.ksp, "created": time.strftime("%Y-%m-%d %H:%M:%S"), "moves": [],
                        "collected": False, "snapshot": {"store": store, "dirs": [], "files": [], "manifest": {}}}
        journal.write()
        with open(pointer_path(self.ksp), "w", encoding="utf-8") as f:
            json.dump({"journal": journal.path, "variant": self.variant}, f)
        try:
            # The side files first (copies), then the moves, each journalled before it happens.
            snap.take()
            journal.data["snapshot"] = {"store": store, "dirs": snap.dirs, "files": snap.files, "manifest": snap.manifest}
            journal.write()
            ksp_log = join(self.ksp, "KSP.log")
            if os.path.isfile(ksp_log):
                shutil.copy2(ksp_log, os.path.join(archive, "KSP.log.before"))
            for m in plan["moves"]:
                src = join(self.ksp, m["rel"])
                m["hashes"] = tree_hashes(src)
                journal.data["moves"].append({"rel": m["rel"], "kind": m["kind"], "hashes": m["hashes"]})
                journal.write()
                move(src, join(os.path.join(archive, "aside"), m["rel"]))
                self.log("aside: %s" % m["rel"])
            if self.use_profile:
                apply_profile_filtered(self.profile_cache, self.ksp, self.variant, self.log)
            for line in series.apply_settings(self.ksp, pins):
                self.log("pinned %s" % line)
            if self.variant == "ours":
                self.log("side files of Atmospheres removed: %d" % series.clean_side_files(self.ksp))
            # The plugin and its configuration.
            os.makedirs(join(self.ksp, LITE_FOLDER + "/PluginData"), exist_ok=True)
            shutil.copy2(dll, join(self.ksp, LITE_FOLDER + "/ProbeLite.dll"))
            with open(join(self.ksp, LITE_FOLDER + "/PluginData/config.txt"), "w", encoding="utf-8") as f:
                f.write("variant=%s\nsaveFolder=%s\nspaceCentre=1\nflight=1\nwatchdogS=%d\n" % (self.variant, SAVE_FOLDER, self.timeout_s - 90))
            if os.path.isfile(ksp_log):
                os.remove(ksp_log)
            t0 = time.time()
            self.log("launch: KSP up to %d s; the plugin quits the game when its last picture is written" % self.timeout_s)
            how, code = self.launch(WINDOW_ARGS + self.extra_args, self.timeout_s)
            self.log("launch: %s (exit %s) after %.0f s" % (how, code, time.time() - t0))
            result["how"] = how
            result["done"] = read_done(self.ksp)
            result["ok"] = bool(result["done"]) and result["done"].splitlines()[0].strip() == "ok" and result["how"] == "quit"
            result["warnings"] = done_warnings(result["done"])
            result["pictures"] = collect_pictures(self.ksp, self.mainrepo, self.variant)
            journal.data["collected"] = True
            journal.write()
            if result["pictures"]:
                self.log("pictures: %s" % result["pictures"])
        except (Refusal, OSError, ValueError, KeyError) as e:
            result["problems"].append("the run: %s" % e)
            self.log("the run stopped: %s" % e)
        finally:
            try:
                # The game must be gone before anything is put back.
                if self.running():
                    self.log("the game still runs; it is ended")
                    subprocess.run(["taskkill", "/T", "/F", "/IM", "KSP_x64.exe"], capture_output=True)
                result["problems"] += restore(self.ksp, self.mainrepo, self.running, journal.path, self.log)
                try:
                    after = tree_stat(parsek)
                    result["parsek_changed"] = len({k for k, v in after.items() if parsek_before.get(k) != v} | (set(parsek_before) - set(after)))
                except OSError:
                    pass
            finally:
                if lock_info is not None:
                    gpu_lock.release(lock_path, "vanilla-" + self.variant, os.getpid(), lock_info["since"])
        result["ok"] = result["ok"] and not result["problems"]
        return result


# ---------------------------------------------------------------- results

def read_results(folder):
    """The rows of the plugin's results.tsv as dicts, in the order they were taken."""
    path = os.path.join(folder, "shots", "results.tsv")
    if not os.path.isfile(path):
        return []
    with open(path, "r", encoding="utf-8") as f:
        lines = [line.rstrip("\r\n") for line in f if line.strip()]
    if not lines:
        return []
    keys = lines[0].split("\t")
    return [dict(zip(keys, line.split("\t"))) for line in lines[1:]]


def summarize(folder):
    """A table of the ground colour per picture, and the proof of what the game loaded."""
    rows = read_results(folder)
    out = ["%-12s %-12s %7s %9s  %-26s %-24s %s" % ("picture", "scene", "camAGL", "overColl", "lowest quarter mean r,g,b (green)", "looks at", "verdict")]
    for r in rows:
        out.append("%-12s %-12s %7s %9s  %-26s %-24s %s" % (r["name"], r["scene"], r["cam_agl_pqs_m"], r["cam_over_collider_m"],
                                                             "%s,%s,%s (%s)" % (r["low_r"], r["low_g"], r["low_b"], r["low_green"]),
                                                             "%s / %s" % (r["looks_at_object"], r["looks_at_shader"]), r.get("verdict", "")))
    asm = os.path.join(folder, "assemblies.txt")
    if os.path.isfile(asm):
        with open(asm, "r", encoding="utf-8", errors="replace") as f:
            names = [line.split(" ")[0] for line in f if line.strip()]
        ours = [n for n in names if n.lower().startswith("redefinition")]
        out.append("assemblies loaded: %d; named ReDefinition*: %s" % (len(names), ", ".join(ours) if ours else "none"))
    return "\n".join(out)


def vanilla_leak(folder):
    """The assemblies named ReDefinition* that a vanilla run loaded (there must be none)."""
    path = os.path.join(folder, "assemblies.txt")
    if not os.path.isfile(path):
        return ["(assemblies.txt missing)"]
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return [line.split(" ")[0] for line in f if line.lower().startswith("redefinition")]


def compare_text(mainrepo, vanilla="vanilla"):
    base = os.path.join(mainrepo, *PICTURES_REL)
    a, b = read_results(os.path.join(base, vanilla)), read_results(os.path.join(base, "ours"))
    if not a or not b:
        return "compare needs both runs: %s has %d rows, %s has %d" % (vanilla, len(a), "ours", len(b))
    by_b = {r["name"]: r for r in b}
    out = ["%-12s %-22s %-22s %s" % ("picture", "vanilla low r,g,b", "ours low r,g,b", "difference r,g,b")]
    for r in a:
        o = by_b.get(r["name"])
        if o is None:
            out.append("%-12s only in vanilla" % r["name"])
            continue
        d = [float(o[k]) - float(r[k]) for k in ("low_r", "low_g", "low_b")]
        out.append("%-12s %-22s %-22s %+.1f,%+.1f,%+.1f" % (r["name"], "%s,%s,%s" % (r["low_r"], r["low_g"], r["low_b"]), "%s,%s,%s" % (o["low_r"], o["low_g"], o["low_b"]), *d))
    return "\n".join(out)


# ---------------------------------------------------------------- commands

def cmd_plan(args):
    ksp = args.ksp or DEFAULT_KSP
    mainrepo = gpu_lock.main_repo()
    print(format_plan(build_plan(ksp, args.variant, os.path.join(mainrepo, *series.PROFILE_CACHE_REL), args.profile != "none", args.keep_eve)))
    return 0


def cmd_run(args):
    ksp = args.ksp or DEFAULT_KSP
    run = Run(ksp, args.variant, use_profile=args.profile != "none", timeout_min=args.timeout_min, extra_args=args.extra_arg, keep_eve=args.keep_eve)
    try:
        result = run.execute()
    except Refusal as e:
        print("REFUSED: %s" % e)
        return 2
    if result["pictures"]:
        print(summarize(result["pictures"]))
    if result["parsek_changed"]:
        print("note: %d file(s) under saves/%s/Parsek changed during the run (the Parsek mod writes there on scene changes; not restored, as the series does not either)" % (result["parsek_changed"], SAVE_FOLDER))
    print("done file: %s" % (result["done"].strip().replace("\n", " | ") if result["done"] else "none"))
    for p in result["problems"]:
        print("PROBLEM: " + p)
    leaked = vanilla_leak(result["pictures"]) if is_vanilla(args.variant) and result["pictures"] else []
    for name in leaked:
        print("PROBLEM: the vanilla run loaded an assembly named %s" % name)
    if not result["ok"] or leaked:
        print("RESULT: FAILED")
        return 1
    if result["warnings"]:
        print("RESULT: OK WITH %d WARNING(S): see the verdict column and probelite.log in %s" % (result["warnings"], result["pictures"]))
        return 3
    print("RESULT: OK")
    return 0


def cmd_restore(args):
    ksp = args.ksp or DEFAULT_KSP
    problems = restore(ksp, gpu_lock.main_repo(), series.ksp_running_tasklist)
    print("RESULT: %s" % ("RESTORED" if not problems else "PROBLEMS"))
    return 0 if not problems else 1


def cmd_compare(args):
    print(compare_text(gpu_lock.main_repo(), args.vanilla))
    return 0


def build_parser():
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = p.add_subparsers(dest="command", required=True)
    for name, fn in (("plan", cmd_plan), ("run", cmd_run), ("restore", cmd_restore), ("compare", cmd_compare)):
        s = sub.add_parser(name)
        s.set_defaults(fn=fn)
        if name in ("plan", "run", "restore"):
            s.add_argument("--ksp")
        if name in ("plan", "run"):
            s.add_argument("--variant", choices=list(VARIANTS), required=True)
            s.add_argument("--profile", choices=["medium", "none"], default="medium")
            s.add_argument("--keep-eve", action="store_true", help="leave EnvironmentalVisualEnhancements in (K-S2's starts remove it)")
        if name == "compare":
            s.add_argument("--vanilla", choices=["vanilla", "vanilla-original"], default="vanilla")
        if name == "run":
            s.add_argument("--timeout-min", type=float, default=MAX_MINUTES)
            s.add_argument("--extra-arg", action="append", default=[])
    return p


def main(argv):
    args = build_parser().parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
