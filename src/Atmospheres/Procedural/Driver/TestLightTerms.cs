using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Measurement keys for the scene's non-sun light (master plan 46.1 part 3, PRUEFPROTOKOLL 293): the land keeps light with the sun off that
    // RenderSettings' flat ambient does not give (287, 292). light.testShScale scales the SH ambient probe (RenderSettings.ambientProbe, the deferred
    // GBuffer's ambient), light.testProbeScale the reflection probes' intensity and RenderSettings.reflectionIntensity (the deferred reflections pass).
    // 1 in the product: nothing is touched. The values are the ones found when the scaling starts; Restore puts them back.
    internal sealed class TestLightTerms
    {
        internal const string TuneShScale = "light.testShScale", TuneProbeScale = "light.testProbeScale";
        // light.testDefaultReflection g below 1: Unity's default reflection (outside every probe box) a custom cube of grey g, 0 black (PRUEFPROTOKOLL 297, 301:
        // what the distant sea mirrors). The cube is made once per game with the first value.
        internal const string TuneDefaultReflection = "light.testDefaultReflection";

        private bool shActive, probeActive, defaultActive;
        private DefaultReflectionMode defaultModeBase;
        private Cubemap defaultCubeBase;
        private Cubemap black;
        private SphericalHarmonicsL2 shBase;
        private float reflectionBase;
        private ReflectionProbe[] probes;
        private float[] probeBase;

        // Whether a key asks for a scaling (no engine call: the tests run the writer outside the game).
        internal static bool Wanted
        {
            get { return Tuning.Get(TuneShScale, 1f) != 1f || Tuning.Get(TuneProbeScale, 1f) != 1f || Tuning.Get(TuneDefaultReflection, 1f) != 1f; }
        }

        // Applies the scalings each frame (from the writer's pre-cull).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal void Apply()
        {
            float sh = Mathf.Max(0f, Tuning.Get(TuneShScale, 1f)), probe = Mathf.Max(0f, Tuning.Get(TuneProbeScale, 1f));
            if (sh != 1f)
            {
                if (!shActive)
                {
                    shBase = RenderSettings.ambientProbe;
                    shActive = true;
                    AtmospheresLog.Say("light measurement: the SH ambient probe x " + sh.ToString("0.00") + " (ambient mode " + RenderSettings.ambientMode + ")");
                }
                RenderSettings.ambientProbe = shBase * sh;
            }
            if (probe != 1f)
            {
                if (!probeActive)
                {
                    reflectionBase = RenderSettings.reflectionIntensity;
                    probes = Object.FindObjectsOfType<ReflectionProbe>();
                    probeBase = new float[probes.Length];
                    for (int i = 0; i < probes.Length; i++) probeBase[i] = probes[i].intensity;
                    probeActive = true;
                    AtmospheresLog.Say("light measurement: " + probes.Length + " reflection probe(s) and the reflection intensity " + reflectionBase.ToString("0.00") + " x " + probe.ToString("0.00")
                                       + " (default reflection mode " + RenderSettings.defaultReflectionMode + ")");
                }
                RenderSettings.reflectionIntensity = reflectionBase * probe;
                for (int i = 0; i < probes.Length; i++)
                    if (probes[i] != null) probes[i].intensity = probeBase[i] * probe;
            }
            float grey = Tuning.Get(TuneDefaultReflection, 1f);
            if (grey != 1f && !defaultActive)
            {
                // The cube's grey level is the key's value below 1 (0 black; PRUEFPROTOKOLL 301: a grey cube shows whether the sea reads it at all).
                if (black == null)
                {
                    float g = Mathf.Clamp01(grey);
                    black = new Cubemap(16, TextureFormat.RGBA32, false);
                    Color[] px = new Color[16 * 16];
                    for (int i = 0; i < px.Length; i++) px[i] = new Color(g, g, g, 1f);
                    for (int f = 0; f < 6; f++) black.SetPixels(px, (CubemapFace)f);
                    black.Apply();
                }
                defaultModeBase = RenderSettings.defaultReflectionMode;
                defaultCubeBase = RenderSettings.customReflection;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflection = black;
                defaultActive = true;
                AtmospheresLog.Say("light measurement: the default reflection a custom cube of grey " + grey.ToString("0.00") + " (was " + defaultModeBase + ")");
            }
        }

        // Puts the values back.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal void Restore()
        {
            if (shActive) RenderSettings.ambientProbe = shBase;
            shActive = false;
            if (probeActive)
            {
                RenderSettings.reflectionIntensity = reflectionBase;
                for (int i = 0; i < probes.Length; i++)
                    if (probes[i] != null) probes[i].intensity = probeBase[i];
            }
            probeActive = false;
            probes = null;
            if (defaultActive)
            {
                RenderSettings.defaultReflectionMode = defaultModeBase;
                RenderSettings.customReflection = defaultCubeBase;
            }
            defaultActive = false;
        }

        internal bool Active { get { return shActive || probeActive || defaultActive; } }
    }
}
