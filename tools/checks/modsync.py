#!/usr/bin/env python3
"""MODSYNC: which mods of the owner's KSP install have a newer release, what the new release changes in the code ReDefinition
relies on, and the deterministic install of an update. Stdlib only.

  python tools/checks/modsync.py inventory [--json FILE]
  python tools/checks/modsync.py check [--refresh] [--offline] [--json FILE]
  python tools/checks/modsync.py review <mod> [--old REF] [--new REF] [--no-fetch] [--out FILE]
  python tools/checks/modsync.py install <mod> [--dry-run] [--report FILE] [--series NAME]
  python tools/checks/modsync.py selftest

inventory  every folder of GameData with its installed version (.version file, assembly file version, CKAN registry), where it
           came from (CKAN or by hand), how ReDefinition uses it (direct, indirect, ours) and how many catalogue entries rest on it.
check      the latest upstream release of each folder: CKAN metadata (KSP-CKAN/CKAN-meta, one 5 MB archive), the GitHub latest
           release, the mod's own remote version file and SpaceDock, next to the installed version. Exit code 2 where a mod
           that ReDefinition uses directly has a newer release. tools/checks/check_mod_updates.py (the version each mod was last
           checked against) stays as it was; this adds the installed side and the review.
review     for one mod: the commits and the diff between the installed and the new version in its local clone
           (References\\KspUpscaler), restricted to what ReDefinition names: the types, methods, fields and literals of
           src/**/*Assumptions*.cs and the other catalogue entries, the bundled registration of the mod and the reflection
           lookups of our code. Per name: where it is in the old and the new source and which changed lines carry it.
install    no network text but the mod's repository in tools/checks/mod_updates.json decides what is downloaded: the version is the
           latest stable release check_mod_updates.py reports, the file is a release asset of that repository whose name matches
           modsync_mods.json. It runs only where the review report .local/work/modsync/<mod>-<version>.json says "safe", the
           branch of that report builds and passes its unit tests, KSP_x64 is not running and no GPU or Unity lock is held. It
           backs the folder up to .local/archive/modsync/<mod>-<oldversion>/, installs, runs the IL fingerprint check and the
           series preflight, and puts the backup back where a new FAIL shows. Closed-source and Patreon mods are never fetched.

Nothing sent to a server carries anything of the owner: the user agent is generic, no path or name leaves the machine.
GITHUB_TOKEN / GH_TOKEN in the environment lifts GitHub's limit of 60 API requests an hour; without one the check falls back to
the release redirect, which has no limit.
"""
import argparse
import datetime
import fnmatch
import hashlib
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
REPO = os.path.dirname(TOOLS)
sys.path.insert(0, HERE)
sys.path.insert(0, TOOLS)
import check_mod_updates as cmu  # noqa: E402

DEFAULT_KSP = r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program"
DEFAULT_REFS = r"C:\Users\franz\IdeaProjects\References\KspUpscaler"
USER_AGENT = "modsync-tool"
CKAN_META_URL = "https://github.com/KSP-CKAN/CKAN-meta/archive/master.tar.gz"
CACHE_HOURS = 6
REPORT_DIR = os.path.join(".local", "work", "modsync")
ARCHIVE_DIR = os.path.join(".local", "archive", "modsync")
SAFE_BRANCH = re.compile(r"^claude/mod-update-[A-Za-z0-9._-]+$")


def ksp_root():
    return os.environ.get("KspRoot") or DEFAULT_KSP


def refs_root():
    return os.environ.get("MODSYNC_REFS") or DEFAULT_REFS


def load_config():
    with open(os.path.join(HERE, "modsync_mods.json"), encoding="utf-8") as f:
        return json.load(f)


def load_mod_updates():
    with open(os.path.join(HERE, "mod_updates.json"), encoding="utf-8") as f:
        return json.load(f)


def slug(name):
    """The mod's id in file names: its name in mod_updates.json, lower case, other characters as dashes."""
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


# ---------------------------------------------------------------------------------------------------------------- versions

def _segments(text):
    return re.findall(r"\d+|[A-Za-z]+", text)


def normalise(text):
    """(epoch, segments) of a version as CKAN writes it: 'epoch:version', a leading v dropped, trailing zero segments dropped."""
    text = (text or "").strip()
    epoch = 0
    match = re.match(r"^(\d+):(.*)$", text)
    if match:
        epoch, text = int(match.group(1)), match.group(2)
    text = re.sub(r"^\D+(?=\d)", "", text)  # 'v', 'release-', 'RELEASE/', 'dev-' before the first digit
    parts = [int(s) if s.isdigit() else s.lower() for s in _segments(text)]
    while len(parts) > 1 and parts[-1] == 0:
        parts.pop()
    return epoch, parts


def compare(a, b, use_epoch=False):
    """-1, 0 or 1. Numbers compare as numbers, words as words, a number before a word, a missing segment as zero. The epoch
    of a CKAN version counts only where use_epoch is set (versions of one CKAN identifier): another source has none."""
    ea, pa = normalise(a)
    eb, pb = normalise(b)
    if use_epoch and ea != eb:
        return -1 if ea < eb else 1
    for i in range(max(len(pa), len(pb))):
        # Where one ends, a word on the other side (alpha, bleeding, rc) marks a pre-release: the shorter one is the higher.
        if i >= len(pa) and isinstance(pb[i], str):
            return 1
        if i >= len(pb) and isinstance(pa[i], str):
            return -1
        x = pa[i] if i < len(pa) else 0
        y = pb[i] if i < len(pb) else 0
        if x == y:
            continue
        if isinstance(x, int) and isinstance(y, int):
            return -1 if x < y else 1
        if isinstance(x, int):
            return -1
        if isinstance(y, int):
            return 1
        return -1 if x < y else 1
    return 0


def avc_version(value):
    """A .version file's VERSION: a string or {MAJOR, MINOR, PATCH, BUILD}, as dotted text."""
    if isinstance(value, dict):
        keys = ("MAJOR", "MINOR", "PATCH", "BUILD")
        return ".".join(str(value[k]) for k in keys if k in value)
    return str(value) if value is not None else None


def read_json_lenient(raw):
    text = raw.decode("utf-8-sig", errors="replace") if isinstance(raw, bytes) else raw
    return json.loads(text)


def pe_versions(path):
    """(FileVersion, ProductVersion) of a Windows binary, read from its version resource without Windows; None where absent."""
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError:
        return None, None
    return _version_strings(data)


def _version_strings(data):
    out = []
    for key in ("FileVersion", "ProductVersion"):
        marker = key.encode("utf-16-le") + b"\x00\x00"
        index = data.find(marker)
        value = None
        if index >= 0:
            pos = index + len(marker)
            while pos + 1 < len(data) and data[pos:pos + 2] == b"\x00\x00":
                pos += 2
            end = pos
            while end + 1 < len(data) and data[end:end + 2] != b"\x00\x00":
                end += 2
            value = data[pos:end].decode("utf-16-le", errors="replace").strip() or None
            if value is not None and not re.match(r"^\d", value):
                value = None  # the marker was found in other data
        out.append(value)
    return out[0], out[1]


# ------------------------------------------------------------------------------------------------------------------- network

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def cache_dir():
    path = os.path.join(REPO, ".local", "cache", "modsync")
    os.makedirs(path, exist_ok=True)
    return path


