#!/usr/bin/env python
"""Ports ndepoel/FSR3Unity so its compute shaders compile with Unity 2019.4.18f1
-- the version KSP 1.12.5 was built with.

Two things stand in the way, both only available from Unity 2020.1:

  1. "#pragma multi_compile" in compute shaders. FSR3Unity uses it for seven
     keywords. Six of those are set once by the runtime at initialisation and
     never again -- for a fixed integration into KSP the combination is therefore
     known up front and can be baked in as a define. Only APPLY_SHARPENING
     switches at runtime, so a second variant of the accumulate pass is
     generated for it.

  2. CommandBuffer.SetComputeConstantBufferParam. Without that API an explicitly
     registered cbuffer cannot be filled. Global uniforms set through
     SetComputeIntParams/SetComputeFloatParams do not work either: those calls
     write raw into the constant buffer and follow the HLSL layout rules, which
     align elements to float4, so an int2 occupies 16 bytes instead of 8 and every
     field lands on the next one.

     So every cbuffer becomes a StructuredBuffer. SetComputeBufferParam
     already exists in 2019.4, the struct goes across as a whole, and FSR3Unity's
     C# structs are laid out 16 byte friendly anyway, so both sides see the same
     layout. The getter functions and all FidelityFX passes stay unchanged apart
     from the accesses to the constants, which are redirected to the struct
     member.

It also adds FSR 3.1.4's four tuning constants, which FSR3Unity's FSR 3.1.3 does
not have.

The FSR files in unity/Assets/ReDefinition/Shaders are replaced; Unity's .meta
files and ReDefinition's own shaders there stay.

Usage:  python tools/port_fsr3_shaders.py <FSR3Unity checkout>
"""

import io
import os
import re
import sys

# The keyword combination for KSP. Reasoning per line, because a wrong value here
# gives no compiler error but a wrong image.
BAKED_DEFINES = [
    # KSP's motion vector buffer is produced at the camera's render resolution --
    # and with upscaling active that is the low one.
    ("FFX_FSR3UPSCALER_OPTION_LOW_RESOLUTION_MOTION_VECTORS", True),
    # Unity's motion vectors already contain the jitter, FSR does not have to
    # cancel it out.
    ("FFX_FSR3UPSCALER_OPTION_JITTERED_MOTION_VECTORS", False),
    # Fsr3Upscaler.CreateContext sets EnableDepthInverted itself as soon as
    # SystemInfo.usesReversedZBuffer holds -- and under D3D11 it always does.
    # UpscalerRig swaps near and far as well, as FSR3Unity's image effect does.
    ("FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH", True),
    # Only useful at wave width 64, and the runtime only queries that from Unity
    # 2022 onwards anyway.
    ("FFX_FSR3UPSCALER_OPTION_REPROJECT_USE_LANCZOS_TYPE", False),
    # 16 bit path: off.
    ("FFX_HALF", False),
    # Single pass instanced for VR. KSP renders monoscopically.
    ("UNITY_FSR_TEXTURE2D_X_ARRAY", False),
]

SHARPEN_KEYWORD = "FFX_FSR3UPSCALER_OPTION_APPLY_SHARPENING"

# Set per variant, not baked in.
#
# KSP computes in gamma colour space and renders without HDR in the editor, with
# it in flight. A forced floating point buffer adds no information there but
# changes the clamping during blending and turns KSP's water at the horizon
# transparent, so both sets exist and the integration takes the one that matches
# the camera.
HDR_KEYWORD = "FFX_FSR3UPSCALER_OPTION_HDR_COLOR_INPUT"

# FSR 3.1.4's tuning constants (AMD's FFX_API_CONFIGURE_UPSCALE_KEY_*,
# super-resolution-upscaler.md), which FSR3Unity's FSR 3.1.3 does not have.
#
# fShadingChangeScale scales FSR's own shading change detection at read time.
# Without a reactive mask, AMD: "shading change detection logic will handle
# these cases as best it can".
#
# fMinDisocclusionAccumulation takes the place of the literal 0.25 in FSR3Unity's
# prepare reactivity pass, and fAccumulationAddedPerFrame that of 1/3. ffxMin is
# symmetric, so the order of its arguments does not matter.
BACKPORTED_CONSTANTS = [
    "fReactivenessScale",
    "fShadingChangeScale",
    "fAccumulationAddedPerFrame",
    "fMinDisocclusionAccumulation",
]

