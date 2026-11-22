"""Compile one pass of a Unity CG shader with fxc as Unity does for D3D11 and print what the GPU must hold: temp registers, indexable
temp arrays (local memory) and instruction counts. Offline, no Unity, no game.

Usage: python tools/shader_stats.py [--shader PATH] [--pass N] [--define NAME ...] [--keep]
The CGINCLUDE block plus pass N's CGPROGRAM body are written to one HLSL file and compiled with /T ps_5_0 /E frag (Unity's target 3.5
maps to SM 4.x/5 on D3D11; ps_5_0 gives the same register picture).
"""
import argparse
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEF_SHADER = os.path.join(ROOT, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresLayers.shader")
CGINC = r"C:\Program Files\Unity\Editor 2019.4.18f1\Editor\Data\CGIncludes"


def find_fxc():
    base = r"C:\Program Files (x86)\Windows Kits\10\bin"
    best = None
    for v in sorted(os.listdir(base)):
        p = os.path.join(base, v, "x64", "fxc.exe")
        if os.path.exists(p):
            best = p
    return best


def extract(shader, pass_index):
    lines = open(shader, encoding="utf-8-sig").read().split("\n")
    inc, i = [], 0
    while i < len(lines) and "CGINCLUDE" not in lines[i]:
        i += 1
    i += 1
    while i < len(lines) and "ENDCG" not in lines[i]:
        inc.append(lines[i])
        i += 1
    passes, cur, inside = [], [], False
    for ln in lines[i + 1:]:
        if "CGPROGRAM" in ln:
            inside, cur = True, []
            continue
        if "ENDCG" in ln and inside:
            passes.append(cur)
            inside = False
            continue
        if inside:
            cur.append(ln)
    body = [l for l in passes[pass_index] if not l.strip().startswith("#pragma")]
    return inc, body


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shader", default=DEF_SHADER)
    ap.add_argument("--pass", dest="pass_index", type=int, default=0)
    ap.add_argument("--define", action="append", default=[])
    ap.add_argument("--keep", action="store_true")
    a = ap.parse_args()
    inc, body = extract(a.shader, a.pass_index)
    src = "\n".join(inc + body) + "\n"
    tmp = tempfile.mkdtemp(prefix="shaderstats_")
    hl = os.path.join(tmp, "pass%d.hlsl" % a.pass_index)
    with open(hl, "w", encoding="utf-8") as fh:
        fh.write(src)
    lst = os.path.join(tmp, "pass%d.asm" % a.pass_index)
    # /Gec: the backwards compatibility Unity's D3D11 compile uses (half globals); Unity sets its own UNITY_COMPILER_HLSL.
    args = [find_fxc(), "/nologo", "/T", "ps_5_0", "/E", "frag", "/O3", "/Gec", "/Fc", lst,
            "/I", CGINC, "/I", os.path.dirname(a.shader),
            "/D", "SHADER_API_D3D11=1", "/D", "SHADER_TARGET=35", "/D", "UNITY_VERSION=201904"]
    for d in a.define:
        args += ["/D", d]
    args.append(hl)
    p = subprocess.run(args, capture_output=True, text=True)
    if p.returncode != 0:
        print(p.stdout[-3000:])
        print(p.stderr[-3000:])
        print("fxc failed; source kept at", hl)
        return 1
    asm = open(lst, encoding="utf-8", errors="replace").read()
    temps = re.search(r'dcl_temps\s+(\d+)', asm)
    idx = re.findall(r'dcl_indexableTemp\s+x(\d+)\[(\d+)\],\s*(\d+)', asm)
    m = re.search(r'//\s*Approximately\s+(\d+)\s+instruction slots used', asm)
    loops = len(re.findall(r'^\s*loop\b', asm, re.M))
    samples = len(re.findall(r'^\s*sample', asm, re.M))
    print("pass %d defines %s" % (a.pass_index, a.define or "-"))
    print("  temp registers (float4): %s" % (temps.group(1) if temps else "?"))
    print("  indexable temp arrays (local memory): %s" % (", ".join("x%s[%s] x %s comps" % t for t in idx) or "none"))
    print("  instruction slots: %s, loops: %d, texture sample ops: %d" % (m.group(1) if m else "?", loops, samples))
    print("  listing: %s" % lst)
    return 0


if __name__ == "__main__":
    sys.exit(main())
