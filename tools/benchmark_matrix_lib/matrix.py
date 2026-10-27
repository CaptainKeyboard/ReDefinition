"""The scenario matrix and its groups: reading tools/benchmark_matrix.json and expanding it into KSP starts, each
with its phases (the key sets the probe sets in turn)."""
import copy
import json
import re

from .constants import DEFAULT_SCENE, MAIN_GROUP


def load_matrix(path):
    """The scenario matrix, read from its JSON file."""
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _vendor_ok(entry, vendor):
    """Whether a matrix entry runs on this vendor's GPU: one without "vendors", or a vendor not known, always does."""
    allowed = entry.get("vendors")
    return not allowed or vendor is None or vendor in allowed


# What a group names that is not a part of the matrix it overrides.
GROUP_META = ("id", "name", "description", "default", "scenes", "variants", "show")
# A group's values of these merge into the matrix's; every other key it names replaces the matrix's.
GROUP_MERGED = ("proxy_load", "mod_settings")
# What only a group has: its renderings of live proxy keys and the values every other phase has.
GROUP_EXTRA = ("tweaks", "tweak_defaults")
# The lists a group may name entries of by id, or extend ({"extends": id, ...}).
GROUP_LISTS = ("techniques", "frame_generation", "latency")


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
    matrix's own lists. A group's "scenes" (matrix["scenes"]) and "variants" (proxy values at
    load, "proxy_load") multiply its starts. The id of a start outside the main group begins with the group's and ends with its variant's and scene's. A scene may name
    "requires" (resources; a scene with one missing is skipped) and "ready" (see
    scene_unavailable)."""
    starts, skipped = [], []
    for group in select_groups(matrix, groups):
        sub = resolve_group(matrix, group)
        scenes = group.get("scenes") or [DEFAULT_SCENE]
        variants = group.get("variants") or [None]
        for scene in scenes:
            if scene not in matrix.get("scenes", {DEFAULT_SCENE: {}}):
                raise ValueError("group {} names the scene {}, which the matrix does not define".format(group["id"], scene))
            scene_definition = matrix.get("scenes", {}).get(scene, {})
            missing = [r for r in scene_definition.get("requires", []) if not (resources or {}).get(r)]
            if missing:
                skipped.append(("scene " + scene, "needs " + ", ".join(missing)))
                continue
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
                    start["probe_args"] = list(scene_definition.get("args", []))
                    start["scene_ready"] = scene_definition.get("ready")
                    if group["id"] != MAIN_GROUP:
                        parts = [group["id"], start["id"]]
                        if variant:
                            parts.append(variant["id"])
                        if len(scenes) > 1 or scene != DEFAULT_SCENE:
                            parts.append(scene)
                        start["id"] = "-".join(parts)
                starts.extend(found)
                skipped.extend(why)
    return starts, _without_repeats(skipped)


def _expand_cell(matrix, vendor=None, resources=None):
    """The starts one group, scene and variant ask for, each with its phases (the key sets) in order.

    Only what is read at load splits the starts: the frame generation kind
    (frameGenerationTechnique, and the DLSS-G package next to KSP_x64.exe), and whatever
    "separate_starts" names. The radiance cache ("radiance_cache") is the proxy's live key, a rendering of its own for
    the one technique and raytracing mode it names, with the latency models it names. So
    are the opacity micromaps ("opacity_micromaps"), the last rendering of the start that
    has that technique and mode.
    Returns (starts, skipped), skipped as (what, why)."""
    resources = resources or {}
    skipped = []
    latencies = [l for l in matrix["latency"] if _vendor_ok(l, vendor)]
    for l in matrix["latency"]:
        if not _vendor_ok(l, vendor):
            skipped.append(("latency " + l["id"], "only on " + "/".join(l["vendors"])))
    renderings, tail = _renderings_of(matrix, vendor, resources, skipped)
    kinds = _frame_generation_kinds(matrix, vendor, resources, skipped)
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
    return starts, _without_repeats(skipped)


def _renderings_of(matrix, vendor, resources, skipped):
    """The renderings of a cell, as (renderings, tail). Each technique that runs on the vendor with the resources
    it needs comes at each raytracing mode and Ray Reconstruction choice.
    The radiance cache's and the tweaks' renderings follow the mode they name. The tail holds the renderings that
    end a start. What is left out is added to skipped, as (what, why)."""
    renderings = []
    # The renderings that end a start: a tweak that is "last", and the opacity micromaps. The
    # first live 1 makes the passes' pipelines with micromap support, which they keep, so no row
    # after it is without.
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
    return renderings, tail


def _frame_generation_kinds(matrix, vendor, resources, skipped):
    """The frame generation kinds that run on the vendor with the resources they need; what is left out is
    added to skipped, as (what, why)."""
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
    return kinds


def _without_repeats(items):
    """The items in order, each once."""
    seen, unique = set(), []
    for item in items:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return unique


def _allowed(fg, rendering):
    """Whether the frame generation kind is measured with the rendering ("only").

    The kind's "only" names the renderings it is measured with: "techniques", "raytracing", and
    "extras": false for none of the radiance cache's, the micromaps' and the tweaks'."""
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
    """The value as a list: a list stays, anything else becomes a list of one."""
    return value if isinstance(value, list) else [value]


