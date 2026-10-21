"""The standing comparison of upscalers, frame generation, raytracing and latency models.

Runs the scene probe's flight (the launch pad, or a field of grass) once per KSP start of a
scenario matrix (tools/benchmark_matrix.json), sets the proxy's live keys in turn inside
each start, reads the frame rate and the latency of each phase from the logs, and writes the
run to docs/development/benchmarks/<date>.json and the tables in docs/development/benchmarks.md.
The matrix is split into groups: the main matrix runs by default, the others (raytracing
modes, frame generation steps, DLSS-G presets, latency brake targets, build variants, a
grass scene) each by --group NAME.

    python tools/benchmark_matrix.py run                 the default groups (the main matrix)
    python tools/benchmark_matrix.py run --group NAME    one named group, or --group all
    python tools/benchmark_matrix.py plan                the groups, starts and phases, with minutes, nothing run
    python tools/benchmark_matrix.py parse <raw folder>  a run's logs read again
    python tools/benchmark_matrix.py render              benchmarks.md from the JSON files

The KSP install's settings files are copied before the run and put back after it, also
when a start fails (snapshot and restore, as the probe scripts do). Python's standard
library only.
"""
import argparse
import copy
import datetime
import filecmp
import hashlib
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import textwrap
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_KSP = r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program"
DEFAULT_MATRIX = os.path.join(REPO, "tools", "benchmark_matrix.json")
DOC = os.path.join(REPO, "docs", "development", "benchmarks.md")
RESULTS = os.path.join(REPO, "docs", "development", "benchmarks")
SETTINGS_REL = os.path.join("GameData", "ReDefinition", "PluginData", "settings.cfg")
PROXY_INI = "ReDefinitionProxy.ini"
SHOT = "bench"

# The probe holds each set of live keys this long before it takes the next
# (SceneProbe.IniShots), and waits this long in the flight before the first.
PROBE_SET_SECONDS = 12.0
PROBE_FLIGHT_SECONDS = 30.0
# How far under the multiplier asked for the frames presented per rendered frame may lie
# before the step counts as not delivered.
STEP_TOLERANCE = 0.25
# Longest command line the tool launches KSP with; CreateProcess takes 32767 characters.
MAX_COMMAND_CHARS = 30000
# Measured on the RTX A4500: from the process start to the flight, and the quit after. The six
# runs of 2026-10-21 (1 to 9 starts, 18 to 60 phases) fit 86 s a start and 62 s for the profile start.
START_OVERHEAD_SECONDS = 86.0
PROFILE_START_SECONDS = 60.0
# How often the game's window is looked at while it runs, and how long it is left alone after
# Windows refused to bring it forward.
FOREGROUND_SECONDS = 2.0
FOREGROUND_RETRY_SECONDS = 15.0

# What the snapshot keeps: every settings file of the install (snapshot.py).
SNAP_EXT = (".cfg", ".xml", ".json", ".txt", ".ini", ".settings")
SNAP_SKIP = ("Logs", "saves", "ReDefinitionCaptures", "Screenshots")

# What the DLSS-G package for Ampere puts next to KSP_x64.exe (dlssg_sm86.ini's own
# instructions: version.dll, the ini and the dlssg_sm86 folder).
PACKAGE_FILES = ("version.dll", "dlssg_sm86.ini")
PACKAGE_DIR = "dlssg_sm86"
PACKAGE_SUBDIRS = ("backend", "kernels", "runtime")

# The package ini's keys the runs' sections name.
PACKAGE_KEYS_SHOWN = ("Engine", "MaxGeneratedFrames", "Preset", "Optimized", "FlowResolution", "UseHudless", "Mode", "FrameTiming", "Level")

TABLE_START = "<!-- benchmark-table:start -->"
TABLE_END = "<!-- benchmark-table:end -->"
RUNS_START = "<!-- benchmark-runs:start -->"
RUNS_END = "<!-- benchmark-runs:end -->"


# ----------------------------------------------------------------------------- the matrix

