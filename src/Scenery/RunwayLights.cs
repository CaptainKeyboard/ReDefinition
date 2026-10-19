using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scenery
{
    // The runway's lights dimmed. KSP draws them, pools of light around each lamp
    // included, with the runway's light meshes (Section1_lights to Section4_lights,
    // End09_lights, End27_lights; measured 2026-10-05: without them the pools are gone):
    // each an additive sprite (model_vab_interior_lights, Legacy Shaders/Particles/Additive)
    // and a lamp (model_vab_exterior_details_01, KSP/Scenery/Emissive/Specular), both
    // materials the other buildings' lights share. KSP brightens them by the time of day
    // each LateUpdate, a property block per renderer with its colour for the materials it
    // animates (TimeOfDayAnimation.MaterialProperty, by the material's name). Next to the
    // other lights of the space centre the runway's stood out far too bright (the player,
    // 2026-10-05). Before each camera draws, after KSP set its block, every material of
    // the runway's light meshes gets a block of its own, which Unity uses in place of the
    // renderer's: KSP's colour of this frame, or the material's own, scaled.
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class RunwayLights : MonoBehaviour
    {
        private static readonly Regex RunwayLight = new Regex(@"^(Section\d+|End\d+)_lights", RegexOptions.CultureInvariant);
        private static readonly string[] OwnColours = { "_TintColor", "_EmissiveColor" };
        private static readonly List<Renderer> lights = new List<Renderer>();
        private static float brightness = 0.5f;

        private readonly List<TimeOfDayAnimation> animations = new List<TimeOfDayAnimation>();
        // Per light, found with it: its materials and, for each, the property its colour is
        // in and the time-of-day animation that sets it, if one does -- looked up when the
        // lights are looked for, not every frame (names, arrays and delegates would be new
        // garbage each time).
        private sealed class Light
        {
            public Renderer Renderer;
            public Material[] Materials;
            public string[] Properties;
            public TimeOfDayAnimation[] Animations;
        }

        private readonly List<Light> resolved = new List<Light>();
        // Made in Start: Unity does not allow a MaterialPropertyBlock in a MonoBehaviour's
        // constructor, where a field initializer runs (a development player throws, and
        // the component's other fields stay empty).
        private MaterialPropertyBlock block;
        private int drawnFrame = -1;
        // When the space centre's buildings last changed -- upgraded, collapsed, rebuilt --,
        // for a look a second later, when the new models stand; -1 for none since.
        private float changedAt = -1f;

        // The lights' brightness, 1 as KSP draws them.
        internal static float Brightness
        {
            get { return brightness; }
            set { brightness = value; }
        }

        // The runway's light renderers found so far.
        internal static IList<Renderer> Lights
        {
            get { return lights; }
        }

        // The lights looked for as the scene starts, 5 and 20 seconds into it for the models
        // spawned after it began, and a second after the space centre's buildings changed:
        // KSP switches a city's parts on and off by distance without changing its hierarchy,
        // and the inactive ones are found too. Every ten seconds only while none is found, as
        // in a flight far from the space centre, whose city is not active then; not
        // throughout, each look going through every renderer of every city.
        private IEnumerator Start()
        {
            block = new MaterialPropertyBlock();
            Camera.onPreCull += Dim;
            GameEvents.OnKSCFacilityUpgraded.Add(OnFacilityUpgraded);
            GameEvents.OnKSCStructureCollapsed.Add(OnStructureChanged);
            GameEvents.OnKSCStructureRepaired.Add(OnStructureChanged);
            LookForLights();
            yield return new WaitForSeconds(5f);
            LookForLights();
            yield return new WaitForSeconds(15f);
            LookForLights();
            var wait = new WaitForSeconds(1f);
            float lookedAt = Time.unscaledTime;
            while (true)
            {
                bool changed = changedAt >= 0f && Time.unscaledTime - changedAt > 1f;
                if (changed || lights.Count == 0 && Time.unscaledTime - lookedAt >= 10f)
                {
                    changedAt = -1f;
                    lookedAt = Time.unscaledTime;
                    LookForLights();
                }
                // A light found while its city's parts were off -- a flight that starts far
                // from the space centre -- has no time-of-day animation yet, whose objects are
                // inactive then and not found: looked for again every ten seconds until they
                // are, the animations alone.
                else if (unanimated && Time.unscaledTime - animationsAt >= 10f)
                {
                    animationsAt = Time.unscaledTime;
                    TimeOfDayAnimation[] now = FindObjectsOfType<TimeOfDayAnimation>();
                    if (now.Length != animations.Count)
                    {
                        animations.Clear();
                        animations.AddRange(now);
                        Resolve();
                    }
                }
                yield return wait;
            }
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= Dim;
            GameEvents.OnKSCFacilityUpgraded.Remove(OnFacilityUpgraded);
            GameEvents.OnKSCStructureCollapsed.Remove(OnStructureChanged);
            GameEvents.OnKSCStructureRepaired.Remove(OnStructureChanged);
        }

        private void OnFacilityUpgraded(Upgradeables.UpgradeableFacility facility, int level)
        {
            changedAt = Time.unscaledTime;
        }

        private void OnStructureChanged(DestructibleBuilding building)
        {
            changedAt = Time.unscaledTime;
        }

        private void LookForLights()
        {
            lights.RemoveAll(l => l == null);
            int found = 0;
            foreach (PQSCity city in FindObjectsOfType<PQSCity>())
                foreach (Renderer renderer in city.GetComponentsInChildren<Renderer>(true))
                {
                    if (!RunwayLight.IsMatch(renderer.name) || lights.Contains(renderer)) continue;
                    lights.Add(renderer);
                    found++;
                }
            animations.Clear();
            animations.AddRange(FindObjectsOfType<TimeOfDayAnimation>());
            Resolve();
            if (found > 0)
                Debug.Log(Log.Tag + " Runway lights: " + found + " light meshes at " + (brightness * 100f).ToString("0") + " %, "
                          + animations.Count + " time-of-day animations.");
        }

        private void Dim(Camera camera)
        {
            if (Time.frameCount == drawnFrame) return;
            drawnFrame = Time.frameCount;
            foreach (Light entry in resolved)
            {
                Renderer light = entry.Renderer;
                if (light == null) continue;
                for (int i = 0; i < entry.Materials.Length; i++)
                {
                    Material material = entry.Materials[i];
                    if (material == null) continue;
                    block.Clear();
                    if (brightness < 0.999f)
                    {
                        string property = entry.Properties[i];
                        TimeOfDayAnimation animation = entry.Animations[i];
                        if (property != null)
                        {
                            Color colour = animation != null ? animation.emissiveColor : material.GetColor(property);
                            block.SetColor(property, new Color(colour.r * brightness, colour.g * brightness, colour.b * brightness, colour.a));
                        }
                    }
                    else
                    {
                        // KSP's own block, as it set it this frame.
                        light.GetPropertyBlock(block);
                    }
                    light.SetPropertyBlock(block, i);
                }
            }
        }

        // Each light's materials with the property and the animation of each.
        // Whether the lights were found and none of their materials has a time-of-day
        // animation found for it: the city's parts off as they were looked for. Where some
        // have one, the others are materials no animation colours.
        private bool unanimated;
        private float animationsAt;

        private void Resolve()
        {
            resolved.Clear();
            bool anyAnimated = false;
            foreach (Renderer renderer in lights)
            {
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                var entry = new Light
                {
                    Renderer = renderer, Materials = materials, Properties = new string[materials.Length],
                    Animations = new TimeOfDayAnimation[materials.Length],
                };
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null) continue;
                    if (!Animated(material, out entry.Properties[i], out entry.Animations[i]))
                        entry.Properties[i] = System.Array.Find(OwnColours, material.HasProperty);
                    else
                        anyAnimated = true;
                }
                resolved.Add(entry);
            }
            unanimated = resolved.Count > 0 && !anyAnimated;
        }

        // The time-of-day animation that colours the material, and its property: KSP's
        // colour of the frame is the animation's then.
        private bool Animated(Material material, out string property, out TimeOfDayAnimation by)
        {
            string name = material.name.Replace(" (Instance)", "");
            foreach (TimeOfDayAnimation animation in animations)
            {
                if (animation == null || animation.emissives == null) continue;
                foreach (TimeOfDayAnimation.MaterialProperty animated in animation.emissives)
                    if (animated != null && animated.material != null && animated.material.name == name)
                    {
                        property = animated.propertyName;
                        by = animation;
                        return true;
                    }
            }
            property = null;
            by = null;
            return false;
        }
    }
}