def http(url, accept=None, timeout=60, method="GET"):
    request = urllib.request.Request(url, method=method, headers={"User-Agent": USER_AGENT})
    if accept:
        request.add_header("Accept", accept)
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token and urllib.parse.urlparse(url).hostname in ("api.github.com",):
        request.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def cached(name, producer, refresh=False, offline=False):
    """A cached result (JSON-able) under .local/cache/modsync; produced again after CACHE_HOURS or on refresh."""
    path = os.path.join(cache_dir(), re.sub(r"[^A-Za-z0-9._-]", "_", name) + ".json")
    if os.path.exists(path) and (offline or (not refresh and time.time() - os.path.getmtime(path) < CACHE_HOURS * 3600)):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    if offline:
        return None
    value = producer()
    with open(path, "w", encoding="utf-8") as f:
        json.dump(value, f)
    return value


def github_latest_redirect(repo):
    """The tag of a repository's latest stable release by the release redirect (no API, no limit); None where there is none."""
    opener = urllib.request.build_opener(NoRedirect)
    request = urllib.request.Request("https://github.com/%s/releases/latest" % repo, method="HEAD", headers={"User-Agent": USER_AGENT})
    try:
        opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        if error.code in (301, 302, 303, 307, 308):
            location = error.headers.get("Location", "")
            match = re.search(r"/releases/tag/(.+)$", location)
            return urllib.parse.unquote(match.group(1)) if match else None
        raise
    return None


def raw_url(url):
    """A github.com /blob/ link as the raw file."""
    match = re.match(r"^https://github\.com/([^/]+/[^/]+)/blob/(.+)$", url or "")
    return "https://raw.githubusercontent.com/%s/%s" % (match.group(1), match.group(2)) if match else url


def ckan_index(ids, refresh=False, offline=False):
    """{identifier: [slim module dict, ...]} for the wanted identifiers from the CKAN-meta archive."""
    key = "ckan-index-" + hashlib.sha1(",".join(sorted(ids)).encode()).hexdigest()[:12]

    def produce():
        data = http(CKAN_META_URL, timeout=180)
        return slim_ckan(data, set(ids))

    return cached(key, produce, refresh, offline) or {}


def slim_ckan(archive_bytes, ids):
    index = {}
    with tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:gz") as tar:
        for member in tar:
            if not member.isfile() or not member.name.endswith(".ckan"):
                continue
            parts = member.name.split("/")
            if len(parts) < 3 or parts[1] not in ids:
                continue
            try:
                module = read_json_lenient(tar.extractfile(member).read())
            except ValueError:
                continue
            index.setdefault(parts[1], []).append({
                "version": module.get("version"), "ksp_version": module.get("ksp_version"),
                "ksp_version_min": module.get("ksp_version_min"), "ksp_version_max": module.get("ksp_version_max"),
                "release_status": module.get("release_status", "stable"), "resources": module.get("resources", {}),
                "download": module.get("download"), "kind": module.get("kind", "package"),
            })
    return index


def ksp_compatible(module, ksp):
    """Whether a CKAN module (or an AVC file) is for this KSP version; where it names none, it counts as compatible."""
    def parse(text):
        if not text or text == "any":
            return None
        return [int(p) if p.isdigit() else 0 for p in text.split(".")]

    def le(a, b):
        for i in range(max(len(a), len(b))):
            x = a[i] if i < len(a) else 0
            y = b[i] if i < len(b) else 0
            if x != y:
                return x < y
        return True

    target = parse(ksp)
    exact = parse(module.get("ksp_version"))
    low = parse(module.get("ksp_version_min")) or exact
    high = parse(module.get("ksp_version_max")) or exact
    if low and not le(low, target):
        return False
    if high:
        cut = target[:len(high)]
        if not le(cut, high):
            return False
    return True


# ---------------------------------------------------------------------------------------------------------------- inventory

def load_registry(ksp):
    path = os.path.join(ksp, "CKAN", "registry.json")
    try:
        with open(path, encoding="utf-8") as f:
            reg = json.load(f)
    except (OSError, ValueError):
        return None
    modules = {}
    for ident, entry in reg.get("installed_modules", {}).items():
        src = entry.get("source_module", {})
        modules[ident] = {"version": src.get("version"), "resources": src.get("resources", {}), "installed": entry.get("install_time")}
    owners = {}
    for file_path, ident in reg.get("installed_files", {}).items():
        parts = file_path.replace("\\", "/").split("/")
        if len(parts) >= 2 and parts[0] == "GameData":
            owners.setdefault(parts[1], set()).add(ident)
    return {"modules": modules, "owners": owners, "mtime": os.path.getmtime(path)}


def find_files(folder, suffix, depth=4):
    found = []
    base = folder.rstrip(os.sep).count(os.sep)
    for dirpath, dirnames, filenames in os.walk(folder):
        if dirpath.count(os.sep) - base >= depth:
            dirnames[:] = []
        for name in filenames:
            if name.lower().endswith(suffix):
                found.append(os.path.join(dirpath, name))
    return sorted(found)


def scan_folder(ksp, folder, registry):
    """What is known of one GameData folder from the disk and the CKAN registry."""
    path = os.path.join(ksp, "GameData", folder)
    info = {"folder": folder, "avc": [], "dll": [], "ckan": [], "source": "manual"}
    if not os.path.isdir(path):
        info["source"] = "file"
        return info
    for avc in find_files(path, ".version"):
        try:
            with open(avc, "rb") as handle:
                data = read_json_lenient(handle.read())
        except (OSError, ValueError):
            continue
        github = data.get("GITHUB") or {}
        info["avc"].append({
            "file": os.path.relpath(avc, path).replace("\\", "/"), "name": data.get("NAME"), "version": avc_version(data.get("VERSION")),
            "url": data.get("URL"), "ksp_min": avc_version(data.get("KSP_VERSION_MIN")), "ksp_max": avc_version(data.get("KSP_VERSION_MAX")),
            "github": "%s/%s" % (github["USERNAME"], github["REPOSITORY"]) if github.get("USERNAME") and github.get("REPOSITORY") else None,
        })
    dlls = [d for d in find_files(path, ".dll", 5) if "ReDefinition" not in d]
    dlls.sort(key=lambda d: -os.path.getsize(d))
    for dll in dlls[:3]:
        file_version, product = pe_versions(dll)
        info["dll"].append({"file": os.path.relpath(dll, path).replace("\\", "/"), "file_version": file_version, "product_version": product})
    if registry and folder in registry["owners"]:
        info["source"] = "CKAN"
        for ident in sorted(registry["owners"][folder]):
            module = registry["modules"].get(ident, {})
            info["ckan"].append({"id": ident, "version": module.get("version"), "resources": module.get("resources", {})})
    info["newest_mtime"] = max([os.path.getmtime(os.path.join(d, n)) for d, _, ns in os.walk(path) for n in ns] or [0])
    return info


def meaningful(version):
    """Whether an assembly's version says anything: not 0.0.0.0 and not the 1.0.0.0 of a project that never set one."""
    return bool(version) and re.match(r"^\d+(\.\d+)*$", version) is not None and compare(version, "0.0.0.0") != 0 and compare(version, "1.0.0.0") != 0


def installed_candidates(info, folder_hint=None):
    """[(version, where)] of every reading of a folder's version: its version files, the assembly named like the folder (or the
    largest one) and the CKAN registry. Readings that disagree show the folder was changed outside one of them."""
    found = []
    for avc in info["avc"]:
        if avc["version"]:
            found.append((avc["version"], "version file " + avc["file"]))
    hint = (folder_hint or info["folder"]).lower().lstrip("0123456789_")
    for dll in info["dll"]:
        stem = os.path.splitext(os.path.basename(dll["file"]))[0].lower()
        if hint and (stem in hint or hint in stem) and meaningful(dll["file_version"]):
            found.append((dll["file_version"], "assembly " + dll["file"]))
            break
    for ck in info["ckan"]:
        if ck["version"]:
            found.append((ck["version"], "CKAN registry " + ck["id"]))
    return found


