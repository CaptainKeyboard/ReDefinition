#!/usr/bin/env python3
"""The vanilla-ground test: pictures of the KSC ground and the pad's far and ground-level views, once WITHOUT any ReDefinition file in the game and
once with ours in place, taken by the same small plugin (tools/ProbeLite) at the same poses. Stdlib only (plus tools/atmo_series.py for helpers).

  python tools/vanilla_ground.py plan    --variant vanilla|vanilla-original|ours [--ksp <KSP folder>]
  python tools/vanilla_ground.py run     --variant vanilla|vanilla-original|ours [--ksp <KSP folder>] [--profile medium|none] [--keep-eve] [--timeout-min N] [--extra-arg A ...]
  python tools/vanilla_ground.py bisect  [--variants a,b,..] [--keep-going]   (runs the variants that remove ONE component of ours, likeliest first)
  python tools/vanilla_ground.py flagbisect [--presets probe,probe+stay,...] [--keep-going]   (ours + K-S2's launch arguments one preset after the other)
  python tools/vanilla_ground.py run --variant ours+flags [--flags probe+stay+clouds] [--extra-args "-atmoSet enabled=0"]
  python tools/vanilla_ground.py restore [--ksp <KSP folder>]
  python tools/vanilla_ground.py compare [--vanilla vanilla|vanilla-original]
  python tools/vanilla_ground.py table                                          (every run's far_p2 ground: ours, ours-hover, vanilla, vanilla-hover, ...)

Hover variants (ours-hover, vanilla-hover, ours-hover-lowfly) and `--hover-m 300` on run/bisect: the pad vessel is lifted and held over the pad as K-S2's viewer holds it
(far poses 300 m up, 60 m for lowfly; ground poses 6.25 m). `bisect --hover-m 300` bisects ours-hover against vanilla-hover.

Bisect variants (each starts from ours and removes one thing): no-proxy, no-atmo, no-main, no-patches, settings-original, other-mod-files; see VANILLA_VARIANTS and
BISECT_VARIANTS below. `bisect` needs the results of `vanilla` and `ours`, stops at the first variant whose far_p2 ground (the lowest quarter's green over grey) is
nearer to vanilla's than to ours, and prints a table 'variant | far_p2 ground mean r,g,b | green | sc_default ground'.

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
import re
import shlex
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

# The variants. The first three are the plain comparison; the rest remove ONE component of ours from variant ours (the bisect, `bisect` runs them in order):
#   vanilla           no ReDefinition file (GameData/ReDefinition, the proxy and its DLLs, the Ampere package); what ReDefinition wrote into settings.cfg stays
#   vanilla-original  as vanilla, with the render settings pinned back to the originals ReDefinition remembered
#   ours              everything in place
#   no-proxy          dxgi.dll, version.dll, ReDefinitionProxy.*, sl.*, nvngx*, the FidelityFX DLL and the dlssg_sm86 folder aside; GameData/ReDefinition stays
#   no-atmo           GameData/ReDefinition/Atmospheres aside
#   no-main           GameData/ReDefinition/Main aside (the Atmospheres plugin needs only Common; the run checks which assemblies loaded)
#   no-patches        only ReDefinition's ModuleManager patch files (Main/Patches/*.cfg, and any other patch-like cfg of ours) aside
#   settings-original ours with settings.cfg's render settings pinned to the originals
#   other-mod-files   ours with the other mods' own files (Scatterer, Parallax, DistantObject, Deferred, TUFX's scene profiles in the save) set back to the originals
#   ours-hover / vanilla-hover / ours-hover-lowfly   ours and vanilla with the pad vessel lifted and HELD over the pad as K-S2's viewer holds it (ProbeLite's Hover.cs: the far
#                     poses with the vessel 300 m up, 60 m for lowfly; the ground poses 6.25 m up), because K-S2's grey ground was seen only with the vessel in the air
#   ours-direct / vanilla-direct   ours and vanilla, but ProbeLite goes from the main menu straight to the flight (no space centre visit), as ReDefinition's own
#                     scene launcher does with -redefinitionProbe: separates "no space centre visit" from "ReDefinition's probe arguments"
VANILLA_VARIANTS = ("vanilla", "vanilla-original", "vanilla-hover", "vanilla-direct")
DIRECT_VARIANTS = ("ours-direct", "vanilla-direct")
BISECT_VARIANTS = ("no-proxy", "no-atmo", "no-main", "no-patches", "settings-original", "other-mod-files")
# The height the vessel is held at for the far poses, by variant (metres over the ground; no entry = the vessel stays on the pad).
HOVER_M = {"ours-hover": 300.0, "vanilla-hover": 300.0, "ours-hover-lowfly": 60.0}
VARIANTS = VANILLA_VARIANTS + ("ours",) + BISECT_VARIANTS + ("ours-hover", "ours-hover-lowfly", "ours+flags", "ours-direct")
# Launch arguments of a K-S2 start (the 'Args: KSP_x64.exe ...' line of its KSP.log: -popupwindow -atmoSeries K-S2 -atmoStart ground_on -redefinitionProbe
# -redefinitionProbeStay -redefinitionProbeClouds -screen-width 2560 -screen-height 1440 -screen-fullscreen 0 -atmoProbe SurfaceGroundProbe -atmoSet
# weatherEffects=1,cloudSource=ReDefinition,ownClouds=1; tests/series/K-S2.json). The window's are always passed. -atmoSeries/-atmoStart/-atmoProbe are not in a preset: they start
# K-S2's own probe, which would drive the scene and the camera against ProbeLite; --extra-args passes them anyway. -redefinitionProbe alone is not the stay mode: ReDefinition's
# probe then measures a camera path and quits the game when it is done, possibly before ProbeLite's last picture (the pictures taken are kept).
FLAG_PRESETS = {
    "probe": ["-redefinitionProbe"],
    "probe+stay": ["-redefinitionProbe", "-redefinitionProbeStay"],
    "probe+stay+clouds": ["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeClouds"],
    "atmoset": ["-atmoSet", "weatherEffects=1,cloudSource=ReDefinition,ownClouds=1"],
    "probe+stay+clouds+atmoset": ["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeClouds", "-atmoSet", "weatherEffects=1,cloudSource=ReDefinition,ownClouds=1"],
}
# The order `flagbisect` adds the flags in.
FLAG_ORDER = ("probe", "probe+stay", "probe+stay+clouds", "probe+stay+clouds+atmoset")
# The K-S2 ground of capture_ab (B, the camera alone, the lower-left box): mean r,g,b as the coordinator read them from the screen picture.
KS2_GROUND_RGB = (65.7, 71.5, 72.5)
# The runs the table lists first, in this order.
TABLE_ORDER = ("ours", "ours-direct", "ours-hover", "ours-hover-lowfly", "vanilla", "vanilla-direct", "vanilla-hover", "vanilla-original")
# The order `bisect` tries them in (the likeliest first): the settings ReDefinition wrote into the game's own settings, the proxy, the patches, Atmospheres, Main, the others' files.
BISECT_ORDER = ("settings-original", "no-proxy", "no-patches", "no-atmo", "no-main", "other-mod-files")
# The render settings ReDefinition's profile changed in the game's settings.cfg; bundled.cfg keeps their originals in BEFORE_REDEFINITION. Variants vanilla-original and
# settings-original pin them back.
ORIGINAL_KEYS = ("TEXTURE_QUALITY", "TERRAIN_SHADER_QUALITY", "LIGHT_QUALITY")
# What each variant keeps of ours: the proxy files, GameData/ReDefinition/<part>, and the pins/edits it applies.
REDEF_PARTS = ("Atmospheres", "Main")
# The assemblies a variant must have loaded (and must not have), by their names: ReDefinition = Main, ReDefinition.Atmospheres, ReDefinition.Common.
ASSEMBLY_MAIN, ASSEMBLY_ATMO = "ReDefinition", "ReDefinition.Atmospheres"
# Other mods' own files and where each key of the BEFORE_REDEFINITION block lives (the keys the block holds are 'mod.key = value'). Keys not in a file are reported, not invented.
MOD_FILES = {"parallax": "GameData/ParallaxContinued/Config/ParallaxGlobalSettings.cfg", "scatterer": "GameData/scatterer/config/config.cfg",
             "distantobject": "GameData/DistantObject/PluginData/Settings.cfg", "deferred": "GameData/zzz_Deferred/Deferred.cfg"}
# renderMode is a name in the block and a number in the file; eve.* and ksp.* have their own paths (EVE is moved aside, ksp.* are pinned).
MOD_SKIP = {"distantobject.renderMode": "a name in the block, a number in DistantObject's file"}
TUFX_SCENE_KEYS = {"profileFlight": "FlightSceneProfile", "profileInternal": "IVAProfile", "profileMap": "MapSceneProfile", "profileEditor": "EditorSceneProfile",
                   "profileSpaceCenter": "SpaceCenterSceneProfile", "profileTrackingStation": "TrackingStationProfile"}


def is_vanilla(variant):
    return variant in VANILLA_VARIANTS


def keeps_proxy(variant):
    return not is_vanilla(variant) and variant != "no-proxy"


def keeps_part(variant, part):
    """Whether GameData/ReDefinition/<part> stays in place in a variant."""
    return not is_vanilla(variant) and variant != {"Atmospheres": "no-atmo", "Main": "no-main"}[part]


def keeps_patches(variant):
    return keeps_part(variant, "Main") and variant != "no-patches"


def redef_gone(variant, rel):
    """Whether a path relative to the KSP folder lies under a part of GameData/ReDefinition that the variant moved away (such a file is not written back by the profile)."""
    low = rel.replace("\\", "/").lower()
    if not low.startswith("gamedata/redefinition/"):
        return False
    if is_vanilla(variant):
        return True
    return any(not keeps_part(variant, part) and low.startswith("gamedata/redefinition/%s/" % part.lower()) for part in REDEF_PARTS)


BEFORE_FILE = "GameData/ReDefinition/Main/PluginData/bundled.cfg"


def read_before_block(ksp):
    """(top, saves): the BEFORE_REDEFINITION block of bundled.cfg as {'mod.key': value} and {save name: {'mod.key': value}}; empty where there is none."""
    path = join(ksp, BEFORE_FILE)
    top, saves = {}, {}
    if not os.path.isfile(path):
        return top, saves
    inside, depth, save = False, 0, None
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.strip()
            if line == "BEFORE_REDEFINITION":
                inside = True
                continue
            if not inside:
                continue
            if line == "{":
                depth += 1
            elif line == "}":
                depth -= 1
                if depth <= 0:
                    break
                if depth == 1:
                    save = None
            elif "=" in line and depth >= 1:
                key, value = [s.strip() for s in line.split("=", 1)]
                if depth == 1:
                    top[key] = value
                elif depth == 2 and key == "name":
                    save = value
                    saves.setdefault(save, {})
                elif depth == 2 and save is not None:
                    saves[save][key] = value
    return top, saves


def original_ksp_settings(ksp):
    """{key: value} of the render settings ReDefinition remembered in bundled.cfg's BEFORE_REDEFINITION block ('ksp.KEY = value'); empty where there is none."""
    top, _saves = read_before_block(ksp)
    return {k[4:]: v for k, v in top.items() if k.startswith("ksp.") and k[4:] in ORIGINAL_KEYS}