PRAGMA_MULTI_COMPILE = re.compile(r"^\s*#pragma\s+multi_compile(_local)?\s")
PRAGMA_KERNEL = re.compile(r"^\s*#pragma\s+kernel\s")
CBUFFER_OPEN = re.compile(r"^cbuffer\s+(\w+)\s*:\s*FFX_\w*DECLARE_CB\(")


def read(path):
    with io.open(path, encoding="utf-8") as handle:
        return handle.read()


def write(path, text):
    directory = os.path.dirname(path)
    if directory and not os.path.isdir(directory):
        os.makedirs(directory)
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def backport_constants(source):
    """Adds FSR 3.1.4's constants to the callbacks header and uses them."""
    declarations = "\n".join(
        "    FfxFloat32    %s;" % name for name in BACKPORTED_CONSTANTS)

    getters = "\n\n".join(
        "FfxFloat32 %s()\n{\n    return %s;\n}" % (name[1:], name)
        for name in BACKPORTED_CONSTANTS)

    pairs = [
        ("    FfxFloat32    fVelocityFactor;",
         "    FfxFloat32    fVelocityFactor;\n\n"
         "    // FSR 3.1.4's tuning constants (tools/port_fsr3_shaders.py).\n"
         + declarations),

        ("FfxFloat32 VelocityFactor()\n{\n    return fVelocityFactor;\n}",
         "FfxFloat32 VelocityFactor()\n{\n    return fVelocityFactor;\n}\n\n" + getters),

        ("    return r_reactive_mask[UNITY_FSR_POS(iPxPos)];",
         "    return r_reactive_mask[UNITY_FSR_POS(iPxPos)] * ReactivenessScale();"),

        ("    return r_reactive_mask.SampleLevel(s_LinearClamp, UNITY_FSR_UV(fUV), 0).x;",
         "    return r_reactive_mask.SampleLevel(s_LinearClamp, UNITY_FSR_UV(fUV), 0).x"
         " * ReactivenessScale();"),

        ("    return r_shading_change[iPxPos];",
         "    return r_shading_change[iPxPos] * ShadingChangeScale();"),

        ("    return r_shading_change.SampleLevel(s_LinearClamp, fUV, 0);",
         "    return r_shading_change.SampleLevel(s_LinearClamp, fUV, 0) * ShadingChangeScale();"),
    ]

    for old, new in pairs:
        if old not in source:
            raise SystemExit("backport anchor not found: " + old.strip()[:60])
        source = source.replace(old, new, 1)
    return source


def backport_accumulation(source):
    """Uses two of the constants in place of FSR3Unity's literals."""
    pairs = [
        ("    fAccumulation = ffxLerp(fAccumulation, ffxMin(fAccumulation, 0.25f), fDisocclusion);",
         "    fAccumulation = ffxLerp(fAccumulation,"
         " ffxMin(MinDisocclusionAccumulation(), fAccumulation), fDisocclusion);"),

        ("    const FfxFloat32 fAccumulatedFramesMax = 3.0f;" + chr(10) +
         "    const FfxFloat32 fAccumulatedFramesToStore ="
         " ffxSaturate(fAccumulation + (1.0f / fAccumulatedFramesMax));",
         "    const FfxFloat32 fAccumulatedFramesToStore ="
         " ffxSaturate(fAccumulation + AccumulationAddedPerFrame());"),
    ]

    for old, new in pairs:
        if old not in source:
            raise SystemExit("accumulation anchor not found: " + old.strip()[:60])
        source = source.replace(old, new, 1)
    return source