def installed_version(info, override=None, last_number=False):
    """The version to compare: a configured override (checked by hand), else the highest reading, for a stale reading
    is one nobody updated while the files changed. Returns (version, where)."""
    if override:
        return override["version"], "configured: " + override.get("evidence", "")
    best = None
    for version, where in installed_candidates(info):
        if best is None or compare(version if not last_number else last_of(version), best[0] if not last_number else last_of(best[0])) > 0:
            best = (version, where)
    return best if best else (None, None)


def last_of(version):
    numbers = re.findall(r"\d+", version or "")
    return numbers[-1] if numbers else "0"


def catalogue_counts(src_root=None):
    """{catalogue mod name: number of ForeignAssumption entries}."""
    counts = {}
    for entry in parse_catalogue(src_root or os.path.join(REPO, "src")):
        counts[entry["mod"]] = counts.get(entry["mod"], 0) + 1
    return counts


def inventory(ksp=None):
    ksp = ksp or ksp_root()
    config = load_config()
    registry = load_registry(ksp)
    counts = catalogue_counts()
    on_disk = {d for d in os.listdir(os.path.join(ksp, "GameData")) if os.path.isdir(os.path.join(ksp, "GameData", d))}
    # A configured folder that is not there (moved aside for a test, or removed) stays in the list as absent.
    folders = sorted(on_disk | {f for f, c in config["gamedata"].items() if c.get("use") == "direct"})
    tested = {m["name"]: m["tested"] for m in load_mod_updates()}
    rows = []
    for folder in folders:
        info = scan_folder(ksp, folder, registry)
        cfg = config["gamedata"].get(folder, {})
        version, where = installed_version(info, cfg.get("installed_override"), bool(cfg.get("last_number")))
        if folder not in on_disk:
            info["source"] = "absent"
            version, where = tested.get(cfg.get("updates_name")), "folder absent: the version last checked (mod_updates.json)"
        info.update({
            "label": cfg.get("label", folder), "use": cfg.get("use", "not ours"), "installed": version, "installed_from": where,
            "readings": installed_candidates(info), "last_number": bool(cfg.get("last_number")),
            "tested": tested.get(cfg.get("updates_name")), "no_feed": bool(cfg.get("no_feed")),
            "catalogue_entries": sum(counts.get(n, 0) for n in cfg.get("catalogue_mods", [])), "closed": bool(cfg.get("closed")),
            "repo": cfg.get("repo") or next((a["github"] for a in info["avc"] if a["github"]), None) or repo_of(info), "clone": cfg.get("clone"),
            "note": cfg.get("note", ""), "no_upstream": bool(cfg.get("no_upstream")),
        })
        rows.append(info)
    return {"ksp": ksp, "registry_mtime": registry["mtime"] if registry else None, "mods": rows}


def repo_of(info):
    for ck in info["ckan"]:
        match = re.match(r"^https://github\.com/([^/]+/[^/#?]+?)(?:\.git)?/?$", ck["resources"].get("repository", ""))
        if match:
            return match.group(1)
    return None


def print_inventory(inv):
    print("%-32s %-10s %-14s %-9s %-7s %s" % ("GameData folder", "use", "installed", "source", "entries", "note"))
    for row in inv["mods"]:
        print("%-32s %-10s %-14s %-9s %-7s %s" % (row["folder"], row["use"], row["installed"] or "?", row["source"], row["catalogue_entries"] or "", ("no release feed; " if row["no_feed"] else "") + row["note"]))
    if inv["registry_mtime"]:
        print("\nCKAN registry written %s; a folder CKAN does not list was put in by hand." % datetime.datetime.fromtimestamp(inv["registry_mtime"]).strftime("%Y-%m-%d"))


# ------------------------------------------------------------------------------------------------------------------- check

def upstream(row, ksp_version, refresh=False, offline=False, index=None):
    """The latest releases of one inventory row from each source: [{source, version, date, url, ksp_ok, note}]."""
    found = []
    ids = [c["id"] for c in row["ckan"]]
    if index:
        for ident in ids:
            modules = [m for m in index.get(ident, []) if m.get("kind", "package") == "package"]
            ok = [m for m in modules if ksp_compatible(m, ksp_version)]
            pool = ok or modules
            if pool:
                best = pool[0]
                for module in pool[1:]:
                    if compare(module["version"], best["version"], True) > 0:
                        best = module
                found.append({"source": "CKAN " + ident, "version": best["version"], "url": best["resources"].get("repository"), "ksp_ok": bool(ok), "release_status": best.get("release_status")})
    repo = row.get("repo")
    if row.get("no_upstream"):
        return found
    if repo and not offline:
        try:
            tag = cached("gh-latest-" + repo.replace("/", "_"), lambda: github_latest_redirect(repo), refresh, offline)
        except (urllib.error.URLError, OSError) as error:
            tag, found = None, found + [{"source": "GitHub", "error": str(error)}]
        if tag:
            found.append({"source": "GitHub release", "version": tag, "url": "https://github.com/%s/releases/tag/%s" % (repo, tag), "ksp_ok": True})
    for avc in row["avc"]:
        if avc["url"] and not offline:
            try:
                data = cached("avc-" + hashlib.sha1(avc["url"].encode()).hexdigest()[:12],
                              lambda: read_json_lenient(http(raw_url(avc["url"]), timeout=30)), refresh, offline)
            except (urllib.error.URLError, OSError, ValueError) as error:
                found.append({"source": "remote version file", "error": "%s: %s" % (avc["url"], error)})
                continue
            if data:
                # A hint only: the file of a branch can be ahead of every release.
                found.append({"source": "remote version file", "version": avc_version(data.get("VERSION")), "url": avc["url"], "ksp_ok": True, "hint": True})
    for ck in row["ckan"]:
        match = re.match(r"^https://spacedock\.info/mod/(\d+)", ck["resources"].get("spacedock", ""))
        if match and not offline:
            try:
                data = cached("spacedock-" + match.group(1), lambda: json.loads(http("https://spacedock.info/api/mod/" + match.group(1), timeout=30)), refresh, offline)
            except (urllib.error.URLError, OSError, ValueError) as error:
                found.append({"source": "SpaceDock", "error": str(error)})
                continue
            versions = (data or {}).get("versions") or []
            if versions:
                found.append({"source": "SpaceDock", "version": versions[0].get("friendly_version"), "url": ck["resources"]["spacedock"], "ksp_ok": True, "ksp_game": versions[0].get("game_version")})
            break
    return found


def newest(found, installed, last_number=False):
    """The highest version among the sources (errors left out) and whether it is newer than the installed one. With last_number
    only the last number of each version counts (Kopernicus: 'release-251' against '1.12.1.249')."""
    def key(v):
        return last_of(v) if last_number else v

    best = None
    releases = [e for e in found if e.get("version") and not e.get("hint")]
    for entry in releases or [e for e in found if e.get("version")]:
        if best is None or compare(key(entry["version"]), key(best["version"])) > 0:
            best = entry
    if best is None:
        return None, "unknown"
    if installed is None:
        return best, "unknown"
    order = compare(key(best["version"]), key(installed))
    return best, ("newer" if order > 0 else "same" if order == 0 else "older")


