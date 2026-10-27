using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // A moon's light on EVE's clouds, while the raytracing lights the ground with it
    // (RtReflectors). Where Scatterer integrates EVE's clouds it puts in shaders of its own
    // for them. It sets their light each frame (SkyNode.UpdateEVECloudMaterials, in its
    // OnPreRender), the main sun's alone. A postfix sets it anew for the body the camera is
    // at.
    //
    // The raymarched layers (Scatterer-EVE/RaymarchCloud) take one directional light,
    // _Sun_WorldSunDir and _sunColor. As the sun sets the moon's light is added
    // at each cloud by its own place (EveCloudRelight), the sun staying their light, until
    // the sun reaches none of them in view. Then that light turns to the moon, and back as
    // the sun rises. By day it is the sun's as before. Where the light per pixel cannot
    // run, the one light turns from the sun to the moon for all clouds together, by the
    // layers' heights (CloudLightBlend). Light2D carries the 2D layers.
    //
    // The moon's light is the raytracing's moons' together (RtReflectors.Combined), in units
    // of the sunlight, at CloudShare of it, times the brightness of the sun's colour
    // Scatterer sets on the material. Without Scatterer, EVE's own
    // clouds take the sun's direction alone, without a colour to dim: nothing here then.
    internal static class EveCloudMoonlight
    {
        // The Harmony id; the 2D cloud shader's keywords for its secondary lights; the cloud shaders' properties:
        // the raymarched layers' one light, the 2D layers' sun colour and secondary lights, the layers' radii.
        private const string HarmonyId = "ReDefinition.ScattererCloudLight";
        private const string Keyword = "PLANETSHINE_ON";
        private const string KeywordOff = "PLANETSHINE_OFF";
        private static readonly int SunDirId = Shader.PropertyToID("_Sun_WorldSunDir");
        private static readonly int SunColourId = Shader.PropertyToID("_sunColor");
        private static readonly int CloudSunColourId = Shader.PropertyToID("cloudSunColor");
        private static readonly int SourcesId = Shader.PropertyToID("planetShineSources");
        private static readonly int ColoursId = Shader.PropertyToID("cloudPlanetShineRGB");
        private static readonly int InnerId = Shader.PropertyToID("innerSphereRadius");
        private static readonly int OuterId = Shader.PropertyToID("outerSphereRadius");

        // Scatterer's members read (InstallHook), and whether a failure inside Scatterer was logged.
        private static FieldInfo bodyNameField, handlerField, layersField, flatField, raymarchedField, probeField, atlasField;
        private static PropertyInfo scattererInstance;
        private static bool warned;

        // The same members through compiled getters, read each frame without a reflection call.
        private static Func<object, object> readBodyName, readAtlas, readInstance, readHandler, readLayers, readFlat, readRaymarched,
                                            readProbe;

        // The 2D materials whose secondary lights this class turned on, to turn off again.
        private static readonly List<Material> lit2D = new List<Material>();
        private static readonly List<Material> raymarched = new List<Material>();
        private static readonly List<Material> flat = new List<Material>();
        private static readonly double[] sunDir = new double[3], moonDir = new double[3], lightDir = new double[3];
        private static string state;
        private static double lastReach = 1.0;

        // The share of the raytracing's moonlight the clouds take. EVE's clouds answer their
        // light far more brightly than the raytraced ground does. From 14 km over the pad,
        // the clouds came out 17 times as bright as the land under them in the sun and 49
        // times in the full Mun's light. That is the light probe of 2026-10-23: the brightest clouds
        // against the land's median, linear. The player found them too bright at night.
        // At this share they stand to the land as in the sun.
        internal const float CloudShare = 0.35f;

        // For the light probe: the clouds in the sun's light alone, as without this, to compare.
        internal static bool HeldOff;

        // The light last set, for the probe's log.
        internal static string Describe()
        {
            return (state ?? "not set") + ", the sun's share "
                   + lastReach.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        // From the addon's Awake, before any scene has clouds.
        public static void InstallHook()
        {
            Type skyNode = TypeLookup.Find("Scatterer.SkyNode");
            if (skyNode == null) return;
            if (!ForeignAssumptions.ScattererCloudLight.Holds) return;
            Type scatterer = TypeLookup.Find("Scatterer.Scatterer");
            Type handler = TypeLookup.Find("Scatterer.EVEReflectionHandler");
            Type layer = TypeLookup.Find("Scatterer.EVECloudLayer");
            MethodInfo update = skyNode.GetMethod("UpdateEVECloudMaterials", TypeLookup.Any, null, Type.EmptyTypes, null);
            bodyNameField = skyNode.GetField("celestialBodyName", TypeLookup.Any);
            atlasField = skyNode.GetField("atmosphereAtlas", TypeLookup.Any);
            scattererInstance = scatterer != null ? scatterer.GetProperty("Instance", TypeLookup.Any) : null;
            handlerField = scatterer != null ? scatterer.GetField("EveReflectionHandler", TypeLookup.Any) : null;
            layersField = handler != null ? handler.GetField("EVECloudLayers", TypeLookup.Any) : null;
            flatField = layer != null ? layer.GetField("Clouds2dMaterial", TypeLookup.Any) : null;
            raymarchedField = layer != null ? layer.GetField("RaymarchedVolumetricsMaterial", TypeLookup.Any) : null;
            probeField = layer != null ? layer.GetField("ReflectionProbeRaymarchedVolumetricsMaterial", TypeLookup.Any) : null;
            readBodyName = CompiledGetter.Of<object>(bodyNameField);
            readAtlas = CompiledGetter.Of<object>(atlasField);
            readInstance = CompiledGetter.Of<object>(scattererInstance);
            readHandler = CompiledGetter.Of<object>(handlerField);
            readLayers = CompiledGetter.Of<object>(layersField);
            readFlat = CompiledGetter.Of<object>(flatField);
            readRaymarched = CompiledGetter.Of<object>(raymarchedField);
            readProbe = CompiledGetter.Of<object>(probeField);
            if (update == null || bodyNameField == null || scattererInstance == null || handlerField == null || layersField == null
                || flatField == null || raymarchedField == null || probeField == null || readBodyName == null || readInstance == null
                || readHandler == null || readLayers == null || readFlat == null || readRaymarched == null || readProbe == null)
            {
                CompatibilityLog.Warn("scatterer-cloud-light", "EVE's clouds take the sun's light alone: this Scatterer"
                                                               + " has not the members read.");
                return;
            }
            try
            {
                HarmonyHooks.Install(HarmonyId, typeof(EveCloudMoonlight),
                    new List<HarmonyHook> { new HarmonyHook(update, null, nameof(AfterUpdate), null) });
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("scatterer-cloud-light", "Scatterer's cloud light could not be hooked ("
                                                               + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Runs inside Scatterer: nothing may escape.
        private static void AfterUpdate(object __instance)
        {
            try
            {
                Apply(__instance);
            }
            catch (Exception e)
            {
                if (warned) return;
                warned = true;
                CompatibilityLog.Warn("scatterer-cloud-light", "EVE's clouds could not take the moon's light ("
                                                               + CompatibilityLog.Reason(e) + ").");
            }
        }

        // After Scatterer set the clouds' light for the body of `skyNode`: the moon's light set for the body the camera
        // is at, and taken back from another body's 2D layers.
        private static void Apply(object skyNode)
        {
            CelestialBody main = RtKspMods.SceneBody();
            string name = readBodyName(skyNode) as string;
            if (name == null) return;
            bool otherBody = main == null || name != main.bodyName;
            // Another body's clouds change only where this class lit some of them.
            if (otherBody && lit2D.Count == 0) return;
            if (!Collect(name)) return;
            // Another body's clouds, still in view from orbit: the moonlight this class gave
            // them while the camera was there taken back.
            if (otherBody)
            {
                if (lit2D.Count > 0) Light2D(false, null, null, Color.black);
                return;
            }
            // Scatterer's atmosphere atlas for the light per pixel: it sets it on the cloud
            // materials, whose shader does not declare it, so it cannot be read back from them.
            if (readAtlas != null)
            {
                Texture atlas = readAtlas(skyNode) as Texture;
                if (atlas != null) EveCloudRelight.Atlas = atlas;
            }

            CelestialBody moon;
            Vector3d eye;
            Vector3 towards;
            Color moonColour;
            // The raytracing's moonlight, the moons' together as they light the ground; none
            // while it is off. On the clouds at CloudShare of it.
            bool lit = RtReflectors.Combined(main, out moon, out eye, out towards, out moonColour)
                       && RtReflectors.Luminance(moonColour) > 0f && !HeldOff;
            moonColour *= CloudShare;
            Light2D(lit, main, moon, moonColour);
            lastReach = 1.0;
            if (!lit || raymarched.Count == 0)
            {
                Report(0, null);
                return;
            }

            // The layers' highest top, from all of them: EVE's light volume holds them all,
            // filled with each one's light.
            double highest = 0.0;
            foreach (Material material in raymarched)
            {
                float inner = material.GetFloat(InnerId), outer = material.GetFloat(OuterId);
                if (inner <= 0f || outer <= inner) continue;
                highest = Math.Max(highest, outer);
            }
            if (highest <= 0.0) return;

            Material first = raymarched[0];
            Vector3 sun = first.GetVector(SunDirId);
            Color sunColour = first.GetColor(SunColourId);
            if (sun.sqrMagnitude < 0.5f) return;
            sun.Normalize();
            Vector3d up = (eye - main.position).normalized;
            double elevation = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(up, (Vector3d)sun))));
            double top = CloudLightBlend.Dip(main.Radius, highest);
            float sunBrightness = RtReflectors.Luminance(sunColour);
            Color moonLight = moonColour * sunBrightness;

            if (EveCloudRelight.Ready)
            {
                LightPerPixel(main, moon, eye, up, towards, moonLight, sunColour, elevation, top);
                return;
            }

            // From when the sun leaves the top of the clouds overhead to when it leaves the
            // farthest ones in view (CloudLightBlend.FarthestDip).
            double reach = CloudLightBlend.Reach(elevation, top, CloudLightBlend.FarthestDip(top));
            lastReach = reach;
            if (reach >= 1.0)
            {
                Report(0, null);
                return;
            }
            sunDir[0] = sun.x; sunDir[1] = sun.y; sunDir[2] = sun.z;
            moonDir[0] = towards.x; moonDir[1] = towards.y; moonDir[2] = towards.z;
            CloudLightBlend.Direction(sunDir, sunBrightness, moonDir, RtReflectors.Luminance(moonLight), reach, lightDir);
            Vector3 light = new Vector3((float)lightDir[0], (float)lightDir[1], (float)lightDir[2]);
            Color colour = sunColour * (float)reach + moonLight * (float)(1.0 - reach);
            colour.a = sunColour.a;
            foreach (Material material in raymarched)
            {
                material.SetVector(SunDirId, light);
                material.SetColor(SunColourId, colour);
            }
            Report(reach <= 0.0 ? 2 : 1, moon);
        }

        // With the moon's light per pixel (EveCloudRelight), the sun stays the raymarched
        // layers' light as Scatterer set it, each cloud in its own sunset. The moon's light
        // is added at each cloud by its own place, from the dusk on and while the moon reaches
        // a cloud in view. Once the sun reaches none of them, the layers' light turns to the
        // moon and the per-pixel light gives way, over CloudLightBlend.HandoffWindow.
        private static void LightPerPixel(CelestialBody main, CelestialBody moon, Vector3d eye, Vector3d up, Vector3 towards,
                                          Color moonLight, Color sunColour, double elevation, double top)
        {
            double cameraDip = CloudLightBlend.Dip(main.Radius, (eye - main.position).magnitude);
            double farthest = CloudLightBlend.FarthestDip(top, cameraDip);
            double handoff = ProbeHandoff >= 0f ? ProbeHandoff : CloudLightBlend.Handoff(elevation, farthest);
            lastReach = 1.0 - handoff;
            // From the planet's centre, as Scatterer places the sun for the clouds' planet
            // shadow: the brightest moon's place, the moons' direction from the eye without one.
            Vector3 fromCentre = moon != null ? (Vector3)(moon.position - main.position).normalized : towards;
            double moonElevation = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(up, (Vector3d)towards))));
            float share = ProbeShare >= 0f ? ProbeShare : (float)((1.0 - handoff) * CloudLightBlend.Dusk(elevation));
            bool perPixel = (share > 0f || EveCloudRelight.ProbeAlways) && moonElevation > -farthest;
            if (perPixel) EveCloudRelight.Light(main, fromCentre, moonLight, share);
            if (handoff <= 0.0)
            {
                Report(perPixel ? 3 : 0, moon);
                return;
            }
            Color colour = moonLight * (float)handoff;
            colour.a = sunColour.a;
            foreach (Material material in raymarched)
            {
                material.SetVector(SunDirId, fromCentre);
                material.SetColor(SunColourId, colour);
            }
            Report(handoff >= 1.0 ? 2 : 4, moon);
        }

        // For the light probe: the moon's share of the clouds' one light with the light per
        // pixel, forced; below 0 as the sun's place gives it.
        internal static float ProbeHandoff = -1f;

        // The same for the share of the moon's light added per pixel.
        internal static float ProbeShare = -1f;

        // The body's cloud materials Scatterer keeps, into raymarched and flat; false where
        // it keeps none. EVE's light volume is filled with the same material as the raymarched
        // layers' (LightVolume.Update, its passes 4 to 7).
        private static bool Collect(string name)
        {
            raymarched.Clear();
            flat.Clear();
            object instance = readInstance(null);
            object handler = instance != null ? readHandler(instance) : null;
            IDictionary layers = handler != null ? readLayers(handler) as IDictionary : null;
            if (layers == null || !layers.Contains(name)) return false;
            IList list = layers[name] as IList;
            if (list == null) return false;
            // By index: an IList's enumerator is a new object each frame.
            for (int i = 0; i < list.Count; i++)
            {
                object layer = list[i];
                if (layer == null) continue;
                Material material = readRaymarched(layer) as Material;
                if (material != null) raymarched.Add(material);
                material = readProbe(layer) as Material;
                if (material != null) raymarched.Add(material);
                material = readFlat(layer) as Material;
                if (material != null) flat.Add(material);
            }
            return raymarched.Count > 0 || flat.Count > 0;
        }

        // The moon as the 2D layers' second light, or off again where this class turned it on.
        // The 2D layers, which show from orbit (Scatterer-EVE/Cloud), take up to four lights
        // beside the sun, as Scatterer sets a secondary sun's (ProlandManager.UpdateSecondarySuns).
        // The moon goes in the first row, its position relative to the planet, its colour times its
        // strength. They take it from the brightest moon's place, only where Scatterer has its
        // secondary lights off on that material, which then are this class's to turn on and off.
        private static void Light2D(bool lit, CelestialBody main, CelestialBody moon, Color moonColour)
        {
            foreach (Material material in flat)
            {
                bool ours = lit2D.Contains(material);
                if (!lit)
                {
                    if (!ours) continue;
                    material.DisableKeyword(Keyword);
                    material.EnableKeyword(KeywordOff);
                    lit2D.Remove(material);
                    continue;
                }
                // Scatterer's own secondary lights on it: left to Scatterer.
                if (!ours && material.IsKeywordEnabled(Keyword)) continue;
                if (!ours) lit2D.Add(material);
                // Each frame: Scatterer turns it off again when it sets the clouds up anew
                // (InitEVEClouds).
                material.DisableKeyword(KeywordOff);
                material.EnableKeyword(Keyword);
                // As Scatterer sets a secondary sun's row (ProlandManager.UpdateSecondarySuns).
                Vector3 position = (Vector3)((moon.position - main.position) * ScaledSpace.ScaleFactor);
                Matrix4x4 sources = Matrix4x4.zero;
                Matrix4x4 colours = Matrix4x4.zero;
                sources.SetRow(0, new Vector4(position.x, position.y, position.z, 1f));
                Color light = moonColour * RtReflectors.Luminance(material.GetColor(CloudSunColourId));
                colours.SetRow(0, new Vector4(light.r, light.g, light.b, 1f));
                material.SetMatrix(SourcesId, sources);
                material.SetMatrix(ColoursId, colours);
            }
            lit2D.RemoveAll(m => m == null);
        }

        // The kind of the cloud light. Kind 0 is the sun's alone, 1 from the sun to the moon, 2 the moon's
        // alone. With the light per pixel, 3 is the sun's with the moon's per pixel, and 4 from the sun
        // to the moon while the per-pixel light gives way.
        private static int stateKind = -1;
        private static CelestialBody stateMoon;

        // Notes the cloud light's kind (stateKind) for Describe and logs it when kind or moon change.
        private static void Report(int kind, CelestialBody moon)
        {
            if (kind == stateKind && moon == stateMoon) return;
            stateKind = kind;
            stateMoon = moon;
            string name = moon != null ? moon.bodyName : "the moons";
            state = kind == 0 ? "the sun's alone" : kind == 1 ? "from the sun to " + name
                  : kind == 3 ? "the sun's, " + name + "'s per pixel" : kind == 4 ? "from the sun to " + name + ", per pixel giving way"
                  : name + "'s alone";
            Debug.Log(Log.Tag + " Clouds: their light " + state + ".");
        }
    }
}