def edit_first_key(text, key, value):
    """(new text, old value or None): the first 'key = value' line of a cfg text with its value replaced (indent kept); None where the key is not there."""
    lines = text.split("\n")
    for i, line in enumerate(lines):
        m = re.match(r"^(\s*)%s(\s*)=(\s*)(.*?)(\r?)$" % re.escape(key), line)
        if m:
            lines[i] = "%s%s%s=%s%s%s" % (m.group(1), key, m.group(2), m.group(3), value, m.group(5))
            return "\n".join(lines), m.group(4)
    return text, None


def mod_file_edits(ksp):
    """The edits of variant other-mod-files as ({file: [(key, original value)]}, [notes of what could not be set back]). Read from the BEFORE block."""
    top, saves = read_before_block(ksp)
    edits, notes = {}, []
    for full, value in top.items():
        mod, _dot, key = full.partition(".")
        if mod in ("ksp", "eve"):
            continue
        if full in MOD_SKIP:
            notes.append("%s not set back: %s" % (full, MOD_SKIP[full]))
        elif mod in MOD_FILES:
            edits.setdefault(MOD_FILES[mod], []).append((key, value))
        elif mod == "tufx":
            notes.append("%s is a scene the menu uses; the probe save holds the scenes' profiles (set back below)" % full)
        else:
            notes.append("%s has no file known here" % full)
    scenes = saves.get(SAVE_FOLDER, {})
    for full, value in scenes.items():
        mod, _dot, key = full.partition(".")
        if mod == "tufx" and key in TUFX_SCENE_KEYS:
            for name in ("probe.sfs", "persistent.sfs"):
                edits.setdefault("saves/%s/%s" % (SAVE_FOLDER, name), []).append((TUFX_SCENE_KEYS[key], value))
        elif full not in MOD_SKIP and not (mod == "distantobject"):
            notes.append("%s of the save %s is not set back" % (full, SAVE_FOLDER))
    return edits, notes


