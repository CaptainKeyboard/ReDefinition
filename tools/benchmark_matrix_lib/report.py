"""benchmarks.md: each run's section, the groups' tables, the results folder's JSON files and the page written from them."""
import json
import os
import textwrap

from .constants import DOC, MAIN_GROUP, PACKAGE_KEYS_SHOWN, RESULTS, RUNS_END, RUNS_START, TABLE_END, TABLE_START
from .matrix import group_list
from .tables import _fmt, group_definition, record_groups, summary_table


def _machine_line(record):
    """The run's machine in one line: the GPU of the record's vendor (else the first), its driver, the
    CPU, memory, Windows, the screen and render size of the first upscaler that ran, and the refresh rate."""
    m = record.get("machine") or {}
    gpu = next((a for a in m.get("adapters", []) if record.get("vendor") and record["vendor"].upper() in (a.get("name") or "").upper()),
               (m.get("adapters") or [{}])[0])
    driver = gpu.get("nvidia_driver") or gpu.get("driver")
    screen = None
    for s in record["starts"]:
        for p in s.get("phases", []):
            if p.get("upscaler"):
                u = p["upscaler"]
                screen = "{}x{} rendered at {}x{}".format(u["display"][0], u["display"][1], u["render"][0], u["render"][1])
                break
        if screen:
            break
    parts = [gpu.get("name") or "GPU unknown", "driver " + (driver or "unknown"), m.get("cpu") or "CPU unknown",
             "{} GB".format(m.get("memory_gb")) if m.get("memory_gb") else None, m.get("os"),
             "screen " + screen if screen else None,
             "{} Hz".format(gpu.get("refresh_hz")) if gpu.get("refresh_hz") else None]
    return ", ".join(p for p in parts if p)


def _matrix_shown(stored):
    """The matrix file of a record as the page shows it: a path outside the repository (a record from before
    matrix_label) only by its name."""
    path = (stored or "").replace("\\", "/")
    return path.rsplit("/", 1)[-1] if path.startswith("..") else path


def _settling(record):
    """The seconds the record's phases settled before they were measured, as text: those the phases
    name (a group may settle longer than the matrix does), else the record's own. Several values,
    for a record of groups that differ, are joined with a slash."""
    phases = [p for s in record["starts"] for p in s.get("phases", [])]
    def seconds(rebuilt, fallback):
        found = sorted({p["settle_seconds"] for p in phases if bool(p.get("rebuild")) == rebuilt and p.get("settle_seconds") is not None})
        return " / ".join("{:g}".format(v) for v in found) if found else "{:g}".format(fallback) if fallback is not None else "?"
    matrix = record.get("matrix") or {}
    return (seconds(False, record.get("settle_seconds")),
            seconds(True, matrix.get("settle_after_rebuild_seconds", record.get("settle_seconds"))))


def run_section(record, json_name):
    """A run's section of benchmarks.md: its date, commit, build, machine, profile, groups and matrix,
    and, folded, what ran in each phase, the proxy's load configuration, the package ini, the scene
    arguments and the raytracing's load spikes."""
    lines = _summary_lines(record, json_name)
    lines.append("")
    lines.append("<details><summary>What ran in each phase</summary>")
    lines.append("")
    lines.extend(_phase_table_lines(record))
    lines.append("")
    lines.extend(_proxy_config_lines(record))
    lines.extend(_probe_notes_lines(record))
    lines.append("")
    lines.append("</details>")
    lines.append("")
    return "\n".join(lines)