def check(refresh=False, offline=False, ksp=None):
    inv = inventory(ksp)
    config = load_config()
    ids = sorted({c["id"] for row in inv["mods"] for c in row["ckan"]})
    index = ckan_index(ids, refresh, offline) if ids else {}
    tested = {m["name"]: m for m in load_mod_updates()}
    results = []
    for row in inv["mods"]:
        if row["use"] in ("ours",):
            continue
        found = upstream(row, config["ksp_version"], refresh, offline, index)
        best, status = newest(found, row["installed"], row["last_number"])
        extra = {}
        if row["no_feed"]:
            # No release feed of the installed line: the releases found are of another line. What can be said is whether the owner
            # installed another build by hand (the installed version against the one last checked) and how far the public branch is ahead.
            hand = bool(row["tested"] and row["installed"] and row["source"] != "absent" and compare(row["installed"], row["tested"]) != 0)
            extra = {"changed_by_hand": hand, "tested": row["tested"], "public_ahead": public_ahead(config["gamedata"].get(row["folder"], {}), row["installed"])}
            status = "no feed"
        results.append({**{k: row[k] for k in ("folder", "label", "use", "installed", "installed_from", "readings", "source", "no_feed", "repo", "clone", "catalogue_entries")},
                        "latest": None if row["no_feed"] else best, "status": status, "sources": found, **extra})
    return {"date": datetime.date.today().isoformat(), "ksp": config["ksp_version"], "results": results, "tested": list(tested)}


def public_ahead(cfg, installed):
    """For a mod without a release feed: {commit of the installed version, branch head, commits ahead} in the local clone as it is
    (no fetch), or None where the clone or the version cannot be found."""
    clone = os.path.join(refs_root(), cfg["clone"]) if cfg.get("clone") else None
    source = cfg.get("source")
    if not clone or not source or not installed or not os.path.isdir(os.path.join(clone, ".git")):
        return None
    base = commit_of_version(clone, source, installed)
    if not base:
        return None
    count = git(clone, "rev-list", "--count", "%s..%s" % (base, source["branch"])).strip()
    head = git(clone, "rev-parse", "--short", source["branch"]).strip()
    return {"installed_commit": base, "head": head, "ahead": int(count) if count.isdigit() else None}


def commit_of_version(clone, source, version):
    """The first commit of a branch whose file holds the installed version, by the version file ('avc') or a source line ('needle')."""
    if source["kind"] == "avc":
        found = None
        for commit in git(clone, "log", source["branch"], "--format=%h", "--", source["file"]).split():
            text = git(clone, "show", "%s:%s" % (commit, source["file"]))
            try:
                v = avc_version(read_json_lenient(text).get("VERSION"))
            except ValueError:
                continue
            if v is not None and compare(v, version) == 0:
                found = commit
            elif found:
                break
        return found
    # needle: the version text as the source writes it (Scatterer: "0.908.0.0" in the assembly is "0.0908" in the source)
    digits = version.split(".")
    candidates = {version, "%s.%s" % (digits[0], digits[1].zfill(4)) if len(digits) > 1 else version}
    if len(digits) > 1 and digits[0] == "0" and len(digits[1]) == 3:
        candidates.add("0.0" + digits[1])
    for candidate in sorted(candidates):
        needle = source["needle_format"].replace("{v}", candidate)
        out = git(clone, "log", source["branch"], "--format=%h", "-S" + needle, "--", source["file"]).split()
        if out:
            return out[-1]
    return None


def print_check(result):
    print("%-32s %-9s %-14s %-9s %-26s %s" % ("GameData folder", "use", "installed", "status", "latest (source)", "note"))
    for r in result["results"]:
        latest = "%s (%s)" % (r["latest"]["version"], r["latest"]["source"]) if r["latest"] else "?"
        extra = ""
        if r["no_feed"]:
            latest = "-"
            ahead = r.get("public_ahead")
            extra = "no release feed; %s; public branch %s" % (
                "CHANGED BY HAND (checked against %s)" % r["tested"] if r.get("changed_by_hand") else "as last checked (%s)" % r["tested"],
                "%s commit(s) ahead (head %s)" % (ahead["ahead"], ahead["head"]) if ahead else "not readable")
        print("%-32s %-9s %-14s %-9s %-26s %s" % (r["folder"], r["use"], r["installed"] or "?", r["status"].upper() if r["status"] == "newer" else r["status"], latest[:26], extra))
    newer = [r for r in result["results"] if r["status"] == "newer"]
    direct = [r for r in newer if r["use"] == "direct"]
    hand = [r for r in result["results"] if r.get("changed_by_hand")]
    print("\n%d folder(s) with a newer release, %d of them used directly: %s" % (len(newer), len(direct), ", ".join(r["folder"] for r in direct) or "none"))
    if hand:
        print("installed by hand since the last check: %s" % ", ".join(r["folder"] for r in hand))


# ----------------------------------------------------------------------------------------------------------- the catalogue

STRING = r'"((?:[^"\\]|\\.)*)"'


def _read_balanced(text, start):
    """The index after the matching close of the parenthesis at text[start]; strings and chars are skipped."""
    depth = 0
    i = start
    while i < len(text):
        c = text[i]
        if c == '"':
            i += 1
            while i < len(text) and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return len(text)


def _split_args(text):
    args, depth, cur, i = [], 0, "", 0
    while i < len(text):
        c = text[i]
        if c == '"':
            j = i + 1
            while j < len(text) and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            cur += text[i:j + 1]
            i = j + 1
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        if c == "," and depth == 0:
            args.append(cur.strip())
            cur = ""
        else:
            cur += c
        i += 1
    if cur.strip():
        args.append(cur.strip())
    return args


def parse_catalogue(src_root):
    """Every `new ForeignAssumption(...)` chain under src_root: id, mod, uses, file and the names it relies on."""
    entries = []
    for dirpath, _, files in os.walk(src_root):
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            try:
                text = open(path, encoding="utf-8-sig", errors="replace").read()
            except OSError:
                continue
            if "new ForeignAssumption(" not in text:
                continue
            consts = dict(re.findall(r'const\s+string\s+(\w+)\s*=\s*"((?:[^"\\]|\\.)*)"', text))

            def resolve(arg):
                arg = arg.strip()
                m = re.fullmatch(STRING, arg)
                if m:
                    return m.group(1)
                return consts.get(arg.split(".")[-1])

            for m in re.finditer(r"new ForeignAssumption\(", text):
                open_at = m.end() - 1
                close = _read_balanced(text, open_at)
                args = _split_args(text[open_at + 1:close - 1])
                end = close
                while end < len(text) and text[end] != ";":
                    if text[end] == '"':
                        end += 1
                        while end < len(text) and text[end] != '"':
                            end += 2 if text[end] == "\\" else 1
                    end += 1
                chain = text[close:end]
                entry = {"id": resolve(args[0]) if args else None, "mod": resolve(args[1]) if len(args) > 1 else None,
                         "uses": resolve(args[2]) if len(args) > 2 else None, "file": os.path.relpath(path, REPO).replace("\\", "/"),
                         "types": [], "members": [], "literals": [], "tested": [], "pairs": []}
                for call in re.finditer(r"\.(Type|Method|Calls|Field|Literal|Tested|TestedVersions)\(", chain):
                    stop = _read_balanced(chain, call.end() - 1)
                    cargs = _split_args(chain[call.end():stop - 1])
                    kind = call.group(1)
                    if kind == "Type" and cargs:
                        entry["types"].append(resolve(cargs[0]))
                    elif kind in ("Method", "Calls", "Field") and len(cargs) >= 2:
                        entry["types"].append(resolve(cargs[0]))
                        entry["members"].append(resolve(cargs[1]))
                        entry["pairs"].append((resolve(cargs[0]), resolve(cargs[1]), "member"))
                    elif kind == "Literal" and cargs:
                        entry["types"].append(resolve(cargs[0]))
                        entry["literals"] += [resolve(a) for a in cargs[1:]]
                        entry["pairs"] += [(resolve(cargs[0]), resolve(a), "literal") for a in cargs[1:]]
                    elif kind in ("Tested", "TestedVersions"):
                        entry["tested"] += [resolve(a) for a in cargs]
                for key in ("types", "members", "literals", "tested"):
                    entry[key] = sorted({v for v in entry[key] if v})
                entries.append(entry)
    return entries