def bake_compute(source, with_sharpening, hdr):
    """Replaces the multi_compile pragmas with the fixed keyword combination."""
    lines = source.split("\n")
    out = []
    inserted = False
    dropped = 0

    for line in lines:
        if PRAGMA_MULTI_COMPILE.match(line):
            dropped += 1
            continue

        out.append(line)

        if not inserted and PRAGMA_KERNEL.match(line):
            inserted = True
            out.append("")
            out.append("// Baked in by tools/port_fsr3_shaders.py -- multi_compile")
            out.append("// only exists in compute shaders from Unity 2020.1.")
            for name, enabled in BAKED_DEFINES:
                out.append(("#define %s 1" % name) if enabled else ("// off: %s" % name))
            out.append(("#define %s 1" % HDR_KEYWORD) if hdr
                       else ("// off: %s" % HDR_KEYWORD))
            out.append(("#define %s 1" % SHARPEN_KEYWORD) if with_sharpening
                       else ("// off: %s" % SHARPEN_KEYWORD))

    if not inserted:
        raise SystemExit("no pragma kernel found -- file laid out unexpectedly")

    return "\n".join(out), dropped


ACCESSORS = {}

# Field names that must not be touched outside the callbacks file: they are so
# generic that FidelityFX also uses them as local variables and function
# parameters. ffx_spd.h for instance has a parameter numWorkGroups. These
# constants are not needed outside anyway -- the SPD passes read them through the
# getters.
GENERIC_NAMES = frozenset(["mips", "numWorkGroups", "workGroupOffset", "renderSize"])


def convert_cbuffers(source):
    """Turns every cbuffer block into a StructuredBuffer.

    The accesses to the fields are then redirected to the struct member within
    the same file. Not via #define: the field names appear
    elsewhere in the FidelityFX code as local variables -- for instance
    "const FfxFloat32x4 fDeviceToViewDepth = ..." in
    ffx_fsr3upscaler_common.h -- and a macro would wreck those declarations.
    Restricted to the callbacks file the substitution is safe, because there the
    names refer exclusively to the constants.
    """
    lines = source.split(chr(10))
    out = []
    index = 0
    converted = []
    protected = set()      # lines of the struct declarations, which stay as they are
    accessors = ACCESSORS  # field name -> access expression

    field = re.compile(r"^\s*(Ffx\w+)\s+(\w+)\s*;")

    while index < len(lines):
        line = lines[index]
        match = CBUFFER_OPEN.match(line)
        if not match:
            out.append(line)
            index += 1
            continue

        name = match.group(1)
        converted.append(name)

        index += 1
        if not line.rstrip().endswith("{"):
            if lines[index].strip() != "{":
                raise SystemExit("expected brace after cbuffer " + name)
            index += 1

        body = []
        while lines[index].strip() != "};":
            body.append(lines[index])
            index += 1
        index += 1

        fields = [m.group(2) for m in (field.match(e) for e in body) if m]
        if not fields:
            raise SystemExit("no fields in cbuffer " + name)

        struct_name = "KspCb_" + name
        buffer_name = "KspCbBuf_" + name
        for name_of_field in fields:
            accessors[name_of_field] = "%s[0].%s" % (buffer_name, name_of_field)

        out.append("// cbuffer %s -> StructuredBuffer (tools/port_fsr3_shaders.py)."
                   % name)
        out.append("// SetComputeConstantBufferParam only exists from Unity 2020.1, and")
        out.append("// global uniforms fail on the float4 alignment of SetInts.")
        out.append("struct %s" % struct_name)
        out.append("{")
        for entry in body:
            protected.add(len(out))
            out.append(entry)
        out.append("};")
        out.append("StructuredBuffer<%s> %s;" % (struct_name, buffer_name))
        out.append("")

    for number in range(len(out)):
        if number not in protected:
            out[number] = rewrite_line(out[number])

    return chr(10).join(out), converted


def rewrite_line(line, inside_callbacks=True):
    """Redirects field accesses to the struct member.

    Declarations are left alone: ffx_fsr3upscaler_common.h keeps a local variable
    named fDeviceToViewDepth, and that must not turn into a buffer access -- it
    would become a nonsensical declaration.
    """
    usable = dict(ACCESSORS)
    if not inside_callbacks:
        for name in GENERIC_NAMES:
            usable.pop(name, None)
    if not usable:
        return line

    boundary = chr(92) + "b"
    names = "|".join(sorted(usable, key=len, reverse=True))

    # Skip declarations and parameter lists -- there the name is followed by an
    # equals sign, semicolon, comma or a closing parenthesis.
    declaration = re.compile(r"(?:const\s+)?Ffx\w+\s+(" + names + r")\s*[=;,)]")
    if declaration.search(line):
        return line

    pattern = re.compile(boundary + "(" + names + ")" + boundary)
    return pattern.sub(lambda m: usable[m.group(1)], line)


