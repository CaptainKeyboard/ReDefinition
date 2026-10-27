"""The run command: the profile start, the starts of the matrix and the restore of the install's settings."""
import datetime
import json
import os
import shutil
import subprocess
import sys
import time

from .constants import (MAX_COMMAND_CHARS, PACKAGE_DIR, PROXY_INI, REPO, SETTINGS_REL, SHOT,
                        START_OVERHEAD_SECONDS)
from .files import set_cfg_values, set_ini_values
from .install import Install
from .machine import git_record, machine_record, vendor_of
from .matrix import expand, load_matrix, select_groups
from .probe_plan import command_length, estimate_seconds, probe_arguments, start_seconds
from .record import build_record
from .report import write_results


def run(args):
    """The run command: checks the install and the starts, snapshots the settings, applies the profile,
    runs each start, restores the settings (also after a failure), then writes the record and the page."""
    matrix = load_matrix(args.matrix)
    install = Install(args.ksp)
    _check_install(install, args.ksp)
    resources = resources_of(args)
    machine = machine_record()
    vendor = args.vendor or vendor_of(machine)
    groups, starts, skipped = _starts_to_run(matrix, args, vendor, resources)
    _check_starts(install, starts)
    stamp = datetime.datetime.now().strftime("%Y-%m-%d-%H%M")
    raw = os.path.join(args.raw, stamp)
    os.makedirs(raw, exist_ok=True)
    plan = {"matrix": matrix, "matrix_file": matrix_label(args.matrix), "groups": groups, "starts": starts, "skipped": skipped,
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
        _run_starts(install, starts, raw, settings_template, ini_template, resources.get("dlssg_package"))
    finally:
        _restore_install(install, starts, snapshot, raw, plan)
    record = build_record(raw)
    for s in record["starts"]:
        for problem in s.get("problems") or []:
            print("{}: {}".format(s["id"], problem))
    write_results(record, args.no_doc)


def _check_install(install, ksp):
    """Stops the run where the install has no KSP_x64.exe, or the game is running."""
    if not os.path.exists(install.path("KSP_x64.exe")):
        sys.exit("No KSP_x64.exe in " + ksp)
    if install.running():
        sys.exit("KSP is running; close it first.")


def _starts_to_run(matrix, args, vendor, resources):
    """The groups' ids, starts and skipped entries a run covers: the groups asked for (--group), expanded for
    the vendor and the resources, and cut to the starts --only names. Stops where nothing is left."""
    try:
        groups = [g["id"] for g in select_groups(matrix, args.group)]
        starts, skipped = expand(matrix, vendor, resources, groups)
    except ValueError as e:
        sys.exit(str(e))
    if args.only:
        starts = [s for s in starts if any(o in s["id"] for o in args.only)]
    if not starts:
        sys.exit("No start to run.")
    return groups, starts, skipped


def _check_starts(install, starts):
    """Stops the run where a start's command line is too long for Windows, or the DLSS-G package it
    would put next to KSP_x64.exe is there already (the run would remove it)."""
    for s in starts:
        if command_length(s) > MAX_COMMAND_CHARS:
            sys.exit("The command line of {} is {} characters, past the {} that Windows takes: fewer phases in that start."
                     .format(s["id"], command_length(s), MAX_COMMAND_CHARS))
    if any(s["dlssg_package"] for s in starts):
        found = install.package_in_place()
        if found:
            sys.exit("The DLSS-G package is already next to KSP_x64.exe ({}); the run would remove it.".format(", ".join(found)))


def _run_starts(install, starts, raw, settings_template, ini_template, package):
    """Runs each start in turn; one that cannot run is named in its outcome file and the others go on."""
    for n, start in enumerate(starts):
        print("[{}/{}] {} ({} phases)".format(n + 1, len(starts), start["id"], len(start["phases"])))
        try:
            run_start(install, start, raw, settings_template, ini_template, package)
        except (OSError, subprocess.SubprocessError) as e:
            # One start that cannot run leaves the others to measure; its row says why.
            print("   failed:", e)
            with open(os.path.join(raw, start["id"] + ".outcome.txt"), "w") as f:
                f.write("failed: {}\n".format(e))


def _restore_install(install, starts, snapshot, raw, plan):
    """Takes the DLSS-G package away and puts the snapshot of the settings back, writes what it did to
    restore.txt, and saves the plan with the time the run finished."""
    if any(s["dlssg_package"] for s in starts):
        install.remove_package()
    report = install.restore(snapshot)
    with open(os.path.join(raw, "restore.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(report) + "\n")
    print("restored: {} file(s), removed {}".format(sum(1 for r in report if r.startswith("restored")),
                                                     sum(1 for r in report if r.startswith("removed"))))
    plan["finished"] = datetime.datetime.now().isoformat(timespec="seconds")
    _save_plan(raw, plan)


def apply_profile(install, profile, settings_template, raw):
    """The profile applied in a KSP start of its own, from the given settings. Returns its
    log lines and the mod's settings.cfg as the profile left it. The profile sets the
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


def matrix_label(path):
    """The matrix file as the record names it: relative to the repository, or just its name where it
    lies outside (a matrix of one run kept in a scratch folder, or on another drive)."""
    try:
        relative = os.path.relpath(path, REPO)
    except ValueError:
        return os.path.basename(path)
    return os.path.basename(path) if relative.startswith("..") else relative


def _save_plan(raw, plan):
    """Writes the run's plan to plan.json in its raw folder, which build_record reads."""
    with open(os.path.join(raw, "plan.json"), "w", encoding="utf-8") as f:
        json.dump(plan, f, indent=1)


def run_start(install, start, raw, settings_template, ini_template, package):
    """One start. It writes its settings.cfg and proxy ini (the first phase's keys at load), puts the
    DLSS-G package in place where it needs it, and launches KSP with the probe. Then it copies the
    logs and shots this start wrote into the raw folder and puts the settings template back."""
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
    """The resources the matrix's entries may require, by name, each only where its files are there: the
    DLSS-G package, the AMD, XeSS and XeSS-FG DLL folders (the given one, else the KSP folder) and Parallax."""
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
    parallax = parallax_folder(args.ksp)
    if parallax:
        out["parallax"] = parallax
    fg_files = ("libxess_fg.dll", "libxell.dll")
    if args.xess_fg_dll_dir and all(os.path.exists(os.path.join(args.xess_fg_dll_dir, n)) for n in fg_files):
        out["xess_fg_dll_dir"] = os.path.abspath(args.xess_fg_dll_dir)
    elif all(os.path.exists(os.path.join(args.ksp, n)) for n in fg_files):
        out["xess_fg_dll_dir"] = os.path.abspath(args.ksp)
    return out


def parallax_folder(ksp):
    """The folder of Parallax's plugin in the install's GameData (ParallaxContinued, or Parallax in
    older versions; the texture packs, Parallax_*, are not it), or None."""
    root = os.path.join(ksp, "GameData")
    try:
        names = sorted(os.listdir(root))
    except OSError:
        return None
    for name in names:
        lower = name.lower()
        if not lower.startswith("parallax") or lower.startswith("parallax_"):
            continue
        try:
            plugins = os.listdir(os.path.join(root, name, "Plugins"))
        except OSError:
            continue
        if any(n.lower().startswith("parallax") and n.lower().endswith(".dll") for n in plugins):
            return os.path.join(root, name)
    return None
