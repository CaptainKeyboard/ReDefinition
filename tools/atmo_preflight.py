#!/usr/bin/env python3
"""The offline preflight of a game series start (python tools/atmo_series.py preflight <series> [--start X]; `run` calls it first and
refuses on any FAIL). It starts no game and changes nothing in the install: it reads the runner version, the series, the planned KSP
command line, the pinned settings, the processes, VMs, memory, locks, the deploy plan, the mod-set plan, KSPCF's PNG texture cache and
the probe's files. Its checks take their inputs as arguments so that the selftest (atmo_series.py selftest) can feed them fixtures."""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gpu_lock  # noqa: E402
from atmo_series import (BUILD_ARTIFACTS, BUNDLE, DEFAULT_DOTNET, DEFAULT_REPO, DISABLED_DIR, MOD_FOLDERS, PROFILE_CACHE_REL,  # noqa: E402
                         Refusal, Snapshot, apply_settings, free_memory_gb, is_texture_cache_file, ksp_running_tasklist, load_series,
                         plan_args, settings_text, sha256, tree_files)

PASS, WARN, FAIL, INFO = "PASS", "WARN", "FAIL", "INFO"
TEXTURE_CACHE_REL = "GameData/KSPCommunityFixes/PluginData/TextureCache"
VM_NAMES = ("gameserver", "homeassistant")
ORPHAN_PROCESS_NAMES = ("testhost", "dotnet", "msbuild", "vbcscompiler")
DEFAULT_CRAFT = "Kerbal X.craft"


def git_head(path):
    """The commit a checkout stands on, or None where git cannot say."""
    try:
        out = subprocess.run(["git", "-C", path, "rev-parse", "HEAD"], capture_output=True, text=True, timeout=30).stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        return None
    return out or None


def list_processes():
    """(pid, name, command line) of the processes that could hold a DLL of the install; [] where the listing fails."""
    command = ("Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(testhost|dotnet|MSBuild|VBCSCompiler)' } | "
               "ForEach-Object { '{0}|{1}|{2}' -f $_.ProcessId, $_.Name, $_.CommandLine }")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True, timeout=60).stdout
    except (OSError, subprocess.TimeoutExpired):
        return []
    rows = []
    for line in out.splitlines():
        parts = line.strip().split("|", 2)
        if len(parts) == 3:
            rows.append((parts[0], parts[1], parts[2]))
    return rows


def vm_state(name):
    """The state of a Hyper-V VM ('Running', 'Off', ...), or None where it cannot be read."""
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-VM -Name %s -ErrorAction Stop).State" % name],
                           capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return r.stdout.strip() or None if r.returncode == 0 else None


def pf_runner_version(script_repo, worktree, head=git_head):
    """The runner that executes must be the one of the worktree it works on (the old runner of the main checkout with --worktree
    ignored the pins and added -screen-width 1600)."""
    a, b = head(script_repo), head(worktree)
    same_dir = os.path.normcase(os.path.abspath(script_repo)) == os.path.normcase(os.path.abspath(worktree))
    if same_dir:
        return [(PASS, "runner version", "the runner is the worktree's own (%s)" % ((a or "?")[:10]))]
    if a is None or b is None:
        return [(FAIL, "runner version", "cannot read git HEAD (runner repo %s, worktree %s)" % (a, b))]
    if a != b:
        return [(FAIL, "runner version", "the runner's repo %s is at %s, the worktree %s at %s; run tools/atmo_series.py of the "
                                         "worktree, not this one" % (script_repo, a[:10], worktree, b[:10]))]
    return [(PASS, "runner version", "runner repo and worktree are both at %s" % a[:10])]


def pf_series(repo, name):
    try:
        defn, errors = load_series(repo, name)
    except Refusal as e:
        return None, [(FAIL, "series valid", str(e))]
    if errors:
        return defn, [(FAIL, "series valid", "%d error(s): %s" % (len(errors), "; ".join(errors[:5])))]
    return defn, [(PASS, "series valid", "%s: %d starts, %d rows" % (name, len(defn["starts"]), len(defn.get("rows", []))))]


