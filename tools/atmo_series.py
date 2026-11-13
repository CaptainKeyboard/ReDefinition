#!/usr/bin/env python3
"""The series runner of the Atmospheres work: builds the worktree WITH deploy, starts KSP, restores, and prints one fixed table.
Stdlib only.

  python tools/atmo_series.py run <series> [--start <id>] [--who <id>] [--ksp <KSP folder>] [--owner-consent]
  python tools/atmo_series.py run <series> --dry --log <recorded KSP.log> [--start <id>]
  python tools/atmo_series.py preflight <series> [--start <id>] [--ksp <KSP folder>]
  python tools/atmo_series.py validate [<series>]
  python tools/atmo_series.py modset-restore [--ksp <KSP folder>]
  python tools/atmo_series.py selftest

This is the only place that builds with deploy, deploys and starts the game; every other build and test uses
-p:DeployToGameData=false (tools/dotnet_test.py). Before anything is touched, `run` calls the offline preflight
(tools/atmo_preflight.py, also `preflight <series>`): runner = worktree version, series valid, the planned KSP command line, the pins, the
snapshot round trip, KSP/orphans/VMs/RAM/locks, deploy and mod-set plans, KSPCF's PNG texture cache, the probe's save, craft and scene
guard; any FAIL refuses the run. A run, inside the GPU lock (tools/gpu_lock.py, exclusive for a timing series):

  1. refuses while KSP_x64 runs, and above the run cap of master plan 9.3 (20 starts) without --owner-consent;
  2. builds src/ReDefinition.csproj of THIS worktree in the configuration of the definition ("configuration": "Debug" by default,
     "Release" for a series whose rows are Release limits, such as K-W3a with the audio CPU budget; the report names the
     configuration that ran) with the default deploy into the KSP folder (and, for a series with
     "bundle": true, the shader bundle through tools/build_atmospheres_bundle.ps1 inside the unity lock; a missing bundle refuses
     the run), then compares the deployed DLLs
     (Main, Common, Planner, Dashboard, Atmospheres) and the shader bundle (build/atmospheres.shaders, installed into the game by this
     tool) with the worktree's by SHA-256, and refuses on any difference;
  3. snapshots the side files (every PluginData folder of ReDefinition, KSP's own PluginData and settings.cfg, the probe save, the files
     the Medium profile overwrites), applies the Medium profile from MAINREPO/.local/work/profile-cache, starts from an empty set of
     side files (settings.cfg stays), and puts everything back byte for byte in a finally;
  4. stops the game-server VM before (when it runs) and starts it again after, also on failure; needs >= 12 GB free memory;
  5. per start: moves the GameData folders of the mods the start disables out of GameData (and back in a finally), starts the game with
     -atmoSeries <name> -atmoStart <id> [-atmoVariants a,b], ends it on the probe's verdict line, after the time limit (at most 20
     minutes), or when it quits, and reads KSP.log;
  6. collects the ATMORESULT {json} lines and the files the series names into the worktree, prints ONE table (metric, value,
     threshold, PASS/FAIL), writes build/atmo-series/<series>-<time>.json, and exits 0 only if every row passed, every expected
     line was present and the probe said PASSED.

Series definitions are tests/series/K-*.json, rows tests/series/rows/<Id>.json, both validated against tests/series/schema.json.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import atmo_logguard  # noqa: E402
import gpu_lock  # noqa: E402

DEFAULT_REPO = os.path.dirname(HERE)
DEFAULT_KSP = r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program"
DEFAULT_DOTNET = os.path.join(os.path.expanduser("~"), ".dotnet", "dotnet.exe")
RUN_CAP = 20
MAX_MINUTES = 20
PROFILE_CACHE_REL = (".local", "work", "profile-cache")
STARTS_LOG_REL = (".local", "work", "atmo-series-starts.jsonl")
DISABLED_DIR = "_atmo_disabled"
PROBE_GRACE_SECONDS = 90

# The mods the mod-set manager can move away: logical name -> the GameData folders (or files) that belong to it. [VERIFY] against the
# installed GameData: StockVolumetricClouds, ScattererAtmosphereCache, StockScattererConfigs and the Parallax_Stock* folders are taken
# to be the configurations and caches of EVE, Scatterer and Parallax.
MOD_FOLDERS = {
    "EVE": ["EnvironmentalVisualEnhancements", "StockVolumetricClouds"],
    "Scatterer": ["scatterer", "ScattererAtmosphereCache", "StockScattererConfigs"],
    "Deferred": ["zzz_Deferred"],
    "TUFX": ["TUFX", "Blackrack_TUFX.cfg"],
    # Throws a NullReferenceException every few seconds in the flight scene (RealMaxAtmosphereAltitude, 96 a run in K-T2 start 42): CPU load that hides GPU cost.
    "Trajectories": ["Trajectories"],
    "Parallax": ["ParallaxContinued", "Parallax_StockPlanetTextures", "Parallax_StockScatterTextures", "Parallax_StockTerrainTextures"],
}

# What a build deploys and what the worktree built: (worktree file, file in the KSP folder).
BUILD_ARTIFACTS = [
    ("build/ReDefinition.dll", "GameData/ReDefinition/Main/Plugins/ReDefinition.dll"),
    ("build/ReDefinition.Common.dll", "GameData/ReDefinition/Common/Plugins/ReDefinition.Common.dll"),
    ("build/ReDefinition.Planner.dll", "GameData/ReDefinition/Planner/Plugins/ReDefinition.Planner.dll"),
    ("build/ReDefinition.Dashboard.dll", "GameData/ReDefinition/Dashboard/Plugins/ReDefinition.Dashboard.dll"),
    ("build/ReDefinition.Atmospheres.dll", "GameData/ReDefinition/Atmospheres/Plugins/ReDefinition.Atmospheres.dll"),
]
BUNDLE = ("build/atmospheres.shaders", "GameData/ReDefinition/Atmospheres/Shaders/atmospheres.shaders")
ATMO_PLUGINDATA = "GameData/ReDefinition/Atmospheres/PluginData"


class Refusal(Exception):
    """The run does not start (or does not go on) for a stated reason."""


# ---------------------------------------------------------------- small helpers

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def ksp_running_tasklist():
    """Whether KSP_x64.exe runs, as tasklist sees it; False where tasklist cannot be run."""
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe"], capture_output=True, text=True).stdout
    except OSError:
        return False
    return tasklist_has_ksp(out)


def tasklist_has_ksp(text):
    """Whether tasklist's output names KSP_x64.exe."""
    return "KSP_x64.exe" in text


def free_memory_gb():
    """Available physical memory in GB (Windows)."""
    import ctypes

    class Mem(ctypes.Structure):
        _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong), ("total", ctypes.c_ulonglong), ("avail", ctypes.c_ulonglong),
                    ("totalPage", ctypes.c_ulonglong), ("availPage", ctypes.c_ulonglong), ("totalVirtual", ctypes.c_ulonglong),
                    ("availVirtual", ctypes.c_ulonglong), ("ext", ctypes.c_ulonglong)]
    mem = Mem()
    mem.length = ctypes.sizeof(Mem)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(mem))
    return mem.avail / 2 ** 30


def norm_name(name):
    """A series name as the probe compares it: K-pre, Kpre and kpre are the same."""
    return re.sub(r"[-_ ]", "", name).lower()


# ---------------------------------------------------------------- schema validation (the subset the schema file uses)

def validate_schema(data, schema, root, path="$"):
    """Errors of data against a schema node (type, enum, pattern, minimum, maximum, minItems, items, required, properties,
    additionalProperties, $ref into #/definitions)."""
    errors = []
    if "$ref" in schema:
        target = root
        for part in schema["$ref"].lstrip("#/").split("/"):
            target = target[part]
        return validate_schema(data, target, root, path)
    if "enum" in schema and data not in schema["enum"]:
        errors.append("%s: %r is not one of %s" % (path, data, schema["enum"]))
        return errors
    kind = schema.get("type")
    if kind:
        ok = {"object": isinstance(data, dict), "array": isinstance(data, list), "string": isinstance(data, str),
              "boolean": isinstance(data, bool), "integer": isinstance(data, int) and not isinstance(data, bool),
              "number": isinstance(data, (int, float)) and not isinstance(data, bool)}[kind]
        if not ok:
            return ["%s: expected %s, got %s" % (path, kind, type(data).__name__)]
    if isinstance(data, str) and "pattern" in schema and not re.search(schema["pattern"], data):
        errors.append("%s: %r does not match %s" % (path, data, schema["pattern"]))
    if isinstance(data, (int, float)) and not isinstance(data, bool):
        if "minimum" in schema and data < schema["minimum"]:
            errors.append("%s: %r is below %r" % (path, data, schema["minimum"]))
        if "maximum" in schema and data > schema["maximum"]:
            errors.append("%s: %r is above %r" % (path, data, schema["maximum"]))
    if isinstance(data, list):
        if "minItems" in schema and len(data) < schema["minItems"]:
            errors.append("%s: fewer than %d items" % (path, schema["minItems"]))
        if "items" in schema:
            for i, item in enumerate(data):
                errors += validate_schema(item, schema["items"], root, "%s[%d]" % (path, i))
    if isinstance(data, dict):
        for key in schema.get("required", []):
            if key not in data:
                errors.append("%s: missing %s" % (path, key))
        props = schema.get("properties", {})
        for key, value in data.items():
            if key in props:
                errors += validate_schema(value, props[key], root, "%s.%s" % (path, key))
            elif schema.get("additionalProperties") is False:
                errors.append("%s: unknown key %s" % (path, key))
    return errors


# ---------------------------------------------------------------- series definitions