def registration_names(repo, mod_label):
    """Names the bundled registration of a mod reads: detect, needs and the member names of its settings."""
    names = set()
    folder = os.path.join(REPO, "GameData", "ReDefinition", "Main", "Mods")
    if not os.path.isdir(folder):
        return names
    for name in os.listdir(folder):
        text = open(os.path.join(folder, name), encoding="utf-8-sig", errors="replace").read()
        if mod_label.lower() not in name.lower():
            continue
        for key in ("detect", "needs", "type", "member", "field", "property"):
            for m in re.finditer(r"^\s*%s\s*=\s*(.+)$" % key, text, re.M):
                for part in re.split(r"[,\s]+", m.group(1).strip()):
                    if part and not part.startswith("/"):
                        names.add(part)
    return names


# Words that make a commit message worth reading for ReDefinition, besides the 'keywords' of a mod in modsync_mods.json.
DEFAULT_KEYWORDS = ["harmony", "deferred", "scatterer", " eve", "tufx", "upscal", "motion vector", "jitter", "dlss", "taa", "ksc", "breaking",
                    "incompatib", "renam", "remov", "reflection", "depth", "shadow", "ocean", "cloud", "wind", "aerodynam", "ksp 1.", "api"]

GENERIC = {"type", "mode", "enabled", "update", "start", "awake", "name", "material", "camera", "value"}


def touchpoints(label_cfg, src_root=None):
    """{name: {'kind', 'owners'}} the review searches the source of a mod for: the identifiers of its catalogue entries (a member or
    literal with the simple names of the types it was named with) and of its registration."""
    names = {}

    def simple(t):
        return t.split(".")[-1].split("`")[0]

    def add(name, kind, owner=None):
        slot = names.setdefault(name, {"kind": kind, "owners": set()})
        if owner:
            slot["owners"].add(simple(owner))

    wanted = set(label_cfg.get("catalogue_mods", []))
    for entry in parse_catalogue(src_root or os.path.join(REPO, "src")):
        if entry["mod"] not in wanted:
            continue
        for t in entry["types"]:
            add(simple(t), "type")
        for owner, member, kind in entry["pairs"]:
            add(member[4:] if member.startswith(("get_", "set_")) else member, kind, owner)
    for token in registration_names(label_cfg.get("repo"), label_cfg.get("label", "")):
        add(token.split(".")[-1], "registration")
    for extra in label_cfg.get("touch", []):
        add(extra, "config")
    return names


# ------------------------------------------------------------------------------------------------------------------ review

