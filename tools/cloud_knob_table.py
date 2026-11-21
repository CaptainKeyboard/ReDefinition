"""The cloud knob table: every tuning key and setting that reaches the own clouds, traced from the code, never from memory.

For each Tuning.Get / Tuning.Flag call in src/Atmospheres (probes excluded) the table gives the key, the default as written and as resolved,
the statement that reads it (the target it sets), and, where the target is a field of a frame object that LayerRenderer (or another cloud
renderer) uploads, the uniform component it lands in and the shader lines that read that component. A key whose value is clamped,
max()'d or otherwise bounded on the way is shown with the whole expression, so a bound is visible where it is.

Usage: python tools/cloud_knob_table.py [--out FILE]   (default: .local/plans/wolken-regler-tabelle.md)
"""
import argparse
import os
import re
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "Atmospheres")
SHADERS = os.path.join(ROOT, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders")

# Files whose readers belong to the own clouds (march, reconstruction, light, shadow, culling, maps feeding the march).
CLOUD_PARTS = ("Settings/OwnCloudOptions.cs", "Light/Cloud/", "Render/Layers/", "Render/Recon", "Settings/Profiles/")


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def read(p):
    with open(p, encoding="utf-8-sig", errors="replace") as fh:
        return fh.read().split("\n")


def cs_files():
    for d, _, fs in os.walk(SRC):
        for f in fs:
            if f.endswith(".cs"):
                p = os.path.join(d, f)
                r = rel(p)
                if "/Probe/" in r:
                    continue
                yield p


def shader_files():
    for d, _, fs in os.walk(SHADERS):
        for f in fs:
            if f.endswith((".shader", ".cginc", ".compute", ".hlsl")):
                yield os.path.join(d, f)


STR_CONST = re.compile(r'\b([A-Z][A-Za-z0-9]*)\s*=\s*"([a-zA-Z]+\.[A-Za-z0-9.]+)"')
NUM_CONST = re.compile(r'\bconst\s+(?:float|int|bool|double)\s+([A-Za-z0-9_]+)\s*=\s*([^;]+);')
FIELD_INIT = re.compile(r'^\s*(?:public|internal)\s+(?:float|int|bool|double)\s+(.+);')
GET = re.compile(r'Tuning\.(Get|Flag)\(\s*([A-Za-z0-9_."]+)\s*,\s*([^)]*(?:\([^)]*\)[^)]*)*)\)')
CLASS = re.compile(r'\b(?:class|struct)\s+([A-Za-z0-9_]+)')


def scan_cs():
    keyconst = {}       # const name -> key
    numconst = {}       # const name -> value text
    fields = {}         # Class.Field -> initializer text
    reads = []          # dicts
    for p in cs_files():
        lines = read(p)
        cls = None
        for i, ln in enumerate(lines, 1):
            m = CLASS.search(ln)
            if m and not ln.strip().startswith("//"):
                cls = m.group(1)
            code = ln.split("//")[0]
            for n, k in STR_CONST.findall(code):
                keyconst[n] = k
            m = NUM_CONST.search(code)
            if m:
                # const float A = 6f, B = 0f;  -> each name its own value
                chunks = (m.group(1) + " = " + m.group(2)).split(",")
                for ch in chunks:
                    if "=" in ch:
                        n, v = ch.split("=", 1)
                        numconst[n.strip()] = v.strip()
            m = FIELD_INIT.match(code)
            if m and cls and "(" not in m.group(1):
                for part in m.group(1).split(","):
                    if "=" in part:
                        n, v = part.split("=", 1)
                        fields[cls + "." + n.strip()] = v.strip()
            for m in GET.finditer(code):
                reads.append({"file": rel(p), "line": i, "kind": m.group(1), "keyexpr": m.group(2), "default": m.group(3).strip(),
                              "stmt": code.strip(), "cls": cls})
    return keyconst, numconst, fields, reads


def resolve(expr, numconst, fields, depth=0):
    e = expr.strip()
    if depth > 4:
        return e
    if re.fullmatch(r'-?[0-9.]+f?|true|false', e):
        return e.rstrip("f") if re.fullmatch(r'-?[0-9.]+f', e) else e
    if e.startswith("CompiledFrame."):
        v = fields.get("LayerFrame." + e.split(".", 1)[1])
        return resolve(v, numconst, fields, depth + 1) if v else e
    if e.startswith("CompiledCull."):
        v = fields.get("CullSettings." + e.split(".", 1)[1])
        return resolve(v, numconst, fields, depth + 1) if v else e
    name = e.split(".")[-1]
    if name in numconst:
        return resolve(numconst[name], numconst, fields, depth + 1)
    for k, v in fields.items():
        if k.endswith("." + name) and "." in e:
            return resolve(v, numconst, fields, depth + 1)
    return e


TARGET = re.compile(r'^(?:[A-Za-z<>\[\]]+\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*=[^=]')


def target_of(stmt):
    m = TARGET.match(stmt)
    return m.group(1) if m else ""


def uniform_map():
    """Field name -> list of (uniform, component, upload expression, file:line), from SetVector/SetFloat calls on the cloud materials."""
    idname = {}
    uploads = defaultdict(list)
    comp = "xyzw"
    for p in cs_files():
        r = rel(p)
        if not any(c in r for c in CLOUD_PARTS):
            continue
        lines = read(p)
        text = "\n".join(lines)
        for m in re.finditer(r'(Id[A-Za-z0-9]+)\s*=\s*Shader\.PropertyToID\("(_[A-Za-z0-9]+)"\)', text):
            idname[m.group(1)] = m.group(2)
        for i, ln in enumerate(lines, 1):
            m = re.search(r'\.Set(Vector|Float|Int)\((Id[A-Za-z0-9]+),\s*(.*)\);', ln)
            if not m:
                continue
            args = m.group(3)
            if m.group(1) == "Vector" and args.startswith("new Vector4("):
                inner = args[len("new Vector4("):-1]
                parts, depth, cur = [], 0, ""
                for ch in inner:
                    if ch == "(":
                        depth += 1
                    elif ch == ")":
                        depth -= 1
                    if ch == "," and depth == 0:
                        parts.append(cur)
                        cur = ""
                    else:
                        cur += ch
                parts.append(cur)
                for ci, pexpr in enumerate(parts[:4]):
                    for fm in re.findall(r'\b(?:f|s|c|frame|settings)\.([A-Za-z0-9_]+)', pexpr):
                        uploads[fm].append((m.group(2), comp[ci], pexpr.strip(), r + ":" + str(i)))
            else:
                for fm in re.findall(r'\b(?:f|s|c|frame|settings)\.([A-Za-z0-9_]+)', args):
                    uploads[fm].append((m.group(2), "", args.strip(), r + ":" + str(i)))
    return idname, uploads


def shader_uses():
    uses = defaultdict(list)
    for p in shader_files():
        for i, ln in enumerate(read(p), 1):
            code = ln.split("//")[0]
            seen = set()
            for m in re.finditer(r'\b(_[A-Za-z0-9]+)\.([xyzw])\b', code):
                k = (m.group(1), m.group(2))
                if k in seen:
                    continue
                seen.add(k)
                uses[k].append(rel(p).split("/")[-1] + ":" + str(i) + "  " + code.strip()[:150])
            for m in re.finditer(r'\b(_[A-Za-z0-9]+)\b(?!\.)', code):
                k = (m.group(1), "")
                if k in seen:
                    continue
                seen.add(k)
                uses[k].append(rel(p).split("/")[-1] + ":" + str(i))
    return uses


def field_writes():
    """Field name -> every assignment `.Field =` in the cloud code (who else sets it after the key was read)."""
    writes = defaultdict(list)
    for p in cs_files():
        r = rel(p)
        if not any(c in r for c in CLOUD_PARTS):
            continue
        for i, ln in enumerate(read(p), 1):
            code = ln.split("//")[0]
            for m in re.finditer(r'\.([A-Z][A-Za-z0-9_]*)\s*=[^=>]', code):
                writes[m.group(1)].append("%s:%d  %s" % (r, i, code.strip()[:160]))
    return writes


def settings_table():
    rows = []
    p = os.path.join(SRC, "Settings", "AtmospheresSettings.Clouds.cs")
    for i, ln in enumerate(read(p), 1):
        m = re.search(r'SettingDef\.(Bool|Int|Float|Choice)\("([A-Za-z0-9]+)",\s*([^,]+(?:,[^,"]+)*?),\s*CL\b', ln)
        if m:
            rows.append((m.group(2), m.group(1), m.group(3).strip(), rel(p) + ":" + str(i)))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(ROOT, ".local", "plans", "wolken-regler-tabelle.md"))
    a = ap.parse_args()
    keyconst, numconst, fields, reads = scan_cs()
    idname, uploads = uniform_map()
    uses = shader_uses()
    writes = field_writes()

    cloud_reads = [r for r in reads if any(c in r["file"] for c in CLOUD_PARTS)]
    out = []
    out.append("# Cloud knob table (generated by tools/cloud_knob_table.py; do not edit by hand)\n")
    out.append("Every Tuning.Get / Tuning.Flag in the cloud code (OwnCloudOptions, Light/Cloud, Render/Layers, Render/Recon*, Settings/Profiles), probes excluded.")
    out.append("Columns: key | default as written -> resolved | reader (file:line) | statement (shows clamps and overrides) | uploaded as | shader readers of that component.\n")
    bykey = defaultdict(list)
    for r in cloud_reads:
        ke = r["keyexpr"]
        key = ke.strip('"') if ke.startswith('"') else keyconst.get(ke.split(".")[-1], "?" + ke)
        r["key"] = key
        bykey[key].append(r)
    out.append("## Tuning keys (%d keys, %d reads)\n" % (len(bykey), len(cloud_reads)))
    for key in sorted(bykey):
        for r in bykey[key]:
            d = r["default"]
            res = resolve(d, numconst, fields)
            tgt = target_of(r["stmt"])
            field = tgt.split(".")[-1] if "." in tgt else ""
            up = uploads.get(field, []) if field else []
            out.append("### %s\n" % key)
            out.append("- default: `%s`%s" % (d, "" if res == d else " -> `%s`" % res))
            out.append("- reader: %s:%d (class %s)" % (r["file"], r["line"], r["cls"]))
            out.append("- statement: `%s`" % r["stmt"][:260])
            if field:
                others = [w for w in writes.get(field, []) if not w.startswith("%s:%d " % (r["file"], r["line"]))]
                if others:
                    out.append("- other writes of `.%s` (may override the key):" % field)
                    for w in others[:8]:
                        out.append("    - `%s`" % w)
                    if len(others) > 8:
                        out.append("    - ... %d more" % (len(others) - 8))
            if up:
                for (idn, c, expr, where) in up:
                    u = idname.get(idn, idn)
                    out.append("- uploaded: %s%s = `%s` (%s)" % (u, "." + c if c else "", expr[:120], where))
                    lines = uses.get((u, c), []) if c else uses.get((u, ""), [])
                    if lines:
                        out.append("  - shader reads (%d): " % len(lines))
                        for l in lines[:6]:
                            out.append("    - `%s`" % l)
                        if len(lines) > 6:
                            out.append("    - ... %d more" % (len(lines) - 6))
                    else:
                        out.append("  - shader reads: NONE found for this component")
            elif field:
                out.append("- uploaded: field `%s` not found in a SetVector of the cloud renderers (read in C# only, or uploaded under another name)" % field)
            else:
                out.append("- uploaded: local value `%s`; its next uses:" % (tgt or "?"))
                if tgt:
                    src = read(os.path.join(ROOT, r["file"]))
                    n = 0
                    for j in range(r["line"], min(len(src), r["line"] + 40)):
                        c = src[j].split("//")[0]
                        if re.search(r'\b%s\b' % re.escape(tgt), c):
                            out.append("    - `%s:%d  %s`" % (r["file"], j + 1, c.strip()[:180]))
                            n += 1
                            if n >= 4:
                                break
            out.append("")
    out.append("## Player settings of the clouds (AtmospheresSettings.Clouds.cs)\n")
    out.append("| key | type | default, range | where |\n|---|---|---|---|")
    for k, t, d, w in settings_table():
        out.append("| %s | %s | %s | %s |" % (k, t, d.replace("|", "/"), w))
    out.append("\n## Profile rows (TierRows.cs, as written)\n")
    tp = os.path.join(SRC, "Settings", "Profiles", "TierRows.cs")
    for i, ln in enumerate(read(tp), 1):
        if "new TierRow(" in ln or "TierRow(QualityTier" in ln and "static" in ln:
            out.append("- `%s` (TierRows.cs:%d)" % (ln.strip()[:220], i))
    out.append("\n## Look constants (OwnCloudOptions.Look*)\n")
    for n, v in sorted(numconst.items()):
        if n.startswith("Look"):
            out.append("- %s = %s" % (n, v))
    unknown = [k for k in bykey if k.startswith("?")]
    if unknown:
        out.append("\n## Unresolved key expressions\n")
        for k in unknown:
            out.append("- " + k)
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out) + "\n")
    print("%d keys, %d reads, %d unresolved -> %s" % (len(bykey), len(cloud_reads), len(unknown), a.out))


if __name__ == "__main__":
    sys.exit(main())
