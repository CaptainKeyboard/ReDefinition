#!/usr/bin/env python3
"""Writes the per-body EVE_CLOUDS blocks of ReDefinition Atmospheres from one template.

  python tools/gen_eve_clouds.py            write GameData/ReDefinition/Atmospheres/Clouds/Clouds-<Body>.cfg and Blank.png
  python tools/gen_eve_clouds.py --check    exit 1 when a checked-in file differs from what the template makes
  python tools/gen_eve_clouds.py --list     print the layers and cloud types the template makes

One block per supported body (Kerbin, Duna, Laythe): the VOLUME layer the cloud-map painter (C23) writes its coverage map and type map
to. It holds every cloud type the painter can select (the type count is read at load time only), a layer2D for the orbit picture, and
placeholder maps. The pack's own layers keep the particle fields (rain, snow, dust); this layer carries none.

The cloud types per level are the table of src/Atmospheres/CONTRACT.md, section 5 ("Cloud types per level"). The names of the types
are the defaults of the role lists of the body's CLOUDS node (snowTypeNames = Polar-snow, Snow; stratusTypeNames = Stratus; a name
with "storm" in it is the storm type; dustTypeNames is set by Duna.cfg), so the roles are found without a body key. EVE's cloud type
has no colour key: the lightness of a type is its density and its multipleScatteringBrightness, and the layer's `color` is the light
grey of the lightest type [VERIFY the look of the storm base in the game].

The numbers of the phase functions, the worley noise, the raymarching settings and the scaled fade of Kerbin are those of
AtmospheresStormLayer.cfg (credited in docs/credits-and-licences.md under StockVolumetricClouds). The scaled fade altitudes of the other
bodies scale that file's 200 km and 260 km (Kerbin, radius 600 km) with the radius of the body [VERIFY: the fade is meant for the
atmosphere height; the layer is invisible to the volume pass above the end altitude either way].

Kerbin's body node names its EVE layers (CLOUDS.weatherLayers); the file of Kerbin ends with a ModuleManager patch that adds our layer
to that list [VERIFY the patch syntax in the game's ModuleManager log].

The C# tests of E41 parse the written files; --check is the test that the files are the template's output.

Pattern for a pack (JNSQ, KSRSS, OPM, Beyond Home) that wants a layer of ours on another body: copy the OBJECT of a generated file,
change `name` and `body`, and add it by a ModuleManager patch. A body without a PQS (a gas giant) needs a PQS_MANAGER node first: the
layer is applied to `celestialBody.pqsController`, else to PQSManagerClass.GetPQS(body), and with neither CloudsPQS.Apply logs "PQS is
null somehow!?" and does not apply the layer (CloudsPQS.cs:402-453, PQSManager.cs GetPQS, release-5 tree). The node layout of
PQS_MANAGER is the plan's reading of the pack's pqs.cfg [VERIFY]:
  EVE_CLOUDS:NEEDS[StockVolumetricClouds]
  {
      OBJECT { name = MyPack-Atmospheres-Sarnus  body = Sarnus  altitude = 2700  ...  }
  }
  PQS_MANAGER { OBJECT { body = Sarnus  deactivateDistance = ... } }
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_DIR = os.path.join(ROOT, "GameData", "ReDefinition", "Atmospheres", "Clouds")
TEX = "ReDefinition/Atmospheres/Clouds"

# Cloud types. Altitudes in metres above sea level (EVE's minAltitude/maxAltitude); CONTRACT.md gives them above the ground, which on
# the flat parts the cover covers is the same to a few hundred metres [VERIFY on Duna and Laythe with the terrain height].
#   Snow (SnowLight): the table says "600 to 1500 m, 300 m thick". It is read as a deck 300 m thick with its base at 600 m; the
#   stratus type raises the top to 1500 m and beyond through the blend of the type map. The density 0.002 to 0.003 takes the middle.
TYPES = {
    "Snow": dict(role="SnowLight", min=600, max=900, density=0.0025, tiling=2600, edge=0.5, msb=1.0,
                 cover=[(0, 0, 0, 3), (0.2, 0.7, 0, 0), (1, 0.9, 0, 0)]),
    "Stratus": dict(role="SnowStratus", min=400, max=1800, density=0.0065, tiling=2600, edge=0.7, msb=0.85,
                    cover=[(0, 0, 0, 3), (0.15, 0.8, 0, 0), (1, 0.95, 0, 0)]),
    "Storm": dict(role="Storm", min=300, max=2300, density=0.014, tiling=2600, edge=0.85, msb=0.55,
                  cover=[(0, 0, 0, 5), (0.12, 0.85, 0, 0), (0.5, 0.95, 0, 0), (0.82, 0.35, -3, -3), (0.95, 0, 0, 0)]),
    "Dust": dict(role="Dust", min=150, max=6000, density=0.006, tiling=3400, edge=0.4, msb=1.0,
                 cover=[(0, 0, 0, 2), (0.1, 0.6, 0, 0), (0.6, 0.95, 0, 0), (1, 1, 0, 0)]),
}

# The bodies. radius_m: the body's radius (scaled fade). altitude: the radius of the 2D sphere above the body (the layer2D).
# weather_patch: Kerbin's body node names its layers, so the file adds ours to that list.
BODIES = [
    dict(name="Kerbin", radius_m=600000, altitude=2700, types=["Snow", "Stratus", "Storm"], layer_colour=(190, 192, 196), weather_patch=True),
    dict(name="Duna", radius_m=320000, altitude=2700, types=["Dust"], layer_colour=(196, 150, 110), weather_patch=False),
    dict(name="Laythe", radius_m=500000, altitude=2700, types=["Snow", "Stratus", "Storm"], layer_colour=(190, 192, 196), weather_patch=False),
]

BLANK_W, BLANK_H = 16, 8


def layer_name(body):
    return "ReDefinition-Atmospheres-" + body["name"]


def fade(body):
    # Kerbin: 200000 m to 260000 m at radius 600000 m.
    return int(round(body["radius_m"] / 3.0)), int(round(body["radius_m"] * 13.0 / 30.0))


def curve_lines(keys, indent):
    return "".join("%skey = %s\n" % (indent, " ".join(("%g" % v) for v in k)) for k in keys)


def type_item(name, t, indent):
    i = indent
    j = i + "\t"
    s = i + "Item\n" + i + "{\n"
    s += "%s// %s: %d to %d m, density %g, multipleScatteringBrightness %g\n" % (j, t["role"], t["min"], t["max"], t["density"], t["msb"])
    s += "%stypeName = %s\n" % (j, name)
    s += "%sminAltitude = %d\n%smaxAltitude = %d\n" % (j, t["min"], j, t["max"])
    s += "%sinterpolateCloudHeights = true\n" % j
    s += "%sdensity = %g\n%sbaseNoiseTiling = %d\n%snoiseEdgeHardness = %g\n" % (j, t["density"], j, t["tiling"], j, t["edge"])
    s += "%smultipleScatteringBrightness = %g\n" % (j, t["msb"])
    s += "%sparticleFieldDensity = 0\n%sdropletsDensity = 0\n%swetSurfacesIntensity = 0\n%slightningFrequency = 0\n%sambientVolume = 0\n" % ((j,) * 5)
    s += j + "coverageCurve\n" + j + "{\n" + curve_lines(t["cover"], j + "\t") + j + "}\n"
    s += j + "densityCurve\n" + j + "{\n" + j + "\tkey = 0 1\n" + j + "}\n"
    s += i + "}\n"
    return s


def texture_node(key, indent):
    i = indent
    return ("%s%s\n%s{\n%s\tvalue = %s/Blank\n%s\ttype = AlphaMap\n%s\talphaMask = ALPHAMAP_R\n%s}\n"
            % (i, key, i, i, TEX, i, i, i))


def volume_object(body):
    fs, fe = fade(body)
    r, g, b = body["layer_colour"]
    s = "\tOBJECT\n\t{\n"
    s += "\t\tname = %s\n\t\tbody = %s\n\t\taltitude = %d\n" % (layer_name(body), body["name"], body["altitude"])
    s += "\t\tspeed = 0,0,0\n\t\tdetailSpeed = 0,0,0\n\t\tspeedMode = LinearSurface\n"
    s += "\t\tlayer2D\n\t\t{\n\t\t\tmacroCloudMaterial\n\t\t\t{\n\t\t\t\t_DetailDist = 2E-06\n\t\t\t}\n\t\t}\n"
    s += "\t\tlayerRaymarchedVolumeV5\n\t\t{\n"
    s += "\t\t\tcolor = %d, %d, %d, 255\n" % (r, g, b)
    s += "\t\t\tupwardsCloudSpeed = 2\n\t\t\tscaledFadeStartAltitude = %d\n\t\t\tscaledFadeEndAltitude = %d\n" % (fs, fe)
    s += "\t\t\tskylightMultiplier = 1.0\n\t\t\tskylightTintMultiplier = 0.5\n"
    s += ("\t\t\traymarchingSettings\n\t\t\t{\n\t\t\t\tlightMarchSteps = 4\n\t\t\t\tlightMarchDistance = 1900\n\t\t\t\tbaseStepSize = 120\n"
          "\t\t\t\tadaptiveStepSizeFactor = 0.006\n\t\t\t\tmaxStepSize = 1200\n\t\t\t\toverlapRenderOrder = 3\n\t\t\t}\n")
    s += "\t\t\tnoise\n\t\t\t{\n\t\t\t\terosionDepth = 0.65\n\t\t\t\tworley\n\t\t\t\t{\n\t\t\t\t\tspherical = 1\n\t\t\t\t\tpersistence = 0.57\n\t\t\t\t}\n\t\t\t}\n"
    s += "\t\t\t// Placeholders: the cloud-map painter binds its own render textures to both maps at run time.\n"
    s += texture_node("coverageMap", "\t\t\t")
    s += texture_node("cloudTypeMap", "\t\t\t")
    s += "\t\t\tcloudTypes\n\t\t\t{\n"
    for name in body["types"]:
        s += type_item(name, TYPES[name], "\t\t\t\t")
    s += "\t\t\t}\n"
    s += "\t\t\tlightVolumeSettings\n\t\t\t{\n\t\t\t\tuseLightVolume = False\n\t\t\t}\n"
    s += ("\t\t\tphaseFunctions\n\t\t\t{\n\t\t\t\tsingleScattering1 = 0.95, 0.01\n\t\t\t\tsingleScattering2 = 0.8, 0.05\n"
          "\t\t\t\tmultipleScattering1 = 0.2, 1.5\n\t\t\t\tmultipleScattering2 = -0.4, 0.2\n\t\t\t}\n")
    s += "\t\t}\n"
    s += ("\t\tsettings\n\t\t{\n\t\t\t_Color = 255,255,255,255\n\t\t\t_Lambertian = 0.75\n\t\t\t_UVNoiseStrength = 0\n"
          "\t\t\t_MainTex\n\t\t\t{\n\t\t\t\tvalue = %s/StormSwirl\n\t\t\t\tisClamped = True\n\t\t\t}\n\t\t\t_FlowMap\n\t\t\t{\n\t\t\t}\n\t\t}\n" % TEX)
    s += "\t}\n"
    return s


def weather_patch(body):
    return ("\n// The body node of %(b)s names its EVE layers (CLOUDS.weatherLayers); the layer of ours is appended to that list.\n"
            "@REDEFINITION_BODY[%(b)s]:NEEDS[StockVolumetricClouds]:FINAL\n{\n\t@CLOUDS\n\t{\n"
            "\t\t@weatherLayers ^= :$:, %(l)s:\n\t}\n}\n" % {"b": body["name"], "l": layer_name(body)})


def body_file(body):
    head = ("// GENERATED by tools/gen_eve_clouds.py from one template; edit the template, not this file (python tools/gen_eve_clouds.py --check).\n"
            "// ReDefinition Atmospheres: the EVE layer of %s that the cloud-map painter writes to. EVE reads the node EVE_CLOUDS;\n"
            "// ModuleManager keeps it when StockVolumetricClouds is installed (NEEDS) and drops the suffix. Without the pack nothing here is read.\n"
            "// Volume layer %s: the cloud types %s, a layer2D for the orbit picture, placeholder maps.\n"
            "// The particle fields (rain, snow, dust) stay on the pack's own layers.\n"
            % (body["name"], layer_name(body), ", ".join(body["types"])))
    s = head + "EVE_CLOUDS:NEEDS[StockVolumetricClouds]\n{\n"
    s += volume_object(body)
    s += "}\n"
    if body["weather_patch"]:
        s += weather_patch(body)
    return s


def blank_png():
    """A 16 x 8 black, opaque greyscale PNG (zlib + CRC written by hand, no imaging library)."""
    import struct
    import zlib

    def chunk(kind, data):
        c = struct.pack(">I", len(data)) + kind + data
        return c + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + b"\x00" * BLANK_W for _ in range(BLANK_H))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", BLANK_W, BLANK_H, 8, 0, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def outputs():
    files = {}
    for body in BODIES:
        files[os.path.join(OUT_DIR, "Clouds-%s.cfg" % body["name"])] = body_file(body).encode("utf-8")
    files[os.path.join(OUT_DIR, "Blank.png")] = blank_png()
    return files


def main(argv):
    if "--list" in argv:
        for body in BODIES:
            print("%s: volume %s types %s" % (body["name"], layer_name(body), body["types"]))
        return 0
    files = outputs()
    if "--check" in argv:
        bad = 0
        for path, data in files.items():
            on_disk = open(path, "rb").read() if os.path.exists(path) else None
            # A checkout may turn the line ends into CRLF (core.autocrlf); the text files are compared by their lines.
            if on_disk is not None and path.endswith(".cfg"):
                on_disk = on_disk.replace(b"\r\n", b"\n")
            if on_disk != data:
                print("differs: " + os.path.relpath(path, ROOT))
                bad += 1
        print("gen_eve_clouds: %d of %d files differ" % (bad, len(files)))
        return 1 if bad else 0
    os.makedirs(OUT_DIR, exist_ok=True)
    for path, data in files.items():
        with open(path, "wb") as f:
            f.write(data)
        print("wrote " + os.path.relpath(path, ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