def git(clone, *args, check_status=False, timeout=300):
    result = subprocess.run(["git", "-C", clone] + list(args), capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    if check_status and result.returncode != 0:
        raise RuntimeError("git %s: %s" % (" ".join(args), result.stderr.strip()))
    return result.stdout


def resolve_tag(clone, version, last_number=False):
    """The tag of a clone that names a version (same numbers, trailing zeros ignored; with last_number only the last number, as
    Kopernicus' 'release-249' stands for 1.12.1.249), newest first on ties; None where none."""
    if not version:
        return None
    version = re.sub(r"^\d+:", "", version)
    tags = git(clone, "tag", "--list").split()
    if last_number:
        exact = [t for t in tags if last_of(t) == last_of(version) and "release" in t.lower()]
    else:
        wanted = cmu.numbers(version)
        exact = [t for t in tags if cmu.numbers(t) == wanted]
    exact.sort(key=lambda t: (t.startswith("dev-"), len(t)))
    return exact[0] if exact else None


SOURCE_GLOBS = ["*.cs", "*.shader", "*.cginc", "*.hlsl", "*.compute", "*.cfg", "*.glsl"]
STOP_AFTER = 40


def diff_hits(diff_text, name):
    """[(file, line, '+'|'-', text)] of the changed lines of a unified -U0 diff that carry a name."""
    hits, file_name, old_line, new_line = [], None, 0, 0
    for line in diff_text.splitlines():
        if line.startswith("+++ "):
            file_name = line[6:] if line.startswith("+++ b/") else line[4:]
        elif line.startswith("@@"):
            m = re.match(r"@@ -(\d+)(?:,\d+)? \+(\d+)", line)
            if m:
                old_line, new_line = int(m.group(1)), int(m.group(2))
        elif line.startswith("+") and not line.startswith("+++"):
            if name in line:
                hits.append((file_name, new_line, "+", line[1:].strip()[:140]))
            new_line += 1
        elif line.startswith("-") and not line.startswith("---"):
            if name in line:
                hits.append((file_name, old_line, "-", line[1:].strip()[:140]))
            old_line += 1
    return hits


def review(mod_key, old=None, new=None, fetch=True, ksp=None):
    """The source review of one GameData folder; a dict that review_markdown() prints."""
    cfg = load_config()["gamedata"].get(mod_key)
    if cfg is None:
        raise SystemExit("modsync: %s is not in modsync_mods.json" % mod_key)
    chk = [r for r in check(ksp=ksp)["results"] if r["folder"] == mod_key]
    row = chk[0] if chk else None
    clone = ensure_clone(cfg, fetch)
    out = {"mod": mod_key, "label": cfg.get("label", mod_key), "no_feed": bool(cfg.get("no_feed")), "installed": row["installed"] if row else None,
           "latest": row["latest"] if row else None, "clone": clone, "findings": [], "problems": []}
    if clone is None:
        out["problems"].append("no local git clone of this mod's source and no repository to clone (modsync_mods.json 'clone', 'repo')")
        return out
    source = cfg.get("source")
    if source:
        # No release feed: the installed build is found in the public branch by its version text, the end is the branch head.
        old_ref = old or (commit_of_version(clone, source, out["installed"]) if out["installed"] else None)
        new_ref = new or source["branch"]
        out["problems"].append("no release feed: the installed build is read as the commit %s of %s; the public source can differ from the build" % (old_ref, source["branch"]))
    else:
        last = bool(cfg.get("last_number"))
        old_ref = old or resolve_tag(clone, out["installed"], last)
        new_ref = new or (resolve_tag(clone, out["latest"]["version"], last) if out["latest"] else None)
    out["old_ref"], out["new_ref"] = old_ref, new_ref
    if not old_ref or not new_ref:
        out["problems"].append("could not map the versions %s -> %s to refs of the clone; give --old and --new" % (out["installed"], out["latest"] and out["latest"]["version"]))
        return out
    out["commits"] = [l for l in git(clone, "log", "--format=%h %cs %s", "%s..%s" % (old_ref, new_ref)).splitlines()]
    out["stat"] = git(clone, "diff", "--shortstat", old_ref, new_ref).strip()
    words = DEFAULT_KEYWORDS + cfg.get("keywords", [])
    out["keyword_commits"] = [c for c in out["commits"] if any(w.lower() in c.lower() for w in words)]
    names = touchpoints(cfg)
    out["names"] = len(names)
    diff = git(clone, "diff", "-U0", "-M", old_ref, new_ref, "--", *SOURCE_GLOBS, *EXCLUDE_TESTS)
    owner_files = {}
    for name, info in sorted(names.items()):
        out["findings"].append(examine(clone, old_ref, new_ref, diff, name, info, owner_files))
    return out


EXCLUDE_TESTS = [":(exclude)*Test*", ":(exclude)*test*", ":(exclude)*Sample*", ":(exclude)*Docs*"]
DECLARATION = re.compile(r"\b(?:public|private|internal|protected|static|override|virtual|abstract|sealed|class|struct|enum|interface|delegate)\b[^;=()]*\b%s\b(?!\s*\.)")


def grep_lines(clone, ref, name):
    """[(path, line number, text)] of the source lines of a ref that hold a name as a whole word; tests and samples left out."""
    text = git(clone, "grep", "-n", "-I", "-w", "-F", name, ref, "--", *SOURCE_GLOBS, *EXCLUDE_TESTS)
    found = []
    prefix = ref + ":"
    for line in text.splitlines():
        if line.startswith(prefix):
            line = line[len(prefix):]
        parts = line.split(":", 2)
        if len(parts) == 3 and parts[1].isdigit():
            found.append((parts[0], int(parts[1]), parts[2]))
    return found


def files_declaring(clone, ref, owner, cache):
    key = (ref, owner)
    if key not in cache:
        text = git(clone, "grep", "-l", "-I", "-E", r"\b(class|struct|interface|enum)\s+%s\b" % re.escape(owner), ref, "--", "*.cs", *EXCLUDE_TESTS)
        prefix = ref + ":"
        cache[key] = {l[len(prefix):] if l.startswith(prefix) else l for l in text.splitlines()}
    return cache[key]


def examine(clone, old_ref, new_ref, diff, name, info, cache):
    """One name of the catalogue against the old and new source. Types and members count by their declaration lines, restricted to the
    files of the types they were named with; literals and registration names count by any line."""
    kind = info["kind"]
    result = {"name": name, "kind": kind, "owners": sorted(info["owners"])}
    if len(name) < 4 or name.lower() in GENERIC:
        result["skipped"] = "too generic to search"
        return result
    declared = kind in ("type", "member")
    pattern = re.compile(DECLARATION.pattern % re.escape(name))
    allowed = None
    if declared and info["owners"]:
        allowed = set()
        for owner in info["owners"]:
            allowed |= files_declaring(clone, old_ref, owner, cache) | files_declaring(clone, new_ref, owner, cache)
        if not allowed:
            result["skipped"] = "owner type %s is not declared in this source" % ", ".join(sorted(info["owners"]))
            return result

    def keep(path, text):
        if declared and not pattern.search(text):
            return False
        return allowed is None or path in allowed

    old_lines = [l for l in grep_lines(clone, old_ref, name) if keep(l[0], l[2])]
    new_lines = [l for l in grep_lines(clone, new_ref, name) if keep(l[0], l[2])]
    hits = [h for h in diff_hits(diff, name) if keep(h[0], h[3])]
    result.update({"at_old": len(old_lines), "at_new": len(new_lines), "changed_lines": hits[:12], "changed_count": len(hits)})
    return result


def ensure_clone(cfg, fetch):
    """The local clone of a mod's source: the one under References, else a bare clone in the cache (made once). Fetched where asked."""
    clone = os.path.join(refs_root(), cfg["clone"]) if cfg.get("clone") else None
    if clone and not os.path.isdir(os.path.join(clone, ".git")):
        clone = None
    if clone is None and cfg.get("repo"):
        clone = os.path.join(cache_dir(), "clones", cfg["repo"].replace("/", "_") + ".git")
        if not os.path.isdir(clone):
            os.makedirs(os.path.dirname(clone), exist_ok=True)
            subprocess.run(["git", "clone", "--bare", "--quiet", "https://github.com/%s.git" % cfg["repo"], clone], check=False, timeout=900)
            if not os.path.isdir(clone):
                return None
            return clone
    if clone and fetch:
        try:
            git(clone, "fetch", "--all", "--tags", "--quiet", check_status=True, timeout=600)
        except (RuntimeError, subprocess.TimeoutExpired):
            pass
    return clone


_DIFF_CACHE = {}


def verdict_hint(out):
    """A first reading, to be judged by a reader: breaking where a searched name vanished from the source, touched where a line carrying it changed."""
    gone = [f["name"] for f in out["findings"] if f.get("at_old") and not f.get("at_new")]
    touched = [f["name"] for f in out["findings"] if f.get("changed_count")]
    if gone:
        return "breaking?", gone
    if touched:
        return "touched", touched
    if out["problems"] and not out.get("commits"):
        return "unclear", []
    return "untouched", []


def review_markdown(out):
    lines = ["### %s" % out["label"], ""]
    lines.append("Installed %s, latest %s (%s); clone refs %s -> %s." % (out["installed"], out["latest"] and out["latest"]["version"], out["latest"] and out["latest"]["source"], out.get("old_ref"), out.get("new_ref")))
    for p in out["problems"]:
        lines.append("- Problem: " + p)
    if out.get("commits") is not None:
        lines.append("- %d commits, %s." % (len(out["commits"]), out.get("stat") or "no diff"))
        for c in out["commits"][:12]:
            lines.append("  - " + c[:160])
        keyed = out.get("keyword_commits", [])
        if keyed:
            lines.append("- %d commit(s) whose message names something ReDefinition cares about:" % len(keyed))
            for c in keyed[:STOP_AFTER]:
                lines.append("  - " + c[:200])
        hint, names = verdict_hint(out)
        lines.append("- Reading: **%s** %s" % (hint, ", ".join(names)))
        for f in out["findings"]:
            if f.get("skipped"):
                continue
            if f.get("changed_count") or (f.get("at_old") and not f.get("at_new")):
                lines.append("  - `%s` (%s%s): %s line(s) at old, %s at new, %d changed" % (f["name"], f["kind"], " of " + ", ".join(f["owners"]) if f["owners"] else "", f["at_old"], f["at_new"], f["changed_count"]))
                for file_name, line_no, sign, text in f["changed_lines"][:4]:
                    lines.append("    - %s:%s %s %s" % (file_name, line_no, sign, text))
        silent = [f["name"] for f in out["findings"] if not f.get("skipped") and not f.get("changed_count") and f.get("at_new")]
        lines.append("- Names searched with no changed line: %d; skipped as too generic: %d; names absent in both: %d." % (
            len(silent), sum(1 for f in out["findings"] if f.get("skipped")), sum(1 for f in out["findings"] if not f.get("skipped") and not f.get("at_old") and not f.get("at_new"))))
    return "\n".join(lines) + "\n"


# ----------------------------------------------------------------------------------------------------------------- install

class Refusal(Exception):
    pass


def ksp_running():
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe"], capture_output=True, text=True).stdout
    except OSError:
        return False
    return "KSP_x64.exe" in out


def locks_held(repo=None):
    import gpu_lock
    held = []
    for kind in gpu_lock.KINDS:
        info = gpu_lock.read_lock(gpu_lock.lock_path(kind))
        if info and not gpu_lock.is_stale(info):
            held.append(gpu_lock.describe(info))
    return held


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def pick_asset(assets, repo, pattern):
    """The one release asset whose name matches the mod's pattern and whose URL is a download of that repository."""
    prefix = "https://github.com/%s/releases/download/" % repo
    matching = [a for a in assets if re.search(pattern, a.get("name", "")) and a.get("browser_download_url", "").startswith(prefix)]
    if len(matching) != 1:
        raise Refusal("%d release assets match %s in %s; exactly one is needed" % (len(matching), pattern, repo))
    return matching[0]


def extract_zip(zip_path, ksp, folders, preserve):
    """Extracts only the allowed folders of GameData from a release zip; preserved files that exist stay as they are.
    Returns the list of files written. Paths with .. or drive letters are refused."""
    gamedata = os.path.realpath(os.path.join(ksp, "GameData"))
    written = []
    with zipfile.ZipFile(zip_path) as z:
        names = [n for n in z.namelist() if not n.endswith("/")]
        prefix = "GameData/" if any(n.startswith("GameData/") for n in names) else ""
        for name in names:
            if prefix and not name.startswith(prefix):
                continue
            rel = name[len(prefix):]
            parts = rel.split("/")
            if rel.startswith("/") or ".." in parts or ":" in parts[0]:
                raise Refusal("unsafe path in the archive: " + name)
            if parts[0] not in folders:
                raise Refusal("the archive holds %s, outside the folders %s" % (parts[0], folders))
            target = os.path.realpath(os.path.join(gamedata, *parts))
            if not target.startswith(gamedata + os.sep):
                raise Refusal("unsafe path in the archive: " + name)
            inner = "/".join(parts[1:])
            if os.path.exists(target) and any(fnmatch.fnmatch(inner.lower(), p.lower()) for p in preserve):
                continue
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with z.open(name) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            written.append(rel)
    return written


def foreign_check(repo=None):
    """{assumption id: 'PASS'|'FAIL <state>'} from tools/checks/check_foreign_code.ps1, and its exit code."""
    script = os.path.join(HERE, "check_foreign_code.ps1")
    result = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script], capture_output=True, text=True, timeout=900)
    states = {}
    for line in result.stdout.splitlines():
        m = re.match(r"^(PASS|FAIL)\s+(\w+)\s+(\S+)", line)
        if m:
            states[m.group(3)] = "PASS" if m.group(1) == "PASS" else "FAIL " + m.group(2)
    return states, result.returncode, result.stdout[-2000:]