def _split_value(rendering, dimension):
    """The rendering's value of one "separate_starts" dimension, as a part of a start's id; renderings
    with different values go into different starts."""
    if dimension == "technique":
        return rendering["tech"]["id"]
    if dimension == "raytracing":
        return rendering["raytracing"].lower()
    if dimension == "ray_reconstruction":
        rr = rendering["ray_reconstruction"]
        return "rrna" if rr is None else ("rr" if rr else "norr")
    raise ValueError("separate_starts takes technique, raytracing and ray_reconstruction, not " + dimension)


def _fill(value, resources):
    """The value as text with each {name} replaced by that resource's path; an unknown name stays as written."""
    return re.sub(r"\{(\w+)\}", lambda m: resources.get(m.group(1), m.group(0)), str(value))


def _mod_values(rendering):
    """The mod's settings that make this rendering, by their settings.cfg keys. The upscaler,
    the raytracing mode and Ray Reconstruction are the mod's own settings, which the probe sets
    live through its mod.<key> keys. Every phase names all of them, so that none inherits a
    value from the phase before."""
    values = dict(rendering["tech"].get("settings", {}))
    values["raytracing"] = rendering["raytracing"]
    rr = rendering["ray_reconstruction"]
    # Where Ray Reconstruction does not apply, it stays at the mod's default.
    values["rayReconstruction"] = "True" if rr is None or rr else "False"
    return values


def _start(matrix, fg, renderings, group, latencies, resources, plain_off):
    """One KSP start of a frame generation kind. It holds the settings.cfg and proxy values the start
    loads with, and its phases. The phases are every rendering with frame generation off (where the
    kind has those) and on, at each step, with each latency model, repeated as phase_repeats says.
    The argument plain_off names the "FG off" rows plainly, else they say which kind's files were
    loaded. A kind may name a "kind" other than
    its id (a variant of the same technology, its rows checked against that kind) and "dlssg_ini"
    (sections, keys and values written into the DLSS-G package's ini for its start)."""
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
    phases = []
    for n, r in enumerate(renderings):
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
                keys = _phase_keys(matrix, r, phase_mod, fg_keys, lat)
                s = _phase_scenario(matrix, fg, r, state, step, lat, plain_off)
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


def _phase_keys(matrix, rendering, phase_mod, fg_keys, lat):
    """The live keys one phase sets. They are the matrix's phase keys, the mod's settings as mod.<key>, the frame
    generation state's keys and the latency model's.
    The cache's, the micromaps' and the tweaks' keys are named in every phase, so that none inherits one."""
    cache_key = (matrix.get("radiance_cache") or {}).get("key")
    micromaps_key = (matrix.get("opacity_micromaps") or {}).get("key")
    keys = dict(matrix.get("phase_keys", {}))
    keys.update({"mod." + k: v for k, v in phase_mod.items()})
    keys.update(fg_keys)
    keys.update(lat["proxy"])
    # Every phase names the cache's and the micromaps' keys, so that none inherits them, also
    # in a group that has neither rendering.
    keys.update(matrix.get("_pinned_keys", {}))
    if cache_key:
        keys[cache_key] = "1" if rendering.get("radiance_cache") else "0"
    if micromaps_key:
        keys[micromaps_key] = "1" if rendering.get("opacity_micromaps") else "0"
    # Every phase names the tweaks' keys at their defaults, so that none inherits one.
    keys.update(matrix.get("tweak_defaults", {}))
    if rendering.get("tweak"):
        keys.update(rendering["tweak"]["keys"])
    return keys


def _phase_scenario(matrix, fg, rendering, state, step, lat, plain_off):
    """The scenario of one phase. It holds the rendering, the frame generation kind in its state ("on", or "off"
    named plainly or after the kind whose files were loaded), the step and the latency model.
    It holds the group, scene and variant of the cell it was expanded in too."""
    tech = rendering["tech"]
    if state == "on":
        fg_id, fg_name = fg["id"], fg["name"]
    elif plain_off:
        fg_id, fg_name = "off", "off"
    else:
        fg_id, fg_name = "off-" + fg["id"] + "-loaded", "off ({} loaded)".format(fg["name"])
    s = {
        "technique": tech["id"], "technique_name": tech["name"], "technique_log_name": tech.get("log_name", tech["name"]),
        "raytracing": rendering["raytracing"], "ray_reconstruction": rendering["ray_reconstruction"],
        "radiance_cache": rendering.get("radiance_cache"), "opacity_micromaps": rendering.get("opacity_micromaps"),
        "fg": fg_id, "fg_name": fg_name, "fg_requested": state == "on", "fg_kind": fg.get("kind", fg["id"]),
        "latency": lat["id"], "latency_name": lat["name"],
    }
    if step:
        s["step"] = step
    if rendering.get("tweak"):
        s["tweak"], s["tweak_name"] = rendering["tweak"]["id"], rendering["tweak"].get("name", rendering["tweak"]["id"])
    if matrix.get("_group") and matrix["_group"] != MAIN_GROUP:
        s["group"] = matrix["_group"]
    if matrix.get("_scene") and matrix["_scene"] != DEFAULT_SCENE:
        s["scene"] = matrix["_scene"]
    if matrix.get("_variant"):
        s["variant"], s["variant_name"] = matrix["_variant"]["id"], matrix["_variant"].get("name", matrix["_variant"]["id"])
    return s
