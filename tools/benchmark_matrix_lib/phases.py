"""The phases of one start, measured: each phase's window in the two logs, its numbers and the warnings that say
the phase did not run as asked."""
import math

from .constants import PROBE_SET_SECONDS, STEP_TOLERANCE
from .logs import (_align, _fg_kind_ran, _in_window, _last_before, _unfocused_at, aggregate, parse_ksp_log,
                   parse_proxy_log, scene_unavailable)
from .scenarios import scenario_key, scenario_label


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
    # A scene that was not the one asked for measures the wrong place: no phase of it counts.
    unavailable = scene_unavailable(start, ksp_text)
    if unavailable:
        problems.insert(0, unavailable)
    elif len(ksp["sets"]) < len(start["phases"]):
        problems.append("the probe set {} of {} key sets".format(len(ksp["sets"]), len(start["phases"])))
    results = []
    for i, phase in enumerate(start["phases"]):
        if i >= len(ksp["sets"]) or unavailable:
            break
        results.append(_measure_phase(i, phase, start, ksp, proxy, fg_kind, settle_seconds))
    phases = _merge_repeats(results)
    _remeasure_merged(phases, proxy, fg_kind)
    return _start_overview(ksp, proxy, load_config, fg_kind, problems, phases)


def _measure_phase(i, phase, start, ksp, proxy, fg_kind, settle_seconds):
    """Phase i of the start measured in its window: the set of keys it began with, the probe's shot that
    ended it, the proxy's reload of the keys in between and the numbers of the reports inside the window,
    with the warnings that say it did not run as asked."""
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
    s = phase["scenario"]
    warnings = _window_warnings(reload_, shot, window, ksp, begin, end, upscaler, s)
    ratio = measured.get("presented_per_rendered")
    ratio_warnings, step_check = _ratio_findings(proxy, s, ratio, end)
    warnings.extend(ratio_warnings)
    warnings.extend(_requested_warnings(phase, start, ksp, s, fg_kind, config, end))
    return {
        "index": i, "scenario": s, "key": scenario_key(s), "label": scenario_label(s), "keys": phase["keys"],
        "window": [round(begin, 3), round(end, 3)], "settle_seconds": settle, "rebuild": phase.get("rebuild", False),
        "mod_settings_set": phase.get("mod_settings"),
        "ray_reconstruction_lines": [e["text"] for e in ksp["rr"] if set_time <= e["time"] <= end] or None,
        "proxy_config": config["values"] if config else None,
        "proxy_config_line": config["text"] if config else None,
        "upscaler": upscaler, "fg_kind_ran": fg_kind if ratio and ratio >= 1.5 else "none",
        "fg_step": step_check, "_ratio_warnings": ratio_warnings,
        "fg_lines": [x["text"] for key in ("dlssg", "fg_multiplier", "xess_fg") for x in proxy["lines"].get(key, [])
                     if set_time - 0.5 <= x["time"] <= end] + [
                         "mod: frame generation multiplier " + m["choice"] for m in ksp["multiplier"]
                         if set_time - 0.5 <= m["time"] <= end] or None,
        "measured": measured, "warnings": warnings,
    }


def _window_warnings(reload_, shot, window, ksp, begin, end, upscaler, s):
    """The notes on a phase's window: the lines the probe and the proxy should have written and did not,
    an upscaler set up again inside it, and an upscaler other than the scenario's."""
    warnings = []
    if reload_ is None:
        warnings.append("no Config reloaded line after the probe set the keys")
    if shot is None:
        warnings.append("the probe's shot that ends the phase is missing")
    if not window["reports"]:
        warnings.append("no frame time report inside the phase")
    if any(begin <= u["time"] <= end for u in ksp["upscaler"]):
        warnings.append("the upscaler was set up again during the phase")
    if upscaler is None:
        warnings.append("no upscaler ran")
    elif upscaler["technique"] != s["technique_log_name"]:
        warnings.append("asked for {}, ran {}".format(s["technique_log_name"], upscaler["technique"]))
    return warnings


def _requested_warnings(phase, start, ksp, s, fg_kind, config, end):
    """The notes on what the phase asked for and did not get: the frame generation kind, the raytracing
    mode and each proxy key the probe set, against the proxy's configuration."""
    warnings = []
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
    return warnings


def _remeasure_merged(phases, proxy, fg_kind):
    """The phases that were repeated (phase_repeats), measured again over their merged window; the notes
    that depend on the frames per rendered frame follow the merged numbers, not the first pass's."""
    for r in phases:
        earlier = r.pop("_ratio_warnings", [])
        if r.get("repeats"):
            rs = float((r["proxy_config"] or {}).get("reportSeconds", 10))
            r["measured"] = aggregate(*[_in_window(proxy[k], r["window"][0], r["window"][1], rs)
                                        for k in ("reports", "latency", "pcl", "brake", "stages")])
            # What the frames per rendered frame say is of the merged window, not of its first pass:
            # the step check, the notes on them and the frame generation that ran.
            ratio = r["measured"].get("presented_per_rendered")
            ratio_warnings, r["fg_step"] = _ratio_findings(proxy, r["scenario"], ratio, r["window"][1])
            r["warnings"] = sorted((set(r["warnings"]) - set(earlier)) | set(ratio_warnings))
            r["fg_kind_ran"] = fg_kind if ratio and ratio >= 1.5 else "none"


def _start_overview(ksp, proxy, load_config, fg_kind, problems, phases):
    """The record of one start: what its logs say about the GPU, the proxy's load configuration, the
    frame generation, the upscaler and the raytracing builds, with the problems found and the phases."""
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


def _ratio_findings(proxy, s, ratio, end):
    """The notes the frames presented per rendered frame give a phase of scenario s, and its
    step check: frame generation asked for and not there, held off and there, a step not delivered."""
    warnings = []
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
    return warnings, step_check


def _step_check(scenario, ratio):
    """What a frame generation step asked for and what the frames per rendered frame show.
    Asked "3x" and 2.0 presented per rendered frame is a step the GPU or the driver did not
    deliver (shown_as the whole number nearest). So is 2x asked for with 3.0 shown, which is a
    multiplier that did not reach the proxy. Dynamic has no number to be held to."""
    step = scenario.get("step")
    if not step or not scenario.get("fg_requested") or ratio is None:
        return None
    shown_as = int(math.floor(ratio + 0.5))
    if step == "Dynamic":
        return {"asked": step, "expected": None, "shown": ratio, "shown_as": shown_as, "delivered": None}
    expected = int(step.rstrip("x"))
    return {"asked": step, "expected": expected, "shown": ratio, "shown_as": shown_as,
            "delivered": abs(ratio - expected) <= STEP_TOLERANCE}


def _same_number(a, b):
    """Whether two values are the same number ("1" and "1.000000"), or, where either is not a number, the same text."""
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
            if any("_ratio_warnings" in p for p in parts):
                m["_ratio_warnings"] = sorted({w for p in parts for w in p.get("_ratio_warnings", [])})
        out.append(m)
    return out