def pf_args(args, st):
    """The planned command line of a start, checked: one window size, equal to the start's probeArgs, no leftover default."""
    out = [(INFO, "KSP command line [%s]" % st["id"], "KSP_x64.exe " + " ".join(args))]
    problems = []
    for flag in ("-screen-width", "-screen-height"):
        n = args.count(flag)
        if n != 1:
            problems.append("%s appears %d times (exactly 1 wanted)" % (flag, n))
    probe = st.get("probeArgs", [])
    for flag in ("-screen-width", "-screen-height"):
        if flag in probe and flag in args and args.count(flag) == 1:
            want, got = probe[probe.index(flag) + 1:probe.index(flag) + 2], args[args.index(flag) + 1:args.index(flag) + 2]
            if want != got:
                problems.append("%s is %s, the start's probeArgs say %s" % (flag, got, want))
    if "-screen-width" in probe and "-atmoSeries" in args and "-screen-width" in args and \
            args.index("-screen-width") < args.index("-atmoSeries"):
        problems.append("the default window size (added before -atmoSeries) is still on the command line")
    for flag in ("-atmoSeries", "-atmoStart"):
        if args.count(flag) != 1:
            problems.append("%s appears %d times (exactly 1 wanted)" % (flag, args.count(flag)))
    if problems:
        out.append((FAIL, "command line [%s]" % st["id"], "; ".join(problems)))
    else:
        w = args[args.index("-screen-width") + 1] if "-screen-width" in args else "?"
        h = args[args.index("-screen-height") + 1] if "-screen-height" in args else "?"
        out.append((PASS, "command line [%s]" % st["id"], "one window size %sx%s, matches the start" % (w, h)))
    return out


def pf_pins(ksp, profile_cache, settings):
    """Each pinned key stands exactly once in the file it is written to (after the profile cache's copy of the file is applied)."""
    out = []
    for entry in settings or []:
        rel = entry["file"]
        cached = os.path.join(profile_cache, *rel.split("/")) if profile_cache else None
        live = os.path.join(ksp, *rel.split("/"))
        path, origin = (cached, "profile cache") if cached and os.path.isfile(cached) else (live, "install")
        if not os.path.isfile(path):
            out.append((FAIL, "pin file", "%s does not exist (%s)" % (rel, path)))
            continue
        with open(path, "r", encoding="utf-8", errors="replace", newline="") as f:
            lines = f.read().split("\n")
        for key, value in entry["set"].items():
            hits = [re.match(r"^\s*%s\s*=\s*(.*?)\s*$" % re.escape(key), line) for line in lines]
            hits = [m for m in hits if m]
            if len(hits) != 1:
                out.append((FAIL, "pin %s" % key, "%s (%s) has the key %d times, exactly once is required" % (rel, origin, len(hits))))
            else:
                out.append((PASS, "pin %s" % key, "%s: %s -> %s (%s)" % (rel, hits[0].group(1), settings_text(value), origin)))
    return out


def pf_snapshot_selftest(settings, snapshot_cls=Snapshot):
    """Snapshot, pin and restore on a fake install built from the series' own pins: the files come back byte for byte."""
    root = tempfile.mkdtemp(prefix="atmo-preflight-")
    try:
        ksp, store = os.path.join(root, "ksp"), os.path.join(root, "store")
        entries = settings or [{"file": "settings.cfg", "set": {"A_KEY": 1}}]
        originals = {}
        for entry in entries:
            path = os.path.join(ksp, *entry["file"].split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            text = "head = 1\r\n" + "".join("\t%s = 0\r\n" % k for k in entry["set"]) + "tail = 2\r\n"
            with open(path, "w", newline="") as f:
                f.write(text)
            originals[path] = text
        snap = snapshot_cls(ksp, store)
        for entry in entries:
            snap.add_file(entry["file"])
        snap.take()
        written = apply_settings(ksp, entries)
        changed = all(open(p, newline="").read() != t for p, t in originals.items())
        stray = os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "checkpoint.bin")
        os.makedirs(os.path.dirname(stray), exist_ok=True)
        with open(stray, "w") as f:
            f.write("x")
        snap.add_dir("GameData/ReDefinition/Atmospheres/PluginData")
        bad = snap.restore()
        same = all(open(p, newline="").read() == t for p, t in originals.items())
        if bad or not same or not changed or os.path.exists(stray):
            return [(FAIL, "snapshot/restore selftest", "pinned=%s restored=%s mismatches=%s stray left=%s" % (changed, same, bad, os.path.exists(stray)))]
        return [(PASS, "snapshot/restore selftest", "%d pin(s) written and put back byte for byte" % len(written))]
    except Exception as e:  # noqa: BLE001 - the report names it
        return [(FAIL, "snapshot/restore selftest", "%s: %s" % (type(e).__name__, e))]
    finally:
        shutil.rmtree(root, ignore_errors=True)


def pf_ksp_running(running):
    return [(FAIL, "KSP not running", "KSP_x64.exe runs; end it first") if running else (PASS, "KSP not running", "no KSP_x64.exe")]