def apply_mod_file_edits(ksp, edits, log=say):
    """Writes the originals into the other mods' files; returns the list of 'file: key old -> new' lines and of keys the file does not hold."""
    lines = []
    for rel, pairs in edits.items():
        path = join(ksp, rel)
        if not os.path.isfile(path):
            lines.append("%s: file missing, %d key(s) not set" % (rel, len(pairs)))
            continue
        with open(path, "r", encoding="utf-8", newline="") as f:
            original = text = f.read()
        for key, value in pairs:
            text, old = edit_first_key(text, key, value)
            lines.append("%s: %s %s" % (rel, key, "not in the file" if old is None else ("%s (unchanged)" % old if old == value else "%s -> %s" % (old, value))))
        if text != original:
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(text)
    for line in lines:
        log("other mod files: " + line)
    return lines


def patch_files(ksp):
    """ReDefinition's ModuleManager patch files that change something of another mod or the game (relative to the KSP folder): everything under Main/Patches, and
    any other cfg of ours outside PluginData with a top-level patch operation (@NODE, +NODE, !NODE, %NODE, or a :FOR/:NEEDS/:AFTER/:BEFORE/:FINAL pass) on a node that
    is not ReDefinition's own (REDEFINITION_*, EVE_* are the data Atmospheres reads, not patches of anything else)."""
    base = join(ksp, "GameData/ReDefinition")
    out = []
    if not os.path.isdir(base):
        return out
    ops = re.compile(r":(FOR|NEEDS|AFTER|BEFORE|FINAL|LAST)\b|:(FOR|NEEDS|AFTER|BEFORE)\[")
    for rel in series.tree_files(base):
        if not rel.lower().endswith(".cfg") or "/PluginData/" in "/" + rel:
            continue
        if rel.startswith("Main/Patches/"):
            out.append("GameData/ReDefinition/" + rel)
            continue
        with open(os.path.join(base, *rel.split("/")), "r", encoding="utf-8", errors="replace") as f:
            lines = [line.strip() for line in f.read().split("\n") if line.strip() and not line.strip().startswith("//")]
        # Only the lines at depth 0 name a node.
        depth, hit = 0, False
        for s in lines:
            if depth == 0 and s != "{":
                m = re.match(r"^([@+!%\-]?)(\w+)", s)
                if m and (m.group(1) or ops.search(s)) and not m.group(2).upper().startswith(("REDEFINITION_", "EVE_")):
                    hit = True
                    break
            depth += s.count("{") - s.count("}")
        if hit:
            out.append("GameData/ReDefinition/" + rel)
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
    if not keeps_proxy(variant):
        for pattern in ASIDE_PATTERNS:
            for path in sorted(glob.glob(os.path.join(ksp, pattern))):
                if os.path.isfile(path):
                    items.append((os.path.basename(path), "file"))
        if os.path.isdir(join(ksp, "dlssg_sm86")):
            items.append(("dlssg_sm86", "dir"))
    if is_vanilla(variant):
        if os.path.isdir(join(ksp, "GameData/ReDefinition")):
            items.append(("GameData/ReDefinition", "dir"))
    else:
        for part in REDEF_PARTS:
            if not keeps_part(variant, part) and os.path.isdir(join(ksp, "GameData/ReDefinition/" + part)):
                items.append(("GameData/ReDefinition/" + part, "dir"))
        if variant == "no-patches":
            items += [(rel, "file") for rel in patch_files(ksp)]
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
    if keeps_proxy(variant):
        for rel in ("ReDefinitionProxy.log",):
            if os.path.isfile(join(ksp, rel)):
                files.append(rel)
        if os.path.isdir(join(ksp, "dlssg_sm86/logs")):
            dirs.append("dlssg_sm86/logs")
    if variant == "other-mod-files":
        for rel in mod_file_edits(ksp)[0]:
            if rel not in files:
                files.append(rel)
    save = join(ksp, "saves/" + SAVE_FOLDER)
    if os.path.isdir(save):
        for name in sorted(os.listdir(save)):
            if os.path.isfile(os.path.join(save, name)):
                files.append("saves/%s/%s" % (SAVE_FOLDER, name))
    gd = join(ksp, "GameData/ReDefinition")
    if not is_vanilla(variant) and os.path.isdir(gd):
        for name in sorted(os.listdir(gd)):
            if name in REDEF_PARTS and not keeps_part(variant, name):
                continue
            if os.path.isdir(os.path.join(gd, name, "PluginData")):
                dirs.append("GameData/ReDefinition/%s/PluginData" % name)
    if use_profile and os.path.isdir(profile_cache):
        for rel in series.tree_files(profile_cache):
            if series.is_texture_cache_file(rel):
                continue
            if redef_gone(variant, rel):
                continue
            if rel not in files:
                files.append(rel)
    return files, dirs