def read_json(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def load_schema(repo):
    return read_json(os.path.join(repo, "tests", "series", "schema.json"))


def series_path(repo, name):
    return os.path.join(repo, "tests", "series", name + ".json")


def check_definition(defn, rows_by_package, schema):
    """Errors of a series definition (already merged with its row files): schema, unique ids, thresholds, names in the rows."""
    errors = validate_schema(defn, schema, schema)
    if errors:
        return errors
    pin_lists = [("settings", defn.get("settings", []))] + [("starts[%s].settings" % s.get("id"), s.get("settings", [])) for s in defn["starts"]]
    for where, entries in pin_lists:
        for i, entry in enumerate(entries):
            for key, value in entry["set"].items():
                if not re.match(r"^[A-Za-z0-9_.]+$", key) or isinstance(value, (dict, list)) or value is None:
                    errors.append("%s[%d]: %s must be a plain key with a string, number or boolean value" % (where, i, key))
    ids = [s["id"] for s in defn["starts"]]
    if len(set(ids)) != len(ids):
        errors.append("starts: duplicate id")
    sections = [s["id"] for s in defn.get("sections", [])]
    if len(set(sections)) != len(sections):
        errors.append("sections: duplicate id")
    seen = set()
    for i, row in enumerate(defn.get("rows", [])):
        where = "rows[%d] %s" % (i, row.get("metric"))
        if "start" in row and row["start"] not in ids:
            errors.append("%s: unknown start %s" % (where, row["start"]))
        if "section" in row and sections and row["section"] not in sections:
            errors.append("%s: unknown section %s" % (where, row["section"]))
        if "min" in row and "max" in row and row["min"] > row["max"]:
            errors.append("%s: min above max" % where)
        if "equals" in row and ("min" in row or "max" in row):
            errors.append("%s: equals with min or max" % where)
        if "tolerance" in row and "equals" not in row:
            errors.append("%s: tolerance without equals" % where)
        key = (row["metric"], row.get("start"), row.get("variant"))
        if key in seen:
            errors.append("%s: duplicate row" % where)
        seen.add(key)
    return errors


def load_series(repo, name):
    """The series definition with the rows of all row files merged in. Returns (definition, errors)."""
    schema = load_schema(repo)
    path = series_path(repo, name)
    if not os.path.exists(path):
        raise Refusal("no series definition " + path)
    defn = read_json(path)
    errors = []
    if defn.get("name") != name:
        errors.append("name %r does not match the file %s" % (defn.get("name"), name))
    rows_dir = os.path.join(repo, "tests", "series", "rows")
    if os.path.isdir(rows_dir):
        for fname in sorted(os.listdir(rows_dir)):
            if not fname.endswith(".json"):
                continue
            rpath = os.path.join(rows_dir, fname)
            try:
                rows = read_json(rpath)
            except ValueError as e:
                errors.append("%s: not JSON (%s)" % (fname, e))
                continue
            sub = dict(schema["definitions"]["rowFile"])
            errs = validate_schema(rows, sub, schema, fname)
            errors += errs
            if errs:
                continue
            for row in rows["rows"]:
                if row.get("series") != name:
                    continue
                row = dict(row)
                row.pop("series", None)
                row.setdefault("package", rows["package"])
                defn.setdefault("rows", []).append(row)
    sections = [s["id"] for s in defn.get("sections", [])]
    for row in defn.get("rows", []):
        if "section" not in row:
            row["section"] = row.get("package") if row.get("package") in sections else (sections[0] if sections else "main")
    errors += check_definition(defn, None, schema)
    return defn, errors


# ---------------------------------------------------------------- the log

RESULT_RE = re.compile(r"ATMORESULT\s*(\{.*\})\s*$")
PROBE_RE = re.compile(r"Probe:\s*(PASSED|FAILED)")
# A fault of a side task of the probe (the pictures of K-T2) that does not fail the series: "Probe: warning <name>: <detail>".
PROBE_WARNING_RE = re.compile(r"Probe: warning (\S+): (.*)$")


def parse_log(text, series):
    """The ATMORESULT lines of one series and the probe's verdict in a log. Returns (results, warnings, verdict) where verdict is
    'PASSED', 'FAILED' or None. A malformed line is ignored with a warning."""
    results, warnings = [], []
    verdict = None
    want = norm_name(series)
    for number, line in enumerate(text.splitlines(), 1):
        m = PROBE_RE.search(line)
        if m:
            verdict = "FAILED" if (m.group(1) == "FAILED" or verdict == "FAILED") else "PASSED"
        m = PROBE_WARNING_RE.search(line)
        if m:
            warnings.append("probe warning %s: %s" % (m.group(1), m.group(2).strip()))
            continue
        if "ATMORESULT" not in line:
            continue
        m = RESULT_RE.search(line)
        if not m:
            warnings.append("line %d: ATMORESULT without a JSON object" % number)
            continue
        try:
            obj = json.loads(m.group(1))
        except ValueError:
            warnings.append("line %d: malformed ATMORESULT JSON ignored" % number)
            continue
        if not isinstance(obj, dict) or "metric" not in obj or "value" not in obj or not isinstance(obj["metric"], str):
            warnings.append("line %d: ATMORESULT without metric and value ignored" % number)
            continue
        if norm_name(str(obj.get("series", ""))) != want:
            continue
        results.append({"metric": obj["metric"], "value": obj["value"], "variant": obj.get("variant"), "line": number})
    return results, warnings, verdict


def metric_results(repo, defn, texts):
    """The result lines the series' metric scripts ("metrics": [{"script": "tools/..."}]) compute from the logs of all starts, texts being
    {start id: log text}. A script defines series_results(texts) -> [{metric, value, variant}] and may fill a module list NOTES.
    Returns (results, notes, failures); a script that cannot run or answers wrongly is a failure of the run, not a silent gap."""
    results, notes, failures = [], [], []
    for item in defn.get("metrics", []):
        path = os.path.join(repo, *item["script"].split("/"))
        try:
            spec = importlib.util.spec_from_file_location("atmo_metric_" + re.sub(r"\W", "_", item["script"]), path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            for r in module.series_results(texts):
                if not isinstance(r, dict) or "metric" not in r or r.get("value") is None:
                    continue
                results.append({"metric": r["metric"], "value": r["value"], "variant": r.get("variant"), "line": 0})
            notes += list(getattr(module, "NOTES", []))
        except Exception as e:  # a metric script is code of the series: its failure fails the run with the reason
            failures.append(("metrics@" + item["script"], "%s: %s" % (type(e).__name__, e)))
    return results, notes, failures


# ---------------------------------------------------------------- evaluation and the table

def number_of(value):
    if isinstance(value, bool):
        return 1.0 if value else 0.0
    if isinstance(value, (int, float)):
        return float(value)
    return None


def threshold_text(row):
    if "equals" in row:
        t = row.get("tolerance", 0)
        return "== %g%s" % (row["equals"], " +-%g" % t if t else "")
    if "min" in row and "max" in row:
        return "[%g, %g]" % (row["min"], row["max"])
    if "min" in row:
        return ">= %g" % row["min"]
    if "max" in row:
        return "<= %g" % row["max"]
    return "present"


def aggregate(values, how):
    if how == "min":
        return min(values)
    if how == "max":
        return max(values)
    if how == "mean":
        return sum(values) / len(values)
    if how == "sum":
        return sum(values)
    if how == "count":
        return float(len(values))
    return values[-1]


def evaluate_row(row, results):
    """(value text, status) of one row against the results of the starts. status: PASS, FAIL or SKIP."""
    cands = [r for r in results if r["metric"] == row["metric"] and (row.get("start") is None or r.get("start") == row["start"])
             and (row.get("variant") is None or r.get("variant") == row["variant"])]
    if not cands:
        return "(no line)", ("FAIL" if row.get("required", True) else "SKIP")
    how = row.get("agg", "last")
    numbers = [number_of(r["value"]) for r in cands]
    if any(n is None for n in numbers):
        if how != "count" and ("min" in row or "max" in row or "equals" in row):
            return str(cands[-1]["value"]), "FAIL"
        if how != "count":
            return str(cands[-1]["value"]), "PASS"
        numbers = [0.0 for _ in cands]
    value = aggregate(numbers, how)
    ok = True
    if "equals" in row:
        ok = abs(value - row["equals"]) <= row.get("tolerance", 0) + 1e-12
    if "min" in row and value < row["min"]:
        ok = False
    if "max" in row and value > row["max"]:
        ok = False
    return "%.6g" % value, ("PASS" if ok else "FAIL")


def evaluate(defn, results, verdicts, extra_failures=(), ran=None):
    """The table: a list of dicts (section, metric, value, threshold, status), and whether the series passed. Rows bound to a start
    that did not run (ran lists the ones that did) are left out."""
    sections = [s["id"] for s in defn.get("sections", [])] or ["main"]
    titles = {s["id"]: s.get("title", s["id"]) for s in defn.get("sections", [])}
    table = []
    for sec in sections:
        for row in defn.get("rows", []):
            if row.get("section", sections[0]) != sec:
                continue
            if ran is not None and row.get("start") is not None and row["start"] not in ran:
                continue
            value, status = evaluate_row(row, results)
            label = row["metric"] + ("@" + row["start"] if row.get("start") else "") + ("#" + row["variant"] if row.get("variant") else "")
            table.append({"section": titles.get(sec, sec), "metric": label, "value": value, "threshold": threshold_text(row), "status": status})
    if defn.get("expectProbeLine", True):
        for sid, verdict in verdicts.items():
            table.append({"section": "probe", "metric": "Probe@" + sid, "value": verdict or "(no line)", "threshold": "PASSED",
                          "status": "PASS" if verdict == "PASSED" else "FAIL"})
    for name, why in extra_failures:
        table.append({"section": "run", "metric": name, "value": why, "threshold": "ok", "status": "FAIL"})
    ok = bool(table) and all(r["status"] in ("PASS", "SKIP") for r in table)
    return table, ok


def format_table(series, table, unlisted=()):
    lines = ["Series %s" % series, "%-52s %-16s %-18s %s" % ("metric", "value", "threshold", "result"), "-" * 96]
    last = None
    for r in table:
        if r["section"] != last:
            last = r["section"]
            lines.append("== %s" % last)
        lines.append("%-52s %-16s %-18s %s" % (r["metric"][:52], str(r["value"])[:16], r["threshold"][:18], r["status"]))
    if unlisted:
        lines.append("== lines without a row (not judged): " + ", ".join(sorted(unlisted)[:20]) + (" ..." if len(unlisted) > 20 else ""))
    lines.append("-" * 96)
    lines.append("RESULT: %s" % ("PASS" if all(r["status"] != "FAIL" for r in table) and table else "FAIL"))
    return "\n".join(lines)


# ---------------------------------------------------------------- side files, profile, mod sets, VM

class Snapshot:
    """Side files put back byte for byte: folders are mirrored (files made since are removed), single files restored or removed."""

    def __init__(self, ksp, store):
        self.ksp = ksp
        self.store = store
        self.dirs = []
        self.files = []
        self.manifest = {}

    def path(self, rel):
        return os.path.join(self.ksp, *rel.replace("\\", "/").split("/"))

    def add_dir(self, rel):
        if rel not in self.dirs:
            self.dirs.append(rel)

    def add_file(self, rel):
        if rel not in self.files:
            self.files.append(rel)

    def take(self):
        """Copies the protected paths into the store and records their hashes."""
        for rel in self.dirs:
            live = self.path(rel)
            if not os.path.isdir(live):
                continue
            for root, _dirs, names in os.walk(live):
                for n in names:
                    src = os.path.join(root, n)
                    r = os.path.relpath(src, self.ksp)
                    dst = os.path.join(self.store, r)
                    os.makedirs(os.path.dirname(dst), exist_ok=True)
                    shutil.copy2(src, dst)
                    self.manifest[r] = sha256(src)
        for rel in self.files:
            live = self.path(rel)
            if os.path.isfile(live):
                r = os.path.relpath(live, self.ksp)
                dst = os.path.join(self.store, r)
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy2(live, dst)
                self.manifest[r] = sha256(live)

    def restore(self):
        """Puts the protected paths back. Returns the list of mismatches left (empty when every file equals its original)."""
        for rel in self.dirs:
            live = self.path(rel)
            prefix = os.path.normpath(rel)
            if os.path.isdir(live):
                for root, _dirs, names in os.walk(live):
                    for n in names:
                        p = os.path.join(root, n)
                        if os.path.relpath(p, self.ksp) not in self.manifest:
                            os.remove(p)
            for r, digest in self.manifest.items():
                if os.path.normpath(r).startswith(prefix + os.sep) or os.path.normpath(r) == prefix:
                    self._put_back(r, digest)
        for rel in self.files:
            live = self.path(rel)
            r = os.path.relpath(live, self.ksp)
            if r in self.manifest:
                self._put_back(r, self.manifest[r])
            elif os.path.isfile(live):
                os.remove(live)
        bad = []
        for r, digest in self.manifest.items():
            live = os.path.join(self.ksp, r)
            if not os.path.isfile(live) or sha256(live) != digest:
                bad.append(r)
        return bad

    def _put_back(self, r, digest):
        live = os.path.join(self.ksp, r)
        if os.path.isfile(live) and sha256(live) == digest:
            return
        os.makedirs(os.path.dirname(live), exist_ok=True)
        shutil.copy2(os.path.join(self.store, r), live)


def tree_files(root):
    """The files under a folder, relative to it, with forward slashes."""
    out = []
    for r, _d, names in os.walk(root):
        for n in names:
            out.append(os.path.relpath(os.path.join(r, n), root).replace("\\", "/"))
    return sorted(out)


def apply_profile(cache, ksp):
    """Copies the files of the profile cache over the KSP folder (the Medium settings of the mods). KSPCF's texture cache files are
    never copied: a bare index without its data files made the loader of K-T2 starts 37 and 38 fail. Returns the skipped files."""
    skipped = []
    for rel in tree_files(cache):
        if is_texture_cache_file(rel):
            skipped.append(rel)
            print("apply_profile: skipped %s (KSPCF texture cache files are not injected)" % rel, flush=True)
            continue
        dst = os.path.join(ksp, *rel.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(os.path.join(cache, *rel.split("/")), dst)
    return skipped


def is_texture_cache_file(rel):
    """Whether a path relative to the KSP folder (or the profile cache) is KSPCF's PNG texture cache: the index or a data file."""
    low = rel.replace("\\", "/").lower()
    return low.endswith("/texturedata.json") or low == "texturedata.json" or "/texturecache/" in "/" + low or low.endswith("/texturecache")


def settings_text(value):
    """A settings value as the files write it (True/False capitalised)."""
    return str(value)


def merge_settings(*lists):
    """Merges pin lists (series-wide first, then a start's own): per file, a later list adds keys and overrides the value of a key
    an earlier one set. Returns a new list of {"file", "set"} entries, one per file, in order of first appearance."""
    merged = {}
    for entries in lists:
        for entry in entries or []:
            merged.setdefault(entry["file"], {}).update(entry["set"])
    return [{"file": f, "set": dict(keys)} for f, keys in merged.items()]


def effective_settings(defn, st):
    """The pins one start runs with: the series' pins, overridden and extended by the start's own ("settings" of the start)."""
    return merge_settings(defn.get("settings"), st.get("settings"))


def apply_settings(ksp, settings):
    """Pins the keys a series names ("settings" in its definition): each key must already stand in the file as 'key = value'
    (first match, indent kept). Returns the lines written, 'file: key = value'. Raises Refusal for a missing file or key."""
    written = []
    for entry in settings or []:
        path = os.path.join(ksp, *entry["file"].split("/"))
        if not os.path.isfile(path):
            raise Refusal("the series pins settings in %s, which does not exist" % entry["file"])
        with open(path, "r", encoding="utf-8", newline="") as f:
            lines = f.read().split("\n")
        for key, value in entry["set"].items():
            hit = None
            for i, line in enumerate(lines):
                m = re.match(r"^(\s*)%s\s*=" % re.escape(key), line)
                if m:
                    hit = i
                    lines[i] = "%s%s = %s%s" % (m.group(1), key, settings_text(value), "\r" if line.endswith("\r") else "")
                    break
            if hit is None:
                raise Refusal("the series pins %s in %s, which has no such key" % (key, entry["file"]))
            written.append("%s: %s = %s" % (entry["file"], key, settings_text(value)))
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write("\n".join(lines))
    return written


def clean_side_files(ksp):
    """Removes the side files the Atmospheres left in their PluginData (checkpoints, caches); settings.cfg stays. Returns the count."""
    folder = os.path.join(ksp, *ATMO_PLUGINDATA.split("/"))
    count = 0
    if not os.path.isdir(folder):
        return 0
    for root, _d, names in os.walk(folder):
        for n in names:
            if os.path.normcase(os.path.join(root, n)) == os.path.normcase(os.path.join(folder, "settings.cfg")):
                continue
            os.remove(os.path.join(root, n))
            count += 1
    return count


class ModSets:
    """Moves the GameData folders of mods out of GameData (into <ksp>/_atmo_disabled, a rename on the same volume) and back. The journal
    in that folder lets modset-restore put them back after a crash of the runner."""

    def __init__(self, ksp, ksp_running, folders=None):
        self.ksp = ksp
        self.running = ksp_running
        self.folders = folders or MOD_FOLDERS
        self.store = os.path.join(ksp, DISABLED_DIR)
        self.journal = os.path.join(self.store, "journal.json")

    def pending(self):
        return os.path.exists(self.journal)

    def disable(self, names):
        if not names:
            return []
        if self.running():
            raise Refusal("KSP_x64 runs; mod folders are not moved while it does")
        if self.pending():
            raise Refusal("an earlier run left mod folders in %s; run: atmo_series.py modset-restore" % self.store)
        unknown = [n for n in names if n not in self.folders]
        if unknown:
            raise Refusal("unknown mod set name(s): " + ", ".join(unknown))
        os.makedirs(self.store, exist_ok=True)
        moved = []
        self._write(moved)
        for name in names:
            for folder in self.folders[name]:
                src = os.path.join(self.ksp, "GameData", folder)
                dst = os.path.join(self.store, folder)
                if os.path.exists(src):
                    os.rename(src, dst)
                    moved.append([folder, name])
                    self._write(moved)
        return moved

    def restore(self):
        """Moves everything back. Raises where KSP still runs (after a wait) or a folder cannot go back."""
        if not self.pending():
            return []
        deadline = time.time() + 30
        while self.running() and time.time() < deadline:
            time.sleep(0.5)
        if self.running():
            raise Refusal("KSP_x64 still runs; mod folders stay in %s (atmo_series.py modset-restore puts them back)" % self.store)
        moved = read_json(self.journal)
        back = []
        for folder, _name in reversed(moved):
            src = os.path.join(self.store, folder)
            dst = os.path.join(self.ksp, "GameData", folder)
            if os.path.exists(src):
                if os.path.exists(dst):
                    raise Refusal("cannot put back %s: %s exists" % (folder, dst))
                os.rename(src, dst)
                back.append(folder)
        os.remove(self.journal)
        try:
            os.rmdir(self.store)
        except OSError:
            pass
        return back

    def _write(self, moved):
        with open(self.journal, "w", encoding="utf-8") as f:
            json.dump(moved, f)


class VmControl:
    """The game-server VM: stopped before the run when it runs, started again after. A failing command is a warning; the memory check
    decides whether the run can go on."""

    def __init__(self, name="gameserver", runner=None, log=print):
        self.name = name
        self.run = runner or self._powershell
        self.log = log
        self.stopped = False

    @staticmethod
    def _powershell(command):
        r = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True, timeout=300)
        return r.returncode, r.stdout.strip()

    def begin(self):
        try:
            code, out = self.run("(Get-VM -Name %s).State" % self.name)
        except (OSError, subprocess.TimeoutExpired) as e:
            self.log("VM: state unknown (%s); left alone" % e)
            return
        if code != 0:
            self.log("VM: state unknown (exit %s); left alone" % code)
            return
        if "Running" not in out:
            self.log("VM: %s is not running (%s); left alone" % (self.name, out or "?"))
            return
        try:
            code, out = self.run("Stop-VM -Name %s -Force" % self.name)
        except (OSError, subprocess.TimeoutExpired) as e:
            self.log("VM: stop failed (%s)" % e)
            return
        if code != 0:
            self.log("VM: stop failed (exit %s) %s" % (code, out))
            return
        self.stopped = True
        self.log("VM: %s stopped" % self.name)

    def end(self):
        if not self.stopped:
            return
        try:
            code, out = self.run("Start-VM -Name %s" % self.name)
            self.log("VM: %s started again" % self.name if code == 0 else "VM: start failed (exit %s) %s" % (code, out))
        except (OSError, subprocess.TimeoutExpired) as e:
            self.log("VM: start failed (%s)" % e)
        self.stopped = False


# ---------------------------------------------------------------- build and launch

def build_command(repo, ksp, dotnet, configuration="Debug"):
    """The command line of the build with deploy, in the given configuration."""
    return [dotnet, "build", os.path.join(repo, "src", "ReDefinition.csproj"), "-c", configuration, "-nologo", "-v:minimal",
            "-p:KspRoot=" + ksp]


def build_with_deploy(repo, ksp, dotnet, configuration="Debug"):
    """Builds src/ReDefinition.csproj of the worktree with the default deploy into the KSP folder, in the given configuration (Debug
    unless the series definition says "configuration": "Release"). Returns the exit code."""
    return subprocess.call(build_command(repo, ksp, dotnet, configuration), cwd=repo)


def build_bundle_with_lock(repo, ksp, who):
    """Builds build/atmospheres.shaders of the worktree with Unity in batch mode (tools/build_atmospheres_bundle.ps1) inside the
    unity lock, which is a second lock next to the gpu lock the run already holds. Returns the exit code."""
    out = os.path.join(repo, *BUNDLE[0].split("/"))
    os.makedirs(os.path.dirname(out), exist_ok=True)
    path = gpu_lock.lock_path("unity")
    info = gpu_lock.wait_acquire(path, who, 25, False, "unity", os.getpid(), "build_atmospheres_bundle")
    try:
        command = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", os.path.join(repo, "tools", "build_atmospheres_bundle.ps1"),
                   "-Ksp", ksp, "-Output", out]
        return subprocess.call(command, cwd=repo)
    finally:
        gpu_lock.release(path, who, os.getpid(), info["since"])


def launch_game(ksp, args, timeout, log_path, exe_prefix=None, grace=PROBE_GRACE_SECONDS, poll=2.0):
    """Starts the game from its folder and waits: until it quits, until the probe's verdict line has been in the log for the grace
    time, or until the timeout. Returns (how it ended, exit code or None)."""
    command = (exe_prefix or [os.path.join(ksp, "KSP_x64.exe")]) + args
    # A start that names several probes (-atmoProbe A,B) ends after the verdict line of each of them.
    expected = 1
    if "-atmoProbe" in args and args.index("-atmoProbe") + 1 < len(args):
        expected = max(1, len([p for p in args[args.index("-atmoProbe") + 1].split(",") if p.strip()]))
    proc = subprocess.Popen(command, cwd=ksp, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    stop = threading.Event()
    foreground = None
    if exe_prefix is None:
        try:
            # Frame generation produces nothing while the game's window is not in front: the benchmark tool's helper keeps it there.
            from benchmark_matrix_lib.install import Foreground
            foreground = Foreground(proc.pid)
        except Exception:
            foreground = None
    if foreground is not None and not os.environ.get("REDEFINITION_BENCH_NO_FOREGROUND"):
        def keep():
            while not stop.wait(2.0):
                try:
                    foreground.keep()
                except Exception:
                    pass
        threading.Thread(target=keep, daemon=True).start()
    started = time.time()
    verdict_at = None
    guard = atmo_logguard.LogGuard(started)
    tail = atmo_logguard.LogTail(log_path)
    try:
        while True:
            code = proc.poll()
            if code is not None:
                return "quit", code
            now = time.time()
            reason = guard.feed_text(tail.read_new(), now)
            if reason:
                gpu_lock.kill_tree(proc.pid)
                proc.wait()
                return "aborted: " + reason, None
            if now - started > timeout:
                gpu_lock.kill_tree(proc.pid)
                proc.wait()
                return "timeout after %d s" % timeout, None
            try:
                with open(log_path, "r", encoding="utf-8", errors="replace") as f:
                    seen = len(PROBE_RE.findall(f.read())) >= expected
            except OSError:
                seen = False
            if seen and verdict_at is None:
                verdict_at = now
            if verdict_at is not None and now - verdict_at > grace:
                gpu_lock.kill_tree(proc.pid)
                proc.wait()
                return "ended after the verdict line", None
            time.sleep(poll)
    finally:
        stop.set()
        if proc.poll() is None:
            gpu_lock.kill_tree(proc.pid)


# ---------------------------------------------------------------- the runner

def plan_args(name, st):
    """The command line arguments of one start (what Runner.execute passes to KSP_x64.exe). The default window size only where the
    start names none: a switch twice on the command line has no documented winner."""
    args = ["-popupwindow"]
    if "-screen-width" not in st.get("probeArgs", []):
        args += ["-screen-width", "1600", "-screen-height", "900"]
    args += ["-atmoSeries", name, "-atmoStart", st["id"]]
    if st.get("variants"):
        args += ["-atmoVariants", ",".join(st["variants"])]
    args += st.get("probeArgs", [])
    return args


class Runner:
    def __init__(self, repo, ksp, mainrepo, who="series", dotnet=DEFAULT_DOTNET, hooks=None, log=print):
        self.repo = repo
        self.ksp = ksp
        self.mainrepo = mainrepo
        self.who = who
        self.dotnet = dotnet
        self.log = log
        h = hooks or {}
        self.ksp_running = h.get("ksp_running", ksp_running_tasklist)
        # The configuration of the build in force: set from the series definition just before the build, read by the default hook.
        self.configuration = "Debug"
        self.build = h.get("build", lambda: build_with_deploy(repo, ksp, dotnet, self.configuration))
        self.build_bundle = h.get("build_bundle", lambda: build_bundle_with_lock(repo, ksp, who))
        self.launch = h.get("launch", lambda args, timeout, log_path: launch_game(ksp, args, timeout, log_path))
        self.vm = h.get("vm") or VmControl(log=log)
        self.free_gb = h.get("free_gb", free_memory_gb)
        self.profile_cache = h.get("profile_cache", os.path.join(mainrepo, *PROFILE_CACHE_REL))
        self.starts_log = h.get("starts_log", os.path.join(mainrepo, *STARTS_LOG_REL))
        self.mod_folders = h.get("mod_folders")
        self.now = h.get("now", time.time)

    # -- checks before anything is touched
    def count_starts(self):
        if not os.path.exists(self.starts_log):
            return 0
        n = 0
        with open(self.starts_log, "r", encoding="utf-8") as f:
            for line in f:
                try:
                    if json.loads(line).get("counted", True):
                        n += 1
                except ValueError:
                    pass
        return n

    def record_start(self, series, start, how):
        os.makedirs(os.path.dirname(self.starts_log), exist_ok=True)
        with open(self.starts_log, "a", encoding="utf-8") as f:
            f.write(json.dumps({"time": time.strftime("%Y-%m-%d %H:%M:%S"), "series": series, "start": start, "who": self.who, "how": how,
                                "counted": True}) + "\n")

    def verify_hashes(self, install_bundle=True, require_bundle=False):
        """Compares the deployed DLLs and the bundle with the worktree's; installs the bundle first. Returns the list of
        (file, worktree hash, deployed hash) and raises Refusal on any difference."""
        pairs = list(BUILD_ARTIFACTS)
        wt_bundle = os.path.join(self.repo, *BUNDLE[0].split("/"))
        game_bundle = os.path.join(self.ksp, *BUNDLE[1].split("/"))
        if os.path.isfile(wt_bundle):
            if install_bundle and (not os.path.isfile(game_bundle) or sha256(game_bundle) != sha256(wt_bundle)):
                os.makedirs(os.path.dirname(game_bundle), exist_ok=True)
                shutil.copy2(wt_bundle, game_bundle)
            pairs.append(BUNDLE)
        elif require_bundle or not os.path.isfile(game_bundle):
            raise Refusal("no shader bundle: %s is not in the worktree and the game has none (set \"bundle\": true in the series "
                          "definition or run tools/build_atmospheres_bundle.ps1 -Output %s under the unity lock)" % (BUNDLE[0], BUNDLE[0]))
        else:
            self.log("bundle: %s is not in the worktree; the game's own bundle is used" % BUNDLE[0])
        hashes, bad = [], []
        for wt, game in pairs:
            wp, gp = os.path.join(self.repo, *wt.split("/")), os.path.join(self.ksp, *game.split("/"))
            wh = sha256(wp) if os.path.isfile(wp) else None
            gh = sha256(gp) if os.path.isfile(gp) else None
            hashes.append({"file": game, "worktree": wh, "deployed": gh})
            if wh is None or gh is None or wh != gh:
                bad.append("%s: worktree %s, deployed %s" % (game, (wh or "missing")[:12], (gh or "missing")[:12]))
        if bad:
            raise Refusal("the deployed files are not the worktree's build:\n  " + "\n  ".join(bad))
        return hashes

    def snapshot_plan(self, store):
        snap = Snapshot(self.ksp, store)
        gd = os.path.join(self.ksp, "GameData", "ReDefinition")
        if os.path.isdir(gd):
            for name in sorted(os.listdir(gd)):
                if os.path.isdir(os.path.join(gd, name, "PluginData")):
                    snap.add_dir("GameData/ReDefinition/%s/PluginData" % name)
        snap.add_dir(ATMO_PLUGINDATA)
        snap.add_dir("PluginData")
        # The shader bundle is not protected: it stays deployed together with the DLLs the build deployed.
        for f in ("settings.cfg", "saves/ReDefinitionProbe/persistent.sfs"):
            snap.add_file(f)
        if os.path.isdir(self.profile_cache):
            for rel in tree_files(self.profile_cache):
                if not is_texture_cache_file(rel):  # apply_profile never copies these
                    snap.add_file(rel)
        return snap

    # -- the run
    def execute(self, defn, only_start=None, owner_consent=False, mods_ok=True):
        """Runs the series. Returns the report dict (table, ok, starts, hashes, warnings)."""
        name = defn["name"]
        starts = [s for s in defn["starts"] if only_start in (None, s["id"])]
        if not starts:
            raise Refusal("the series has no start %s" % only_start)
        if self.ksp_running():
            raise Refusal("KSP_x64 runs: a build with deploy is refused while the game runs")
        done = self.count_starts()
        if done + len(starts) > RUN_CAP and not owner_consent:
            raise Refusal("%d starts are counted; %d more would pass the cap of %d (master plan 9.3) without the owner's consent" %
                          (done, len(starts), RUN_CAP))
        if not os.path.isdir(self.profile_cache):
            raise Refusal("no profile cache %s (start the Medium profile once by hand and copy its files there)" % self.profile_cache)
        modsets = ModSets(self.ksp, self.ksp_running, self.mod_folders)
        if modsets.pending():
            raise Refusal("an earlier run left mod folders in %s; run: atmo_series.py modset-restore" % modsets.store)

        stamp = time.strftime("%Y%m%d-%H%M%S", time.localtime(self.now()))
        out_dir = os.path.join(self.repo, "build", "atmo-series")
        os.makedirs(out_dir, exist_ok=True)
        self.configuration = defn.get("configuration", "Debug")
        self.log("build: configuration %s" % self.configuration)
        code = self.build()
        if code != 0:
            raise Refusal("the %s build with deploy failed (exit %s)" % (self.configuration, code))
        if defn.get("bundle"):
            code = self.build_bundle()
            if code != 0:
                raise Refusal("the shader bundle build failed (exit %s); see build/atmospheres-bundle.log" % code)
        store = tempfile.mkdtemp(prefix="atmo-snap-")
        snap = self.snapshot_plan(store)
        snap.take()
        report = {"series": name, "time": stamp, "configuration": self.configuration, "starts": [], "warnings": [], "hashes": [],
                  "restore_mismatches": [], "notes": []}
        all_results, verdicts, failures, texts = [], {}, [], {}
        try:
            report["hashes"] = self.verify_hashes(require_bundle=bool(defn.get("bundle")))
            apply_profile(self.profile_cache, self.ksp)
            for line in apply_settings(self.ksp, defn.get("settings")):
                self.log("pinned %s" % line)
            # The files the starts pin on their own, as the series-wide pins left them: every start begins from this state, so that a
            # key one start pinned does not stay in the file for the next.
            own_files = sorted({e["file"] for s in starts for e in s.get("settings", [])})
            base_files = {}
            for rel in own_files:
                path = os.path.join(self.ksp, *rel.split("/"))
                if os.path.isfile(path):
                    with open(path, "rb") as f:
                        base_files[rel] = f.read()
            self.vm.begin()
            try:
                for st in starts:
                    info = {"id": st["id"], "how": None, "exit": None, "seconds": None}
                    report["starts"].append(info)
                    try:
                        for rel, data in base_files.items():
                            with open(os.path.join(self.ksp, *rel.split("/")), "wb") as f:
                                f.write(data)
                        for line in apply_settings(self.ksp, st.get("settings")):
                            self.log("pinned for %s: %s" % (st["id"], line))
                        modsets.disable(st.get("disableMods", []))
                        removed = clean_side_files(self.ksp)
                        need = defn.get("freeMemoryGb", 12)
                        free = self.free_gb()
                        if free < need:
                            raise Refusal("only %.1f GB of memory are free (%g needed); close Rider, OneDrive, Toolbox, Edge" % (free, need))
                        args = plan_args(name, st)
                        timeout = min(defn.get("maxMinutes", MAX_MINUTES), MAX_MINUTES) * 60
                        ksp_log = os.path.join(self.ksp, "KSP.log")
                        if os.path.exists(ksp_log):
                            os.remove(ksp_log)
                        self.record_start(name, st["id"], "series")
                        t0 = time.time()
                        self.log("start %s: %d side files removed; KSP up to %d s" % (st["id"], removed, timeout))
                        how, exit_code = self.launch(args, timeout, ksp_log)
                        info.update({"how": how, "exit": exit_code, "seconds": round(time.time() - t0, 1)})
                        text = ""
                        if os.path.exists(ksp_log):
                            with open(ksp_log, "r", encoding="utf-8", errors="replace") as f:
                                text = f.read()
                            shutil.copy2(ksp_log, os.path.join(out_dir, "%s-%s-%s.log" % (name, stamp, st["id"])))
                        texts[st["id"]] = text
                        results, warnings, verdict = parse_log(text, name)
                        for r in results:
                            r["start"] = st["id"]
                        all_results += results
                        verdicts[st["id"]] = verdict
                        report["warnings"] += ["%s: %s" % (st["id"], w) for w in warnings]
                        if how.startswith("timeout") or how.startswith("aborted"):
                            failures.append(("start@" + st["id"], how))
                        self.collect(defn, report)
                    finally:
                        modsets.restore()
            finally:
                self.vm.end()
        except Refusal as e:
            failures.append(("run", str(e).splitlines()[0]))
            report["notes"].append(str(e))
        finally:
            try:
                modsets.restore()  # before the side files, so that nothing is written into a folder that is still away
            except Refusal as e:
                failures.append(("modset", str(e).splitlines()[0]))
            bad = snap.restore()
            report["restore_mismatches"] = bad
            shutil.rmtree(store, ignore_errors=True)
            if bad:
                failures.append(("restore", "not equal to the original: " + ", ".join(bad)))
        measured, measure_notes, measure_failures = metric_results(self.repo, defn, texts) if texts else ([], [], [])
        all_results += measured
        failures += measure_failures
        report["notes"] += measure_notes
        report["measures"] = measured
        table, ok = evaluate(defn, all_results, {s["id"]: verdicts.get(s["id"]) for s in starts if s["id"] in verdicts} if verdicts else {}, failures,
                             ran=[s["id"] for s in starts])
        listed = {r["metric"] for r in defn.get("rows", [])}
        unlisted = {r["metric"] for r in all_results if r["metric"] not in listed}
        report.update({"table": table, "ok": ok and not failures, "unlisted": sorted(unlisted)})
        with open(os.path.join(out_dir, "%s-%s.json" % (name, stamp)), "w", encoding="utf-8") as f:
            json.dump(report, f, indent=1)
        return report

    def collect(self, defn, report):
        for item in defn.get("collect", []):
            src = os.path.join(self.ksp, *item["from"].split("/"))
            # Paths under .local/ belong to the main repository (the worktrees have none); everything else is the worktree's.
            base = self.mainrepo if item["to"].startswith(".local/") else self.repo
            dst = os.path.join(base, *item["to"].split("/"))
            if not os.path.exists(src):
                if not item.get("optional"):
                    report["warnings"].append("collect: %s is missing" % item["from"])
                continue
            collected = report.setdefault("collected", [])
            if os.path.isdir(src):
                for rel in tree_files(src):
                    target = os.path.join(dst, *rel.split("/"))
                    try:
                        os.makedirs(os.path.dirname(target), exist_ok=True)
                        shutil.copy2(os.path.join(src, *rel.split("/")), target)
                    except OSError as e:
                        report["warnings"].append("collect: %s could not be copied to %s (%s)" % (rel, item["to"], e))
                        continue
                    collected.append({"file": rel, "to": item["to"], "bytes": os.path.getsize(target)})
            else:
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy2(src, dst)
                collected.append({"file": os.path.basename(dst), "to": os.path.dirname(item["to"]) or ".", "bytes": os.path.getsize(dst)})


# ---------------------------------------------------------------- commands

def cmd_validate(args):
    repo = os.path.abspath(args.worktree)
    names = [args.series] if args.series else sorted(f[:-5] for f in os.listdir(os.path.join(repo, "tests", "series")) if re.match(r"K-.*\.json$", f))
    bad = 0
    for name in names:
        try:
            defn, errors = load_series(repo, name)
        except Refusal as e:
            errors = [str(e)]
        print("%-8s %s" % (name, "ok (%d rows)" % len(defn.get("rows", [])) if not errors else "%d error(s)" % len(errors)))
        for e in errors:
            print("   " + e)
        bad += bool(errors)
    return 1 if bad else 0


def collected_lines(collected):
    """The files the run copied out of the game (the pictures of K-T2 among them): one summary line per target folder, then every file with its size."""
    lines = []
    for to in sorted({c["to"] for c in collected}):
        files = sorted({c["file"]: c["bytes"] for c in collected if c["to"] == to}.items())
        lines.append("collected: %d file(s), %.1f MB into %s" % (len(files), sum(b for _f, b in files) / 1048576.0, to))
        for f, b in files:
            lines.append("  %s (%.0f KB)" % (f, b / 1024.0))
    return lines


def print_report(report, defn):
    print(format_table(report["series"], report["table"], report.get("unlisted", ())))
    if report.get("configuration"):
        print("build: %s" % report["configuration"])
    for w in report["warnings"]:
        print("warning: " + w)
    for n in report.get("notes", []):
        print("note: " + n)
    for line in collected_lines(report.get("collected", [])):
        print(line)


def cmd_run(args):
    repo = os.path.abspath(args.worktree)
    defn, errors = load_series(repo, args.series)
    if errors:
        print("The series definition is not valid:")
        for e in errors:
            print("   " + e)
        return 2
    if args.dry:
        if not args.log:
            print("--dry needs --log <recorded KSP.log>")
            return 2
        with open(args.log, "r", encoding="utf-8", errors="replace") as f:
            log_text = f.read()
        results, warnings, verdict = parse_log(log_text, defn["name"])
        sid = args.start or defn["starts"][0]["id"]
        for r in results:
            r["start"] = sid
        measured, notes, measure_failures = metric_results(repo, defn, {sid: log_text})
        results += measured
        table, ok = evaluate(defn, results, {sid: verdict}, measure_failures, ran=[sid])
        listed = {r["metric"] for r in defn.get("rows", [])}
        print_report({"series": defn["name"], "table": table, "warnings": ["dry: " + w for w in warnings], "notes": notes,
                      "unlisted": sorted({r["metric"] for r in results if r["metric"] not in listed})}, defn)
        return 0 if ok else 1
    ksp = args.ksp or os.environ.get("KspRoot") or DEFAULT_KSP
    mainrepo = gpu_lock.main_repo()
    who = args.who or "series-" + defn["name"]
    # Before anything is touched (lock, build, deploy): the offline preflight; any FAIL refuses the run.
    import atmo_preflight
    results = atmo_preflight.run_preflight(repo, defn["name"], args.start, ksp, mainrepo, who, dotnet=_dotnet())
    if not atmo_preflight.preflight_ok(results):
        print(atmo_preflight.format_preflight(defn["name"], results))
        print("REFUSED: the preflight has FAIL(s); nothing was touched")
        return 2
    print(atmo_preflight.format_preflight(defn["name"], results))
    minutes = min(defn.get("maxMinutes", MAX_MINUTES), MAX_MINUTES) * len([s for s in defn["starts"] if args.start in (None, s["id"])]) + 15
    path = gpu_lock.lock_path("gpu")
    info = gpu_lock.wait_acquire(path, who, minutes, defn.get("exclusive", False), "gpu", os.getpid(), "atmo_series run " + defn["name"])
    try:
        runner = Runner(repo, ksp, mainrepo, who, DEFAULT_DOTNET if os.path.exists(DEFAULT_DOTNET) else "dotnet")
        report = runner.execute(defn, args.start, args.owner_consent)
    except Refusal as e:
        print("REFUSED: %s" % e)
        return 2
    finally:
        gpu_lock.release(path, who, os.getpid(), info["since"])
    print_report(report, defn)
    return 0 if report["ok"] else 1


def _dotnet():
    return DEFAULT_DOTNET if os.path.exists(DEFAULT_DOTNET) else "dotnet"


def cmd_preflight(args):
    """Prints the offline preflight of a series (or one start); exit 0 when there is no FAIL."""
    import atmo_preflight
    repo = os.path.abspath(args.worktree)
    ksp = args.ksp or os.environ.get("KspRoot") or DEFAULT_KSP
    mainrepo = gpu_lock.main_repo()
    results = atmo_preflight.run_preflight(repo, args.series, args.start, ksp, mainrepo, args.who or "series-" + args.series, dotnet=_dotnet())
    print(atmo_preflight.format_preflight(args.series, results))
    return 0 if atmo_preflight.preflight_ok(results) else 1


def cmd_modset_restore(args):
    ksp = args.ksp or os.environ.get("KspRoot") or DEFAULT_KSP
    try:
        back = ModSets(ksp, ksp_running_tasklist).restore()
    except Refusal as e:
        print("REFUSED: %s" % e)
        return 2
    print("put back: %s" % (", ".join(back) if back else "nothing"))
    return 0


# ---------------------------------------------------------------- self test

STUB_KSP = '''import os, sys, time
ksp = os.getcwd()
mode = os.environ.get("ATMO_STUB_MODE", "pass")
log = open(os.path.join(ksp, "KSP.log"), "w")
log.write("start %s\\n" % " ".join(sys.argv[1:]))
log.flush()
# the run changes what it must not leave behind
os.makedirs(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData"), exist_ok=True)
open(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "settings.cfg"), "w").write("raytracing = Off\\n")
open(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "checkpoint.bin"), "wb").write(b"run")
open(os.path.join(ksp, "saves", "ReDefinitionProbe", "persistent.sfs"), "w").write("changed by the run")
if mode == "hang":
    time.sleep(120)
for i in range(1, 21):
    log.write('ATMORESULT {"series":"K-test","metric":"m%d","value":%d}\\n' % (i, i))
log.write('ATMORESULT {"series":"K-test","metric":"broken","value":}\\n')
log.write('ATMORESULT {"series":"K-other","metric":"foreign","value":1}\\n')
log.write("Probe: PASSED\\n" if mode == "pass" else "Probe: FAILED\\n")
log.close()
'''


def _selftest():
    failures = []

    def check(name, ok, detail=""):
        print("selftest %-62s %s %s" % (name, "PASS" if ok else "FAIL", detail), flush=True)
        if not ok:
            failures.append(name)

    root = tempfile.mkdtemp(prefix="atmo series selftest ")  # a space in the path on purpose
    try:
        py = sys.executable
        repo = os.path.join(root, "work tree")
        ksp = os.path.join(root, "Kerbal Space Program")
        main = os.path.join(root, "main repo")
        os.makedirs(os.path.join(repo, "tests", "series", "rows"))
        os.makedirs(os.path.join(repo, "build"))
        shutil.copy2(os.path.join(DEFAULT_REPO, "tests", "series", "schema.json"), os.path.join(repo, "tests", "series", "schema.json"))
        stub = os.path.join(root, "stub_ksp.py")
        with open(stub, "w") as f:
            f.write(STUB_KSP)

        def write(path, text, binary=False):
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb" if binary else "w") as f:
                f.write(text)

        def fresh_ksp():
            shutil.rmtree(ksp, ignore_errors=True)
            for mod in ("EnvironmentalVisualEnhancements", "scatterer", "zzz_Deferred", "TUFX", "ParallaxContinued"):
                write(os.path.join(ksp, "GameData", mod, "mod.cfg"), mod)
            write(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "settings.cfg"), "raytracing = Light\nenabled = True\n")
            write(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "sim.chk"), b"\x00\x01side file", True)
            write(os.path.join(ksp, "GameData", "ReDefinition", "Main", "PluginData", "settings.cfg"), "graphicsProfile = High\n")
            write(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "Shaders", "atmospheres.shaders"), b"old bundle", True)
            write(os.path.join(ksp, "saves", "ReDefinitionProbe", "persistent.sfs"), "GAME { original }\n")
            write(os.path.join(ksp, "settings.cfg"), "SCREEN_RESOLUTION = 1920,1080\n")

        def fake_build(good=True):
            def build():
                for wt, game in BUILD_ARTIFACTS:
                    write(os.path.join(repo, *wt.split("/")), ("dll " + wt).encode(), True)
                    write(os.path.join(ksp, *game.split("/")), ("dll " + wt).encode() if good or "Atmospheres" not in wt else b"tampered", True)
                write(os.path.join(repo, *BUNDLE[0].split("/")), b"new bundle", True)
                return 0
            return build

        cache = os.path.join(main, ".local", "work", "profile-cache")
        write(os.path.join(cache, "GameData", "ReDefinition", "Main", "PluginData", "settings.cfg"), "graphicsProfile = Medium\n")
        write(os.path.join(cache, "GameData", "OtherMod", "PluginData", "set.cfg"), "medium\n")

        definition = {
            "name": "K-test", "owner": "A02", "description": "selftest", "plannedStarts": 1,
            "starts": [{"id": "run1"}, {"id": "run2", "disableMods": ["EVE", "Scatterer"]}],
            "collect": [{"from": "GameData/ReDefinition/Atmospheres/PluginData/checkpoint.bin", "to": "tests/data/test/checkpoint.bin"}],
            "rows": [{"metric": "m%d" % i, "min": i, "max": i} for i in range(1, 21)],
        }
        write(os.path.join(repo, "tests", "series", "K-test.json"), json.dumps(definition))
        write(os.path.join(repo, "tests", "series", "rows", "T01.json"), json.dumps({"package": "T01", "rows": [
            {"series": "K-test", "metric": "m1", "start": "run1", "min": 1}, {"series": "K-other", "metric": "zzz"}]}))

        # stand-in for KSP_x64: the real launcher with a stub in place of the exe
        def launcher(mode, grace=0.1):
            def launch(args, timeout, log_path):
                os.environ["ATMO_STUB_MODE"] = mode
                return launch_game(ksp, args, timeout, log_path, exe_prefix=[py, stub], grace=grace, poll=0.2)
            return launch

        class FakeVm:
            def __init__(self, fail=False):
                self.calls, self.fail = [], fail
            def __call__(self, command):
                self.calls.append(command)
                if self.fail and "Stop-VM" in command:
                    return 1, "access denied"
                return 0, "Running" if "State" in command else ""

        def runner(build=None, launch=None, vm=None, running=lambda: False, free=lambda: 40.0, build_bundle=None):
            vmc = VmControl("gameserver", vm or FakeVm(), log=lambda s: None)
            hooks = {
                "ksp_running": running, "build": build or fake_build(), "launch": launch or launcher("pass"), "vm": vmc, "free_gb": free,
                "starts_log": os.path.join(main, "starts.jsonl"), "profile_cache": cache}
            if build_bundle:
                hooks["build_bundle"] = build_bundle
            return Runner(repo, ksp, main, "selftest", hooks=hooks, log=lambda s: None)

        defn, errs = load_series(repo, "K-test")
        check("series definition and row file validate against the schema", not errs, "; ".join(errs[:3]))
        check("rows of a row file are merged by series name", any(r["metric"] == "m1" and r.get("start") == "run1" for r in defn["rows"])
              and not any(r["metric"] == "zzz" for r in defn["rows"]))
        write(os.path.join(repo, "tools", "metric_stub.py"),
              "NOTES = []\ndef series_results(texts):\n    NOTES.append('n=%d' % len(texts))\n    return [{'metric': 'calc', 'value': 2.5, 'variant': 'v'}, {'metric': 'none', 'value': None}]\n")
        got, got_notes, got_fail = metric_results(repo, {"metrics": [{"script": "tools/metric_stub.py"}]}, {"a": "x", "b": "y"})
        check("a metric script's lines join the results, a null value is left out, its notes come back",
              [r["metric"] for r in got] == ["calc"] and got_notes == ["n=2"] and not got_fail)
        _r, _n, got_fail = metric_results(repo, {"metrics": [{"script": "tools/missing_metric.py"}]}, {})
        check("a metric script that cannot run fails the run", len(got_fail) == 1)
        bad = dict(definition, maxMinutes=45)
        check("the schema rejects a start above 20 minutes", bool(validate_schema(bad, load_schema(repo), load_schema(repo))))
        for name in sorted(f[:-5] for f in os.listdir(os.path.join(DEFAULT_REPO, "tests", "series")) if re.match(r"K-.*\.json$", f)):
            _d, e = load_series(DEFAULT_REPO, name)
            check("repository series %s is valid" % name, not e, "; ".join(e[:2]))
        kt3, _e = load_series(DEFAULT_REPO, "K-T3")
        t18_rows = [r for r in kt3.get("rows", []) if r["metric"].startswith("T18.")]
        t18_start = next((s for s in kt3["starts"] if s["id"] == "t18"), None)
        check("K-T3 has a T18 section and its T18 rows are bound to a start that runs T18Probe",
              bool(t18_rows) and any(s["id"] == "T18" for s in kt3.get("sections", [])) and t18_start is not None
              and "T18Probe" in t18_start.get("probeArgs", []) and all(r.get("start") == "t18" and r.get("section") == "T18" for r in t18_rows))

        # 1. A full run: 20 sample lines parsed, the malformed one ignored with a warning, the foreign series ignored.
        fresh_ksp()
        before = {r: sha256(os.path.join(ksp, r)) for r in ["GameData/ReDefinition/Atmospheres/PluginData/settings.cfg", "GameData/ReDefinition/Atmospheres/PluginData/sim.chk",
                  "GameData/ReDefinition/Main/PluginData/settings.cfg", "saves/ReDefinitionProbe/persistent.sfs", "settings.cfg"]}
        vm = FakeVm()
        rep = runner(vm=vm).execute(defn)
        rows_ok = sum(1 for r in rep["table"] if r["section"] != "probe" and r["status"] == "PASS")
        check("20 sample result lines are parsed and judged", rows_ok == 21, "%d rows PASS" % rows_ok)  # 20 rows + the extra m1@run1 row
        check("a malformed JSON line is ignored with a warning", any("malformed" in w for w in rep["warnings"]), str(rep["warnings"][:1]))
        check("the series passes with every line and the probe verdict", rep["ok"], "")
        after = {r: sha256(os.path.join(ksp, r)) for r in before}
        check("side files and settings equal the originals byte for byte after the run", before == after and not rep["restore_mismatches"])
        check("the file the series collects is in the worktree", os.path.isfile(os.path.join(repo, "tests", "data", "test", "checkpoint.bin")))
        check("the collected files are listed in the report and printed", any(c["file"] == "checkpoint.bin" for c in rep.get("collected", []))
              and any("checkpoint.bin" in line for line in collected_lines(rep.get("collected", []))))
        _r, probe_warnings, _v = parse_log("12:00 [ReDefinition] Probe: warning capture_failed: ground plain natural: the read gave no picture\n", "K-test")
        check("a probe warning line is a warning of the report with its name and detail", probe_warnings == ["probe warning capture_failed: ground plain natural: the read gave no picture"])
        pictures = os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "series-pictures")
        write(os.path.join(pictures, "t09-own1707-ground-off-1707x960-natural.png"), b"png", True)
        write(os.path.join(pictures, "t09-own1707-ground-plain-1707x960-aid-opticaldepth-half.png"), b"png2", True)
        pic_report = {"warnings": []}
        runner().collect({"collect": [{"from": "GameData/ReDefinition/Atmospheres/PluginData/series-pictures", "to": ".local/work/series-pictures/K-test", "optional": True}]}, pic_report)
        picture_lines = collected_lines(pic_report["collected"])
        check("a pictures folder is copied next to the logs and every picture is listed with its target",
              os.path.isfile(os.path.join(main, ".local", "work", "series-pictures", "K-test", "t09-own1707-ground-off-1707x960-natural.png"))
              and picture_lines[0].startswith("collected: 2 file(s)") and any("aid-opticaldepth-half.png" in line for line in picture_lines))
        shutil.rmtree(pictures)
        check("mod folders are back after a run with disabled mods", all(os.path.isdir(os.path.join(ksp, "GameData", m)) for m in ("EnvironmentalVisualEnhancements", "scatterer"))
              and not os.path.exists(os.path.join(ksp, DISABLED_DIR)))
        check("the VM was stopped and started again", any("Stop-VM" in c for c in vm.calls) and any("Start-VM" in c for c in vm.calls))
        check("the report file is written", any(f.startswith("K-test-") and f.endswith(".json") for f in os.listdir(os.path.join(repo, "build", "atmo-series"))))
        check("the report records the build configuration (Debug by default)", rep.get("configuration") == "Debug")
        release_command = build_command("wt", "ksp", "dotnet", "Release")
        check("the build command carries the configuration of the definition", release_command[release_command.index("-c") + 1] == "Release"
              and build_command("wt", "ksp", "dotnet")[release_command.index("-c") + 1] == "Debug")
        text = format_table("K-test", rep["table"])
        check("the table has the fixed columns", text.splitlines()[1].split() == ["metric", "value", "threshold", "result"])

        # 1b. Pinned settings: written after the profile, restored byte for byte, a missing key refuses.
        fresh_ksp()
        pins = [{"file": "GameData/ReDefinition/Main/PluginData/settings.cfg", "set": {"graphicsProfile": "High"}},
                {"file": "settings.cfg", "set": {"SCREEN_RESOLUTION": "1,1"}}]
        seen = []
        def pin_launch(args, timeout, log_path):
            seen.append((open(os.path.join(ksp, "GameData", "ReDefinition", "Main", "PluginData", "settings.cfg")).read(),
                         open(os.path.join(ksp, "settings.cfg")).read()))
            return launcher("pass")(args, timeout, log_path)
        d1b = json.loads(json.dumps(defn))
        d1b["settings"] = pins
        rep = runner(launch=pin_launch).execute(d1b)
        check("pinned settings are in the files while the game runs", seen and "graphicsProfile = High" in seen[0][0] and "SCREEN_RESOLUTION = 1,1" in seen[0][1])
        check("pinned settings are restored byte for byte", sha256(os.path.join(ksp, "settings.cfg")) == before["settings.cfg"]
              and sha256(os.path.join(ksp, "GameData/ReDefinition/Main/PluginData/settings.cfg")) == before["GameData/ReDefinition/Main/PluginData/settings.cfg"])
        # 1c. Per-start pins: merged over the series' pins, written per start (no key leaks into the next start), root settings.cfg
        # (CRLF, like KSP's own file) back byte for byte.
        root_cfg = b"SYNC_VBL = 1\r\nSCREEN_RESOLUTION_WIDTH = 2560\r\nSCREEN_RESOLUTION_HEIGHT = 1440\r\nFULLSCREEN = True\r\nEXTRA = 7\r\nTAIL = 1"
        fresh_ksp()
        write(os.path.join(ksp, "settings.cfg"), root_cfg, True)
        root_before = sha256(os.path.join(ksp, "settings.cfg"))
        win = lambda w, h: {"file": "settings.cfg", "set": {"SCREEN_RESOLUTION_WIDTH": w, "SCREEN_RESOLUTION_HEIGHT": h, "FULLSCREEN": False}}
        d1c = json.loads(json.dumps(defn))
        d1c["settings"] = [{"file": "settings.cfg", "set": {"SYNC_VBL": 0, "SCREEN_RESOLUTION_WIDTH": 1}}]
        d1c["starts"] = [dict(d1c["starts"][0], settings=[win(1707, 960), {"file": "settings.cfg", "set": {"EXTRA": 9}}]),
                         dict(d1c["starts"][1], settings=[win(2560, 1440)])]
        merged = effective_settings(d1c, d1c["starts"][0])
        check("per-start pins override and extend the series' pins", merged == [{"file": "settings.cfg", "set": {
              "SYNC_VBL": 0, "SCREEN_RESOLUTION_WIDTH": 1707, "SCREEN_RESOLUTION_HEIGHT": 960, "FULLSCREEN": False, "EXTRA": 9}}], str(merged))
        check("the series' own list is not changed by the merge", d1c["settings"][0]["set"] == {"SYNC_VBL": 0, "SCREEN_RESOLUTION_WIDTH": 1})
        seen_cfg = []
        def start_launch(args, timeout, log_path):
            with open(os.path.join(ksp, "settings.cfg"), "rb") as f:
                seen_cfg.append(f.read())
            return launcher("pass")(args, timeout, log_path)
        rep = runner(launch=start_launch).execute(d1c)
        first, second = (seen_cfg + [b"", b""])[:2]
        check("the first start runs with its own window size, the extra key and the series pin",
              b"SCREEN_RESOLUTION_WIDTH = 1707\r\n" in first and b"SCREEN_RESOLUTION_HEIGHT = 960\r\n" in first and b"FULLSCREEN = False\r\n" in first
              and b"EXTRA = 9\r\n" in first and b"SYNC_VBL = 0\r\n" in first)
        check("the second start has its own size and the first start's extra key is back to the original",
              b"SCREEN_RESOLUTION_WIDTH = 2560\r\n" in second and b"SCREEN_RESOLUTION_HEIGHT = 1440\r\n" in second and b"EXTRA = 7\r\n" in second
              and b"SYNC_VBL = 0\r\n" in second)
        check("the pinned files keep their CRLF line endings and the missing final newline", first.count(b"\r\n") == root_cfg.count(b"\r\n")
              and not first.endswith(b"\n"))
        check("the root settings.cfg is back byte for byte after a run with per-start pins", sha256(os.path.join(ksp, "settings.cfg")) == root_before
              and not rep["restore_mismatches"])
        d1c["starts"][0]["settings"] = [{"file": "settings.cfg", "set": {"NO_SUCH_KEY": 1}}]
        rep = runner(launch=start_launch).execute(d1c)
        check("a per-start pin of a missing key refuses the run and the file is still restored", not rep["ok"]
              and sha256(os.path.join(ksp, "settings.cfg")) == root_before)
        fresh_ksp()
        refused = ""
        try:
            apply_settings(ksp, [{"file": "settings.cfg", "set": {"NO_SUCH_KEY": 1}}])
        except Refusal as e:
            refused = str(e)
        check("a pinned key that is not in the file refuses", "no such key" in refused)
        check("the schema rejects a pinned file outside the snapshot", bool(validate_schema(dict(defn, settings=[{"file": "../x.cfg", "set": {}}]), load_schema(repo), load_schema(repo))))

        # 2. A missing expected line is a FAIL.
        d2 = json.loads(json.dumps(defn))
        d2["rows"].append({"metric": "never_emitted", "min": 0, "section": "main"})
        fresh_ksp()
        rep = runner().execute(d2)
        miss = [r for r in rep["table"] if r["metric"] == "never_emitted"]
        check("a missing expected line is a FAIL and fails the series", miss and miss[0]["status"] == "FAIL" and not rep["ok"])

        # 3. Probe FAILED fails the series.
        fresh_ksp()
        rep = runner(launch=launcher("fail")).execute(defn)
        check("a probe verdict FAILED fails the series", not rep["ok"] and any(r["metric"].startswith("Probe") and r["status"] == "FAIL" for r in rep["table"]))

        # 4. Timeout: the game is ended, the run reports it, files are restored.
        fresh_ksp()
        short = json.loads(json.dumps(defn))
        short["starts"] = [{"id": "run1"}]
        def hang_launch(args, timeout, log_path):
            os.environ["ATMO_STUB_MODE"] = "hang"
            return launch_game(ksp, args, 3, log_path, exe_prefix=[py, stub], grace=0.1, poll=0.2)
        rep = runner(launch=hang_launch).execute(short)
        check("a timeout is handled: reported as FAIL, side files restored", not rep["ok"] and any("timeout" in str(r["value"]) for r in rep["table"])
              and sha256(os.path.join(ksp, "saves/ReDefinitionProbe/persistent.sfs")) == before["saves/ReDefinitionProbe/persistent.sfs"])

        # 5. Hash mismatch refuses the run before the game starts.
        fresh_ksp()
        started = []
        rep = runner(build=fake_build(good=False), launch=lambda a, t, p: started.append(1) or ("quit", 0)).execute(defn)
        check("a hash mismatch refuses the run (no start)", not started and not rep["ok"] and any("not the worktree" in n for n in rep["notes"]))
        check("side files are restored after a refused run", sha256(os.path.join(ksp, "settings.cfg")) == before["settings.cfg"])

        # 5b. A series with "bundle": true builds the bundle first and refuses the run when the build fails or leaves none.
        fresh_ksp()
        d5 = json.loads(json.dumps(defn))
        d5["bundle"] = True
        built = []
        rep = runner(build_bundle=lambda: built.append(1) or 0).execute(d5)
        check("a bundle series calls the bundle build once after the build", len(built) == 1 and rep["ok"])
        fresh_ksp()
        started = []
        refused = ""
        try:
            runner(build_bundle=lambda: 1, launch=lambda a, t, p: started.append(1) or ("quit", 0)).execute(d5)
        except Refusal as e:
            refused = str(e)
        check("a failed bundle build refuses the run (no start)", not started and "bundle build failed" in refused)
        fresh_ksp()
        os.remove(os.path.join(repo, *BUNDLE[0].split("/")))
        os.remove(os.path.join(ksp, *BUNDLE[1].split("/")))
        started = []

        def build_without_bundle():
            for wt, game in BUILD_ARTIFACTS:
                write(os.path.join(repo, *wt.split("/")), ("dll " + wt).encode(), True)
                write(os.path.join(ksp, *game.split("/")), ("dll " + wt).encode(), True)
            return 0
        rep = runner(build=build_without_bundle, build_bundle=lambda: 0, launch=lambda a, t, p: started.append(1) or ("quit", 0)).execute(d5)
        check("no bundle anywhere refuses the run (no start)", not started and not rep["ok"] and any("no shader bundle" in n for n in rep["notes"]))

        # 6. A killed game (crash mid-run) and an exception: files and mod folders are restored.
        fresh_ksp()
        def crash_launch(args, timeout, log_path):
            write(os.path.join(ksp, "saves", "ReDefinitionProbe", "persistent.sfs"), "half written")
            write(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "new side file.bin"), b"x", True)
            raise RuntimeError("boom")
        try:
            runner(launch=crash_launch).execute(defn)
            raised = False
        except RuntimeError:
            raised = True
        check("an exception mid-run propagates", raised)
        check("side files are restored after the exception", sha256(os.path.join(ksp, "saves/ReDefinitionProbe/persistent.sfs")) == before["saves/ReDefinitionProbe/persistent.sfs"]
              and not os.path.exists(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "new side file.bin"))
              and os.path.exists(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "sim.chk")))
        # the exception came in start run1; run2 (with the disabled mods) never began. Provoke it in run2.
        fresh_ksp()
        calls = {"n": 0}
        def crash_second(args, timeout, log_path):
            calls["n"] += 1
            if calls["n"] == 2:
                assert not os.path.isdir(os.path.join(ksp, "GameData", "scatterer")), "mods should be away during run2"
                raise RuntimeError("boom in run2")
            return launcher("pass")(args, timeout, log_path)
        try:
            runner(launch=crash_second).execute(defn)
        except RuntimeError:
            pass
        check("mod folders are renamed back after an exception", os.path.isdir(os.path.join(ksp, "GameData", "scatterer"))
              and os.path.isdir(os.path.join(ksp, "GameData", "EnvironmentalVisualEnhancements")) and not os.path.exists(os.path.join(ksp, DISABLED_DIR)))

        # 7. A deploy is refused while the stand-in KSP runs.
        fresh_ksp()
        standin = subprocess.Popen([py, "-c", "import time; time.sleep(60)"])
        try:
            started = []
            rep_err = None
            try:
                runner(running=lambda: gpu_lock.pid_alive(standin.pid), build=lambda: started.append("build") or 0).execute(defn)
            except Refusal as e:
                rep_err = str(e)
            check("a deploy is refused while the stand-in KSP runs", rep_err is not None and "runs" in rep_err and not started)
            mods = ModSets(ksp, lambda: gpu_lock.pid_alive(standin.pid))
            try:
                mods.disable(["EVE"])
                refused = False
            except Refusal:
                refused = True
            check("mod folders are not moved while the stand-in KSP runs", refused and os.path.isdir(os.path.join(ksp, "GameData", "EnvironmentalVisualEnhancements")))
        finally:
            standin.kill()
            standin.wait()
        check("tasklist output is read for KSP_x64.exe", tasklist_has_ksp("KSP_x64.exe   1234 Console") and not tasklist_has_ksp("INFO: No tasks are running"))

        # 8. A failing VM command does not stop the restore.
        fresh_ksp()
        rep = runner(vm=FakeVm(fail=True)).execute(defn)
        check("a failing VM stop does not stop the run or the restore", rep["ok"] and sha256(os.path.join(ksp, "settings.cfg")) == before["settings.cfg"])

        # 9. Memory below the limit refuses the start and still restores.
        fresh_ksp()
        started = []
        vm = FakeVm()
        rep = runner(free=lambda: 8.0, vm=vm, launch=lambda a, t, p: started.append(1) or ("quit", 0)).execute(defn)
        check("less than 12 GB free refuses the start", not started and not rep["ok"] and any("GB" in n for n in rep["notes"]))
        check("the VM is started again after a refused start", any("Start-VM" in c for c in vm.calls))

        # 10. Run cap.
        fresh_ksp()
        with open(os.path.join(main, "starts.jsonl"), "w") as f:
            for _ in range(RUN_CAP):
                f.write(json.dumps({"counted": True}) + "\n")
        try:
            runner().execute(defn)
            capped = False
        except Refusal:
            capped = True
        check("a start above the run cap is refused without consent", capped)
        os.remove(os.path.join(main, "starts.jsonl"))

        # 11. The dry run validates against a recorded log.
        sample = os.path.join(root, "recorded.log")
        with open(sample, "w") as f:
            f.write("".join('noise\nATMORESULT {"series":"K-test","metric":"m%d","value":%d}\n' % (i, i) for i in range(1, 21)) + "Probe: PASSED\n")
        class A: pass
        a = A()
        a.worktree, a.series, a.dry, a.log, a.start, a.ksp, a.who, a.owner_consent = repo, "K-test", True, sample, "run1", None, None, False
        import io, contextlib
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = cmd_run(a)
        check("--dry judges a recorded log with the same table", code == 0 and "RESULT: PASS" in buf.getvalue(), buf.getvalue().splitlines()[-1])

        # 12. Aggregates and thresholds.
        r = evaluate_row({"metric": "x", "agg": "max", "max": 3}, [{"metric": "x", "value": 2}, {"metric": "x", "value": 5}])
        check("an aggregate and a threshold are judged", r[1] == "FAIL")
        r = evaluate_row({"metric": "x", "equals": 1.0, "tolerance": 0.1}, [{"metric": "x", "value": 1.05}])
        check("equals with a tolerance passes inside it", r[1] == "PASS")
        r = evaluate_row({"metric": "x", "required": False}, [])
        check("a missing optional line is SKIP", r[1] == "SKIP")

        # 13. The offline preflight (tools/atmo_preflight.py): every check with a failing and a passing fixture.
        import atmo_preflight
        atmo_preflight.selftest(check)

        # 14. apply_profile never injects KSPCF texture cache files; the loader guard ends a lost load.
        pcache = os.path.join(root, "pc14")
        pksp = os.path.join(root, "ksp14")
        tcrel = "GameData/KSPCommunityFixes/PluginData/TextureCache"
        for rel in (tcrel + "/textureData.json", tcrel + "/12345", "GameData/Other/a.cfg"):
            os.makedirs(os.path.dirname(os.path.join(pcache, *rel.split("/"))), exist_ok=True)
            with open(os.path.join(pcache, *rel.split("/")), "w") as f:
                f.write("x")
        with contextlib.redirect_stdout(io.StringIO()) as out14:
            skipped = apply_profile(pcache, pksp)
        check("apply_profile skips the KSPCF texture cache files and logs them",
              len(skipped) == 2 and not os.path.exists(os.path.join(pksp, *tcrel.split("/"))) and "skipped" in out14.getvalue()
              and os.path.isfile(os.path.join(pksp, "GameData", "Other", "a.cfg")))
        g = atmo_logguard.LogGuard(0)
        check("loader guard: 20 LOAD FAILED lines pass, 21 abort",
              g.feed_text("LOAD FAILED x\n" * 20, 1) is None and g.feed_text("LOAD FAILED x\n", 2) is not None)
        g = atmo_logguard.LogGuard(0)
        check("loader guard: a healthy load passes", g.feed_text("[LOG] a\n[LOG] b\n", 5) is None and g.tick(100) is None)
        g = atmo_logguard.LogGuard(0)
        g.feed_text("[LOG] a\n", 200)
        check("loader guard: 240 s of loading with 60 s silence aborts", g.tick(250) is None and g.tick(260) is None and g.tick(261) is not None)
        g = atmo_logguard.LogGuard(0)
        g.feed_text("Scene Change : From LOADING to MAINMENU\n", 10)
        check("loader guard: silence after the loading scene does not abort", g.tick(1000) is None)
        script = os.path.join(root, "fake_ksp.py")
        flog = os.path.join(root, "fake.log")
        with open(script, "w") as f:
            f.write("import sys,time\nwith open(sys.argv[1],'w') as g:\n    for i in range(50):\n        g.write('LOAD FAILED : x\\n'); g.flush()\n"
                    "time.sleep(60)\n")
        t14 = time.time()
        how, code = launch_game(root, [flog], 55, flog, exe_prefix=[sys.executable, script], poll=0.1)
        check("launch_game kills the game on the guard and says why", how.startswith("aborted: ") and time.time() - t14 < 30, how)
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print("selftest: %s" % ("PASSED" if not failures else "FAILED (%s)" % ", ".join(failures)))
    return 0 if not failures else 1


def main(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worktree", default=DEFAULT_REPO, help="the worktree to build and read series from (default: this script's)")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("run")
    s.add_argument("series")
    s.add_argument("--start")
    s.add_argument("--who")
    s.add_argument("--ksp")
    s.add_argument("--dry", action="store_true")
    s.add_argument("--log")
    s.add_argument("--owner-consent", action="store_true", help="the owner agreed to starts above the run cap")
    s = sub.add_parser("preflight")
    s.add_argument("series")
    s.add_argument("--start")
    s.add_argument("--who")
    s.add_argument("--ksp")
    s = sub.add_parser("validate")
    s.add_argument("series", nargs="?")
    s = sub.add_parser("modset-restore")
    s.add_argument("--ksp")
    sub.add_parser("selftest")
    args = p.parse_args(argv)
    if args.cmd == "selftest":
        return _selftest()
    if args.cmd == "validate":
        return cmd_validate(args)
    if args.cmd == "preflight":
        return cmd_preflight(args)
    if args.cmd == "modset-restore":
        return cmd_modset_restore(args)
    return cmd_run(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