def pf_orphans(procs, ksp):
    """Processes that could hold DLL locks inside the install: a testhost/dotnet/msbuild whose command line names the KSP folder."""
    key = os.path.normcase(os.path.abspath(ksp)).replace("\\", "/")
    held = [(pid, name, cmd) for pid, name, cmd in procs
            if name.lower().replace(".exe", "") in ORPHAN_PROCESS_NAMES and key in os.path.normcase(cmd).replace("\\", "/")]
    if held:
        return [(FAIL, "no orphan DLL holders", "; ".join("%s %s (pid %s)" % (n, c[:80], p) for p, n, c in held)
                 + " - stop them (Stop-Process -Id <pid>) before the build deploys")]
    return [(PASS, "no orphan DLL holders", "%d testhost/dotnet/msbuild process(es) listed, none names %s" % (len(procs), ksp))]


def pf_vms(states):
    out = []
    for name in VM_NAMES:
        state = states.get(name)
        if state is None:
            out.append((WARN, "VM %s" % name, "state unknown"))
        elif "Running" in state:
            out.append((WARN if name != "gameserver" else INFO, "VM %s" % name,
                        "Running (%s)" % ("the run stops it and starts it again" if name == "gameserver" else "it keeps its RAM during the run")))
        else:
            out.append((PASS, "VM %s" % name, state))
    return out


def pf_ram(free_gb, need, states):
    if free_gb >= need:
        return [(PASS, "free RAM", "%.1f GB free, %g needed" % (free_gb, need))]
    if "Running" in (states.get("gameserver") or ""):
        return [(WARN, "free RAM", "%.1f GB free, %g needed; the gameserver VM runs and is stopped first, the run checks again" % (free_gb, need))]
    return [(FAIL, "free RAM", "%.1f GB free, %g needed; close Rider, OneDrive, Toolbox, Edge" % (free_gb, need))]


def pf_locks(who, readers, now=None):
    """GPU lock and unity lock: another live holder is a warning (the run waits for it)."""
    out = []
    for kind, info in readers:
        if info is None:
            out.append((PASS, "%s lock" % kind, "free"))
        elif gpu_lock.is_stale(info, now):
            out.append((PASS, "%s lock" % kind, "stale entry (%s), the run takes it over" % gpu_lock.describe(info, now)))
        elif info.get("id") == who:
            out.append((PASS, "%s lock" % kind, "held by this runner"))
        else:
            out.append((WARN, "%s lock" % kind, "another agent holds it: %s; the run waits" % gpu_lock.describe(info, now)))
    return out


def pf_hash_plan(repo, ksp, defn, dotnet_exists=True, unity_script=True):
    """What the run will deploy: per file the worktree and the deployed hash (the build overwrites the deployed one)."""
    out = []
    for wt, game in BUILD_ARTIFACTS + [BUNDLE]:
        wp, gp = os.path.join(repo, *wt.split("/")), os.path.join(ksp, *game.split("/"))
        wh, gh = (sha256(wp) if os.path.isfile(wp) else None), (sha256(gp) if os.path.isfile(gp) else None)
        state = "equal" if wh and wh == gh else ("will be built and deployed" if wh is None and wt != BUNDLE[0] else "will be replaced by the worktree's")
        if wt == BUNDLE[0] and wh is None:
            state = "built by the run (bundle: true)" if defn.get("bundle") else ("the game's own bundle stays" if gh else "MISSING")
        out.append((INFO, "deploy %s" % game.rsplit("/", 1)[-1], "worktree %s, deployed %s: %s" % ((wh or "-")[:12], (gh or "-")[:12], state)))
    bundle_wt, bundle_game = os.path.join(repo, *BUNDLE[0].split("/")), os.path.join(ksp, *BUNDLE[1].split("/"))
    if defn.get("bundle") and not unity_script:
        out.append((FAIL, "bundle build", "the series needs the bundle but tools/build_atmospheres_bundle.ps1 is missing"))
    elif not defn.get("bundle") and not os.path.isfile(bundle_wt) and not os.path.isfile(bundle_game):
        out.append((FAIL, "bundle", "no shader bundle in the worktree or the game; set bundle: true or build it"))
    else:
        out.append((PASS, "bundle", "bundle: %s" % ("built by the run" if defn.get("bundle") else "present")))
    out.append((PASS if dotnet_exists else FAIL, "dotnet", "present" if dotnet_exists else "the dotnet.exe of the runner does not exist"))
    return out