def series_preflight(series):
    result = subprocess.run([sys.executable, os.path.join(TOOLS, "atmo_series.py"), "preflight", series], capture_output=True, text=True, timeout=900)
    return result.returncode, (result.stdout + result.stderr)[-2000:]


def branch_passes(report):
    """Builds and tests the branch of a review report in its worktree, under the run guard of tools/dotnet_test.py. The worktree must be
    one this tool's nightly job made: .local/work/nightly-*."""
    branch = report.get("branch") or ""
    worktree = report.get("worktree", "")
    if branch and not SAFE_BRANCH.match(branch):
        raise Refusal("the report's branch %r is not claude/mod-update-*" % branch)
    root = os.path.realpath(os.path.join(REPO, ".local", "work"))
    if not os.path.realpath(worktree).startswith(root + os.sep) or not os.path.basename(worktree).startswith("nightly-"):
        raise Refusal("the report's worktree %r is not under .local/work/nightly-*" % worktree)
    head = git(worktree, "rev-parse", "--abbrev-ref", "HEAD").strip()
    if branch and head != branch:
        raise Refusal("the worktree is on %r, the report says %r" % (head, branch))
    # The installed mod and the main code must match: an adaptation that is not on origin/main yet waits for the owner's merge.
    ahead = git(worktree, "rev-list", "--count", "origin/main..HEAD").strip()
    if ahead not in ("", "0"):
        raise Refusal("the branch holds %s commit(s) that are not on origin/main: merge them first, then install" % ahead)
    result = subprocess.run([sys.executable, os.path.join(worktree, "tools", "dotnet_test.py")], capture_output=True, text=True, timeout=7200, cwd=worktree)
    return result.returncode == 0, result.stdout[-1500:]


def latest_release(entry):
    """(tag, assets) of the release check_mod_updates.py reports as the latest stable for a mod_updates.json entry."""
    stable, _, _ = cmu.latest(entry["repo"], entry.get("tag_regex"))
    if stable is None:
        raise Refusal("no stable release of %s" % entry["repo"])
    tag = stable[0]
    data = json.loads(http("https://api.github.com/repos/%s/releases/tags/%s" % (entry["repo"], urllib.parse.quote(tag)), accept="application/vnd.github+json"))
    return tag, data.get("assets", [])


def install(mod_name, dry_run=False, report_path=None, series=None, ksp=None, deps=None):
    """The install of one mod. `deps` overrides the outside world for the tests (release lookup, download, checks)."""
    deps = deps or {}
    ksp = ksp or ksp_root()
    config = load_config()
    entry = next((m for m in load_mod_updates() if m["name"] == mod_name or slug(m["name"]) == mod_name), None)
    if entry is not None:
        mod_name = entry["name"]
    plan = config["installable"].get(mod_name)
    if entry is None or plan is None:
        raise Refusal("%s is not in mod_updates.json and modsync_mods.json 'installable'" % mod_name)
    if plan.get("never_install") or plan.get("closed"):
        raise Refusal("%s is never fetched or installed by this tool: %s" % (mod_name, plan.get("never_install") or "closed source or Patreon only"))
    log = []

    def say(text):
        log.append(text)
        print("modsync install: " + text, flush=True)

    tag, assets = (deps.get("latest_release") or latest_release)(entry)
    version = (cmu.numbers(tag) and ".".join(str(n) for n in cmu.numbers(tag))) or tag
    asset = pick_asset(assets, entry["repo"], plan["asset"])
    folders = plan["folders"]
    old_version = next((r["installed"] for r in inventory(ksp)["mods"] if r["folder"] == folders[0]), None)
    say("%s: installed %s, latest stable %s, asset %s" % (mod_name, old_version, tag, asset["name"]))
    if old_version and compare(tag, old_version) <= 0:
        raise Refusal("%s is already at %s" % (mod_name, old_version))
    report_file = report_path or find_report(slug(mod_name), tag)
    if not report_file or not os.path.exists(report_file):
        raise Refusal("no review report for %s %s in %s" % (slug(mod_name), tag, os.path.join(REPO, REPORT_DIR)))
    with open(report_file, encoding="utf-8") as f:
        report = json.load(f)
    if report.get("verdict") != "safe":
        raise Refusal("the review verdict is %r, not 'safe'" % report.get("verdict"))
    if report.get("mod") != slug(mod_name) or compare(str(report.get("version")), tag) != 0:
        raise Refusal("the report is for %s %s, not %s %s" % (report.get("mod"), report.get("version"), slug(mod_name), tag))
    if (deps.get("ksp_running") or ksp_running)():
        raise Refusal("KSP_x64 runs")
    held = (deps.get("locks_held") or locks_held)()
    if held:
        raise Refusal("a lock is held: " + "; ".join(held))
    if dry_run:
        say("dry run: would verify the branch %s, download %s, back up %s, extract into GameData, run the fingerprint check and the series preflight" % (report.get("branch"), asset["browser_download_url"], folders))
        return {"dry_run": True, "log": log}
    ok, tail = (deps.get("branch_passes") or branch_passes)(report)
    if not ok:
        raise Refusal("the branch does not build or pass its tests:\n" + tail)
    baseline, _, _ = (deps.get("foreign_check") or foreign_check)()
    archive = os.path.join(REPO, ARCHIVE_DIR, "%s-%s" % (slug(mod_name), old_version or "unknown"))
    download_dir = os.path.join(cache_dir(), "downloads", "%s-%s" % (slug(mod_name), version))
    os.makedirs(download_dir, exist_ok=True)
    zip_path = os.path.join(download_dir, asset["name"])
    (deps.get("download") or download)(asset["browser_download_url"], zip_path)
    digest = sha256_of(zip_path)
    say("downloaded %s, sha256 %s" % (asset["name"], digest))
    if os.path.exists(archive):
        raise Refusal("the backup folder %s exists already" % archive)
    os.makedirs(archive)
    for folder in folders:
        source = os.path.join(ksp, "GameData", folder)
        if os.path.isdir(source):
            shutil.copytree(source, os.path.join(archive, folder))
    shutil.copy2(zip_path, os.path.join(archive, "new-" + asset["name"]))
    preserve = list(config["preserve_default"]) + list(plan.get("preserve", []))
    try:
        for folder in folders:
            target = os.path.join(ksp, "GameData", folder)
            if os.path.isdir(target):
                for dirpath, _, files in os.walk(target, topdown=False):
                    for name in files:
                        inner = os.path.relpath(os.path.join(dirpath, name), target).replace("\\", "/")
                        if not any(fnmatch.fnmatch(inner.lower(), p.lower()) for p in preserve):
                            os.remove(os.path.join(dirpath, name))
                    if not os.listdir(dirpath) and dirpath != target:
                        os.rmdir(dirpath)
        written = extract_zip(zip_path, ksp, folders, preserve)
        say("installed %d files" % len(written))
        after, code, _ = (deps.get("foreign_check") or foreign_check)()
        newly = sorted(i for i, s in after.items() if s.startswith("FAIL") and not baseline.get(i, "").startswith("FAIL"))
        if newly:
            raise Refusal("the fingerprint check shows new FAILs: " + ", ".join(newly))
        pre_code, pre_out = (deps.get("series_preflight") or series_preflight)(series or config["series"])
        if pre_code != 0:
            raise Refusal("the series preflight fails:\n" + pre_out)
    except Exception as error:
        say("verification failed (%s): putting the backup back" % error)
        rollback(ksp, archive, folders)
        write_install_record(archive, mod_name, old_version, tag, digest, asset, "rolled back: %s" % error, log)
        raise
    write_install_record(archive, mod_name, old_version, tag, digest, asset, "installed", log)
    say("done; backup in " + archive)
    return {"dry_run": False, "archive": archive, "sha256": digest, "log": log}


