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
has no colour key: the lightness of a type is its density, and the layer's `color` is the light grey of the lightest type [VERIFY the look of
the storm base in the game].

Every number is ours (master plan 46.3, the owner 2026-11-26: nothing of another author's pack): the noise, the march, the fade and the
noise feature sizes are derived in .local/work/eve-values-derivation.md, each key's action read in EVE's compiled shader
(.local/work/eve-shader-semantics.md). Keys the installed EVE shader does not read are not written (phase functions,
multipleScatteringBrightness, erosionDepth, skylightTintMultiplier, _Lambertian, _DetailDist without a detail texture).

Kerbin's body node names its EVE layers (CLOUDS.weatherLayers); the file of Kerbin ends with a ModuleManager patch that adds our layer
to that list [VERIFY the patch syntax in the game's ModuleManager log].

Our layer is shipped for Kerbin and Laythe although the pack has weather layers there that the role discovery also takes
(Kerbin-Weather-1/2, Laythe-Weather-1/2): the pack's layers hold Fog, Rain, Thunder, Polar-fog and Polar-snow, while ours holds every
cloud type the painter selects from the CONTRACT table (Snow, Stratus, Storm), so it is an addition and the pack's layers stay.

The C# tests of E41 parse the written files; --check is the test that the files are the template's output.

Pattern for a pack (JNSQ, KSRSS, OPM, Beyond Home) that wants a layer of ours on another body: copy the OBJECT of a generated file,
change `name` and `body`, and add it by a ModuleManager patch. A body without a PQS (a gas giant) needs a PQS_MANAGER node first: the
layer is applied to `celestialBody.pqsController`, else to PQSManagerClass.GetPQS(body), and with neither CloudsPQS.Apply logs "PQS is
null somehow!?" and does not apply the layer (CloudsPQS.cs:402-453, PQSManager.cs GetPQS, release-5 tree). The node layout of
PQS_MANAGER is that of PQSManagerClass (configName "PQS_MANAGER", one OBJECT per body, PQSWrapper's config items: body,
deactivateDistance (default 175000), optional overrideKillSphere { altitude }) and of the installed pack's pqs.cfg, which holds one
OBJECT, for Jool:
  EVE_CLOUDS:NEEDS[StockVolumetricClouds]
  {
      OBJECT { name = MyPack-Atmospheres-Sarnus  body = Sarnus  altitude = 2700  ...  }
  }
  PQS_MANAGER { OBJECT { body = Sarnus  deactivateDistance = 175000  overrideKillSphere { altitude = -75000 } } }
No PQS_MANAGER block is shipped: Kerbin, Duna and Laythe have a PQS, and Jool (no surface, status Off) has its block in the pack.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_DIR = os.path.join(ROOT, "GameData", "ReDefinition", "Atmospheres", "Clouds")
TEX = "ReDefinition/Atmospheres/Clouds"

# Cloud types. Altitudes in metres above sea level (EVE's minAltitude/maxAltitude); CONTRACT.md gives them above the ground, which on
# the flat parts the cover covers is the same to a few hundred metres [VERIFY on Duna and Laythe with the terrain height].
#   Snow (SnowLight): the table says "600 to 1500 m, 300 m thick". It is read as a cloud layer 300 m thick with its base at 600 m; the
#   stratus type raises the top to 1500 m and beyond through the blend of the type map. The density 0.002 to 0.003 takes the middle.
#   tiling: the noise's largest feature (EVE's baseNoiseTiling, metres) = the type's mesoscale cell width: closed stratiform cells are 10 to 30
#   layer depths wide (Wood & Hartmann 2006: a 300-600 m stratus 3-18 km; 4 km taken), a cumulonimbus cell 5-15 km (Houze: 8 km), a uniform
#   snow-bearing sheet 8-12 km (10 km), Duna's dust fronts 20 km (.local/work/eve-values-derivation.md section 2).
#   edge: EVE erodes the density by its detail noise, subtracting up to w = 1 - noiseEdgeHardness of it (f' = (f - w (1 - detail)) /
#   (1 - w (1 - detail)), eve-shader-semantics.md 1.3): w is the relative AMPLITUDE of the density's fluctuation at scales finer than the base
#   noise resolves (below tile / 64). Under the k^(-5/3) spectrum of turbulent clouds and dust (Davis et al. 1994) the variance between k and
#   k_max goes as k^(-2/3): that share is (64^(-2/3) - 1000^(-2/3)) / (1 - 1000^(-2/3)) = 0.053 (detail down to a thousandth of the tile), its
#   amplitude sqrt(0.053) = 0.23: hardness 0.77 for every type (PRUEFPROTOKOLL 332; 330 took the variance for the amplitude).
#   profile: EVE's density chain is f = c' dc window(noise) with dc = densityCurve(height): coverage (the weather's map, passed through as it is)
#   and the height profile set both the noise window and the amplitude. In a water cloud the liquid water rises adiabatically with the height
#   above the base (LWC ~ z), and at a fixed droplet number the extinction goes as LWC^(2/3) (Stephens 1978): dc = h^(2/3), with the top cut
#   within the last 3 % (the inversion, entrainment). So the base and the top become uneven where the noise is weak and the thickness varies:
#   the light and dark patches of a real deck seen from below. Dust in a convective boundary layer is well mixed: constant, falling off in the
#   top 15 %. The weather's coverage acts through the window (fewer, thinner clouds at a low value, a closed deck at a high one: the owner
#   2026-11-26). The types' coverageCurve is not in the shader's density chain and has no derivation: not written for these types (EVE's
#   default); the storm keeps the curve of the storm cell's layer (ours, d35a07c0). Densities, heights and the light grey colour are
#   CONTRACT.md's table (ours).


def adiabatic_profile():
    keys = []
    for h in (0.0, 0.05, 0.15, 0.3, 0.5, 0.7, 0.9, 0.97):
        v = h ** (2.0 / 3.0)
        slope = 5.0 if h == 0 else (2.0 / 3.0) * h ** (-1.0 / 3.0)
        keys.append((h, round(v, 3), round(slope, 3), round(slope, 3)))
    keys.append((1.0, 0.0, -32.0, -32.0))
    return keys


PROFILE_WATER = adiabatic_profile()
PROFILE_DUST = [(0.0, 1.0, 0.0, 0.0), (0.85, 1.0, 0.0, 0.0), (1.0, 0.0, -6.7, -6.7)]
TYPES = {
    "Snow": dict(role="SnowLight", min=600, max=900, density=0.0025, tiling=10000, edge=0.77, cover=None, profile=PROFILE_WATER),
    "Stratus": dict(role="SnowStratus", min=400, max=1800, density=0.0065, tiling=4000, edge=0.77, cover=None, profile=PROFILE_WATER),
    "Storm": dict(role="Storm", min=300, max=2300, density=0.014, tiling=8000, edge=0.77,
                  cover=[(0, 0, 0, 5), (0.12, 0.85, 0, 0), (0.5, 0.95, 0, 0), (0.82, 0.35, -3, -3), (0.95, 0, 0, 0)], profile=PROFILE_WATER),
    "Dust": dict(role="Dust", min=150, max=6000, density=0.006, tiling=20000, edge=0.77, cover=None, profile=PROFILE_DUST),
}

# The bodies. radius_m: the body's radius (scaled fade). altitude: the radius of the 2D sphere above the body (the layer2D).
# weather_patch: Kerbin's body node names its layers, so the file adds ours to that list.
# fade: EVE's switch to the 2D layer by the camera's altitude (metres; EVE's C# only, .local/work/eve-values-derivation.md section 3): it starts
# where the layer still shows depth (a 1 km layer subtends about 1 degree at 57 km; the start at about half that so the 2D layer takes over in
# time) and ends below the atmosphere's top (Kerbin 70 km, Duna and Laythe 50 km).
# march (metres; EVE's step = min(base + factor x distance, max)): the base step resolves the smallest noise tile (tile / 128) and stays below half
# a mean free path of the densest type (0.5 / density); the factor keeps one step per pixel footprint (60 degrees / 1080 px = 1e-3); the cap is the
# smallest tile / 32; the light march covers three mean free paths of the middle type (3 / density) in steps of an optical depth of 0.5 (the
# midpoint rule's error near 1 %): 6 steps. altitude: the 2D sphere stands in for the volume from orbit, at the volume's top.
#   Kerbin, Laythe (Snow 0.0025, Stratus 0.0065, Storm 0.014 per m; smallest tile 4 km): base min(31, 36) = 30, cap 125, light 3 / 0.0065 = 460.
#   Duna (Dust 0.006 per m; tile 20 km): base min(156, 83) = 80, cap 625, light 3 / 0.006 = 500.
BODIES = [
    dict(name="Kerbin", radius_m=600000, altitude=2300, types=["Snow", "Stratus", "Storm"], layer_colour=(190, 192, 196), weather_patch=True,
         fade=(30000, 60000), march=dict(base=30, factor=0.001, cap=125, light=460)),
    dict(name="Duna", radius_m=320000, altitude=6000, types=["Dust"], layer_colour=(196, 150, 110), weather_patch=False,
         fade=(25000, 45000), march=dict(base=80, factor=0.001, cap=625, light=500)),
    dict(name="Laythe", radius_m=500000, altitude=2300, types=["Snow", "Stratus", "Storm"], layer_colour=(190, 192, 196), weather_patch=False,
         fade=(25000, 48000), march=dict(base=30, factor=0.001, cap=125, light=460)),
]

BLANK_W, BLANK_H = 16, 8


def layer_name(body):
    return "ReDefinition-Atmospheres-" + body["name"]


def fade(body):
    return body["fade"]


def curve_lines(keys, indent):
    return "".join("%skey = %s\n" % (indent, " ".join(("%g" % v) for v in k)) for k in keys)


def type_item(name, t, indent):
    i = indent
    j = i + "\t"
    s = i + "Item\n" + i + "{\n"
    s += "%s// %s: %d to %d m, density %g per m, largest noise feature %d m\n" % (j, t["role"], t["min"], t["max"], t["density"], t["tiling"])
    s += "%stypeName = %s\n" % (j, name)
    s += "%sminAltitude = %d\n%smaxAltitude = %d\n" % (j, t["min"], j, t["max"])
    s += "%sinterpolateCloudHeights = true\n" % j
    s += "%sdensity = %g\n%sbaseNoiseTiling = %d\n%snoiseEdgeHardness = %g\n" % (j, t["density"], j, t["tiling"], j, t["edge"])
    s += "%sparticleFieldDensity = 0\n%sdropletsDensity = 0\n%swetSurfacesIntensity = 0\n%slightningFrequency = 0\n%sambientVolume = 0\n" % ((j,) * 5)
    if t["cover"] is not None:
        s += j + "coverageCurve\n" + j + "{\n" + curve_lines(t["cover"], j + "\t") + j + "}\n"
    s += j + "densityCurve\n" + j + "{\n" + curve_lines(t["profile"], j + "\t") + j + "}\n"
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
    # No _DetailDist: the 2D layer has no detail texture, so the detail fade has nothing to fade.
    s += "\t\tlayer2D\n\t\t{\n\t\t\tmacroCloudMaterial\n\t\t\t{\n\t\t\t}\n\t\t}\n"
    s += "\t\tlayerRaymarchedVolumeV5\n\t\t{\n"
    s += "\t\t\tcolor = %d, %d, %d, 255\n" % (r, g, b)
    # No upward drift, sky light multiplier or render order: we have no derivation for them, so EVE's own defaults apply.
    s += "\t\t\tscaledFadeStartAltitude = %d\n\t\t\tscaledFadeEndAltitude = %d\n" % (fs, fe)
    m = body["march"]
    s += ("\t\t\traymarchingSettings\n\t\t\t{\n\t\t\t\tlightMarchSteps = 6\n\t\t\t\tlightMarchDistance = %d\n\t\t\t\tbaseStepSize = %d\n"
          "\t\t\t\tadaptiveStepSizeFactor = %g\n\t\t\t\tmaxStepSize = %d\n\t\t\t}\n"
          % (m["light"], m["base"], m["factor"], m["cap"]))
    # The Worley noise (EVE/CloudNoiseGen): octaves k = 0..6 halve the wavelength from the tile to tile / 64, two voxels of the 128-voxel texture
    # (its Nyquist limit) at lacunarity 2 with one period per tile: 7 octaves; persistence 2^(-1/3) = 0.79 from the k^(-5/3) spectrum of cloud
    # liquid water (Davis et al. 1994); spherical 0 keeps Worley's plain F1 distance.
    s += ("\t\t\tnoise\n\t\t\t{\n\t\t\t\tworley\n\t\t\t\t{\n\t\t\t\t\toctaves = 7\n\t\t\t\t\tperiods = 1\n\t\t\t\t\tlacunarity = 2\n"
          "\t\t\t\t\tpersistence = 0.79\n\t\t\t\t\tspherical = 0\n\t\t\t\t}\n\t\t\t}\n")
    s += "\t\t\t// Placeholders: the cloud-map painter binds its own render textures to both maps at run time.\n"
    s += texture_node("coverageMap", "\t\t\t")
    s += texture_node("cloudTypeMap", "\t\t\t")
    s += "\t\t\tcloudTypes\n\t\t\t{\n"
    for name in body["types"]:
        s += type_item(name, TYPES[name], "\t\t\t\t")
    s += "\t\t\t}\n"
    s += "\t\t\tlightVolumeSettings\n\t\t\t{\n\t\t\t\tuseLightVolume = False\n\t\t\t}\n"
    s += "\t\t}\n"
    s += ("\t\tsettings\n\t\t{\n\t\t\t_Color = 255,255,255,255\n\t\t\t_UVNoiseStrength = 0\n"
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