def remove_ported(dst_root):
    """Removes the files an earlier port wrote -- the headers and HLSL under
    shaders/, the common include and the FSR 3 compute shaders. Unity's .meta files
    and every other file stay."""
    removed = 0
    for folder, _dirs, files in os.walk(dst_root):
        inside_shaders = os.path.relpath(folder, dst_root).split(os.sep)[0] == "shaders"
        for name in files:
            ported = (inside_shaders and name.endswith((".h", ".hlsl"))) or (
                folder == dst_root and (name == "ffx_fsr_unity_common.cginc" or
                                        (name.startswith("ffx_fsr3upscaler_") and name.endswith(".compute"))))
            if ported:
                os.remove(os.path.join(folder, name))
                removed += 1
    return removed


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)

    src_root = os.path.join(sys.argv[1], "Packages", "fidelityfx.fsr", "Shaders")
    if not os.path.isdir(src_root):
        raise SystemExit("FSR3Unity shaders not found under " + src_root)

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    dst_root = os.path.join(repo, "unity", "Assets", "ReDefinition", "Shaders")

    if os.path.isdir(dst_root):
        remove_ported(dst_root)

    # 1. Take over headers and HLSL. The FSR2 passes stay out, only FSR3 is
    #    needed -- but the shared ffx_core headers are required.
    copied = 0
    pending = []
    for folder, _dirs, files in os.walk(os.path.join(src_root, "shaders")):
        if os.path.basename(folder) == "fsr2":
            continue
        rel_dir = os.path.relpath(folder, src_root)
        for name in files:
            if not name.endswith((".h", ".hlsl")):
                continue
            if name.startswith("ffx_fsr2_"):
                continue
            pending.append((os.path.join(folder, name), os.path.join(dst_root, rel_dir, name), name))
            copied += 1

    # The header with the cbuffer blocks first: it establishes the accesses that
    # are then needed in the pass files too -- the TCR autogen pass for instance
    # reads fReactiveScale directly.
    for source_path, target_path, name in pending:
        if name != "ffx_fsr3upscaler_callbacks_hlsl.h":
            continue
        text, converted = convert_cbuffers(backport_constants(read(source_path)))
        print("  cbuffer -> StructuredBuffer: " + ", ".join(converted))
        print("  FSR 3.1.4 constants: " + ", ".join(BACKPORTED_CONSTANTS))
        write(target_path, text)

    for source_path, target_path, name in pending:
        if name == "ffx_fsr3upscaler_callbacks_hlsl.h":
            continue
        text = read(source_path)
        if name == "ffx_fsr3upscaler_prepare_reactivity.h":
            text = backport_accumulation(text)
        write(target_path, chr(10).join(rewrite_line(l, False) for l in text.split(chr(10))))

    write(os.path.join(dst_root, "ffx_fsr_unity_common.cginc"),
          read(os.path.join(src_root, "ffx_fsr_unity_common.cginc")))
    copied += 1

    # 2. Compute shaders with the fixed keyword combination.
    baked = 0
    for name in sorted(os.listdir(src_root)):
        if not name.endswith(".compute") or not name.startswith("ffx_fsr3upscaler_"):
            continue
        source = read(os.path.join(src_root, name))

        sharpen_options = [False]
        if SHARPEN_KEYWORD in source:
            sharpen_options.append(True)

        for hdr in (True, False):
            for sharpening in sharpen_options:
                out_name = name.replace(".compute", "")
                if sharpening:
                    out_name += "_sharpen"
                if not hdr:
                    out_name += "_ldr"
                out_name += ".compute"

                text, dropped = bake_compute(source, sharpening, hdr)
                write(os.path.join(dst_root, out_name), text)
                baked += 1

        print("  %-52s %d pragma(s) replaced, %d variant(s)"
              % (name, dropped, 2 * len(sharpen_options)))

    print("\n%d headers/HLSL copied, %d compute shaders baked." % (copied, baked))


if __name__ == "__main__":
    main()
