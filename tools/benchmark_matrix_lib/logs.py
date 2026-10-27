"""Reading the two logs a start writes, ReDefinitionProxy.log and KSP.log, into timed entries, and the numbers of
one phase's window (aggregate)."""
import re
import statistics


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
    """A log line's time of day, from its hour, minute, second and millisecond strings, in seconds."""
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
    """What ReDefinitionProxy.log says, each entry with its time. It holds the Config lines at load
    and on reload, the frame time reports, the latency, NVIDIA's latency and the brake. It also
    holds the stages, the raytracing's mesh builds, and the lines of the backbuffer, the upscalers
    and frame generation."""
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


_SCENE_REASON = re.compile(r"Probe: \w+ scene unavailable: (.*?)\r?$", re.M)


def scene_unavailable(start, ksp_text):
    """Why the start's scene was not the one measured, or None. The scene names a line of the probe
    ("ready", a regular expression) that KSP.log must hold. The probe says why in a line of its
    own when it could not make the scene (it quits then). A start without a "ready" is not checked."""
    ready = start.get("scene_ready")
    if not ready or re.search(ready, ksp_text, re.M):
        return None
    reason = _SCENE_REASON.search(ksp_text)
    return "scene {} unavailable: {}".format(start.get("scene") or "?", reason.group(1) if reason
                                             else "the probe's line that says it stands there is not in KSP.log")


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
    """What KSP.log says of the mod, each entry with its time: the GPU, the upscaler set up, the
    probe's key sets and shots, the profile, Ray Reconstruction, the raytracing mode and the frame
    generation multiplier. Only the mod's lines move the clock (_timed_lines)."""
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
    """One phase's numbers from the proxy's reports in its window: the fps over all frames (frames by
    busy time), the frames shown, the percentiles and the latency medians; a part without reports is
    left out of the result."""
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
    """The last entry at or before time t (the entries in time order), or None."""
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