def _summary_lines(record, json_name):
    """The run's table of what and value: date, commit, build, machine, profile, groups, matrix, what was
    skipped and the link to the full record."""
    git = record.get("git") or {}
    # A table rather than prose: its rows may be as long as the values are.
    lines = ["### Run {}".format(record["stamp"]), "", "| What | Value |", "|---|---|"]
    lines.append("| Date | {} to {} |".format(record["date"], record.get("finished") or "unknown"))
    lines.append("| Commit | `{}`{} on `{}` |".format(git.get("short") or "unknown", " with local changes" if git.get("dirty") else "",
                                                   git.get("branch") or "unknown"))
    build = record.get("build") or {}
    if build:
        lines.append("| Build in the install | " + ", ".join("`{}` {} ({})".format(k, v["sha256"], v["modified"])
                                                           for k, v in build.items()) + " |")
    lines.append("| Machine | " + _machine_line(record) + " |")
    lines.append("| Profile | `{}`: {} |".format(record.get("profile"), "; ".join(record.get("profile_lines") or ["no profile line"])))
    lines.append("| Groups | {} |".format(", ".join("`{}`".format(g) for g in record_groups(record))))
    settle, settle_rebuilt = _settling(record)
    lines.append("| Matrix | `{}`, {} start{}, {} s settling per phase, {} s after a change of the rendering |".format(
        _matrix_shown(record.get("matrix_file")),
        len(record["starts"]), "" if len(record["starts"]) == 1 else "s", settle, settle_rebuilt))
    for what, why in record.get("skipped") or []:
        lines.append("| Skipped | {} ({}) |".format(what, why))
    lines.append("| Full record | [benchmarks/{0}](benchmarks/{0}) |".format(json_name))
    return lines


def _phase_table_lines(record):
    """The table of what ran in each phase: the upscaler and the frame generation that ran, the frames
    presented per rendered frame, what lowered the latency, the reports and the notes."""
    lines = ["| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by "
             "| Reports | fps range | Notes |"]
    lines.append("|" + "---|" * 9)
    for s in record["starts"]:
        if not s.get("phases"):
            lines.append("| {} | – | – | – | – | – | – | – | {} |".format(s["id"], "; ".join(s.get("problems") or [])))
        for p in s.get("phases", []):
            m = p["measured"]
            u = p.get("upscaler")
            ran = "{} {}, {}x{} to {}x{}".format(u["technique"], u["mode"], u["render"][0], u["render"][1],
                                                  u["display"][0], u["display"][1]) if u else "none"
            lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                s["id"], p["label"], ran, p.get("fg_kind_ran") or "none", _fmt(m.get("presented_per_rendered"), 2),
                m.get("latency_lowered_by") or "–", m.get("reports", 0),
                "{}–{}".format(m["fps_min"], m["fps_max"]) if "fps_min" in m else "–",
                "; ".join(p["warnings"]).replace("|", "/")))
    return lines


def _proxy_config_lines(record):
    """The proxy's configuration in each start as it logged it at load, and the DLSS-G package's ini in each
    start that had it, with the values it was read with."""
    lines = ["The proxy's configuration in each start, as the proxy logged it at load:", "", "```"]
    for s in record["starts"]:
        lines.append("{}: {}".format(s["id"], s.get("proxy_load_config_line") or "no Config line"))
    lines.append("```")
    packaged = [s for s in record["starts"] if s.get("package_ini")]
    if packaged:
        lines.append("")
        lines.append("The DLSS-G package's ini in each start that had it, with the values it was read with:")
        lines.append("")
        lines.append("```")
        for s in packaged:
            shown = {sec: {k: v for k, v in keys.items() if k in PACKAGE_KEYS_SHOWN}
                     for sec, keys in s["package_ini"].items()}
            lines.append("{}: {}".format(s["id"], " ".join("[{}] {}".format(sec, " ".join("{}={}".format(k, v) for k, v in keys.items()))
                                                           for sec, keys in shown.items() if keys)))
        lines.append("```")
    return lines


def _probe_notes_lines(record):
    """The probe's scene arguments of the starts that had any, and the raytracing's frames that built 50
    meshes or more at the flight's start; empty where there are none."""
    lines = []
    scened = [s for s in record["starts"] if s.get("probe_args")]
    if scened:
        lines.append("")
        lines.append("The probe's scene arguments: " + "; ".join("{}: `{}`".format(s["id"], " ".join(s["probe_args"])) for s in scened) + ".")
    built = [s for s in record["starts"] if s.get("rt_builds")]
    if built:
        lines.append("")
        lines.append("The raytracing's frames that built 50 meshes or more, at the flight's start:")
        lines.append("")
        for s in built:
            b = s["rt_builds"]
            lines.append("* {}: {} frames, {} meshes, {} triangles;".format(
                s["id"], b["frames"], b["meshes"], b["triangles"]))
            lines.append("  the slowest {} ms of the GPU, {} ms of the CPU".format(
                _fmt(b["slowest_gpu_ms"], 2), _fmt(b["slowest_cpu_ms"], 2)))
    return lines


