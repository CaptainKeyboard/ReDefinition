"""A record's rows and the comparison table of one group, against the group's baseline and the previous run."""
from .constants import MAIN_GROUP
from .scenarios import scenario_key


def record_groups(record):
    """The groups a record covers."""
    return record["groups"]


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
    """How many percent a lies above b (negative below), or None where either is missing or b is 0."""
    if a is None or b in (None, 0):
        return None
    return 100.0 * (a - b) / b


def _fmt(v, digits=1, signed=False, unit=""):
    """A table cell's number with the digits given, a sign where asked and the unit after; a dash for None."""
    if v is None:
        return "–"
    text = ("{:+." + str(digits) + "f}" if signed else "{:." + str(digits) + "f}").format(v)
    return text + unit


def summary_table(record, previous=None, group=MAIN_GROUP):
    """The group's comparison table in Markdown. It holds each row's fps and latency, against the
    group's baseline (the same rendering with frame generation off and no latency model) and the
    previous record. The columns the group's "show" names (ratio, rt_builds) come in addition."""
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