def pins_for(ksp, variant):
    """The pins of a run as [{file, set}]: K-S2's, the main mod's own for ours, ReDefinition's remembered original render settings for vanilla-original.
    Read before anything is moved (bundled.cfg goes away with GameData/ReDefinition)."""
    ksp_pins = dict(PINS_KSP)
    if variant in ("vanilla-original", "settings-original"):
        ksp_pins.update(original_ksp_settings(ksp))
    pins = [{"file": "settings.cfg", "set": ksp_pins}]
    if keeps_part(variant, "Main"):
        pins.append({"file": MAIN_SETTINGS, "set": dict(PINS_MAIN)})
    return pins


def build_plan(ksp, variant, profile_cache, use_profile, keep_eve=False, launch_args=None):
    """The whole plan as plain data (also what `plan` prints and what the tests read)."""
    plan = {"variant": variant, "ksp": ksp, "moves": [], "pins": [], "launch": None, "notes": [], "launch_args": list(launch_args or [])}
    for rel, kind in aside_items(ksp, variant, keep_eve):
        path = join(ksp, rel)
        size = os.path.getsize(path) if kind == "file" else sum(os.path.getsize(join(path, f)) for f in series.tree_files(path))
        plan["moves"].append({"rel": rel, "kind": kind, "bytes": size})
    plan["pins"] = pins_for(ksp, variant)
    original, current = original_ksp_settings(ksp), current_ksp_settings(ksp)
    changed = ["%s %s (ReDefinition's profile; originally %s)" % (k, current[k], original[k]) for k in ORIGINAL_KEYS if k in original and k in current and original[k] != current[k]]
    if changed and variant not in ("vanilla-original", "settings-original"):
        plan["notes"].append("settings.cfg holds values ReDefinition wrote: %s. They stay in this variant (vanilla-original and settings-original pin the originals)." % "; ".join(changed))
    if variant != "other-mod-files":
        plan["notes"].append("what ReDefinition saved into other mods' own files (Scatterer, Parallax, TUFX, Deferred, DistantObject configs) stays in place")
    else:
        edits, skipped = mod_file_edits(ksp)
        plan["edits"] = edits
        plan["notes"] += ["set back in other mods' files: %s: %s" % (f, ", ".join("%s=%s" % kv for kv in pairs)) for f, pairs in edits.items()]
        plan["notes"] += ["not set back: " + n for n in skipped]
    if variant == "no-patches":
        plan["notes"].append("patch files moved: %s" % (", ".join(r for r, _k in aside_items(ksp, variant, True)) or "none"))
    if variant == "no-main":
        plan["notes"].append("without GameData/ReDefinition/Main the main plugin (ReDefinition.dll) does not load; the run checks that ReDefinition.Atmospheres did")
    plan["profile"] = "Medium files from %s" % profile_cache if use_profile else "none"
    unity, release = join(ksp, "UnityPlayer.dll"), join(ksp, "UnityPlayer.dll.release")
    if os.path.isfile(unity) and os.path.isfile(release) and series.sha256(unity) != series.sha256(release):
        plan["notes"].append("UnityPlayer.dll differs from UnityPlayer.dll.release (a profiling build is in); the pictures are of that build")
    return plan


