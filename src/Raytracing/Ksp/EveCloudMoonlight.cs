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
    // for them and sets their light each frame (SkyNode.UpdateEVECloudMaterials, in its
    // OnPreRender), the main sun's alone; a postfix sets it anew for the body the camera is
    // at:
    //
    // - The raymarched layers (Scatterer-EVE/RaymarchCloud) take one directional light,
    //   _Sun_WorldSunDir and _sunColor; EVE's light volume is filled with the same material
    //   (LightVolume.Update, its passes 4 to 7). As the sun sets that light turns from the
    //   sun to the moon, by the layers' heights (CloudLightBlend), and back as it rises; by
    //   day it is the sun's as before.
    // - The 2D layers, which show from orbit (Scatterer-EVE/Cloud), take up to four lights
    //   beside the sun, as Scatterer sets a secondary sun's (ProlandManager
    //   .UpdateSecondarySuns): the moon in the first row, its position relative to the
    //   planet, its colour times its strength. Only where Scatterer has its secondary
    //   lights off on that material, which then are this class's to turn on and off.
    //
    // The moon's colour is the raytracing's, in units of the sunlight, times the brightness
    // of the sun's colour Scatterer sets on the material. Without Scatterer, EVE's own
    // clouds take the sun's direction alone, without a colour to dim: nothing here then.
    internal static class EveCloudMoonlight
    {
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

        private static FieldInfo bodyNameField, handlerField, layersField, flatField, raymarchedField, probeField;
        private static PropertyInfo scattererInstance;
        private static bool warned;

        // The 2D materials whose secondary lights this class turned on, to turn off again.
        private static readonly List<Material> lit2D = new List<Material>();
        private static readonly List<Material> raymarched = new List<Material>();
        private static readonly List<Material> flat = new List<Material>();
        private static readonly double[] sunDir = new double[3], moonDir = new double[3], lightDir = new double[3];
        private static string state;
        private static double lastReach = 1.0;

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
            scattererInstance = scatterer != null ? scatterer.GetProperty("Instance", TypeLookup.Any) : null;
            handlerField = scatterer != null ? scatterer.GetField("EveReflectionHandler", TypeLookup.Any) : null;
            layersField = handler != null ? handler.GetField("EVECloudLayers", TypeLookup.Any) : null;
            flatField = layer != null ? layer.GetField("Clouds2dMaterial", TypeLookup.Any) : null;
            raymarchedField = layer != null ? layer.GetField("RaymarchedVolumetricsMaterial", TypeLookup.Any) : null;
            probeField = layer != null ? layer.GetField("ReflectionProbeRaymarchedVolumetricsMaterial", TypeLookup.Any) : null;
            if (update == null || bodyNameField == null || scattererInstance == null || handlerField == null || layersField == null
                || flatField == null || raymarchedField == null || probeField == null)
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

        private static void Apply(object skyNode)
        {
            CelestialBody main = RtKspMods.SceneBody();
            string name = bodyNameField.GetValue(skyNode) as string;
            if (name == null || !Collect(name)) return;
            // Another body's clouds, still in view from orbit: the moonlight this class gave
            // them while the camera was there taken back.
            if (main == null || name != main.bodyName)
            {
                if (lit2D.Count > 0) Light2D(false, null, null, Color.black);
                return;
            }

            CelestialBody moon;
            Vector3d eye;
            Vector3 towards;
            Color moonColour;
            // The raytracing's moonlight, as it lights the ground; none while it is off.
            bool lit = RtReflectors.Brightest(main, out moon, out eye, out towards, out moonColour)
                       && Luminance(moonColour) > 0f && !HeldOff;
            Light2D(lit, main, moon, moonColour);
            lastReach = 1.0;
            if (!lit || raymarched.Count == 0)
            {
                Report(0, null);
                return;
            }

            // The layers' lowest and highest heights over the ground, from all of them: EVE's
            // light volume holds them all, filled with each one's light.
            double lowest = double.MaxValue, highest = 0.0;
            foreach (Material material in raymarched)
            {
                float inner = material.GetFloat(InnerId), outer = material.GetFloat(OuterId);
                if (inner <= 0f || outer <= inner) continue;
                lowest = Math.Min(lowest, inner);
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
            double reach = CloudLightBlend.Reach(elevation, CloudLightBlend.Dip(main.Radius, lowest),
                                                 CloudLightBlend.Dip(main.Radius, highest));
            lastReach = reach;
            if (reach >= 1.0)
            {
                Report(0, null);
                return;
            }

            float sunBrightness = Luminance(sunColour);
            Color moonLight = moonColour * sunBrightness;
            sunDir[0] = sun.x; sunDir[1] = sun.y; sunDir[2] = sun.z;
            moonDir[0] = towards.x; moonDir[1] = towards.y; moonDir[2] = towards.z;
            CloudLightBlend.Direction(sunDir, sunBrightness, moonDir, Luminance(moonLight), reach, lightDir);
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

        // The body's cloud materials Scatterer keeps, into raymarched and flat; false where
        // it keeps none.
        private static bool Collect(string name)
        {
            raymarched.Clear();
            flat.Clear();
            object instance = scattererInstance.GetValue(null, null);
            object handler = instance != null ? handlerField.GetValue(instance) : null;
            IDictionary layers = handler != null ? layersField.GetValue(handler) as IDictionary : null;
            if (layers == null || !layers.Contains(name)) return false;
            IList list = layers[name] as IList;
            if (list == null) return false;
            // By index: an IList's enumerator is a new object each frame.
            for (int i = 0; i < list.Count; i++)
            {
                object layer = list[i];
                if (layer == null) continue;
                Material material = raymarchedField.GetValue(layer) as Material;
                if (material != null) raymarched.Add(material);
                material = probeField.GetValue(layer) as Material;
                if (material != null) raymarched.Add(material);
                material = flatField.GetValue(layer) as Material;
                if (material != null) flat.Add(material);
            }
            return raymarched.Count > 0 || flat.Count > 0;
        }

        // The moon as the 2D layers' second light, or off again where this class turned it on.
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
                Color light = moonColour * Luminance(material.GetColor(CloudSunColourId));
                colours.SetRow(0, new Vector4(light.r, light.g, light.b, 1f));
                material.SetMatrix(SourcesId, sources);
                material.SetMatrix(ColoursId, colours);
            }
            lit2D.RemoveAll(m => m == null);
        }

        // 0 the sun's alone, 1 from the sun to the moon, 2 the moon's alone.
        private static int stateKind = -1;
        private static CelestialBody stateMoon;

        private static void Report(int kind, CelestialBody moon)
        {
            if (kind == stateKind && moon == stateMoon) return;
            stateKind = kind;
            stateMoon = moon;
            state = kind == 0 ? "the sun's alone" : kind == 1 ? "from the sun to " + moon.bodyName : moon.bodyName + "'s alone";
            Debug.Log(Log.Tag + " Clouds: their light " + state + ".");
        }

        private static float Luminance(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }
    }
}