def load_matrix(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _vendor_ok(entry, vendor):
    allowed = entry.get("vendors")
    return not allowed or vendor is None or vendor in allowed


def scenario_label(s):
    rt = "RT " + s["raytracing"]
    if s.get("ray_reconstruction") is not None:
        rt += ", RR " + ("on" if s["ray_reconstruction"] else "off")
    if s.get("radiance_cache"):
        rt += ", radiance cache"
    if s.get("opacity_micromaps"):
        rt += ", opacity micromaps"
    if s.get("tweak"):
        rt += ", " + (s.get("tweak_name") or s["tweak"])
    fg = "FG " + s["fg_name"] + (" " + s["step"] if s.get("step") else "")
    prefix = []
    if s.get("variant_name"):
        prefix.append(s["variant_name"])
    if s.get("scene") and s["scene"] != DEFAULT_SCENE:
        prefix.append(s["scene"])
    return " · ".join(prefix + [s["technique_name"], rt, fg, s["latency_name"]])


def scenario_key(s):
    """The rows' key: the radiance cache's, the opacity micromaps', a tweak's, a frame
    generation step's, a scene's and a variant's rows have theirs, the others keep the key
    they had before those were rows of their own, so that runs before and after compare."""
    rr = s.get("ray_reconstruction")
    parts = []
    if s.get("variant"):
        parts.append("var-" + s["variant"])
    if s.get("scene") and s["scene"] != DEFAULT_SCENE:
        parts.append("scene-" + s["scene"])
    parts += [s["technique"], s["raytracing"], "rr-" + ("na" if rr is None else ("on" if rr else "off"))]
    if s.get("radiance_cache"):
        parts.append("rc-on")
    if s.get("opacity_micromaps"):
        parts.append("omm-on")
    if s.get("tweak"):
        parts.append("tw-" + s["tweak"])
    parts.append("fg-" + s["fg"])
    if s.get("step"):
        parts.append("x-" + s["step"])
    return "/".join(parts + ["lat-" + s["latency"]])


# ----------------------------------------------------------------------------- the groups

# What a group names that is not a part of the matrix it overrides.
GROUP_META = ("id", "name", "description", "default", "scenes", "variants", "show")
# A group's values of these merge into the matrix's; every other key it names replaces the matrix's.
GROUP_MERGED = ("proxy_load", "mod_settings")
# What only a group has: its renderings of live proxy keys and the values every other phase has.
GROUP_EXTRA = ("tweaks", "tweak_defaults")
# The lists a group may name entries of by id, or extend ({"extends": id, ...}).
GROUP_LISTS = ("techniques", "frame_generation", "latency")
MAIN_GROUP = "main"
DEFAULT_SCENE = "pad"


def group_list(matrix):
    """Every group, the main matrix (the matrix's own top-level lists) first."""
    main = {"id": MAIN_GROUP, "name": matrix.get("main_name", "Main matrix"),
            "description": matrix.get("main_description", ""), "default": True}
    return [main] + [dict(g) for g in matrix.get("groups", [])]


def select_groups(matrix, names=None):
    """The groups a run or plan covers: the default ones, or those named ("all" is every one)."""
    groups = group_list(matrix)
    if not names:
        return [g for g in groups if g.get("default", False)]
    if "all" in names:
        return groups
    known = [g["id"] for g in groups]
    unknown = [n for n in names if n not in known]
    if unknown:
        raise ValueError("no group {}; the groups are {}".format(", ".join(unknown), ", ".join(known)))
    return [g for g in groups if g["id"] in names]


def _entry(item, library, what):
    """A list entry of a group: an id of the matrix's own list, or a dict that may extend one."""
    def find(entry_id):
        for entry in library:
            if entry["id"] == entry_id:
                return copy.deepcopy(entry)
        raise ValueError("no {} {} to take or extend".format(what, entry_id))
    if isinstance(item, str):
        return find(item)
    if "extends" in item:
        entry = find(item["extends"])
        entry.update({k: v for k, v in item.items() if k != "extends"})
        return entry
    return copy.deepcopy(item)


def resolve_group(matrix, group):
    """The matrix a group runs: the matrix's own values with the group's over them.

    A key the group names that neither the matrix nor a group knows is refused: a misspelt
    "techniques" would leave the matrix's own list in place and run something else."""
    known = set(matrix) | set(GROUP_META) | set(GROUP_EXTRA)
    unknown = sorted(k for k in group if k not in known)
    if unknown:
        raise ValueError("group {} names {}, which neither the matrix nor a group has".format(group.get("id"), ", ".join(unknown)))
    sub = {k: copy.deepcopy(v) for k, v in matrix.items() if k != "groups"}
    # The live keys of the radiance cache and the micromaps, which a group without those renderings
    # leaves out of its own lists: every phase still names them at 0 (_start), so that none has
    # the value the install's ini holds.
    sub["_pinned_keys"] = {entry["key"]: "0" for entry in (matrix.get("radiance_cache"), matrix.get("opacity_micromaps"))
                           if entry and entry.get("key")}
    for key, value in group.items():
        if key in GROUP_META:
            continue
        if key in GROUP_LISTS:
            sub[key] = [_entry(item, matrix[key], key) for item in value]
        elif key in GROUP_MERGED:
            sub[key] = dict(sub.get(key, {}), **value)
        else:
            sub[key] = copy.deepcopy(value)
    return sub


def expand(matrix, vendor=None, resources=None, groups=None):
    """The starts of the groups (the default ones, or those named by id), each with its
    phases (the key sets) in order, as (starts, skipped); skipped as (what, why).

    A group is the matrix with its own values over it (resolve_group); the main matrix is the
    matrix's own lists. A group's "scenes" (the probe's scenes, matrix["scenes"]) and
    "variants" (values for the proxy at load, "proxy_load", each a start of its own) multiply
    its starts: the id of a start of another group than the main begins with the group's and
    ends with its variant's and scene's."""
    starts, skipped = [], []
    for group in select_groups(matrix, groups):
        sub = resolve_group(matrix, group)
        scenes = group.get("scenes") or [DEFAULT_SCENE]
        variants = group.get("variants") or [None]
        for scene in scenes:
            if scene not in matrix.get("scenes", {DEFAULT_SCENE: {}}):
                raise ValueError("group {} names the scene {}, which the matrix does not define".format(group["id"], scene))
            for variant in variants:
                cell = copy.deepcopy(sub)
                cell["_group"], cell["_scene"], cell["_variant"] = group["id"], scene, variant
                if variant:
                    cell["proxy_load"] = dict(cell.get("proxy_load", {}), **variant.get("proxy_load", {}))
                found, why = _expand_cell(cell, vendor, resources)
                for start in found:
                    start["group"] = group["id"]
                    start["scene"] = scene
                    start["variant"] = variant["id"] if variant else None
                    start["probe_args"] = list(matrix.get("scenes", {}).get(scene, {}).get("args", []))
                    if group["id"] != MAIN_GROUP:
                        parts = [group["id"], start["id"]]
                        if variant:
                            parts.append(variant["id"])
                        if len(scenes) > 1 or scene != DEFAULT_SCENE:
                            parts.append(scene)
                        start["id"] = "-".join(parts)
                starts.extend(found)
                skipped.extend(why)
    seen, unique = set(), []
    for item in skipped:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return starts, unique


def _expand_cell(matrix, vendor=None, resources=None):
    """The starts one group, scene and variant ask for, each with its phases (the key sets) in order.

    Only what is read at load splits the starts: the frame generation kind
    (frameGenerationTechnique, and the DLSS-G package next to KSP_x64.exe), and whatever
    "separate_starts" names. The upscaler, the raytracing mode and Ray Reconstruction
    are the mod's own settings, which the probe sets live through its mod.<key> keys; the
    radiance cache ("radiance_cache") is the proxy's live key, a rendering of its own for
    the one technique and raytracing mode it names, with the latency models it names; so
    are the opacity micromaps ("opacity_micromaps"), the last rendering of the start that
    has that technique and mode: the first live 1 makes the passes' pipelines with micromap
    support, which they keep, so no row after it is without.
    A frame generation kind may name a "kind" other than its id (a variant of the same
    technology, its rows checked against that kind), "dlssg_ini" (sections, keys and values
    written into the DLSS-G package's ini for its start) and "only" (the renderings it is
    measured with: "techniques", "raytracing", and "extras": false for none of the radiance
    cache's and the micromaps').
    Returns (starts, skipped), skipped as (what, why)."""
    resources = resources or {}
    skipped = []
    latencies = [l for l in matrix["latency"] if _vendor_ok(l, vendor)]
    for l in matrix["latency"]:
        if not _vendor_ok(l, vendor):
            skipped.append(("latency " + l["id"], "only on " + "/".join(l["vendors"])))
    renderings = []
    # The renderings that end a start: a tweak that is "last", and the opacity micromaps, whose
    # first live 1 makes pipelines that stay.
    tail = []
    for tech in matrix["techniques"]:
        if not _vendor_ok(tech, vendor):
            skipped.append(("technique " + tech["id"], "only on " + "/".join(tech["vendors"])))
            continue
        missing = [r for r in tech.get("requires", []) if not resources.get(r)]
        if missing:
            skipped.append(("technique " + tech["id"], "needs " + ", ".join(missing)))
            continue
        for rt in matrix["raytracing"]:
            rr_choices = matrix.get("ray_reconstruction", [True]) if tech.get("ray_reconstruction") and rt != "Off" else [None]
            for rr in rr_choices:
                renderings.append({"tech": tech, "raytracing": rt, "ray_reconstruction": rr, "radiance_cache": None,
                                   "opacity_micromaps": None})
            cache = matrix.get("radiance_cache")
            if cache and cache["technique"] == tech["id"] and cache["raytracing"] == rt:
                renderings.append({"tech": tech, "raytracing": rt, "ray_reconstruction": rr_choices[0],
                                   "radiance_cache": True, "opacity_micromaps": None, "latencies": cache.get("latencies")})
            for tweak in matrix.get("tweaks", []):
                if tweak["technique"] == tech["id"] and rt in _as_list(tweak["raytracing"]):
                    found = {"tech": tech, "raytracing": rt, "ray_reconstruction": rr_choices[0], "radiance_cache": None,
                             "opacity_micromaps": None, "latencies": tweak.get("latencies"), "tweak": tweak}
                    (tail if tweak.get("last") else renderings).append(found)
            micromaps = matrix.get("opacity_micromaps")
            if micromaps and micromaps["technique"] == tech["id"] and micromaps["raytracing"] == rt:
                tail.append({"tech": tech, "raytracing": rt, "ray_reconstruction": rr_choices[0],
                             "radiance_cache": None, "opacity_micromaps": True,
                             "latencies": micromaps.get("latencies")})
    kinds = []
    for fg in matrix["frame_generation"]:
        if not _vendor_ok(fg, vendor):
            skipped.append(("frame generation " + fg["id"], "only on " + "/".join(fg["vendors"])))
            continue
        missing = [r for r in fg.get("requires", []) if not resources.get(r)]
        if missing:
            skipped.append(("frame generation " + fg["id"], "needs " + ", ".join(missing)))
            continue
        kinds.append(fg)
    # The first frame generation kind with phases held off gives the plain "FG off" rows;
    # another kind's are named after it, since its files are loaded all the same.
    plain_off = next((fg for fg in matrix["frame_generation"] if fg.get("fg_off_phases", True)), None)
    split = matrix.get("separate_starts", [])
    starts = []
    for fg in kinds:
        groups = []
        for r in [r for r in renderings if _allowed(fg, r)]:
            group = tuple(_split_value(r, d) for d in split)
            if not groups or groups[-1][0] != group:
                groups.append((group, []))
            groups[-1][1].append(r)
        for last in [r for r in tail if _allowed(fg, r)]:
            # The last of the group it belongs to.
            for _, members in groups:
                if any(m["tech"]["id"] == last["tech"]["id"] for m in members):
                    members.append(last)
                    break
        for group, members in groups:
            starts.append(_start(matrix, fg, members, group, latencies, resources, fg is plain_off))
    # One reason per thing skipped is enough.
    seen, unique = set(), []
    for item in skipped:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return starts, unique


def _allowed(fg, rendering):
    """Whether the frame generation kind is measured with the rendering ("only")."""
    only = fg.get("only")
    if not only:
        return True
    if "techniques" in only and rendering["tech"]["id"] not in only["techniques"]:
        return False
    if "raytracing" in only and rendering["raytracing"] not in only["raytracing"]:
        return False
    if not only.get("extras", True) and (rendering.get("radiance_cache") or rendering.get("opacity_micromaps")
                                         or rendering.get("tweak")):
        return False
    return True


def _as_list(value):
    return value if isinstance(value, list) else [value]


def _split_value(rendering, dimension):
    if dimension == "technique":
        return rendering["tech"]["id"]
    if dimension == "raytracing":
        return rendering["raytracing"].lower()
    if dimension == "ray_reconstruction":
        rr = rendering["ray_reconstruction"]
        return "rrna" if rr is None else ("rr" if rr else "norr")
    raise ValueError("separate_starts takes technique, raytracing and ray_reconstruction, not " + dimension)


def _fill(value, resources):
    return re.sub(r"\{(\w+)\}", lambda m: resources.get(m.group(1), m.group(0)), str(value))


def _mod_values(rendering):
    """The mod's settings that make this rendering, by their settings.cfg keys. Every
    phase names all of them, so that none inherits a value from the phase before."""
    values = dict(rendering["tech"].get("settings", {}))
    values["raytracing"] = rendering["raytracing"]
    rr = rendering["ray_reconstruction"]
    # Where Ray Reconstruction does not apply, it stays at the mod's default.
    values["rayReconstruction"] = "True" if rr is None or rr else "False"
    return values


def _start(matrix, fg, renderings, group, latencies, resources, plain_off):
    start_id = "-".join(list(group) + [fg["id"]])
    settings = dict(matrix.get("mod_settings", {}))
    settings.update(_mod_values(renderings[0]))
    proxy = dict(matrix.get("proxy_load", {}))
    for r in renderings:
        proxy.update({k: _fill(v, resources) for k, v in r["tech"].get("proxy_load", {}).items()})
    proxy.update({k: _fill(v, resources) for k, v in fg.get("proxy_load", {}).items()})
    settle = matrix.get("settle_seconds", 3)
    settle_rebuild = matrix.get("settle_after_rebuild_seconds", settle)
    repeats = max(1, int(matrix.get("phase_repeats", 1)))
    repeats_rebuild = max(1, int(matrix.get("phase_repeats_after_rebuild", repeats)))
    # A kind that names its latency models gets those alone (XeSS-FG runs with XeLL, which
    # it requires); a model tied to kinds runs with those only.
    if fg.get("latencies"):
        latencies = [l for l in latencies if l["id"] in fg["latencies"]]
    else:
        latencies = [l for l in latencies if not l.get("only_with_fg")]
    # A kind with "steps" is measured at each multiplier (the mod's frameGenerationMultiplier,
    # set live through the probe's mod.<key>), each phase naming it.
    steps = fg.get("steps")
    if steps:
        settings["frameGenerationMultiplier"] = steps[0]
    fg_states = [("off", matrix["fg_off"], None)] if fg.get("fg_off_phases", True) else []
    fg_states.extend(("on", matrix["fg_on"], step) for step in (steps or [None]))
    tweak_defaults = matrix.get("tweak_defaults", {})
    cache_key = (matrix.get("radiance_cache") or {}).get("key")
    micromaps_key = (matrix.get("opacity_micromaps") or {}).get("key")
    phases = []
    for n, r in enumerate(renderings):
        tech = r["tech"]
        mod = _mod_values(r)
        first_of_rendering = True
        # The radiance cache's rendering with the latency models it names, or where the
        # frame generation kind names its own, with those.
        rendering_latencies = latencies
        if r.get("latencies"):
            rendering_latencies = [l for l in latencies if l["id"] in r["latencies"]] or latencies
        for state, fg_keys, step in fg_states:
            phase_mod = dict(mod)
            if steps:
                phase_mod["frameGenerationMultiplier"] = step or steps[0]
            for lat in rendering_latencies:
                keys = dict(matrix.get("phase_keys", {}))
                keys.update({"mod." + k: v for k, v in phase_mod.items()})
                keys.update(fg_keys)
                keys.update(lat["proxy"])
                # Every phase names the cache's and the micromaps' keys, so that none inherits them, also
                # in a group that has neither rendering.
                keys.update(matrix.get("_pinned_keys", {}))
                if cache_key:
                    keys[cache_key] = "1" if r.get("radiance_cache") else "0"
                if micromaps_key:
                    keys[micromaps_key] = "1" if r.get("opacity_micromaps") else "0"
                # Every phase names the tweaks' keys at their defaults, so that none inherits one.
                keys.update(tweak_defaults)
                if r.get("tweak"):
                    keys.update(r["tweak"]["keys"])
                if state == "on":
                    fg_id, fg_name = fg["id"], fg["name"]
                elif plain_off:
                    fg_id, fg_name = "off", "off"
                else:
                    fg_id, fg_name = "off-" + fg["id"] + "-loaded", "off ({} loaded)".format(fg["name"])
                s = {
                    "technique": tech["id"], "technique_name": tech["name"], "technique_log_name": tech.get("log_name", tech["name"]),
                    "raytracing": r["raytracing"], "ray_reconstruction": r["ray_reconstruction"],
                    "radiance_cache": r.get("radiance_cache"), "opacity_micromaps": r.get("opacity_micromaps"),
                    "fg": fg_id, "fg_name": fg_name, "fg_requested": state == "on", "fg_kind": fg.get("kind", fg["id"]),
                    "latency": lat["id"], "latency_name": lat["name"],
                }
                if step:
                    s["step"] = step
                if r.get("tweak"):
                    s["tweak"], s["tweak_name"] = r["tweak"]["id"], r["tweak"].get("name", r["tweak"]["id"])
                if matrix.get("_group") and matrix["_group"] != MAIN_GROUP:
                    s["group"] = matrix["_group"]
                if matrix.get("_scene") and matrix["_scene"] != DEFAULT_SCENE:
                    s["scene"] = matrix["_scene"]
                if matrix.get("_variant"):
                    s["variant"], s["variant_name"] = matrix["_variant"]["id"], matrix["_variant"].get("name", matrix["_variant"]["id"])
                # The first phase of a rendering after another one waits for the upscaler's
                # rig and the raytracing's scene to be built again; the start's first has the
                # flight's 30 s before it.
                rebuild = first_of_rendering and n > 0
                first_of_rendering = False
                for _ in range(repeats_rebuild if rebuild else repeats):
                    phases.append({"scenario": s, "keys": keys, "mod_settings": phase_mod,
                                   "settle": settle_rebuild if rebuild else settle, "rebuild": rebuild})
    return {
        "id": start_id, "fg_kind": fg.get("kind", fg["id"]), "renderings": len(renderings),
        "mod_settings": settings, "proxy_load": proxy,
        "dlssg_package": bool(fg.get("dlssg_package")), "max_generated_frames": fg.get("max_generated_frames"),
        "dlssg_ini": fg.get("dlssg_ini"),
        "phases": phases,
    }


def set_package_ini_value(text, section, key, value):
    """The DLSS-G package's ini with section's key set to value: the key's line where the
    section has one, else a line after the section's last one, else the section at the end.
    The rest of the file, its comments and line endings stay."""
    ending = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines(keepends=True)
    inside = False
    first = last = key_line = None
    for n, line in enumerate(lines):
        stripped = line.strip()
        header = re.match(r"^\[(.+)\]$", stripped)
        if header:
            if inside:
                break
            inside = header.group(1).strip().lower() == section.lower()
            if inside:
                first = last = n
            continue
        if not inside:
            continue
        if stripped:
            last = n
        m = re.match(r"^([^;#=][^=]*)=", stripped)
        if m and m.group(1).strip().lower() == key.lower():
            key_line = n
    if key_line is not None:
        old = lines[key_line]
        lines[key_line] = re.sub(r"^(\s*[^=]*=).*?(\r?\n?)$", lambda m: m.group(1) + value + m.group(2), old)
        return "".join(lines)
    if first is not None:
        if not lines[last].endswith("\n"):
            lines[last] += ending
        lines.insert(last + 1, "{}={}{}".format(key, value, ending))
        return "".join(lines)
    head = text if not text or text.endswith("\n") else text + ending
    return head + ("" if not head else ending) + "[{}]{}{}={}{}".format(section, ending, key, value, ending)


def ini_sets_argument(phases):
    """-redefinitionProbeShotIni's value: the sets in order, each with every key the
    matrix varies, so that no set inherits a value from the one before it."""
    return ";".join(",".join("{}={}".format(k, v) for k, v in p["keys"].items()) for p in phases)


def probe_arguments(start):
    """KSP_x64.exe's arguments for one start: the probe, the scene's, and every set of keys."""
    return (["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeClouds"] + list(start.get("probe_args") or [])
            + ["-redefinitionProbeShot", SHOT, "-redefinitionProbeShotVariant", "none",
               "-redefinitionProbeShotIni", ini_sets_argument(start["phases"])])


def command_length(start):
    """The characters of the command line a start launches KSP with (Windows takes 32767)."""
    return len(" ".join(["KSP_x64.exe"] + probe_arguments(start)))


def start_seconds(start):
    return START_OVERHEAD_SECONDS + PROBE_FLIGHT_SECONDS + PROBE_SET_SECONDS * len(start["phases"])


def estimate_seconds(starts, with_profile=True):
    return (PROFILE_START_SECONDS if with_profile else 0.0) + sum(start_seconds(s) for s in starts)


# ----------------------------------------------------------------------------- file edits

def parse_cfg_values(text):
    """The key = value lines of a settings.cfg, in order."""
    values = {}
    for line in text.splitlines():
        m = re.match(r"^\s*(\w+)\s*=\s*(.*?)\s*$", line)
        if m:
            values[m.group(1)] = m.group(2)
    return values


def set_cfg_values(text, values):
    """settings.cfg with each key set: its line replaced where it is, added before the
    closing brace of the ReDefinition block where it is not."""
    lines = text.splitlines()
    pending = dict(values)
    for i, line in enumerate(lines):
        m = re.match(r"^(\s*)(\w+)\s*=", line)
        if m and m.group(2) in pending:
            lines[i] = "{}{} = {}".format(m.group(1) or "\t", m.group(2), pending.pop(m.group(2)))
    if pending:
        close = max(i for i, line in enumerate(lines) if line.strip() == "}")
        for k, v in pending.items():
            lines.insert(close, "\t{} = {}".format(k, v))
            close += 1
    return "\n".join(lines) + "\n"


def set_ini_values(text, values):
    """ReDefinitionProxy.ini with each key set in its [Proxy] section, as xshot.sh does it:
    the first line of a key replaced (GetPrivateProfileString reads the first), a missing
    key appended to the section."""
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines()
    pending = dict(values)
    section = None
    end_of_proxy = None
    for i, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            if section == "proxy":
                end_of_proxy = i
            section = stripped[1:-1].strip().lower()
            continue
        if section != "proxy" or stripped.startswith(";"):
            continue
        m = re.match(r"^\s*([A-Za-z0-9_]+)\s*=", line)
        if m and m.group(1) in pending:
            lines[i] = "{}={}".format(m.group(1), pending.pop(m.group(1)))
    if pending:
        if not any(l.strip().lower() == "[proxy]" for l in lines):
            lines.append("[Proxy]")
            end_of_proxy = None
        insert = end_of_proxy if end_of_proxy is not None else len(lines)
        for k, v in pending.items():
            lines.insert(insert, "{}={}".format(k, v))
            insert += 1
    return newline.join(lines) + newline


# ----------------------------------------------------------------------------- log parsing

_PROXY_TIME = re.compile(r"^(\d\d):(\d\d):(\d\d)\.(\d{3})  (.*)$")
_KSP_TIME = re.compile(r"^\[(?:LOG|WRN|ERR|EXC|AST)\s+(\d\d):(\d\d):(\d\d)\.(\d{3})\]\s?(.*)$")
_REPORT = re.compile(r"(?:D3D12 proxy|Baseline): (\d+) frames, mean ([\d.]+) ms \(([\d.]+) fps\), p50 ([\d.]+), p95 ([\d.]+),"
                     r" p99 ([\d.]+), max ([\d.]+), presented/rendered ([\d.]+), displayed (\d+)/s at (\d+) Hz, sync (\d+)")
_LATENCY = re.compile(r"^Latency: from the frame's start to the GPU's end of its Present ([\d.]+) ms \(median of (\d+)"
                      r" frames, 90th percentile ([\d.]+)\), lowered by (.*)$")
_PCL = re.compile(r"^Low latency: NVIDIA's report, from the simulation's start to the GPU's end of the frame ([\d.]+) ms"
                  r" \(median of (\d+) frames\), the GPU's frame ([\d.]+) ms; low latency mode (\w+)")
# The proxy's line for a frame that built 50 meshes or more, once its GPU time is read
# (RaytracingScene.cpp, NoteMeshBuildTimeLocked): the load spike of a flight's start.
_RT_BUILD = re.compile(r"^Raytracing: the (\d+) meshes built in one frame \((\d+) triangles\) took ([\d.]+) ms of the GPU"
                       r" and ([\d.]+) ms of the CPU")
_BRAKE = re.compile(r"^Latency brake: sleeps ([\d.]+) ms before the frame, ([\d.]+) ms of waits in the proxy(?: with the Present)? left a frame"
                    r" of ([\d.]+) ms work")
_STAGES = re.compile(r"^Latency: the frame's stages, medians in ms -- simulation ([\d.-]+), simulation's end to the Present"
                      r" ([\d.-]+), Present ([\d.-]+), to the GPU's end ([\d.-]+), the render thread's first call to the Present ([\d.-]+)$")
_UPSCALER = re.compile(r"Upscaler running on '([^']*)': (\d+)x(\d+) -> (\d+)x(\d+) \(([^,]+), (.*)\)$")


def _seconds(h, m, s, ms):
    return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000.0


def _timed_lines(text, pattern, keep=None):
    """(seconds, text) of each timestamped line, the clock carried over midnight. With
    keep, only the lines containing it count: KSP.log holds ModuleManager's log of an
    earlier start, its times a day off, and those must not move the clock."""
    out, offset, last = [], 0.0, None
    for raw in text.splitlines():
        if keep is not None and keep not in raw:
            continue
        m = pattern.match(raw.rstrip("\r"))
        if not m:
            continue
        t = _seconds(*m.groups()[:4])
        if last is not None and t + offset < last - 43200:
            offset += 86400.0
        last = t + offset
        out.append((last, m.group(5)))
    return out


def parse_config(text):
    """The keys of a "Config:" or "Config reloaded:" line, as written (Config.cpp
    Describe). A value with a space in it, "pacing=0.1/0.1/spin 2", stays whole."""
    text = text.split("   (")[0].strip()
    values, last = {}, None
    for token in text.split(" "):
        if "=" in token:
            k, v = token.split("=", 1)
            values[k] = v
            last = k
        elif last is not None and token:
            values[last] += " " + token
    return _with_parts(values)


def _with_parts(values):
    """The parts of the values the proxy writes as one ("latencyBrake=1/4.000000/1/dlssg"; "dlssgMode=on/3f")
    as keys of their own, so that every option of the record can be read by its name. The
    composite value stays as written."""
    brake = values.get("latencyBrake")
    if brake and "/" in brake:
        parts = brake.split("/")
        values["latencyBrakeTargetMs"] = parts[1]
        if len(parts) > 2:
            values["latencyBrakeWaits"] = parts[2]
        values["latencyBrakeWithDlssg"] = "1" if "dlssg" in parts[3:] else "0"
    mode = values.get("dlssgMode")
    if mode is not None:
        values["dlssgFrames"] = mode.split("/")[1].rstrip("f") if "/" in mode else "0"
    return values


def parse_proxy_log(text):
    p = {"configs": [], "reports": [], "latency": [], "pcl": [], "brake": [], "stages": [], "rt_builds": [], "lines": {},
         "first_time": None}
    notes = []
    for t, line in _timed_lines(text, _PROXY_TIME):
        if p["first_time"] is None:
            p["first_time"] = t
        if line.startswith("Config: enabled=") or line.startswith("Config reloaded: "):
            body = line.split(": ", 1)[1]
            p["configs"].append({"time": t, "reloaded": line.startswith("Config reloaded"), "text": body.split("   (")[0],
                                 "values": parse_config(body)})
            continue
        m = _REPORT.search(line)
        if m:
            g = m.groups()
            p["reports"].append({"time": t, "frames": int(g[0]), "mean_ms": float(g[1]), "fps": float(g[2]),
                                 "p50_ms": float(g[3]), "p95_ms": float(g[4]), "p99_ms": float(g[5]), "max_ms": float(g[6]),
                                 "presented_per_rendered": float(g[7]), "displayed_per_s": int(g[8]),
                                 "refresh_hz": int(g[9]), "sync": int(g[10])})
            continue
        m = _LATENCY.match(line)
        if m:
            p["latency"].append({"time": t, "median_ms": float(m.group(1)), "frames": int(m.group(2)),
                                 "p90_ms": float(m.group(3)), "lowered_by": m.group(4).strip()})
            continue
        m = _STAGES.match(line)
        if m:
            p["stages"].append({"time": t, "simulation": float(m.group(1)), "to_present": float(m.group(2)),
                                "present": float(m.group(3)), "gpu_tail": float(m.group(4)),
                                "render_thread": float(m.group(5))})
            continue
        m = _PCL.match(line)
        if m:
            p["pcl"].append({"time": t, "median_ms": float(m.group(1)), "frames": int(m.group(2)),
                             "gpu_frame_ms": float(m.group(3)), "mode": m.group(4)})
            continue
        m = _RT_BUILD.match(line)
        if m:
            p["rt_builds"].append({"time": t, "meshes": int(m.group(1)), "triangles": int(m.group(2)),
                                   "gpu_ms": float(m.group(3)), "cpu_ms": float(m.group(4))})
            continue
        m = _BRAKE.match(line)
        if m:
            p["brake"].append({"time": t, "sleep_ms": float(m.group(1)), "wait_ms": float(m.group(2)),
                               "frame_ms": float(m.group(3))})
            continue
        for key, prefix in (("backbuffer", "Backbuffer registered: "), ("dlssg", "DLSS frame generation"),
                            ("fsr_fg", "Frame generation context created"), ("dlss", "DLSS: nvngx_dlss.dll"),
                            ("amd_upscaler", "AMD upscaler: "), ("swapchain", "Swapchain created"),
                            ("low_latency", "Low latency: NVIDIA Reflex"), ("anti_lag", "Low latency: AMD"),
                            ("xell", "Low latency: Intel XeLL"), ("xess_fg", "XeSS frame generation"),
                            ("fg_multiplier", "Frame generation multiplier requested")):
            if line.startswith(prefix):
                notes.append((key, t, line))
                break
        m = re.search(r"NVIDIA driver (\d+\.\d+)", line)
        if m:
            notes.append(("nvidia_driver", t, m.group(1)))
    for key, t, line in notes:
        p["lines"].setdefault(key, []).append({"time": t, "text": line})
    return p


def _unfocused_at(proxy, t):
    """Whether the proxy's last word on the game's window before t was that it is not in the
    foreground while DLSS-G is asked to generate (SwapChainProxyStreamline.cpp)."""
    state = False
    for line in proxy["lines"].get("dlssg", []):
        if line["time"] > t:
            break
        if "the game's window is not in the foreground" in line["text"]:
            state = True
        elif "the game's window is in the foreground again" in line["text"]:
            state = False
    return state


def parse_ksp_log(text):
    k = {"gpu": None, "upscaler": [], "sets": [], "shots": [], "profile": [], "probe": [], "rr": [], "raytracing": [],
         "multiplier": [], "first_time": None}
    for raw in text.splitlines()[:40]:
        m = re.match(r"^GPU: (.*)$", raw.strip())
        if m and k["gpu"] is None:
            k["gpu"] = m.group(1).strip()
    for t, line in _timed_lines(text, _KSP_TIME, "] [ReDefinition] "):
        if k["first_time"] is None:
            k["first_time"] = t
        m = _UPSCALER.search(line)
        if m:
            g = m.groups()
            k["upscaler"].append({"time": t, "camera": g[0], "render": [int(g[1]), int(g[2])], "display": [int(g[3]), int(g[4])],
                                  "mode": g[5], "technique": g[6]})
            continue
        m = re.search(r"Probe: ReDefinitionProxy\.ini set to (.*)\.$", line)
        if m:
            k["sets"].append({"time": t, "set": m.group(1)})
            continue
        m = re.search(r"Probe: shot (\S+)-ini(\d+) with (.*)\.$", line)
        if m:
            k["shots"].append({"time": t, "index": int(m.group(2)), "set": m.group(3)})
            continue
        if "Probe: profile" in line or "Probe: no profile" in line:
            k["profile"].append(line)
        if "DLSS Ray Reconstruction" in line:
            k["rr"].append({"time": t, "text": line.split("[ReDefinition] ", 1)[-1]})
        m = re.search(r"Frame generation multiplier (\S+)$", line)
        if m:
            k["multiplier"].append({"time": t, "choice": m.group(1)})
        m = re.search(r"Raytracing set to (\w+)", line)
        if m:
            k["raytracing"].append({"time": t, "mode": m.group(1), "text": line.split("[ReDefinition] ", 1)[-1]})
        if "Probe:" in line:
            k["probe"].append(line.split("[ReDefinition] ", 1)[-1])
    return k


def _align(ksp, proxy):
    """KSP.log's times on the proxy log's clock: both are the local time of day, and a
    run past midnight moves one of them a day on."""
    if ksp["first_time"] is None or proxy["first_time"] is None:
        return 0.0
    if ksp["first_time"] < proxy["first_time"] - 43200:
        return 86400.0
    if ksp["first_time"] > proxy["first_time"] + 43200:
        return -86400.0
    return 0.0


def _in_window(entries, start, end, report_seconds):
    """The entries whose interval lies inside the window: a report at time t covers the
    report_seconds before it."""
    return [e for e in entries if e["time"] - report_seconds >= start and e["time"] <= end]


def aggregate(reports, latency, pcl, brake, stages=()):
    out = {"reports": len(reports)}
    if reports:
        frames = sum(r["frames"] for r in reports)
        busy = sum(r["frames"] * r["mean_ms"] for r in reports) / 1000.0
        out["frames"] = frames
        out["fps"] = round(frames / busy, 1) if busy > 0 else None
        out["fps_min"] = min(r["fps"] for r in reports)
        out["fps_max"] = max(r["fps"] for r in reports)
        out["p95_ms"] = round(statistics.median(r["p95_ms"] for r in reports), 2)
        out["p99_ms"] = round(statistics.median(r["p99_ms"] for r in reports), 2)
        ratio = sum(r["frames"] * r["presented_per_rendered"] for r in reports) / frames if frames else 0.0
        out["presented_per_rendered"] = round(ratio, 2)
        shown = [r["displayed_per_s"] for r in reports if r["refresh_hz"] > 0]
        out["displayed_per_s"] = round(statistics.mean(shown), 1) if shown else None
        out["refresh_hz"] = statistics.median(r["refresh_hz"] for r in reports)
        out["shown_fps"] = out["displayed_per_s"] if out["displayed_per_s"] else (
            round(out["fps"] * ratio, 1) if out["fps"] else None)
    if latency:
        out["latency_median_ms"] = round(statistics.median(l["median_ms"] for l in latency), 1)
        out["latency_p90_ms"] = round(statistics.median(l["p90_ms"] for l in latency), 1)
        out["latency_reports"] = len(latency)
        lowered = sorted({l["lowered_by"] for l in latency})
        out["latency_lowered_by"] = lowered[0] if len(lowered) == 1 else " / ".join(lowered)
    if stages:
        # Where the latency went, each stage's median over the reports (LowLatency::ReportLocked).
        out["latency_stages_ms"] = {k: round(statistics.median(s[k] for s in stages), 1)
                                    for k in ("simulation", "to_present", "present", "gpu_tail", "render_thread")}
    if pcl:
        out["nvidia_pcl_median_ms"] = round(statistics.median(x["median_ms"] for x in pcl), 1)
        out["nvidia_gpu_frame_ms"] = round(statistics.median(x["gpu_frame_ms"] for x in pcl), 1)
    if brake:
        out["brake_sleep_ms"] = round(statistics.median(b["sleep_ms"] for b in brake), 1)
        out["brake_wait_ms"] = round(statistics.median(b["wait_ms"] for b in brake), 1)
    return out


def _last_before(entries, t):
    best = None
    for e in entries:
        if e["time"] <= t:
            best = e
    return best


def _fg_kind_ran(proxy):
    """Which frame generation the proxy set up in this start, from its own lines."""
    dlssg = proxy["lines"].get("dlssg", [])
    if any("does not run here" in x["text"] for x in dlssg):
        kind = "FSR (DLSS-G does not run here)"
    elif any(x["text"].startswith("DLSS frame generation on") or "Streamline 2.14" in x["text"] and "nvngx_dlssg" in x["text"]
             for x in dlssg) or any("Streamline (DLSS frame generation)" in x["text"] for x in proxy["lines"].get("swapchain", [])):
        kind = "DLSS-G"
    elif any("XeSS-FG (XeSS frame generation" in x["text"] for x in proxy["lines"].get("swapchain", [])):
        kind = "XeSS-FG"
    elif proxy["lines"].get("fsr_fg"):
        kind = "FSR"
    else:
        kind = None
    return kind


def phases_of_start(start, ksp_text, proxy_text, settle_seconds):
    """Each phase of one start, measured: the window from the proxy's reload of the
    phase's keys plus settle_seconds to the probe's shot that ends the phase."""
    ksp = parse_ksp_log(ksp_text)
    proxy = parse_proxy_log(proxy_text)
    shift = _align(ksp, proxy)
    for group in ("upscaler", "sets", "shots", "rr", "raytracing", "multiplier"):
        for e in ksp[group]:
            e["time"] += shift
    load_config = next((c for c in proxy["configs"] if not c["reloaded"]), None)
    fg_kind = _fg_kind_ran(proxy)
    problems = []
    problems.extend(l for l in ksp["probe"] if l.startswith("Probe: no setting"))
    if len(ksp["sets"]) < len(start["phases"]):
        problems.append("the probe set {} of {} key sets".format(len(ksp["sets"]), len(start["phases"])))
    results = []
    for i, phase in enumerate(start["phases"]):
        if i >= len(ksp["sets"]):
            break
        set_time = ksp["sets"][i]["time"]
        shot = next((s for s in ksp["shots"] if s["index"] == i), None)
        end = shot["time"] if shot else set_time + PROBE_SET_SECONDS
        reload_ = next((c for c in proxy["configs"] if c["reloaded"] and set_time - 0.5 <= c["time"] <= end), None)
        config = reload_ or _last_before(proxy["configs"], end)
        settle = phase.get("settle", settle_seconds)
        begin = (reload_["time"] if reload_ else set_time + 1.0) + settle
        report_seconds = float(config["values"].get("reportSeconds", 10)) if config else 10.0
        window = {k: _in_window(proxy[k], begin, end, report_seconds) for k in ("reports", "latency", "pcl", "brake", "stages")}
        measured = aggregate(window["reports"], window["latency"], window["pcl"], window["brake"], window["stages"])
        upscaler = _last_before(ksp["upscaler"], end)
        warnings = []
        if reload_ is None:
            warnings.append("no Config reloaded line after the probe set the keys")
        if shot is None:
            warnings.append("the probe's shot that ends the phase is missing")
        if not window["reports"]:
            warnings.append("no frame time report inside the phase")
        if any(begin <= u["time"] <= end for u in ksp["upscaler"]):
            warnings.append("the upscaler was set up again during the phase")
        s = phase["scenario"]
        if upscaler is None:
            warnings.append("no upscaler ran")
        elif upscaler["technique"] != s["technique_log_name"]:
            warnings.append("asked for {}, ran {}".format(s["technique_log_name"], upscaler["technique"]))
        ratio = measured.get("presented_per_rendered")
        if ratio is not None:
            if s["fg_requested"] and ratio < 1.5:
                text = "frame generation asked for, presented/rendered {:.2f}".format(ratio)
                if _unfocused_at(proxy, end):
                    text += (" -- the game's window was not in the foreground, and DLSS-G generates nothing then"
                             " (the proxy's log says so)")
                warnings.append(text)
            if not s["fg_requested"] and ratio > 1.2:
                warnings.append("frame generation held off, presented/rendered {:.2f}".format(ratio))
        step_check = _step_check(s, ratio)
        if step_check and step_check["delivered"] is False:
            warnings.append("{} asked for, presented/rendered {:.2f}: the GPU or the driver delivered {}x".format(
                step_check["asked"], ratio, step_check["shown_as"]))
        if s["fg_requested"] and s["fg_kind"] == "dlssg" and fg_kind != "DLSS-G":
            warnings.append("DLSS-G asked for, the proxy set up {}".format(fg_kind or "none"))
        if s["fg_requested"] and s["fg_kind"] == "xessfg" and fg_kind != "XeSS-FG":
            warnings.append("XeSS-FG asked for, the proxy set up {}".format(fg_kind or "none"))
        traced = _last_before(ksp["raytracing"], end)
        rt_now = traced["mode"] if traced else start.get("mod_settings", {}).get("raytracing")
        if rt_now is not None and rt_now.lower() != s["raytracing"].lower():
            warnings.append("raytracing {} asked for, {} set".format(s["raytracing"], rt_now))
        for key, want in phase["keys"].items():
            if key.startswith("mod."):
                continue
            got = config["values"].get(key) if config else None
            if key in ("latencyBrake", "latencyBrakeTargetMs") and config:
                brake = config["values"].get("latencyBrake", "")
                got = brake.split("/")[0] if key == "latencyBrake" else (brake.split("/")[1] if "/" in brake else None)
            if got is not None and not _same_number(got, want):
                warnings.append("{}={} asked for, the proxy has {}".format(key, want, got))
        results.append({
            "index": i, "scenario": s, "key": scenario_key(s), "label": scenario_label(s), "keys": phase["keys"],
            "window": [round(begin, 3), round(end, 3)], "settle_seconds": settle, "rebuild": phase.get("rebuild", False),
            "mod_settings_set": phase.get("mod_settings"),
            "ray_reconstruction_lines": [e["text"] for e in ksp["rr"] if set_time <= e["time"] <= end] or None,
            "proxy_config": config["values"] if config else None,
            "proxy_config_line": config["text"] if config else None,
            "upscaler": upscaler, "fg_kind_ran": fg_kind if ratio and ratio >= 1.5 else "none",
            "fg_step": step_check,
            "fg_lines": [x["text"] for key in ("dlssg", "fg_multiplier", "xess_fg") for x in proxy["lines"].get(key, [])
                         if set_time - 0.5 <= x["time"] <= end] + [
                             "mod: frame generation multiplier " + m["choice"] for m in ksp["multiplier"]
                             if set_time - 0.5 <= m["time"] <= end] or None,
            "measured": measured, "warnings": warnings,
        })
    phases = _merge_repeats(results)
    for r in phases:
        if r.get("repeats"):
            rs = float((r["proxy_config"] or {}).get("reportSeconds", 10))
            r["measured"] = aggregate(*[_in_window(proxy[k], r["window"][0], r["window"][1], rs)
                                        for k in ("reports", "latency", "pcl", "brake", "stages")])
    return {
        "gpu": ksp["gpu"], "profile_lines": ksp["profile"],
        "proxy_load_config": load_config["values"] if load_config else None,
        "proxy_load_config_line": load_config["text"] if load_config else None,
        "fg_kind_set_up": fg_kind,
        "backbuffer": [x["text"] for x in proxy["lines"].get("backbuffer", [])][-1:] or None,
        "dlss_dll": [x["text"] for x in proxy["lines"].get("dlss", [])][-1:] or None,
        "amd_upscaler": [x["text"] for x in proxy["lines"].get("amd_upscaler", [])] or None,
        "dlssg_lines": [x["text"] for x in proxy["lines"].get("dlssg", [])][:6] or None,
        "nvidia_driver": next((x["text"] for x in proxy["lines"].get("nvidia_driver", [])), None),
        "low_latency_lines": [x["text"] for x in proxy["lines"].get("low_latency", []) + proxy["lines"].get("anti_lag", [])
                                                 + proxy["lines"].get("xell", [])] or None,
        "ray_reconstruction_lines": [x["text"] for x in ksp["rr"]][:4] or None,
        "upscaler_lines": ksp["upscaler"][:4],
        "rt_builds": rt_builds_of(proxy["rt_builds"]),
        "problems": problems, "phases": phases,
    }


def rt_builds_of(builds):
    """The frames of a start that built 50 meshes or more, as the proxy logged them, and the
    slowest of them: the raytracing's load spike at the flight's start, which the phases'
    steady frame rates do not show. The proxy's rtBlas* and rtMegaGeometry keys of the
    start's proxy_load decide how they were built."""
    if not builds:
        return None
    return {"frames": len(builds), "meshes": sum(b["meshes"] for b in builds),
            "triangles": sum(b["triangles"] for b in builds),
            "slowest_gpu_ms": max(b["gpu_ms"] for b in builds), "slowest_cpu_ms": max(b["cpu_ms"] for b in builds),
            "lines": builds[:10]}


def _step_check(scenario, ratio):
    """What a frame generation step asked for and what the frames per rendered frame show:
    asked "3x" and 2.0 presented per rendered frame is a step the GPU or the driver did not
    deliver (shown_as the whole number nearest). Dynamic has no number to be held to."""
    step = scenario.get("step")
    if not step or not scenario.get("fg_requested") or ratio is None:
        return None
    shown_as = int(round(ratio))
    if step == "Dynamic":
        return {"asked": step, "expected": None, "shown": ratio, "shown_as": shown_as, "delivered": None}
    expected = int(step.rstrip("x"))
    return {"asked": step, "expected": expected, "shown": ratio, "shown_as": shown_as,
            "delivered": ratio >= expected - STEP_TOLERANCE}


def _same_number(a, b):
    try:
        return abs(float(a) - float(b)) < 1e-6
    except ValueError:
        return str(a).strip() == str(b).strip()


def _merge_repeats(results):
    """Consecutive phases of the same scenario (phase_repeats) as one window, from the
    first one's start after its settling time to the last one's end."""
    merged = []
    for r in results:
        if merged and merged[-1]["key"] == r["key"]:
            merged[-1]["_parts"].append(r)
        else:
            r = dict(r)
            r["_parts"] = [r.copy()]
            merged.append(r)
    out = []
    for m in merged:
        parts = m.pop("_parts")
        if len(parts) > 1:
            m["window"] = [parts[0]["window"][0], parts[-1]["window"][1]]
            m["repeats"] = len(parts)
            m["warnings"] = sorted({w for p in parts for w in p["warnings"]})
        out.append(m)
    return out


# ----------------------------------------------------------------------------- the install

class _Win32:
    """The few user32 calls that bring a process's window forward. Windows only."""

    def __init__(self):
        import ctypes
        from ctypes import wintypes
        self.ctypes = ctypes
        self.wintypes = wintypes
        self.user32 = user32 = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel32 = kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        user32.GetWindowThreadProcessId.restype = wintypes.DWORD
        user32.GetForegroundWindow.restype = wintypes.HWND
        for name in ("IsWindowVisible", "IsIconic", "SetForegroundWindow", "GetWindowTextLengthW"):
            getattr(user32, name).argtypes = [wintypes.HWND]
        user32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
        user32.GetWindow.restype = wintypes.HWND
        user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
        user32.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
        kernel32.GetCurrentThreadId.restype = wintypes.DWORD

    def find_window(self, pid):
        """The process's top-level window, or None."""
        ctypes, wintypes, user32 = self.ctypes, self.wintypes, self.user32
        found = []
        visit_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def visit(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            # Visible, titled and not owned by another window: the game's own, not a tool window.
            if (owner.value == pid and user32.IsWindowVisible(hwnd) and not user32.GetWindow(hwnd, 4)
                    and user32.GetWindowTextLengthW(hwnd) > 0):
                found.append(hwnd)
            return True

        user32.EnumWindows.argtypes = [visit_type, wintypes.LPARAM]
        user32.EnumWindows(visit_type(visit), 0)
        return found[0] if found else None

    def foreground(self):
        return self.user32.GetForegroundWindow()

    def minimised(self, hwnd):
        return bool(self.user32.IsIconic(hwnd))

    def restore(self, hwnd):
        self.user32.ShowWindow(hwnd, 9)   # SW_RESTORE

    def set_foreground(self, hwnd):
        self.user32.SetForegroundWindow(hwnd)
        return self.user32.GetForegroundWindow() == hwnd

    def set_foreground_attached(self, hwnd):
        """Sharing the input queue of the window in front is what lets this process take the foreground."""
        user32 = self.user32
        front = user32.GetForegroundWindow()
        front_thread = user32.GetWindowThreadProcessId(front, None) if front else 0
        mine = self.kernel32.GetCurrentThreadId()
        attached = bool(front_thread) and front_thread != mine and user32.AttachThreadInput(mine, front_thread, True)
        try:
            return self.set_foreground(hwnd)
        finally:
            if attached:
                user32.AttachThreadInput(mine, front_thread, False)

    def alt_tap(self, hwnd):
        """An Alt press, which Windows counts as input."""
        self.user32.keybd_event(0x12, 0, 0, 0)
        self.user32.keybd_event(0x12, 0, 2, 0)
        return self.set_foreground(hwnd)


class Foreground:
    """Keeps a process's window in front, as little intrusively as it can. NVIDIA's DLSS-G
    generates nothing while the game's window is not (Streamline: "DLSS-G disabled: window not
    focused"), and a KSP that this tool starts is not given the foreground when another
    program's window is in front. keep() does nothing while the window is in front; else it
    restores it only if it is minimised, asks plainly, then with the input queue of the window
    in front shared, and taps Alt only once for a process, when both failed. After a failure
    it leaves the window alone for FOREGROUND_RETRY_SECONDS."""

    def __init__(self, pid, win=None, clock=time.monotonic):
        self.pid = pid
        self.win = win
        self.clock = clock
        self.alt_tapped = False
        self.retry_at = 0.0

    def keep(self):
        """True where the window is in front afterwards; False where there is none, or Windows did
        not allow it."""
        if self.win is None:
            if os.name != "nt":
                return False
            self.win = _Win32()
        win = self.win
        hwnd = win.find_window(self.pid)
        if hwnd is None:
            return False
        if win.foreground() == hwnd:
            self.retry_at = 0.0
            return True
        if self.clock() < self.retry_at:
            return False
        if win.minimised(hwnd):
            win.restore(hwnd)
        ok = win.set_foreground(hwnd) or win.set_foreground_attached(hwnd)
        if not ok and not self.alt_tapped:
            self.alt_tapped = True
            ok = win.alt_tap(hwnd)
        self.retry_at = 0.0 if ok else self.clock() + FOREGROUND_RETRY_SECONDS
        return ok


def bring_to_front(pid):
    """Makes the top-level window of the process the foreground window once (Foreground). False
    where there is no such window, or Windows did not allow it. Windows only."""
    return Foreground(pid).keep()


class Install:
    def __init__(self, ksp):
        self.ksp = ksp

    def path(self, *parts):
        return os.path.join(self.ksp, *parts)

    def settings_files(self):
        for name in os.listdir(self.ksp):
            p = os.path.join(self.ksp, name)
            if os.path.isfile(p) and name.lower().endswith(SNAP_EXT):
                yield name
        for top in ("GameData", "PluginData"):
            for root, _dirs, names in os.walk(os.path.join(self.ksp, top)):
                for n in names:
                    if n.lower().endswith(SNAP_EXT):
                        yield os.path.relpath(os.path.join(root, n), self.ksp)

    def snap(self, out):
        count = 0
        for rel in self.settings_files():
            dst = os.path.join(out, "files", rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(os.path.join(self.ksp, rel), dst)
            count += 1
        # The time is the file system's own, as a file written later gets it: time.time()
        # runs ahead of a file's coarse clock, and a file written just after the snapshot
        # would then look older than it. A file made once the clock has ticked is later
        # than the marker, and every file made before it is not.
        marker = os.path.join(out, "snap.json")
        with open(marker, "w") as f:
            json.dump({"time": time.time()}, f)
        taken = os.path.getmtime(marker)
        probe = os.path.join(out, "tick")
        for _ in range(200):
            with open(probe, "w"):
                pass
            if os.path.getmtime(probe) > taken:
                break
            time.sleep(0.005)
        os.remove(probe)
        with open(marker, "w") as f:
            json.dump({"time": taken}, f)
        os.utime(marker, (taken, taken))
        return count

    def restore(self, out):
        with open(os.path.join(out, "snap.json")) as f:
            since = json.load(f)["time"]
        kept = os.path.join(out, "files")
        report = []
        for root, _dirs, names in os.walk(kept):
            for n in names:
                src = os.path.join(root, n)
                rel = os.path.relpath(src, kept)
                dst = os.path.join(self.ksp, rel)
                if not os.path.exists(dst) or not filecmp.cmp(src, dst, shallow=False):
                    shutil.copy2(src, dst)
                    report.append("restored " + rel)
        # A settings file the run made where there was none: the snapshot took every
        # file of its kind, so it did not exist before. The two seconds allow for the
        # file system's coarser clock.
        for rel in list(self.settings_files()):
            p = self.path(rel)
            if not os.path.exists(os.path.join(kept, rel)) and os.path.getmtime(p) >= since - 2.0:
                os.remove(p)
                report.append("removed " + rel)
        # The rest is KSP's own (squadcorefx.ksp, Launcher_Data's backups): only named.
        for root, _dirs, names in os.walk(self.ksp):
            rel_root = os.path.relpath(root, self.ksp)
            if rel_root.split(os.sep)[0] in SNAP_SKIP:
                continue
            for n in names:
                p = os.path.join(root, n)
                rel = os.path.relpath(p, self.ksp)
                if os.path.getmtime(p) > since and not os.path.exists(os.path.join(kept, rel)):
                    report.append("written since, not kept: " + rel)
        return report

    def running(self):
        try:
            out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe"], capture_output=True, text=True).stdout
        except OSError:
            return False
        return "KSP_x64.exe" in out

    def launch(self, args, timeout):
        """KSP_x64.exe with these arguments, from its own folder, until it quits."""
        proc = subprocess.Popen([self.path("KSP_x64.exe")] + args, cwd=self.ksp,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # The game's window stays in the foreground for as long as it runs (bring_to_front):
        # DLSS-G generates no frame otherwise, and a start of the run that no one watched
        # would measure the frames it renders alone. REDEFINITION_BENCH_NO_FOREGROUND leaves
        # the window where Windows puts it, to reproduce that.
        done = threading.Event()
        foreground = Foreground(proc.pid)

        def keep_in_front():
            while not done.wait(FOREGROUND_SECONDS):
                try:
                    foreground.keep()
                except Exception:
                    pass

        if not os.environ.get("REDEFINITION_BENCH_NO_FOREGROUND"):
            threading.Thread(target=keep_in_front, daemon=True).start()
        try:
            proc.wait(timeout=timeout)
            return "quit"
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
            return "killed after {} s".format(timeout)
        except KeyboardInterrupt:
            # KSP writes its settings when it quits: closed first, so that the restore
            # after this is the last to write them.
            proc.kill()
            proc.wait()
            raise
        finally:
            done.set()

    def package_in_place(self):
        return [n for n in PACKAGE_FILES + (PACKAGE_DIR,) if os.path.exists(self.path(n))]

    def put_package(self, package, max_generated_frames, ini_values=None):
        # A copy an earlier start left half done goes first.
        self.remove_package()
        shutil.copy2(os.path.join(package, "version.dll"), self.path("version.dll"))
        with open(os.path.join(package, "dlssg_sm86.ini"), encoding="utf-8") as f:
            text = f.read()
        if max_generated_frames is not None:
            text = re.sub(r"(?m)^MaxGeneratedFrames=.*$", "MaxGeneratedFrames={}".format(max_generated_frames), text)
        # The start's own values: sections, keys and values the package's ini is read with.
        for section, keys in (ini_values or {}).items():
            for key, value in keys.items():
                text = set_package_ini_value(text, section, key, value)
        with open(self.path("dlssg_sm86.ini"), "w", encoding="utf-8") as f:
            f.write(text)
        for sub in PACKAGE_SUBDIRS:
            shutil.copytree(os.path.join(package, PACKAGE_DIR, sub), self.path(PACKAGE_DIR, sub))

    def remove_package(self):
        for n in PACKAGE_FILES:
            if os.path.exists(self.path(n)):
                os.remove(self.path(n))
        if os.path.isdir(self.path(PACKAGE_DIR)):
            shutil.rmtree(self.path(PACKAGE_DIR))

    def build_identity(self):
        out = {}
        for rel in ("dxgi.dll", os.path.join("GameData", "ReDefinition", "Plugins", "ReDefinition.dll")):
            p = self.path(rel)
            if os.path.exists(p):
                with open(p, "rb") as f:
                    digest = hashlib.sha256(f.read()).hexdigest()[:16]
                out[rel.replace(os.sep, "/")] = {
                    "sha256": digest,
                    "modified": datetime.datetime.fromtimestamp(os.path.getmtime(p)).isoformat(timespec="seconds")}
        return out

    def latest_capture_run(self):
        root = self.path("ReDefinitionCaptures")
        if not os.path.isdir(root):
            return None
        runs = [os.path.join(root, d) for d in os.listdir(root) if d.endswith("-run")]
        return max(runs, key=os.path.getmtime) if runs else None


def machine_record():
    """The GPUs and drivers as Windows names them, the CPU and the memory."""
    record = {"adapters": [], "cpu": None, "memory_gb": None, "os": None}
    script = ("$v = Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, "
              "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate; "
              "$c = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name; "
              "$m = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory; "
              "$o = (Get-CimInstance Win32_OperatingSystem); "
              "@{adapters=@($v); cpu=$c; memory=$m; os=($o.Caption + ' ' + $o.BuildNumber)} | ConvertTo-Json -Depth 4")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=60).stdout
        data = json.loads(out)
    except (OSError, ValueError, subprocess.TimeoutExpired):
        return record
    for a in data.get("adapters") or []:
        record["adapters"].append({
            "name": a.get("Name"), "driver": a.get("DriverVersion"),
            "nvidia_driver": nvidia_driver_from_windows(a.get("DriverVersion")) if "NVIDIA" in (a.get("Name") or "") else None,
            "resolution": "{}x{}".format(a.get("CurrentHorizontalResolution"), a.get("CurrentVerticalResolution"))
            if a.get("CurrentHorizontalResolution") else None,
            "refresh_hz": a.get("CurrentRefreshRate")})
    record["cpu"] = (data.get("cpu") or "").strip() or None
    if data.get("memory"):
        record["memory_gb"] = round(int(data["memory"]) / 2 ** 30, 1)
    record["os"] = data.get("os")
    return record


def nvidia_driver_from_windows(version):
    """NVIDIA's driver number from Windows' version of it: 32.0.15.9716 is 597.16."""
    if not version:
        return None
    digits = version.replace(".", "")[-5:]
    return "{}.{}".format(digits[:3], digits[3:]) if len(digits) == 5 and digits.isdigit() else None


def vendor_of(machine):
    for a in machine.get("adapters", []):
        name = (a.get("name") or "").upper()
        for vendor in ("NVIDIA", "AMD", "INTEL"):
            if vendor in name or (vendor == "AMD" and "RADEON" in name):
                return vendor if vendor != "INTEL" else "Intel"
    return None


def git_record():
    def git(*args):
        try:
            return subprocess.run(["git"] + list(args), cwd=REPO, capture_output=True, text=True).stdout.strip()
        except OSError:
            return ""
    return {"commit": git("rev-parse", "HEAD"), "short": git("rev-parse", "--short", "HEAD"),
            "branch": git("rev-parse", "--abbrev-ref", "HEAD"), "dirty": bool(git("status", "--porcelain", "--untracked-files=no"))}


# ----------------------------------------------------------------------------- the run

def run(args):
    matrix = load_matrix(args.matrix)
    install = Install(args.ksp)
    if not os.path.exists(install.path("KSP_x64.exe")):
        sys.exit("No KSP_x64.exe in " + args.ksp)
    if install.running():
        sys.exit("KSP is running; close it first.")
    resources = resources_of(args)
    machine = machine_record()
    vendor = args.vendor or vendor_of(machine)
    try:
        groups = [g["id"] for g in select_groups(matrix, args.group)]
        starts, skipped = expand(matrix, vendor, resources, groups)
    except ValueError as e:
        sys.exit(str(e))
    if args.only:
        starts = [s for s in starts if any(o in s["id"] for o in args.only)]
    if not starts:
        sys.exit("No start to run.")
    for s in starts:
        if command_length(s) > MAX_COMMAND_CHARS:
            sys.exit("The command line of {} is {} characters, past the {} that Windows takes: fewer phases in that start."
                     .format(s["id"], command_length(s), MAX_COMMAND_CHARS))
    if any(s["dlssg_package"] for s in starts):
        found = install.package_in_place()
        if found:
            sys.exit("The DLSS-G package is already next to KSP_x64.exe ({}); the run would remove it.".format(", ".join(found)))
    stamp = datetime.datetime.now().strftime("%Y-%m-%d-%H%M")
    raw = os.path.join(args.raw, stamp)
    os.makedirs(raw, exist_ok=True)
    plan = {"matrix": matrix, "matrix_file": os.path.relpath(args.matrix, REPO), "groups": groups, "starts": starts, "skipped": skipped,
            "vendor": vendor, "resources": resources, "stamp": stamp, "settle_seconds": matrix.get("settle_seconds", 3)}
    print("groups {}: {} starts, about {:.0f} minutes; raw logs in {}".format(
        ", ".join(groups), len(starts), estimate_seconds(starts, not args.no_profile) / 60.0, raw))
    for what, why in skipped:
        print("skipped: {} ({})".format(what, why))

    snapshot = os.path.join(raw, "snapshot")
    print("snapshot:", install.snap(snapshot), "files")
    template = args.settings_template or os.path.join(snapshot, "files", SETTINGS_REL)
    with open(template, encoding="utf-8") as f:
        settings_template = f.read()
    with open(os.path.join(snapshot, "files", PROXY_INI), encoding="utf-8", errors="replace") as f:
        ini_template = f.read()
    plan["settings_template"] = settings_template
    plan["proxy_ini_template"] = ini_template
    plan["git"] = git_record()
    plan["machine"] = machine
    plan["build"] = install.build_identity()
    plan["started"] = datetime.datetime.now().isoformat(timespec="seconds")
    try:
        if not args.no_profile:
            lines, settings_template = apply_profile(install, matrix["profile"], settings_template, raw)
            plan["profile_lines"] = lines
            plan["settings_template"] = settings_template
            print("\n".join(lines) or "no profile line")
        _save_plan(raw, plan)
        for n, start in enumerate(starts):
            print("[{}/{}] {} ({} phases)".format(n + 1, len(starts), start["id"], len(start["phases"])))
            try:
                run_start(install, start, raw, settings_template, ini_template, resources.get("dlssg_package"))
            except (OSError, subprocess.SubprocessError) as e:
                # One start that cannot run leaves the others to measure; its row says why.
                print("   failed:", e)
                with open(os.path.join(raw, start["id"] + ".outcome.txt"), "w") as f:
                    f.write("failed: {}\n".format(e))
    finally:
        if any(s["dlssg_package"] for s in starts):
            install.remove_package()
        report = install.restore(snapshot)
        with open(os.path.join(raw, "restore.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(report) + "\n")
        print("restored: {} file(s), removed {}".format(sum(1 for r in report if r.startswith("restored")),
                                                         sum(1 for r in report if r.startswith("removed"))))
        plan["finished"] = datetime.datetime.now().isoformat(timespec="seconds")
        _save_plan(raw, plan)
    record = build_record(raw)
    write_results(record, args.no_doc)


def apply_profile(install, profile, settings_template, raw):
    """The profile applied in a KSP start of its own, from the given settings. Returns its
    log lines and the mod's settings.cfg as the profile left it: the profile sets the
    mod's own module too (the upscaler's quality and model), so every start begins from
    that file rather than from the one before the profile."""
    with open(install.path(SETTINGS_REL), "w", encoding="utf-8") as f:
        f.write(settings_template)
    launched = time.time()
    install.launch(["-redefinitionProbe", "-redefinitionProbeProfile", profile], 400)
    lines = []
    log = install.path("KSP.log")
    if os.path.exists(log) and os.path.getmtime(log) >= launched:
        shutil.copy2(log, os.path.join(raw, "profile.KSP.log"))
        with open(log, encoding="utf-8", errors="replace") as f:
            lines = [l.strip() for l in f if "Probe: profile" in l or "Probe: no profile" in l]
    if not any("applied" in l for l in lines):
        raise RuntimeError("the profile {} was not applied".format(profile))
    with open(install.path(SETTINGS_REL), encoding="utf-8") as f:
        after = f.read()
    with open(os.path.join(raw, "profile.settings.cfg"), "w", encoding="utf-8") as f:
        f.write(after)
    return lines, after


def _save_plan(raw, plan):
    with open(os.path.join(raw, "plan.json"), "w", encoding="utf-8") as f:
        json.dump(plan, f, indent=1)


def run_start(install, start, raw, settings_template, ini_template, package):
    settings = set_cfg_values(settings_template, start["mod_settings"])
    first = start["phases"][0]["keys"] if start["phases"] else {}
    ini_values = dict(start["proxy_load"])
    ini_values.update(first)
    ini = set_ini_values(ini_template, ini_values)
    with open(install.path(SETTINGS_REL), "w", encoding="utf-8") as f:
        f.write(settings)
    with open(install.path(PROXY_INI), "w", encoding="utf-8", newline="") as f:
        f.write(ini)
    with open(os.path.join(raw, start["id"] + ".settings.cfg"), "w", encoding="utf-8") as f:
        f.write(settings)
    with open(os.path.join(raw, start["id"] + ".proxy.ini"), "w", encoding="utf-8", newline="") as f:
        f.write(ini)
    launched = time.time()
    try:
        # Inside the try: a copy that fails half way is removed all the same.
        if start["dlssg_package"]:
            install.put_package(package, start["max_generated_frames"], start.get("dlssg_ini"))
            # The package's ini as this start ran with it: the preset, the multiplier ceiling.
            shutil.copy2(install.path("dlssg_sm86.ini"), os.path.join(raw, start["id"] + ".dlssg_sm86.ini"))
        timeout = int(start_seconds(start) + 2 * START_OVERHEAD_SECONDS + 60)
        outcome = install.launch(probe_arguments(start), timeout)
    finally:
        if start["dlssg_package"]:
            logs = install.path(PACKAGE_DIR, "logs")
            if os.path.isdir(logs):
                shutil.copytree(logs, os.path.join(raw, start["id"] + ".dlssg-logs"), dirs_exist_ok=True)
            install.remove_package()
    # Only what this start wrote: a start that never got going leaves the one before's
    # logs and screenshots in place, and they would be read as this one's.
    fresh = lambda p: os.path.exists(p) and os.path.getmtime(p) >= launched
    for name, target in (("KSP.log", ".KSP.log"), ("ReDefinitionProxy.log", ".proxy.log")):
        if fresh(install.path(name)):
            shutil.copy2(install.path(name), os.path.join(raw, start["id"] + target))
        else:
            outcome += "; no {} written".format(name)
    with open(os.path.join(raw, start["id"] + ".outcome.txt"), "w") as f:
        f.write(outcome + "\n")
    capture = install.latest_capture_run()
    if capture and fresh(capture):
        for n in os.listdir(capture):
            if n.startswith(SHOT) and n.endswith(".png"):
                shutil.copy2(os.path.join(capture, n), os.path.join(raw, start["id"] + "." + n))
    with open(install.path(SETTINGS_REL), "w", encoding="utf-8") as f:
        f.write(settings_template)
    print("   " + outcome)


def resources_of(args):
    out = {}
    if args.dlssg_package and os.path.exists(os.path.join(args.dlssg_package, "version.dll")):
        out["dlssg_package"] = os.path.abspath(args.dlssg_package)
    if args.amd_dll_dir and os.path.exists(os.path.join(args.amd_dll_dir, "amd_fidelityfx_upscaler_dx12.dll")):
        out["amd_dll_dir"] = os.path.abspath(args.amd_dll_dir)
    elif os.path.exists(os.path.join(args.ksp, "amd_fidelityfx_upscaler_dx12.dll")):
        out["amd_dll_dir"] = os.path.abspath(args.ksp)
    if args.xess_dll_dir and os.path.exists(os.path.join(args.xess_dll_dir, "libxess.dll")):
        out["xess_dll_dir"] = os.path.abspath(args.xess_dll_dir)
    elif os.path.exists(os.path.join(args.ksp, "libxess.dll")):
        out["xess_dll_dir"] = os.path.abspath(args.ksp)
    fg_files = ("libxess_fg.dll", "libxell.dll")
    if args.xess_fg_dll_dir and all(os.path.exists(os.path.join(args.xess_fg_dll_dir, n)) for n in fg_files):
        out["xess_fg_dll_dir"] = os.path.abspath(args.xess_fg_dll_dir)
    elif all(os.path.exists(os.path.join(args.ksp, n)) for n in fg_files):
        out["xess_fg_dll_dir"] = os.path.abspath(args.ksp)
    return out


def parse_ini_sections(text):
    """An ini file's values by section, comments left out: {"DLSSG": {"Preset": "C"}, ...}."""
    sections, current = {}, None
    for line in text.splitlines():
        stripped = line.strip()
        header = re.match(r"^\[(.+)\]$", stripped)
        if header:
            current = sections.setdefault(header.group(1).strip(), {})
        elif current is not None and stripped and not stripped.startswith((";", "#")) and "=" in stripped:
            key, value = stripped.split("=", 1)
            current[key.strip()] = value.strip()
    return sections


def build_record(raw):
    with open(os.path.join(raw, "plan.json"), encoding="utf-8") as f:
        plan = json.load(f)
    settle = plan.get("settle_seconds", 3)
    starts = []
    for start in plan["starts"]:
        base = os.path.join(raw, start["id"])
        entry = {"id": start["id"], "group": start.get("group", MAIN_GROUP), "scene": start.get("scene", DEFAULT_SCENE),
                 "variant": start.get("variant"), "probe_args": start.get("probe_args") or [],
                 "mod_settings_set": start["mod_settings"], "proxy_load_set": start["proxy_load"],
                 "dlssg_package": start["dlssg_package"], "max_generated_frames": start["max_generated_frames"],
                 "dlssg_ini": start.get("dlssg_ini")}
        if not os.path.exists(base + ".proxy.log") or not os.path.exists(base + ".KSP.log"):
            entry["problems"] = ["no logs: the start did not run"]
            entry["phases"] = []
            starts.append(entry)
            continue
        with open(base + ".KSP.log", encoding="utf-8", errors="replace") as f:
            ksp_text = f.read()
        with open(base + ".proxy.log", encoding="utf-8", errors="replace") as f:
            proxy_text = f.read()
        parsed = phases_of_start(start, ksp_text, proxy_text, settle)
        entry.update(parsed)
        if os.path.exists(base + ".settings.cfg"):
            with open(base + ".settings.cfg", encoding="utf-8") as f:
                entry["mod_settings_file"] = f.read()
            # Each phase's settings in full: the file the start began with, and what the
            # phase's mod.<key> keys set over it.
            for p in entry["phases"]:
                values = parse_cfg_values(entry["mod_settings_file"])
                values.update(p.get("mod_settings_set") or {})
                p["mod_settings"] = values
        if os.path.exists(base + ".dlssg_sm86.ini"):
            with open(base + ".dlssg_sm86.ini", encoding="utf-8", errors="replace") as f:
                entry["package_ini"] = parse_ini_sections(f.read())
            for p in entry["phases"]:
                p["package_ini"] = entry["package_ini"]
        if os.path.exists(base + ".proxy.ini"):
            with open(base + ".proxy.ini", encoding="utf-8", errors="replace") as f:
                entry["proxy_ini_at_start"] = parse_ini_sections(f.read()).get("Proxy", {})
        if os.path.exists(base + ".outcome.txt"):
            with open(base + ".outcome.txt") as f:
                entry["outcome"] = f.read().strip()
        starts.append(entry)
    return {
        "date": (plan.get("started") or plan["stamp"]), "stamp": plan["stamp"], "finished": plan.get("finished"),
        "git": plan.get("git"), "machine": plan.get("machine"), "build": plan.get("build"), "vendor": plan.get("vendor"),
        "groups": plan.get("groups") or [MAIN_GROUP],
        "profile": plan["matrix"].get("profile"), "profile_lines": plan.get("profile_lines"),
        "matrix_file": plan.get("matrix_file"), "matrix": plan["matrix"], "skipped": plan.get("skipped"),
        "settle_seconds": settle, "resources": plan.get("resources"),
        "settings_template": plan.get("settings_template"), "raw": raw, "starts": starts,
    }


# ----------------------------------------------------------------------------- the results

def record_groups(record):
    """The groups a record covers: those it names, the main matrix for a record from before there were groups."""
    return record.get("groups") or [MAIN_GROUP]


def rows_of(record, group=None):
    """The phases of a record as rows; of one group only where it is given."""
    rows = []
    for start in record["starts"]:
        for p in start.get("phases", []):
            row_group = p["scenario"].get("group") or start.get("group") or record_groups(record)[0]
            if group is not None and row_group != group:
                continue
            rows.append({"key": p["key"], "label": p["label"], "scenario": p["scenario"], "start": start["id"], "group": row_group,
                         "measured": p["measured"], "warnings": p["warnings"], "upscaler": p.get("upscaler"),
                         "fg_kind_ran": p.get("fg_kind_ran"), "rt_builds": start.get("rt_builds"), "fg_step": p.get("fg_step")})
    return rows


def group_definition(record, group):
    """The group's entry of the record's matrix (the main group is the matrix itself)."""
    matrix = record.get("matrix") or {}
    if group == MAIN_GROUP:
        return dict(matrix, id=MAIN_GROUP, name=matrix.get("main_name", "Main matrix"),
                    description=matrix.get("main_description", ""))
    for g in matrix.get("groups", []):
        if g["id"] == group:
            return g
    return {"id": group, "name": group, "description": ""}


def _baseline_key(record, group=None, scenario=None):
    """The key of the row every row of the group is compared with: the group's baseline, or the
    matrix's, in the scene (and the variant) of the row it is compared with."""
    base = (group_definition(record, group or MAIN_GROUP).get("baseline")) or record["matrix"]["baseline"]
    return scenario_key({"technique": base["technique"], "raytracing": base["raytracing"],
                         "ray_reconstruction": base.get("ray_reconstruction"), "fg": base["fg"], "latency": base["latency"],
                         "variant": base.get("variant") or (scenario or {}).get("variant"),
                         "scene": (scenario or {}).get("scene")})


def _reference_key(s):
    """The same rendering without frame generation and without a latency model."""
    ref = dict(s)
    ref["fg"] = "off"
    ref["latency"] = "none"
    ref.pop("step", None)
    return scenario_key(ref)


def _pct(a, b):
    if a is None or b in (None, 0):
        return None
    return 100.0 * (a - b) / b


def _fmt(v, digits=1, signed=False, unit=""):
    if v is None:
        return "–"
    text = ("{:+." + str(digits) + "f}" if signed else "{:." + str(digits) + "f}").format(v)
    return text + unit


def summary_table(record, previous=None, group=MAIN_GROUP):
    rows = rows_of(record, group)
    by_key = {r["key"]: r for r in rows}
    prev = {r["key"]: r for r in rows_of(previous, group)} if previous else {}
    show = group_definition(record, group).get("show") or []
    head = ["Scenario", "fps", "Shown fps"]
    if "ratio" in show:
        head += ["Shown per rendered", "Delivered"]
    head += ["Latency median ms", "Latency p90 ms", "fps vs baseline", "Latency vs baseline",
             "fps vs FG off, no model", "Latency vs FG off, no model", "fps vs previous run"]
    if "rt_builds" in show:
        head += ["Slowest build GPU ms", "Slowest build CPU ms", "Meshes", "Triangles"]
    head += ["Notes"]
    lines = ["| " + " | ".join(head) + " |", "|" + "---|" * len(head)]
    for r in rows:
        m = r["measured"]
        base_row = by_key.get(_baseline_key(record, group, r["scenario"]))
        ref = by_key.get(_reference_key(r["scenario"]))
        b = base_row["measured"] if base_row else {}
        rf = ref["measured"] if ref and ref is not r else {}
        pv = prev.get(r["key"], {}).get("measured", {})
        lat = m.get("latency_median_ms")
        notes = "; ".join(r["warnings"])
        if base_row is r:
            notes = ("baseline; " + notes) if notes else "baseline"
        cells = [r["label"], _fmt(m.get("fps")), _fmt(m.get("shown_fps"))]
        if "ratio" in show:
            step = r.get("fg_step")
            delivered = "–" if not step else ("not delivered" if step["delivered"] is False else ("yes" if step["delivered"] else "n/a"))
            cells += [_fmt(m.get("presented_per_rendered"), 2) if r["scenario"].get("fg_requested") else "–", delivered]
        cells += [_fmt(lat), _fmt(m.get("latency_p90_ms")),
                  _fmt(_pct(m.get("fps"), b.get("fps")), signed=True, unit=" %"),
                  _fmt(None if lat is None or b.get("latency_median_ms") is None else lat - b["latency_median_ms"], signed=True, unit=" ms"),
                  _fmt(_pct(m.get("fps"), rf.get("fps")), signed=True, unit=" %"),
                  _fmt(None if lat is None or rf.get("latency_median_ms") is None else lat - rf["latency_median_ms"], signed=True, unit=" ms"),
                  _fmt(_pct(m.get("fps"), pv.get("fps")), signed=True, unit=" %")]
        if "rt_builds" in show:
            built = r.get("rt_builds") or {}
            cells += [_fmt(built.get("slowest_gpu_ms"), 2), _fmt(built.get("slowest_cpu_ms"), 2),
                      str(built["meshes"]) if built else "–", str(built["triangles"]) if built else "–"]
        cells += [notes.replace("|", "/") or ""]
        lines.append("| " + " | ".join(cells) + " |")
    return "\n".join(lines)


def _machine_line(record):
    m = record.get("machine") or {}
    gpu = next((a for a in m.get("adapters", []) if record.get("vendor") and record["vendor"].upper() in (a.get("name") or "").upper()),
               (m.get("adapters") or [{}])[0])
    driver = gpu.get("nvidia_driver") or gpu.get("driver")
    screen = None
    for s in record["starts"]:
        for p in s.get("phases", []):
            if p.get("upscaler"):
                u = p["upscaler"]
                screen = "{}x{} rendered at {}x{}".format(u["display"][0], u["display"][1], u["render"][0], u["render"][1])
                break
        if screen:
            break
    parts = [gpu.get("name") or "GPU unknown", "driver " + (driver or "unknown"), m.get("cpu") or "CPU unknown",
             "{} GB".format(m.get("memory_gb")) if m.get("memory_gb") else None, m.get("os"),
             "screen " + screen if screen else None,
             "{} Hz".format(gpu.get("refresh_hz")) if gpu.get("refresh_hz") else None]
    return ", ".join(p for p in parts if p)


def run_section(record, json_name):
    git = record.get("git") or {}
    # A table rather than prose: its rows may be as long as the values are.
    lines = ["### Run {}".format(record["stamp"]), "", "| What | Value |", "|---|---|"]
    lines.append("| Date | {} to {} |".format(record["date"], record.get("finished") or "unknown"))
    lines.append("| Commit | `{}`{} on `{}` |".format(git.get("short") or "unknown", " with local changes" if git.get("dirty") else "",
                                                   git.get("branch") or "unknown"))
    build = record.get("build") or {}
    if build:
        lines.append("| Build in the install | " + ", ".join("`{}` {} ({})".format(k, v["sha256"], v["modified"])
                                                           for k, v in build.items()) + " |")
    lines.append("| Machine | " + _machine_line(record) + " |")
    lines.append("| Profile | `{}`: {} |".format(record.get("profile"), "; ".join(record.get("profile_lines") or ["no profile line"])))
    matrix = record.get("matrix") or {}
    lines.append("| Groups | {} |".format(", ".join("`{}`".format(g) for g in record_groups(record))))
    lines.append("| Matrix | `{}`, {} starts, {} s settling per phase, {} s after a change of the rendering |".format(
        (record.get("matrix_file") or "").replace(os.sep, "/"), len(record["starts"]), record.get("settle_seconds"),
        matrix.get("settle_after_rebuild_seconds", record.get("settle_seconds"))))
    for what, why in record.get("skipped") or []:
        lines.append("| Skipped | {} ({}) |".format(what, why))
    lines.append("| Full record | [benchmarks/{0}](benchmarks/{0}) |".format(json_name))
    lines.append("")
    lines.append("<details><summary>What ran in each phase</summary>")
    lines.append("")
    lines.append("| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by "
                 "| Reports | fps range | Notes |")
    lines.append("|" + "---|" * 9)
    for s in record["starts"]:
        if not s.get("phases"):
            lines.append("| {} | – | – | – | – | – | – | – | {} |".format(s["id"], "; ".join(s.get("problems") or [])))
        for p in s.get("phases", []):
            m = p["measured"]
            u = p.get("upscaler")
            ran = "{} {}, {}x{} to {}x{}".format(u["technique"], u["mode"], u["render"][0], u["render"][1],
                                                  u["display"][0], u["display"][1]) if u else "none"
            lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                s["id"], p["label"], ran, p.get("fg_kind_ran") or "none", _fmt(m.get("presented_per_rendered"), 2),
                m.get("latency_lowered_by") or "–", m.get("reports", 0),
                "{}–{}".format(m["fps_min"], m["fps_max"]) if "fps_min" in m else "–",
                "; ".join(p["warnings"]).replace("|", "/")))
    lines.append("")
    lines.append("The proxy's configuration in each start, as the proxy logged it at load:")
    lines.append("")
    lines.append("```")
    for s in record["starts"]:
        lines.append("{}: {}".format(s["id"], s.get("proxy_load_config_line") or "no Config line"))
    lines.append("```")
    packaged = [s for s in record["starts"] if s.get("package_ini")]
    if packaged:
        lines.append("")
        lines.append("The DLSS-G package's ini in each start that had it, with the values it was read with:")
        lines.append("")
        lines.append("```")
        for s in packaged:
            shown = {sec: {k: v for k, v in keys.items() if k in PACKAGE_KEYS_SHOWN}
                     for sec, keys in s["package_ini"].items()}
            lines.append("{}: {}".format(s["id"], " ".join("[{}] {}".format(sec, " ".join("{}={}".format(k, v) for k, v in keys.items()))
                                                           for sec, keys in shown.items() if keys)))
        lines.append("```")
    scened = [s for s in record["starts"] if s.get("probe_args")]
    if scened:
        lines.append("")
        lines.append("The probe's scene arguments: " + "; ".join("{}: `{}`".format(s["id"], " ".join(s["probe_args"])) for s in scened) + ".")
    built = [s for s in record["starts"] if s.get("rt_builds")]
    if built:
        lines.append("")
        lines.append("The raytracing's frames that built 50 meshes or more, at the flight's start:")
        lines.append("")
        for s in built:
            b = s["rt_builds"]
            lines.append("* {}: {} frames, {} meshes, {} triangles;".format(
                s["id"], b["frames"], b["meshes"], b["triangles"]))
            lines.append("  the slowest {} ms of the GPU, {} ms of the CPU".format(
                _fmt(b["slowest_gpu_ms"], 2), _fmt(b["slowest_cpu_ms"], 2)))
    lines.append("")
    lines.append("</details>")
    lines.append("")
    return "\n".join(lines)


def load_records(folder=RESULTS):
    if not os.path.isdir(folder):
        return []
    records = []
    for n in sorted(os.listdir(folder)):
        if n.endswith(".json"):
            with open(os.path.join(folder, n), encoding="utf-8") as f:
                records.append((n, json.load(f)))
    return records


def _wrapped(text, width=88):
    return "\n".join(textwrap.wrap(text, width)) if text else ""


def tables_of(records):
    """The comparison tables, one section per group: the group's latest record, its rows
    against the group's baseline and, for the fps, against the record of the group before."""
    holders = {}
    for name, record in records:
        for group in record_groups(record):
            holders.setdefault(group, []).append(record)
    order = [MAIN_GROUP] + [g for g in (group_ids_of(records[-1][1]) if records else []) if g in holders and g != MAIN_GROUP]
    order += sorted(g for g in holders if g not in order)
    sections = []
    for group in order:
        have = holders.get(group)
        if not have:
            continue
        latest = have[-1]
        previous = have[-2] if len(have) > 1 else None
        definition = group_definition(latest, group)
        scene_names = (latest.get("matrix") or {}).get("scenes", {})
        text = [("### " + definition.get("name", group)) + " (`{}`)".format(group), ""]
        if definition.get("description") or latest.get("note"):
            text += [_wrapped(definition.get("description") or latest["note"]), ""]
        text += [_wrapped("Run `{}`. Its machine and options are under [the runs](#the-runs).".format(latest["stamp"])), ""]
        if group != MAIN_GROUP and any(g["id"] == group for g in (latest.get("matrix") or {}).get("groups", [])):
            text += [_wrapped("Run it with `python tools/benchmark_matrix.py run --group {}`.".format(group)), ""]
        if definition.get("scenes"):
            text += [_wrapped("Scenes: " + ", ".join("{} ({})".format(s, scene_names.get(s, {}).get("name", s))
                                                    for s in definition["scenes"]) + "."), ""]
        text.append(summary_table(latest, previous, group))
        sections.append("\n".join(text))
    return "\n\n".join(sections)


def group_ids_of(record):
    return [g["id"] for g in group_list(record.get("matrix") or {})]


def render_doc(records, doc_text):
    """benchmarks.md with the table of each group's latest run and every run's section, newest
    first, between the markers; the text around the markers stays as written."""
    table = tables_of(records) if records else "No run yet."
    runs = "\n".join(run_section(r, n) for n, r in reversed(records)) if records else "No run yet.\n"
    doc_text = _replace_between(doc_text, TABLE_START, TABLE_END, "\n" + table + "\n")
    return _replace_between(doc_text, RUNS_START, RUNS_END, "\n" + runs)


def _replace_between(text, start, end, body):
    a = text.index(start) + len(start)
    b = text.index(end)
    return text[:a] + body + text[b:]


def write_results(record, no_doc=False):
    os.makedirs(RESULTS, exist_ok=True)
    name = record["stamp"] + ".json"
    with open(os.path.join(RESULTS, name), "w", encoding="utf-8") as f:
        json.dump(record, f, indent=1, ensure_ascii=False)
    print("written:", os.path.join(RESULTS, name))
    if not no_doc:
        render(None)


def render(_args):
    with open(DOC, encoding="utf-8") as f:
        text = f.read()
    text = render_doc(load_records(), text)
    with open(DOC, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("written:", DOC)


def plan_command(args):
    matrix = load_matrix(args.matrix)
    resources = resources_of(args)
    vendor = args.vendor or vendor_of(machine_record())
    try:
        groups = select_groups(matrix, args.group)
        starts, skipped = expand(matrix, vendor, resources, [g["id"] for g in groups])
    except ValueError as e:
        sys.exit(str(e))
    if args.only:
        starts = [s for s in starts if any(o in s["id"] for o in args.only)]
    for s in starts:
        print("{}  ({} phases{}{})".format(s["id"], len(s["phases"]), ", DLSS-G package" if s["dlssg_package"] else "",
                                          ", scene " + s["scene"] if s.get("scene") not in (None, DEFAULT_SCENE) else ""))
        print("    settings.cfg: " + ", ".join("{} = {}".format(k, v) for k, v in s["mod_settings"].items()))
        print("    proxy at load: " + ", ".join("{}={}".format(k, v) for k, v in s["proxy_load"].items()))
        if s.get("dlssg_package"):
            print("    package ini: MaxGeneratedFrames={} {}".format(s["max_generated_frames"], json.dumps(s.get("dlssg_ini") or {})))
        for p in s["phases"]:
            print("    " + scenario_label(p["scenario"]) + ("   (rebuilt, {} s settling)".format(p["settle"]) if p.get("rebuild") else "")
                  + "   [" + ",".join("{}={}".format(k, v) for k, v in p["keys"].items()) + "]")
    for what, why in skipped:
        print("skipped: {} ({})".format(what, why))
    print("")
    print("{:<16} {:>7} {:>7} {:>9}".format("group", "starts", "phases", "minutes"))
    for g in groups:
        mine = [s for s in starts if s.get("group") == g["id"]]
        print("{:<16} {:>7} {:>7} {:>9.0f}   {}".format(g["id"], len(mine), sum(len(s["phases"]) for s in mine),
                                                       estimate_seconds(mine, False) / 60.0, g.get("name", "")))
    print("{} starts, {} phases, about {:.0f} minutes with the profile start (vendor {}); the other groups: {}".format(
        len(starts), sum(len(s["phases"]) for s in starts), estimate_seconds(starts) / 60.0, vendor or "unknown",
        ", ".join(g["id"] for g in group_list(matrix) if g["id"] not in [x["id"] for x in groups]) or "none"))


def parse_command(args):
    record = build_record(os.path.abspath(args.raw_folder))
    write_results(record, args.no_doc)


def main(argv=None):
    ap = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0],
        epilog="Do not use the machine while a run is going: the game's window is kept in front (DLSS-G generates no\n"
               "frame otherwise), any program or key press in the meantime moves the frame rate and the latency the\n"
               "run measures, and the GPU is shared with whatever else uses it. REDEFINITION_BENCH_NO_FOREGROUND=1 leaves\n"
               "the window where Windows puts it.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)

    def common(p):
        p.add_argument("--matrix", default=DEFAULT_MATRIX, help="the scenario matrix (default tools/benchmark_matrix.json)")
        p.add_argument("--ksp", default=os.environ.get("REDEFINITION_KSP", DEFAULT_KSP), help="the KSP folder")
        p.add_argument("--dlssg-package", default=os.environ.get("REDEFINITION_DLSSG_PACKAGE", os.path.join(REPO, ".local", "dlssg_x")),
                       help="the unpacked DLSS-G package for Ampere (version.dll, dlssg_sm86.ini, dlssg_sm86/)")
        p.add_argument("--amd-dll-dir", default=os.environ.get("REDEFINITION_AMD_DLL_DIR", os.path.join(REPO, ".local", "amd")),
                       help="the folder with amd_fidelityfx_upscaler_dx12.dll, handed to the proxy as amdUpscalerDirectory")
        p.add_argument("--xess-dll-dir", default=os.environ.get("REDEFINITION_XESS_DLL_DIR", os.path.join(REPO, ".local", "xess")),
                       help="the folder with Intel's libxess.dll, handed to the proxy as xessDirectory")
        p.add_argument("--xess-fg-dll-dir", default=os.environ.get("REDEFINITION_XESS_FG_DLL_DIR", os.path.join(REPO, ".local", "xessfg")),
                       help="the folder with Intel's libxess_fg.dll and libxell.dll, handed to the proxy as xessFgDirectory")
        p.add_argument("--vendor", choices=["NVIDIA", "AMD", "Intel"], help="the GPU's vendor, if not the one Windows names")
        p.add_argument("--only", action="append", help="only the starts whose id contains this (repeatable)")
        p.add_argument("--group", action="append",
                       help="a group of the matrix to cover, repeatable, or all (default: the main matrix; the groups are"
                            " listed by plan, each with its starts, phases and minutes)")

    p = sub.add_parser("run", help="run the default group, or the --group ones, in KSP and write the results")
    common(p)
    p.add_argument("--settings-template", help="the mod's settings.cfg to start from (default: the install's, as it was)")
    p.add_argument("--raw", default=os.environ.get("REDEFINITION_BENCH_RAW", os.path.join(REPO, ".local", "benchmarks")),
                   help="where the logs and screenshots of each start go")
    p.add_argument("--no-profile", action="store_true", help="skip the start that applies the profile")
    p.add_argument("--no-doc", action="store_true", help="write the JSON only, not benchmarks.md")
    p.set_defaults(func=run)
    p = sub.add_parser("plan", help="print the starts, the phases and per group the minutes, without running anything")
    common(p)
    p.set_defaults(func=plan_command)
    p = sub.add_parser("parse", help="read a run's raw folder again and write its results")
    p.add_argument("raw_folder")
    p.add_argument("--no-doc", action="store_true")
    p.set_defaults(func=parse_command)
    p = sub.add_parser("render", help="write benchmarks.md from docs/development/benchmarks/*.json")
    p.set_defaults(func=render)
    args = ap.parse_args(argv)
    args.func(args)


if __name__ == "__main__":
    main()