def load_records(folder=RESULTS):
    """Every run's record in the folder as (file name, record), by file name, so oldest first."""
    if not os.path.isdir(folder):
        return []
    records = []
    for n in sorted(os.listdir(folder)):
        if n.endswith(".json"):
            with open(os.path.join(folder, n), encoding="utf-8") as f:
                records.append((n, json.load(f)))
    return records


def _wrapped(text, width=88):
    """The text broken into lines of at most width characters; empty for no text."""
    return "\n".join(textwrap.wrap(text, width)) if text else ""


def tables_of(records):
    """The comparison tables, one section per group: the group's latest record, its rows
    against the group's baseline and, for the fps, against the record of the group before."""
    holders = {}
    for name, record in records:
        for group in record_groups(record):
            holders.setdefault(group, []).append(record)
    order = [MAIN_GROUP] + [g for g in (group_ids_of(records[-1][1]) if records else []) if g in holders and g != MAIN_GROUP]
    order += sorted(g for g in holders if g not in order)
    sections = []
    for group in order:
        have = holders.get(group)
        if not have:
            continue
        latest = have[-1]
        previous = have[-2] if len(have) > 1 else None
        definition = group_definition(latest, group)
        scene_names = (latest.get("matrix") or {}).get("scenes", {})
        text = [("### " + definition.get("name", group)) + " (`{}`)".format(group), ""]
        if definition.get("description") or latest.get("note"):
            text += [_wrapped(definition.get("description") or latest["note"]), ""]
        text += [_wrapped("Run `{}`. Its machine and options are under [the runs](#the-runs).".format(latest["stamp"])), ""]
        for s in latest["starts"]:
            if (s.get("group") or record_groups(latest)[0]) == group and not s.get("phases") and s.get("problems"):
                text += [_wrapped("Not measured, start `{}`: {}.".format(s["id"], "; ".join(s["problems"]).rstrip("."))), ""]
        if group != MAIN_GROUP and any(g["id"] == group for g in (latest.get("matrix") or {}).get("groups", [])):
            text += [_wrapped("Run it with `python tools/benchmark_matrix.py run --group {}`.".format(group)), ""]
        if definition.get("scenes"):
            text += [_wrapped("Scenes: " + ", ".join("{} ({})".format(s, scene_names.get(s, {}).get("name", s))
                                                    for s in definition["scenes"]) + "."), ""]
        text.append(summary_table(latest, previous, group))
        sections.append("\n".join(text))
    return "\n\n".join(sections)


def group_ids_of(record):
    """The ids of the groups of the record's matrix, the main matrix first."""
    return [g["id"] for g in group_list(record.get("matrix") or {})]


def render_doc(records, doc_text):
    """benchmarks.md with the table of each group's latest run and every run's section, newest
    first, between the markers; the text around the markers stays as written."""
    table = tables_of(records) if records else "No run yet."
    runs = "\n".join(run_section(r, n) for n, r in reversed(records)) if records else "No run yet.\n"
    doc_text = _replace_between(doc_text, TABLE_START, TABLE_END, "\n" + table + "\n")
    return _replace_between(doc_text, RUNS_START, RUNS_END, "\n" + runs)


def _replace_between(text, start, end, body):
    """The text with what lies between the two markers replaced by body; the markers stay."""
    a = text.index(start) + len(start)
    b = text.index(end)
    return text[:a] + body + text[b:]


def write_results(record, no_doc=False):
    """Writes the record to docs/development/benchmarks/<stamp>.json and, unless no_doc, benchmarks.md again."""
    os.makedirs(RESULTS, exist_ok=True)
    name = record["stamp"] + ".json"
    with open(os.path.join(RESULTS, name), "w", encoding="utf-8") as f:
        json.dump(record, f, indent=1, ensure_ascii=False)
    print("written:", os.path.join(RESULTS, name))
    if not no_doc:
        render(None)


def render(_args):
    """The render command: benchmarks.md written again from every record in the results folder."""
    with open(DOC, encoding="utf-8") as f:
        text = f.read()
    text = render_doc(load_records(), text)
    with open(DOC, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("written:", DOC)