def pf_modsets(ksp, starts, folders=None):
    """The mod folders the starts disable: names known, nothing left over from an earlier run, every move has a free target."""
    folders = folders or MOD_FOLDERS
    store = os.path.join(ksp, DISABLED_DIR)
    out = []
    if os.path.exists(store):
        out.append((FAIL, "no leftover %s" % DISABLED_DIR, "%s exists; run: python tools/atmo_series.py modset-restore" % store))
    else:
        out.append((PASS, "no leftover %s" % DISABLED_DIR, "none"))
    names = sorted({n for st in starts for n in st.get("disableMods", [])})
    if not names:
        return out + [(PASS, "disabled-mods plan", "no start disables a mod")]
    unknown = [n for n in names if n not in folders]
    if unknown:
        return out + [(FAIL, "disabled-mods plan", "unknown mod set name(s): " + ", ".join(unknown))]
    moves, problems = [], []
    gd = os.path.join(ksp, "GameData")
    for n in names:
        for folder in folders[n]:
            src, dst = os.path.join(gd, folder), os.path.join(store, folder)
            if os.path.exists(src):
                moves.append(folder)
                if os.path.exists(dst):
                    problems.append("%s already stands in the store" % folder)
    if os.path.splitdrive(os.path.abspath(gd))[0].lower() != os.path.splitdrive(os.path.abspath(store))[0].lower():
        problems.append("GameData and the store are on different volumes (the rename would copy)")
    if not os.access(gd, os.W_OK):
        problems.append("GameData is not writable")
    if problems:
        out.append((FAIL, "disabled-mods plan", "; ".join(problems)))
    elif not moves:
        out.append((WARN, "disabled-mods plan", "%s: none of the folders is in GameData, nothing would be moved" % ", ".join(names)))
    else:
        out.append((PASS, "disabled-mods plan", "move %s to %s and back (restorable, journal kept)" % (", ".join(moves), DISABLED_DIR)))
    return out