def find_report(mod_slug, tag):
    """The newest review report <slug>-<version>.json in .local/work/modsync whose version is the tag's (trailing zeros and a v ignored)."""
    folder = os.path.join(REPO, REPORT_DIR)
    if not os.path.isdir(folder):
        return None
    candidates = []
    for name in os.listdir(folder):
        if not (name.startswith(mod_slug + "-") and name.endswith(".json")):
            continue
        path = os.path.join(folder, name)
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            continue
        if data.get("mod") == mod_slug and compare(str(data.get("version", "")), tag) == 0:
            candidates.append((os.path.getmtime(path), path))
    return max(candidates)[1] if candidates else None


def rollback(ksp, archive, folders):
    for folder in folders:
        target = os.path.join(ksp, "GameData", folder)
        saved = os.path.join(archive, folder)
        if os.path.isdir(target):
            shutil.rmtree(target)
        if os.path.isdir(saved):
            shutil.copytree(saved, target)


def write_install_record(archive, mod_name, old_version, tag, digest, asset, outcome, log):
    record = {"mod": mod_name, "old": old_version, "new": tag, "asset": asset["name"], "url": asset["browser_download_url"], "sha256": digest,
              "outcome": outcome, "time": datetime.datetime.now().isoformat(timespec="seconds"), "log": log}
    with open(os.path.join(archive, "install.json"), "w", encoding="utf-8") as f:
        json.dump(record, f, indent=1)


def download(url, path):
    data = http(url, timeout=1800)
    with open(path, "wb") as f:
        f.write(data)


# ----------------------------------------------------------------------------------------------------------------- the summary

def summary_text(date, results):
    """The short German summary of a nightly run, one line per mod: checked version, new version, verdict, branch, install result, report.
    Empty where there is nothing to say (no update and nothing failed or was skipped)."""
    if not results:
        return ""
    lines = ["ModSync %s:" % date]
    for r in results:
        parts = ["%s: geprueft %s, neu %s" % (r.get("mod", "?"), r.get("checked", "?"), r.get("new", "?"))]
        if r.get("verdict"):
            parts.append("Urteil %s" % r["verdict"])
        if r.get("branch"):
            parts.append("Branch %s" % r["branch"])
        parts.append("Installation: %s" % r.get("install", "nicht versucht"))
        if r.get("problem"):
            parts.append("Problem: %s" % r["problem"])
        if r.get("report"):
            parts.append("Bericht %s" % r["report"])
        lines.append("- " + ", ".join(parts))
    return "\n".join(lines) + "\n"


def write_summary(date, results, directory=None):
    """LATEST.md and one line of notify.log under .local/work/modsync; returns the text."""
    text = summary_text(date, results)
    directory = directory or os.path.join(REPO, REPORT_DIR)
    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, "LATEST.md"), "w", encoding="utf-8") as f:
        f.write(text or "ModSync %s: nichts Neues.\n" % date)
    with open(os.path.join(directory, "notify.log"), "a", encoding="utf-8") as f:
        f.write("%s %s\n" % (datetime.datetime.now().isoformat(timespec="seconds"), (text.replace("\n", " | ").strip(" |") if text else "ModSync %s: nichts Neues" % date)))
    return text


# ---------------------------------------------------------------------------------------------------------------------- cli

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("inventory")
    p.add_argument("--json")
    p = sub.add_parser("check")
    p.add_argument("--refresh", action="store_true")
    p.add_argument("--offline", action="store_true")
    p.add_argument("--json")
    p = sub.add_parser("review")
    p.add_argument("mod", help="the GameData folder, e.g. ParallaxContinued")
    p.add_argument("--old")
    p.add_argument("--new")
    p.add_argument("--no-fetch", action="store_true")
    p.add_argument("--out")
    p = sub.add_parser("install")
    p.add_argument("mod", help="the name in mod_updates.json, e.g. Parsek")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--report")
    p.add_argument("--series")
    sub.add_parser("guard", help="exit 0 and 'free' where KSP does not run and no lock is held, else 1 and the reasons")
    p = sub.add_parser("summary", help="the German summary of a nightly run from its results file; --write also writes LATEST.md and notify.log")
    p.add_argument("results", help="a JSON list of {mod, checked, new, verdict, branch, install, problem, report}")
    p.add_argument("--date", default=datetime.date.today().isoformat())
    p.add_argument("--write", action="store_true")
    sub.add_parser("selftest")
    args = parser.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if args.command == "selftest":
        import unittest
        suite = unittest.defaultTestLoader.loadTestsFromName("tools.tests.test_modsync")
        return 0 if unittest.TextTestRunner(verbosity=1).run(suite).wasSuccessful() else 1
    if args.command == "inventory":
        inv = inventory()
        print_inventory(inv)
        if args.json:
            with open(args.json, "w", encoding="utf-8") as f:
                json.dump(inv, f, indent=1)
        return 0
    if args.command == "check":
        result = check(args.refresh, args.offline)
        print_check(result)
        out = args.json or os.path.join(REPO, REPORT_DIR, "check-%s.json" % result["date"])
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=1)
        return 2 if any((r["status"] == "newer" and r["use"] == "direct") or r.get("changed_by_hand") for r in result["results"]) else 0
    if args.command == "review":
        out = review(args.mod, args.old, args.new, not args.no_fetch)
        text = review_markdown(out)
        print(text)
        if args.out:
            with open(args.out, "w", encoding="utf-8") as f:
                f.write(text)
        return 0
    if args.command == "guard":
        reasons = (["KSP_x64 runs"] if ksp_running() else []) + ["lock held: " + h for h in locks_held()]
        print("; ".join(reasons) if reasons else "free")
        return 1 if reasons else 0
    if args.command == "summary":
        with open(args.results, encoding="utf-8-sig") as f:  # PowerShell writes a BOM
            results = json.load(f)
        text = write_summary(args.date, results) if args.write else summary_text(args.date, results)
        print(text, end="")
        return 0
    if args.command == "install":
        try:
            install(args.mod, args.dry_run, args.report, args.series)
        except Refusal as refusal:
            print("modsync install: refused: %s" % refusal, file=sys.stderr)
            return 3
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