def format_plan(plan):
    lines = ["variant %s, KSP %s" % (plan["variant"], plan["ksp"])]
    total = sum(m["bytes"] for m in plan["moves"])
    lines.append("moved aside (%d items, %.1f MB): %s" % (len(plan["moves"]), total / 1048576.0, ", ".join(m["rel"] for m in plan["moves"]) or "nothing"))
    if not is_vanilla(plan["variant"]):
        lines.append("stays in place: %s" % ", ".join(
            [p for p, kept in (("the proxy and its DLLs", keeps_proxy(plan["variant"])), ("Atmospheres", keeps_part(plan["variant"], "Atmospheres")),
                               ("Main", keeps_part(plan["variant"], "Main")), ("the patch files", keeps_patches(plan["variant"]))) if kept] + ["Common, Planner, Dashboard"]))
    for pin in plan["pins"]:
        lines.append("pinned in %s: %s" % (pin["file"], ", ".join("%s = %s" % kv for kv in pin["set"].items())))
    lines.append("profile: %s" % plan["profile"])
    lines.append("plugin: %s in %s, no ReDefinition assembly referenced" % ("ProbeLite.dll", LITE_FOLDER))
    lines.append("launch: KSP_x64.exe %s" % " ".join(WINDOW_ARGS + plan.get("launch_args", [])))
    if any(a.lower().startswith("-redefinitionprobe") for a in plan.get("launch_args", [])):
        lines.append("the game's own probe loads the scene (ReDefinition's scene launcher); ProbeLite waits for the flight and takes the flight pictures only")
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
        if redef_gone(variant, rel):
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

    def __init__(self, ksp, variant, repo=DEFAULT_REPO, mainrepo=None, hooks=None, log=say, use_profile=True, timeout_min=MAX_MINUTES, extra_args=None, keep_eve=False, hover_m=None, flags=None):
        self.ksp = ksp
        # 'ours+flags' is ours launched with K-S2's arguments (the preset probe+stay+clouds unless flags names another).
        if variant == "ours+flags":
            variant, flags = "ours", flags or "probe+stay+clouds"
        if flags is not None and flags not in FLAG_PRESETS and flags != "custom":
            raise Refusal("flags is one of: %s, custom (with --extra-args)" % ", ".join(FLAG_PRESETS))
        self.flags = flags
        self.variant = variant
        # The far poses' vessel height: the variant's own (the hover variants) or hover_m, which lifts any other variant's vessel too (its folder then ends in +hover<m>).
        self.hover_m = HOVER_M.get(variant, 0.0) if hover_m is None else float(hover_m)
        self.label = variant if (hover_m is None or variant in HOVER_M or not self.hover_m) else "%s+hover%g" % (variant, self.hover_m)
        if flags:
            self.label += "+" + flags
        if flags == "custom" and not extra_args:
            raise Refusal("flags custom needs --extra-args")
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
        # The game's launch arguments after the window's: the preset's, then the extra ones. A -redefinitionProbe* argument makes ReDefinition's own scene launcher load the
        # probe save and start the flight from the main menu; ProbeLite then leaves the loading to it (menuLoad=0, no space centre pictures).
        self.launch_args = list(FLAG_PRESETS.get(flags, [])) + self.extra_args
        self.game_loads = any(a.lower().startswith("-redefinitionprobe") for a in self.launch_args)
        # No space centre visit: the game's own probe goes straight to the flight, and so does ProbeLite in the direct variants.
        self.no_space_centre = self.game_loads or variant in DIRECT_VARIANTS
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
        mod_edits = mod_file_edits(self.ksp)[0] if self.variant == "other-mod-files" else {}
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
        journal.data = {"version": 1, "state": "active", "variant": self.label, "ksp": self.ksp, "created": time.strftime("%Y-%m-%d %H:%M:%S"), "moves": [],
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
            if keeps_part(self.variant, "Atmospheres"):
                self.log("side files of Atmospheres removed: %d" % series.clean_side_files(self.ksp))
            if self.variant == "other-mod-files":
                apply_mod_file_edits(self.ksp, mod_edits, self.log)
            # The plugin and its configuration.
            os.makedirs(join(self.ksp, LITE_FOLDER + "/PluginData"), exist_ok=True)
            shutil.copy2(dll, join(self.ksp, LITE_FOLDER + "/ProbeLite.dll"))
            with open(join(self.ksp, LITE_FOLDER + "/PluginData/config.txt"), "w", encoding="utf-8") as f:
                f.write("variant=%s\nsaveFolder=%s\nmenuLoad=%d\nspaceCentre=%d\nflight=1\nwatchdogS=%d\nhoverFarM=%g\n"
                        % (self.label, SAVE_FOLDER, 0 if self.game_loads else 1, 0 if self.no_space_centre else 1, self.timeout_s - 90, self.hover_m))
            if os.path.isfile(ksp_log):
                os.remove(ksp_log)
            t0 = time.time()
            self.log("launch: KSP up to %d s; the plugin quits the game when its last picture is written" % self.timeout_s)
            how, code = self.launch(WINDOW_ARGS + self.launch_args, self.timeout_s)
            self.log("launch: %s (exit %s) after %.0f s" % (how, code, time.time() - t0))
            result["how"] = how
            result["done"] = read_done(self.ksp)
            result["ok"] = bool(result["done"]) and result["done"].splitlines()[0].strip() == "ok" and result["how"] == "quit"
            result["warnings"] = done_warnings(result["done"])
            result["pictures"] = collect_pictures(self.ksp, self.mainrepo, self.label)
            journal.data["collected"] = True
            journal.write()
            if result["pictures"]:
                self.log("pictures: %s" % result["pictures"])
                for p in component_problems(self.variant, result["pictures"]):
                    result["problems"].append(p)
                    self.log("COMPONENT CHECK: " + p)
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


def loaded_assemblies(folder):
    """The assembly names the plugin logged as loaded, or None where the list is missing."""
    path = os.path.join(folder, "assemblies.txt")
    if not os.path.isfile(path):
        return None
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return [line.split(" ")[0] for line in f if line.strip()]


def component_problems(variant, folder):
    """What the loaded assemblies say about the variant: ReDefinition (Main) loaded exactly where Main stays, ReDefinition.Atmospheres exactly where Atmospheres
    stays. A mismatch means the variant did not remove (or could not keep) what it says."""
    names = loaded_assemblies(folder)
    if names is None:
        return ["assemblies.txt is missing: what loaded is unknown"]
    out = []
    for assembly, part in ((ASSEMBLY_MAIN, "Main"), (ASSEMBLY_ATMO, "Atmospheres")):
        want, have = keeps_part(variant, part), assembly in names
        if want != have:
            out.append("%s %s, but the variant %s %s" % (assembly, "loaded" if have else "did not load", variant, "keeps its folder (%s)" % part if want else "removes its folder (%s)" % part))
    return out


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


GROUND_PICTURE = "far_p2"
SECOND_PICTURE = "sc_default"


def ground_row(folder, name=GROUND_PICTURE):
    """The results row of one picture of a run's folder (the mean colour of the lowest quarter of the screen is the ground), or None."""
    for r in read_results(folder):
        if r.get("name") == name:
            return r
    return None


def ground_green(row):
    return float(row["low_green"]) if row else float("nan")


def is_green_again(value, vanilla, ours):
    """Whether a variant's ground (far_p2's lowest quarter, green over grey) is nearer to the vanilla ground than to ours. False where the two do not differ."""
    if any(v != v for v in (value, vanilla, ours)) or abs(vanilla - ours) < 2.0:
        return False
    return abs(value - vanilla) < abs(value - ours)


def bisect_table(mainrepo, variants):
    """'variant | far_p2 ground mean rgb | green | sc_default ground mean rgb' for every variant that has results, vanilla and ours first."""
    base = os.path.join(mainrepo, *PICTURES_REL)
    out = ["%-30s %-22s %-7s %-22s %-7s %-8s %-9s %-22s %s" % ("variant", "far_p2 lowest quarter", "green", "far_p2 K-S2 box (LL)", "green", "horizon", "contrast", "sc_default lowest qtr", "verdicts")]
    seen = set()
    for v in list(TABLE_ORDER) + list(variants):
        if v in seen:
            continue
        seen.add(v)
        folder = os.path.join(base, v)
        far, sc = ground_row(folder), ground_row(folder, SECOND_PICTURE)
        if far is None and sc is None:
            continue
        low = lambda r: "%s,%s,%s" % (r["low_r"], r["low_g"], r["low_b"]) if r else "-"
        box = lambda r: "%s,%s,%s" % (r["ll_r"], r["ll_g"], r["ll_b"]) if r and r.get("ll_r") else "-"
        out.append("%-30s %-22s %-7s %-22s %-7s %-8s %-9s %-22s %s" % (
            v, low(far), far["low_green"] if far else "-", box(far), far.get("ll_green", "-") if far else "-", far.get("horizon_luma", "-") if far else "-",
            far.get("ground_contrast", "-") if far else "-", low(sc), (far or {}).get("verdict", "")))
    return "\n".join(out)


# ---------------------------------------------------------------- commands

def cmd_plan(args):
    ksp = args.ksp or DEFAULT_KSP
    mainrepo = gpu_lock.main_repo()
    flags = args.flags or ("probe+stay+clouds" if args.variant == "ours+flags" else None)
    extra = shlex.split(args.extra_args or "")
    if flags == "custom" or (flags is None and extra):
        flags = flags or "custom"
    launch = list(FLAG_PRESETS.get(flags, [])) + extra
    print(format_plan(build_plan(ksp, "ours" if args.variant == "ours+flags" else args.variant, os.path.join(mainrepo, *series.PROFILE_CACHE_REL),
                                 args.profile != "none", args.keep_eve, launch)))
    return 0


def do_run(ksp, variant, profile="medium", timeout_min=MAX_MINUTES, extra_args=None, keep_eve=False, hover_m=None, flags=None):
    """One run with its report printed. Returns (exit code, result or None): 0 clean, 3 pictures taken with warnings, 1 failed, 2 refused."""
    try:
        run = Run(ksp, variant, use_profile=profile != "none", timeout_min=timeout_min, extra_args=extra_args, keep_eve=keep_eve, hover_m=hover_m, flags=flags)
    except Refusal as e:
        print("REFUSED: %s" % e)
        return 2, None
    try:
        result = run.execute()
    except Refusal as e:
        print("REFUSED: %s" % e)
        return 2, None
    if result["pictures"]:
        print(summarize(result["pictures"]))
    if result["parsek_changed"]:
        print("note: %d file(s) under saves/%s/Parsek changed during the run (the Parsek mod writes there on scene changes; not restored, as the series does not either)" % (result["parsek_changed"], SAVE_FOLDER))
    print("done file: %s" % (result["done"].strip().replace("\n", " | ") if result["done"] else "none"))
    for p in result["problems"]:
        print("PROBLEM: " + p)
    leaked = vanilla_leak(result["pictures"]) if is_vanilla(variant) and result["pictures"] else []
    for name in leaked:
        print("PROBLEM: the vanilla run loaded an assembly named %s" % name)
    if not result["ok"] or leaked:
        print("RESULT: FAILED")
        return 1, result
    if result["warnings"]:
        print("RESULT: OK WITH %d WARNING(S): see the verdict column and probelite.log in %s" % (result["warnings"], result["pictures"]))
        return 3, result
    print("RESULT: OK")
    return 0, result


def cmd_run(args):
    extra = list(args.extra_arg or []) + shlex.split(args.extra_args or "")
    flags = args.flags or ("custom" if args.extra_args else None)
    return do_run(args.ksp or DEFAULT_KSP, args.variant, args.profile, args.timeout_min, extra, args.keep_eve, args.hover_m, flags)[0]


def cmd_table(args):
    """The table of every run that has results (ours, ours-hover, ours-hover-lowfly, vanilla, vanilla-hover, vanilla-original, then the bisect and +hover runs)."""
    mainrepo = gpu_lock.main_repo()
    base = os.path.join(mainrepo, *PICTURES_REL)
    extra = sorted(n for n in (os.listdir(base) if os.path.isdir(base) else []) if n not in TABLE_ORDER and not n.endswith("-previous") and "-salvage-" not in n)
    print(bisect_table(mainrepo, extra))
    return 0


def cmd_bisect(args):
    """Runs the variants that remove one component of ours, in order of likelihood, and stops at the first whose ground is green again (nearer to vanilla's far_p2
    than to ours). Needs the results of `vanilla` and `ours` (ours is run first where it has none)."""
    ksp = args.ksp or DEFAULT_KSP
    mainrepo = gpu_lock.main_repo()
    base = os.path.join(mainrepo, *PICTURES_REL)
    order = [v.strip() for v in args.variants.split(",")] if args.variants else list(BISECT_ORDER)
    unknown = [v for v in order if v not in BISECT_VARIANTS]
    if unknown:
        print("REFUSED: not a bisect variant: %s (one of %s)" % (", ".join(unknown), ", ".join(BISECT_VARIANTS)))
        return 2
    # With --hover-m 300 the vessel is held over the pad in every run and the references are the hover runs; no other height has references.
    hover = args.hover_m
    if hover and hover != HOVER_M["ours-hover"]:
        print("REFUSED: --hover-m must be %g (the height of the reference runs ours-hover and vanilla-hover)" % HOVER_M["ours-hover"])
        return 2
    ref_vanilla, ref_ours = ("vanilla-hover", "ours-hover") if hover else ("vanilla", "ours")
    order_labels = ["%s+hover%g" % (v, hover) for v in order] if hover else order
    if ground_row(os.path.join(base, ref_vanilla)) is None:
        print("REFUSED: no results of the %s variant in %s; run `python tools/vanilla_ground.py run --variant %s` first" % (ref_vanilla, base, ref_vanilla))
        return 2
    if ground_row(os.path.join(base, ref_ours)) is None:
        print("bisect: no results of %s yet; running it first" % ref_ours)
        code, _result = do_run(ksp, ref_ours, args.profile, args.timeout_min, args.extra_arg, args.keep_eve)
        if code in (1, 2):
            return code
    vanilla, ours = ground_green(ground_row(os.path.join(base, ref_vanilla))), ground_green(ground_row(os.path.join(base, ref_ours)))
    if is_green_again(ours, vanilla, ours) or abs(vanilla - ours) < 2.0:
        print("REFUSED: the ground of vanilla (green %.1f) and of ours (green %.1f) do not differ in %s's lowest quarter; there is nothing to bisect" % (vanilla, ours, GROUND_PICTURE))
        return 2
    print("bisect: %s lowest-quarter green: vanilla %.1f, ours %.1f; a variant is 'green again' when it is nearer to vanilla" % (GROUND_PICTURE, vanilla, ours))
    found = None
    for v in order:
        print("\n===== bisect: %s =====" % v)
        code, result = do_run(ksp, v, args.profile, args.timeout_min, args.extra_arg, args.keep_eve, hover)
        if code in (1, 2):
            print("bisect: stopped, %s did not run cleanly" % v)
            print(bisect_table(mainrepo, order_labels))
            return code
        value = ground_green(ground_row(result["pictures"]))
        green = is_green_again(value, vanilla, ours)
        print("bisect: %s: %s green %.1f -> %s" % (v, GROUND_PICTURE, value, "GREEN AGAIN" if green else "still like ours"))
        if green and found is None:
            found = v
            if not args.keep_going:
                break
    print("\n" + bisect_table(mainrepo, order_labels))
    if found:
        print("BISECT: removing %s turns the ground green again" % found)
    else:
        print("BISECT: no single removal turned the ground green; the cause is a combination or something no variant removes")
    return 0


def ll_green(row):
    """The green over grey of the K-S2 box (lower-left) of a results row; the lowest quarter's where the run has no such column."""
    if row is None:
        return float("nan")
    return float(row.get("ll_green") or row["low_green"])


def ks2_green():
    r, g, b = KS2_GROUND_RGB
    return g - 0.5 * (r + b)


def cmd_flagbisect(args):
    """Runs ours with K-S2's launch arguments added one preset after the other (probe, probe+stay, probe+stay+clouds, ...) and stops at the first whose far_p2 ground in the
    K-S2 box (lower-left) is nearer to the K-S2 grey (capture_ab B, KS2_GROUND_RGB) than to plain ours. Prints the table with the haze columns. Needs the results of `ours`
    (it is run first where it has none). A run that fails (the game's own probe may quit the game before ProbeLite's last picture) is reported and the list goes on."""
    ksp = args.ksp or DEFAULT_KSP
    mainrepo = gpu_lock.main_repo()
    base = os.path.join(mainrepo, *PICTURES_REL)
    order = [v.strip() for v in args.presets.split(",")] if args.presets else list(FLAG_ORDER)
    unknown = [v for v in order if v not in FLAG_PRESETS]
    if unknown:
        print("REFUSED: not a flag preset: %s (one of %s)" % (", ".join(unknown), ", ".join(FLAG_PRESETS)))
        return 2
    if ground_row(os.path.join(base, "ours")) is None:
        print("flagbisect: no results of ours yet; running it first")
        code, _result = do_run(ksp, "ours", args.profile, args.timeout_min, [], args.keep_eve)
        if code in (1, 2):
            return code
    ours = ll_green(ground_row(os.path.join(base, "ours")))
    grey = ks2_green()
    if abs(ours - grey) < 2.0:
        print("REFUSED: plain ours (K-S2 box green %.1f) is as grey as K-S2's capture_ab B (green %.1f); there is nothing to bisect" % (ours, grey))
        return 2
    print("flagbisect: %s K-S2-box green: ours %.1f, K-S2 capture_ab B %.1f; a run is 'grey' when it is nearer to K-S2" % (GROUND_PICTURE, ours, grey))
    found, labels = None, []
    for flags in order:
        print("\n===== flagbisect: ours + %s =====" % flags)
        code, result = do_run(ksp, "ours", args.profile, args.timeout_min, [], args.keep_eve, None, flags)
        if code == 2:
            print("flagbisect: stopped, refused")
            return 2
        labels.append("ours+" + flags)
        row = ground_row(result["pictures"]) if result and result.get("pictures") else None
        if row is None:
            print("flagbisect: ours+%s took no %s picture (exit %s)" % (flags, GROUND_PICTURE, code))
            continue
        value = ll_green(row)
        is_grey = is_green_again(value, grey, ours)
        print("flagbisect: ours+%s: K-S2-box green %.1f, horizon strip luma %s, ground contrast %s -> %s" % (
            flags, value, row.get("horizon_luma", "-"), row.get("ground_contrast", "-"), "GREY (like K-S2)" if is_grey else "still green like ours"))
        if is_grey and found is None:
            found = flags
            if not args.keep_going:
                break
    print("\n" + bisect_table(mainrepo, labels))
    print("K-S2 capture_ab B (lower-left, as the coordinator read it): mean r,g,b %s, green %.1f" % (", ".join("%g" % v for v in KS2_GROUND_RGB), grey))
    if found:
        print("FLAGBISECT: the launch arguments of preset %s turn the ground grey" % found)
    else:
        print("FLAGBISECT: no preset turned the ground grey")
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
    for name, fn in (("plan", cmd_plan), ("run", cmd_run), ("bisect", cmd_bisect), ("flagbisect", cmd_flagbisect), ("restore", cmd_restore), ("compare", cmd_compare), ("table", cmd_table)):
        s = sub.add_parser(name)
        s.set_defaults(fn=fn)
        if name in ("plan", "run", "bisect", "flagbisect", "restore"):
            s.add_argument("--ksp")
        if name in ("plan", "run"):
            s.add_argument("--variant", choices=list(VARIANTS), required=True)
            s.add_argument("--flags", choices=list(FLAG_PRESETS) + ["custom"], default=None,
                           help="launch arguments of a K-S2 start (presets: %s); ours+flags defaults to probe+stay+clouds" % ", ".join(FLAG_PRESETS))
            s.add_argument("--extra-args", default=None, help='further launch arguments as one string, e.g. "-atmoSet enabled=0" (alone: flags custom)')
        if name in ("plan", "run", "bisect", "flagbisect"):
            s.add_argument("--profile", choices=["medium", "none"], default="medium")
            s.add_argument("--keep-eve", action="store_true", help="leave EnvironmentalVisualEnhancements in (K-S2's starts remove it)")
        if name == "compare":
            s.add_argument("--vanilla", choices=["vanilla", "vanilla-original"], default="vanilla")
        if name == "flagbisect":
            s.add_argument("--presets", help="comma list of flag presets, in this order (default: %s)" % ",".join(FLAG_ORDER))
            s.add_argument("--keep-going", action="store_true", help="run every preset even after one turned the ground grey")
            s.add_argument("--timeout-min", type=float, default=MAX_MINUTES)
        if name in ("run", "bisect"):
            s.add_argument("--hover-m", type=float, default=None, help="hold the vessel this many metres over the pad for the far poses (the hover variants have their own height)")
            s.add_argument("--timeout-min", type=float, default=MAX_MINUTES)
            s.add_argument("--extra-arg", action="append", default=[])
        if name == "bisect":
            s.add_argument("--variants", help="comma list of bisect variants, in this order (default: %s)" % ",".join(BISECT_ORDER))
            s.add_argument("--keep-going", action="store_true", help="run the whole list even after one variant turned the ground green")
    return p


def main(argv):
    args = build_parser().parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
