"""How a scenario (one phase's rendering, frame generation and latency model) is named: its row label and its key."""
from .constants import DEFAULT_SCENE


def scenario_label(s):
    """The row's name in the tables: variant, scene, upscaler, raytracing (with RR, cache, micromaps or
    tweak), frame generation (with its step) and latency model, joined by middle dots."""
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
    """The rows' key. The radiance cache's, the opacity micromaps', a tweak's, a frame
    generation step's, a scene's and a variant's rows have theirs. The others keep the key
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