def pf_texture_cache(ksp):
    """KSPCF's PNG texture cache: textureData.json and the numeric data files must belong together. An index listing ids without
    files made the loader of start 37 fail (856 'LOAD FAILED', NRE in PartLoader.ReplaceTextures)."""
    folder = os.path.join(ksp, *TEXTURE_CACHE_REL.split("/"))
    if not os.path.isdir(folder):
        return [(PASS, "KSPCF texture cache", "no TextureCache folder; KSPCF builds it on the first load")]
    remedy = ("with KSP not running, move the folder aside: Move-Item '%s' '%s.bad-%s'" %
              (folder, folder, time.strftime("%Y%m%d-%H%M%S")))
    index = os.path.join(folder, "textureData.json")
    files = {n for n in os.listdir(folder) if n != "textureData.json" and os.path.isfile(os.path.join(folder, n))}
    ids = set()
    if os.path.isfile(index):
        with open(index, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line.startswith("{"):
                    continue
                try:
                    ids.add(str(json.loads(line)["id"]))
                except (ValueError, KeyError):
                    return [(FAIL, "KSPCF texture cache", "textureData.json has an unreadable line; " + remedy)]
    elif files:
        return [(FAIL, "KSPCF texture cache", "%d data files but no textureData.json; %s" % (len(files), remedy))]
    missing = sorted(ids - files)
    extra = sorted(files - ids)
    if missing:
        return [(FAIL, "KSPCF texture cache", "textureData.json lists %d ids, %d of them without a file in TextureCache (%d files not in the "
                                              "index); the loader would fail; %s" % (len(ids), len(missing), len(extra), remedy))]
    if extra:
        return [(WARN, "KSPCF texture cache", "every one of %d indexed ids has its file, but %d files are not in the index (%s)" %
                 (len(ids), len(extra), "KSPCF drops them; or " + remedy))]
    return [(PASS, "KSPCF texture cache", "%d indexed ids, %d files, consistent" % (len(ids), len(files)))]


def pf_probe_files(ksp, probe_args):
    """The save and the craft the probe flies: warnings where they are missing."""
    save = probe_args[probe_args.index("-redefinitionProbeSave") + 1] if "-redefinitionProbeSave" in probe_args and \
        probe_args.index("-redefinitionProbeSave") + 1 < len(probe_args) else "ReDefinitionProbe"
    base = os.path.join(ksp, "saves", save)
    out = []
    sfs = os.path.join(base, "persistent.sfs")
    out.append((PASS, "probe save", sfs) if os.path.isfile(sfs) else (WARN, "probe save", "missing: " + sfs))
    crafts = [os.path.join(base, "Ships", kind, DEFAULT_CRAFT) for kind in ("VAB", "SPH")]
    found = [c for c in crafts if os.path.isfile(c)]
    out.append((PASS, "probe craft", found[0]) if found else (WARN, "probe craft", "missing: " + " / ".join(crafts)))
    return out


def pf_scene_guard(repo, probe_args):
    """The probe named by -atmoProbe must not touch the flight state outside the flight scene (start 35: NRE in the loading scene)."""
    if "-atmoProbe" not in probe_args or probe_args.index("-atmoProbe") + 1 >= len(probe_args):
        return [(INFO, "probe scene guard", "the start names no -atmoProbe")]
    name = probe_args[probe_args.index("-atmoProbe") + 1]
    hits = []
    for root, _d, names in os.walk(os.path.join(repo, "src")):
        if name + ".cs" in names:
            hits.append(os.path.join(root, name + ".cs"))
    if not hits:
        return [(WARN, "probe scene guard", "no %s.cs under src/ to read" % name)]
    with open(hits[0], "r", encoding="utf-8", errors="replace") as f:
        text = f.read()
    uses = "FlightGlobals.ActiveVessel" in text or "FlightCamera.fetch" in text
    if uses and "GameScenes.FLIGHT" not in text:
        return [(FAIL, "probe scene guard", "%s uses FlightGlobals/FlightCamera without any GameScenes.FLIGHT check" % hits[0])]
    return [(PASS, "probe scene guard", "%s checks GameScenes.FLIGHT" % os.path.basename(hits[0]) if uses else "no flight state used")]


def pf_profile_cache(profile_cache):
    """The profile cache is copied over the install before every start. It must hold no KSPCF texture cache file: an index without
    its data files made the loader of K-T2 starts 37 and 38 fail (apply_profile skips such files now, but they do not belong there)."""
    if not profile_cache or not os.path.isdir(profile_cache):
        return [(PASS, "profile cache texture files", "no profile cache folder")]
    bad = [rel for rel in tree_files(profile_cache) if is_texture_cache_file(rel)]
    if bad:
        folder = os.path.join(profile_cache, *TEXTURE_CACHE_REL.split("/"))
        return [(FAIL, "profile cache texture files", "%d KSPCF texture cache file(s) in the profile cache (%s); remedy: Remove-Item -Recurse "
                                                      "-Force '%s' (or move it out of the profile cache)" %
                 (len(bad), ", ".join(bad[:3]) + (", ..." if len(bad) > 3 else ""), folder))]
    return [(PASS, "profile cache texture files", "none (KSPCF builds its own cache)")]


def run_preflight(repo, name, only_start, ksp, mainrepo, who, hooks=None, script_repo=None, dotnet=DEFAULT_DOTNET):
    """All checks of a series start, without starting or changing anything. Returns the list of (status, check, detail)."""
    h = hooks or {}
    results = []
    results += pf_runner_version(script_repo or DEFAULT_REPO, repo, h.get("git_head", git_head))
    defn, series_results = pf_series(repo, name)
    results += series_results
    if defn is None or series_results[0][0] == FAIL:
        return results
    starts = [s for s in defn["starts"] if only_start in (None, s["id"])]
    if not starts:
        return results + [(FAIL, "start", "the series has no start %s" % only_start)]
    profile_cache = h.get("profile_cache", os.path.join(mainrepo, *PROFILE_CACHE_REL))
    for st in starts:
        results += pf_args(plan_args(defn["name"], st), st)
    results += pf_pins(ksp, profile_cache, defn.get("settings"))
    results += pf_snapshot_selftest(defn.get("settings"))
    results += pf_ksp_running(h.get("ksp_running", ksp_running_tasklist)())
    results += pf_orphans(h.get("processes", list_processes)(), ksp)
    states = {n: h.get("vm_state", vm_state)(n) for n in VM_NAMES}
    results += pf_vms(states)
    results += pf_ram(h.get("free_gb", free_memory_gb)(), defn.get("freeMemoryGb", 12), states)
    readers = [("gpu", h.get("read_gpu", lambda: gpu_lock.read_lock(gpu_lock.lock_path("gpu")))()),
               ("unity", h.get("read_unity", lambda: gpu_lock.read_lock(gpu_lock.lock_path("unity")))())]
    results += pf_locks(who, readers)
    results += pf_hash_plan(repo, ksp, defn, os.path.exists(dotnet) or bool(shutil.which(dotnet)) or h.get("dotnet_exists", False),
                            os.path.isfile(os.path.join(repo, "tools", "build_atmospheres_bundle.ps1")))
    results += pf_modsets(ksp, starts, h.get("mod_folders"))
    results += pf_texture_cache(ksp)
    results += pf_profile_cache(profile_cache)
    for st in starts:
        results += pf_probe_files(ksp, st.get("probeArgs", []))
        results += pf_scene_guard(repo, st.get("probeArgs", []))
    return results


def format_preflight(name, results):
    lines = ["PREFLIGHT %s" % name]
    for status, check, detail in results:
        lines.append("  %-4s %-34s %s" % (status, check, detail))
    fails = sum(1 for r in results if r[0] == FAIL)
    warns = sum(1 for r in results if r[0] == WARN)
    lines.append("PREFLIGHT RESULT: %s (%d FAIL, %d WARN)" % ("FAIL" if fails else "PASS", fails, warns))
    return "\n".join(lines)


def preflight_ok(results):
    return not any(r[0] == FAIL for r in results)


# ---------------------------------------------------------------- selftest: each check with a failing and a passing fixture

def selftest(check):
    """Runs the fixtures of every check; check(name, ok, detail) records one result. Nothing outside a temp folder is touched."""
    root = tempfile.mkdtemp(prefix="atmo preflight selftest ")

    def status(results, name=None):
        picked = [r for r in results if name is None or r[1].startswith(name)]
        return picked[-1][0] if picked else None

    def write(path, text="x"):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", newline="") as f:
            f.write(text)

    try:
        # runner version
        r = pf_runner_version(os.path.join(root, "a"), os.path.join(root, "b"), lambda p: "1" * 40)
        check("preflight runner version: equal HEADs PASS", status(r) == PASS)
        r = pf_runner_version(os.path.join(root, "a"), os.path.join(root, "b"), lambda p: "1" * 40 if p.endswith("a") else "2" * 40)
        check("preflight runner version: the main checkout's runner FAILs", status(r) == FAIL and "not this one" in r[0][2])
        check("preflight runner version: the worktree's own runner PASSes", status(pf_runner_version(root, root, lambda p: None)) == PASS)

        # series
        defn, r = pf_series(DEFAULT_REPO, "K-T2")
        check("preflight series: K-T2 validates", status(r) == PASS and defn is not None)
        _d, r = pf_series(DEFAULT_REPO, "K-nonexistent")
        check("preflight series: an unknown series FAILs", status(r) == FAIL)

        # command line
        st = {"id": "s", "probeArgs": ["-screen-width", "1707", "-screen-height", "960"]}
        good = plan_args("K-x", st)
        check("preflight command line: one window size matching the start PASSes", status(pf_args(good, st), "command line") == PASS)
        check("preflight command line: 1600 default plus the start's size FAILs",
              status(pf_args(["-popupwindow", "-screen-width", "1600", "-screen-height", "900", "-atmoSeries", "K-x", "-atmoStart", "s"] + st["probeArgs"], st),
                     "command line") == FAIL)
        check("preflight command line: a size differing from probeArgs FAILs",
              status(pf_args(["-popupwindow", "-atmoSeries", "K-x", "-atmoStart", "s", "-screen-width", "1600", "-screen-height", "960"], st),
                     "command line") == FAIL)
        none = {"id": "n"}
        check("preflight command line: the default is used when the start names none", status(pf_args(plan_args("K-x", none), none), "command line") == PASS)

        # pins
        ksp = os.path.join(root, "Kerbal Space Program")
        write(os.path.join(ksp, "settings.cfg"), "SYNC_VBL = 0\nFRAMERATE_LIMIT = 120\n")
        pins = [{"file": "settings.cfg", "set": {"SYNC_VBL": 0, "FRAMERATE_LIMIT": 1000}}]
        r = pf_pins(ksp, None, pins)
        check("preflight pins: keys standing once PASS and print the target", status(r) == PASS and "120 -> 1000" in r[-1][2])
        write(os.path.join(ksp, "settings.cfg"), "SYNC_VBL = 0\nSYNC_VBL = 1\nFRAMERATE_LIMIT = 120\n")
        check("preflight pins: a key twice FAILs", status(pf_pins(ksp, None, pins), "pin SYNC_VBL") == FAIL)
        write(os.path.join(ksp, "settings.cfg"), "FRAMERATE_LIMIT = 120\n")
        check("preflight pins: a missing key FAILs", status(pf_pins(ksp, None, pins), "pin SYNC_VBL") == FAIL)
        check("preflight pins: a missing file FAILs", status(pf_pins(ksp, None, [{"file": "gone.cfg", "set": {"A": 1}}])) == FAIL)
        cache = os.path.join(root, "cache")
        write(os.path.join(cache, "settings.cfg"), "SYNC_VBL = 3\nFRAMERATE_LIMIT = 60\n")
        check("preflight pins: the profile cache's copy of the file is the one read", "(profile cache)" in pf_pins(ksp, cache, pins)[0][2])

        # snapshot selftest
        check("preflight snapshot selftest: restore equals the original PASS", status(pf_snapshot_selftest(pins)) == PASS)

        class BrokenSnapshot(Snapshot):
            def restore(self):
                return ["settings.cfg"]
        check("preflight snapshot selftest: a restore that leaves a mismatch FAILs", status(pf_snapshot_selftest(pins, BrokenSnapshot)) == FAIL)

        # KSP and orphans
        check("preflight KSP not running: not running PASS", status(pf_ksp_running(False)) == PASS)
        check("preflight KSP not running: running FAILs", status(pf_ksp_running(True)) == FAIL)
        procs = [("11", "testhost.exe", "testhost.exe --port 1 C:\\other\\x.dll"), ("12", "dotnet.exe", "dotnet build C:\\work")]
        check("preflight orphans: unrelated testhost/dotnet PASS", status(pf_orphans(procs, ksp)) == PASS)
        procs.append(("13", "testhost.exe", "testhost.exe %s\\GameData\\ReDefinition\\Main\\Plugins\\ReDefinition.dll" % ksp))
        check("preflight orphans: a testhost naming the install FAILs", status(pf_orphans(procs, ksp)) == FAIL)

        # VMs and RAM
        states = {"gameserver": "Off", "homeassistant": "Off"}
        check("preflight VMs: both off PASS", all(x[0] == PASS for x in pf_vms(states)))
        check("preflight VMs: unknown state WARNs", status(pf_vms({"gameserver": None, "homeassistant": "Off"}), "VM gameserver") == WARN)
        check("preflight VMs: homeassistant running WARNs", status(pf_vms({"gameserver": "Off", "homeassistant": "Running"}), "VM homeassistant") == WARN)
        check("preflight RAM: 16 GB of 12 PASS", status(pf_ram(16, 12, states)) == PASS)
        check("preflight RAM: 8 GB of 12 FAILs", status(pf_ram(8, 12, states)) == FAIL)
        check("preflight RAM: 8 GB with the gameserver running WARNs", status(pf_ram(8, 12, {"gameserver": "Running"})) == WARN)

        # locks
        now = time.time()
        mine = {"id": "me", "pid": os.getpid(), "expires": now + 600, "since": now, "kind": "gpu"}
        other = dict(mine, id="T15")
        stale = dict(mine, id="T15", expires=now - 10)
        check("preflight locks: free PASS", status(pf_locks("me", [("gpu", None), ("unity", None)], now)) == PASS)
        check("preflight locks: another live holder WARNs", status(pf_locks("me", [("gpu", other)], now)) == WARN)
        check("preflight locks: a stale entry PASSes", status(pf_locks("me", [("gpu", stale)], now)) == PASS)
        check("preflight locks: our own entry PASSes", status(pf_locks("me", [("gpu", mine)], now)) == PASS)

        # deploy plan
        repo = os.path.join(root, "repo")
        os.makedirs(os.path.join(repo, "build"))
        write(os.path.join(repo, "build", "ReDefinition.dll"), "wt")
        write(os.path.join(ksp, *BUILD_ARTIFACTS[0][1].split("/")), "game")
        r = pf_hash_plan(repo, ksp, {"bundle": False})
        check("preflight hash plan: no bundle anywhere FAILs", status(r, "bundle") == FAIL)
        write(os.path.join(ksp, *BUNDLE[1].split("/")), "bundle")
        r = pf_hash_plan(repo, ksp, {"bundle": False})
        check("preflight hash plan: the game's bundle PASSes and the hashes are printed",
              status(r, "bundle") == PASS and any("ReDefinition.dll" in x[1] and "will be replaced" in x[2] for x in r))
        check("preflight hash plan: bundle series without the build script FAILs",
              status(pf_hash_plan(repo, ksp, {"bundle": True}, True, False), "bundle build") == FAIL)

        # mod sets
        folders = {"EVE": ["EnvironmentalVisualEnhancements"]}
        os.makedirs(os.path.join(ksp, "GameData", "EnvironmentalVisualEnhancements"))
        r = pf_modsets(ksp, [{"id": "s", "disableMods": ["EVE"]}], folders)
        check("preflight mod sets: a restorable plan PASSes", status(r, "disabled-mods") == PASS and status(r, "no leftover") == PASS)
        check("preflight mod sets: an unknown name FAILs", status(pf_modsets(ksp, [{"id": "s", "disableMods": ["Nope"]}], folders), "disabled-mods") == FAIL)
        os.makedirs(os.path.join(ksp, DISABLED_DIR))
        check("preflight mod sets: a leftover _atmo_disabled FAILs", status(pf_modsets(ksp, [{"id": "s"}], folders), "no leftover") == FAIL)
        os.makedirs(os.path.join(ksp, DISABLED_DIR, "EnvironmentalVisualEnhancements"))
        check("preflight mod sets: a taken target FAILs the plan", status(pf_modsets(ksp, [{"id": "s", "disableMods": ["EVE"]}], folders), "disabled-mods") == FAIL)
        shutil.rmtree(os.path.join(ksp, DISABLED_DIR))

        # KSPCF texture cache
        tc = os.path.join(ksp, *TEXTURE_CACHE_REL.split("/"))
        check("preflight texture cache: no folder PASSes", status(pf_texture_cache(ksp)) == PASS)
        index = 'V2\n' + "".join('{"name":"t%d","id":%d,"time":1,"size":9}\n' % (i, 100 + i) for i in range(3))
        write(os.path.join(tc, "textureData.json"), index)
        r = pf_texture_cache(ksp)
        check("preflight texture cache: ids without files FAIL with the remedy (start 37)",
              status(r) == FAIL and "Move-Item" in r[0][2] and "3 of them without a file" in r[0][2])
        for i in range(3):
            write(os.path.join(tc, str(100 + i)))
        check("preflight texture cache: every id with its file PASSes", status(pf_texture_cache(ksp)) == PASS)
        write(os.path.join(tc, "999"))
        check("preflight texture cache: files outside the index WARN", status(pf_texture_cache(ksp)) == WARN)
        os.remove(os.path.join(tc, "textureData.json"))
        check("preflight texture cache: files without an index FAIL", status(pf_texture_cache(ksp)) == FAIL)
        shutil.rmtree(tc)

        # the profile cache holds no KSPCF texture cache file
        pcache = os.path.join(root, "pcache")
        write(os.path.join(pcache, "GameData", "BetterTimeWarp", "x.cfg"))
        check("preflight profile cache: no texture cache file PASSes", status(pf_profile_cache(pcache)) == PASS)
        write(os.path.join(pcache, *TEXTURE_CACHE_REL.split("/"), "textureData.json"))
        r = pf_profile_cache(pcache)
        check("preflight profile cache: a textureData.json FAILs with the remedy", status(r) == FAIL and "Remove-Item" in r[0][2])
        os.remove(os.path.join(pcache, *TEXTURE_CACHE_REL.split("/"), "textureData.json"))
        write(os.path.join(pcache, *TEXTURE_CACHE_REL.split("/"), "1913803525"))
        check("preflight profile cache: a data file FAILs", status(pf_profile_cache(pcache)) == FAIL)
        shutil.rmtree(pcache)

        # probe files
        check("preflight probe files: missing save and craft WARN", status(pf_probe_files(ksp, []), "probe save") == WARN and status(pf_probe_files(ksp, []), "probe craft") == WARN)
        write(os.path.join(ksp, "saves", "ReDefinitionProbe", "persistent.sfs"))
        write(os.path.join(ksp, "saves", "ReDefinitionProbe", "Ships", "VAB", DEFAULT_CRAFT))
        r = pf_probe_files(ksp, [])
        check("preflight probe files: present save and craft PASS", status(r, "probe save") == PASS and status(r, "probe craft") == PASS)

        # scene guard
        write(os.path.join(repo, "src", "A", "GoodProbe.cs"), "if (HighLogic.LoadedScene == GameScenes.FLIGHT) { var v = FlightGlobals.ActiveVessel; }")
        write(os.path.join(repo, "src", "A", "BadProbe.cs"), "var v = FlightGlobals.ActiveVessel;")
        check("preflight scene guard: a guarded probe PASSes", status(pf_scene_guard(repo, ["-atmoProbe", "GoodProbe"])) == PASS)
        check("preflight scene guard: an unguarded probe FAILs", status(pf_scene_guard(repo, ["-atmoProbe", "BadProbe"])) == FAIL)

        # the whole preflight on a fake install and series
        shutil.copytree(os.path.join(DEFAULT_REPO, "tests", "series"), os.path.join(repo, "tests", "series"))
        write(os.path.join(ksp, "GameData", "ReDefinition", "Main", "PluginData", "settings.cfg"),
              "raytracing = Light\nframeGeneration = True\nqualityAuto = True\nquality = Quality\n")
        write(os.path.join(ksp, "settings.cfg"), "SYNC_VBL = 0\nFRAMERATE_LIMIT = 120\n")
        write(os.path.join(repo, "tools", "build_atmospheres_bundle.ps1"))
        hooks = {"git_head": lambda p: "1" * 40, "ksp_running": lambda: False, "processes": lambda: [], "vm_state": lambda n: "Off",
                 "free_gb": lambda: 20.0, "read_gpu": lambda: None, "read_unity": lambda: None, "profile_cache": os.path.join(root, "nocache"),
                 "mod_folders": folders, "dotnet_exists": True}
        whole = run_preflight(repo, "K-T2", "own1707", ksp, root, "me", hooks, script_repo=os.path.join(root, "other"))
        fails = [x for x in whole if x[0] == FAIL]
        check("preflight whole run: a clean fake install has no FAIL", not fails, "; ".join("%s: %s" % (x[1], x[2][:60]) for x in fails))
        write(os.path.join(tc, "textureData.json"), index)
        whole = run_preflight(repo, "K-T2", "own1707", ksp, root, "me", hooks, script_repo=os.path.join(root, "other"))
        check("preflight whole run: the inconsistent texture cache is a FAIL", not preflight_ok(whole) and any(x[1] == "KSPCF texture cache" and x[0] == FAIL for x in whole))
    finally:
        shutil.rmtree(root, ignore_errors=True)


