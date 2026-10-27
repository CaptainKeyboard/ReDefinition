"""A run's record, built from its raw folder: the plan and each start's logs, settings and outcome."""
import json
import os

from .constants import DEFAULT_SCENE, MAIN_GROUP
from .files import parse_cfg_values, parse_ini_sections
from .phases import phases_of_start


def build_record(raw):
    """The run's record from its raw folder: plan.json and each start's logs, settings, package ini and
    outcome, parsed into its phases (phases_of_start); a start without logs says it did not run."""
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
