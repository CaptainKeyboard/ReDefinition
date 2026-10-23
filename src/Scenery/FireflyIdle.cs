using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Scenery
{
    // Firefly's re-entry effects not drawn while they show nothing. Firefly keeps, for
    // each vessel in an atmosphere, a command buffer on the flight camera that draws an
    // envelope over every part with its effect shader, and an orthographic camera that
    // renders the vessel into its airstream texture, every frame -- on the launch pad too,
    // where the effects' strength is 0: 2.2 ms of the GPU's 19.7 for the Kerbal X on the
    // pad (RenderDoc, 2026-10-05, 621 draws), and the vessel once more for the airstream.
    // Its shader draws nothing below an entry strength of 50 in its main pass and of 1000
    // in its bow shock (their geometry stages return), and its glow pass, which is all
    // that is left, stays below a thousandth of a visible step under a strength of 1
    // (GlowPass.cginc: alpha and heat both scale with _EntryStrength / 4000). Below that
    // the command buffer is emptied and the airstream camera stopped; at 1 and above
    // Firefly's own PopulateCommandBuffer fills the buffer again and the camera runs.
    // OwnSettings.FireflyIdle switches it; switched off, every idle vessel's effects are
    // woken that way.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class FireflyIdle : MonoBehaviour
    {
        private const float WakeAt = 1f;
        private const float SleepBelow = 0.5f;
        private static readonly int EntryStrength = Shader.PropertyToID("_EntryStrength");

        private Type moduleType;
        private FieldInfo fxVesselField, isLoadedField, bufferField, cameraField, materialField;
        // The fields read for every module each frame, through compiled getters: a
        // FieldInfo's GetValue boxes the bool (CompiledGetter).
        private Func<object, object> readFxVessel;
        private Func<object, bool> readIsLoaded;
        private Func<object, Material> readMaterial;
        private Func<object, CommandBuffer> readBuffer;
        private Func<object, Camera> readCamera;
        private MethodInfo populate;
        // Modules whose effects are idle now.
        private readonly HashSet<object> idle = new HashSet<object>();
        private readonly List<object> gone = new List<object>();

        private void Start()
        {
            GameEvents.onGameSceneLoadRequested.Add(OnLeaving);
            Application.quitting += OnQuitting;
            if (!ForeignAssumptions.FireflyIdle.Holds)
            {
                enabled = false;
                return;
            }
            moduleType = TypeLookup.Find("Firefly.AtmoFxModule");
            Type vesselType = TypeLookup.Find("Firefly.AtmoFxVessel");
            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
            fxVesselField = moduleType?.GetField("fxVessel", Public);
            isLoadedField = moduleType?.GetField("isLoaded", Public);
            populate = moduleType?.GetMethod("PopulateCommandBuffer", Public, null, Type.EmptyTypes, null);
            bufferField = vesselType?.GetField("commandBuffer", Public);
            cameraField = vesselType?.GetField("airstreamCamera", Public);
            materialField = vesselType?.GetField("material", Public);
            readFxVessel = CompiledGetter.Of<object>(fxVesselField);
            readIsLoaded = CompiledGetter.Of<bool>(isLoadedField);
            readMaterial = CompiledGetter.Of<Material>(materialField);
            readBuffer = CompiledGetter.Of<CommandBuffer>(bufferField);
            readCamera = CompiledGetter.Of<Camera>(cameraField);
            if (readFxVessel == null || readIsLoaded == null || populate == null || readBuffer == null || readCamera == null
                || readMaterial == null)
                enabled = false;
        }

        private void OnDestroy()
        {
            // Not when the flight or the game ends: its vessels and Firefly's effects go with
            // it, and waking them while Firefly tears its own down threw
            // (TargetInvocationException from its populate call, 2026-10-19, as KSP quit).
            if (HighLogic.LoadedSceneIsFlight && !leaving)
                foreach (object module in idle) Wake(module);
            idle.Clear();
            GameEvents.onGameSceneLoadRequested.Remove(OnLeaving);
            Application.quitting -= OnQuitting;
        }

        private void OnQuitting()
        {
            leaving = true;
        }

        private bool leaving;

        private void OnLeaving(GameScenes scene)
        {
            leaving = true;
        }

        // Whether the player keeps it on (OwnSettings.FireflyIdle).
        private static bool SwitchedOn
        {
            get
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return addon == null || addon.Live.FireflyIdle;
            }
        }

        // Whether Firefly is installed: the window's switch shows only then.
        internal static bool FireflyInstalled
        {
            get { return TypeLookup.Find("Firefly.AtmoFxModule") != null; }
        }

        private void LateUpdate()
        {
            if (!SwitchedOn)
            {
                // Switched off: every idle vessel's effects drawn again, by Firefly alone.
                if (idle.Count == 0) return;
                foreach (object module in idle) Wake(module);
                idle.Clear();
                Debug.Log(Log.Tag + " Firefly idle: switched off; Firefly draws every vessel's effects as it does.");
                return;
            }
            try
            {
                foreach (Vessel vessel in FlightGlobals.VesselsLoaded)
                {
                    if (vessel == null) continue;
                    foreach (VesselModule module in vessel.vesselModules)
                    {
                        if (module == null || module.GetType() != moduleType) continue;
                        object fx = readFxVessel(module);
                        if (fx == null || !readIsLoaded(module))
                        {
                            // Firefly took the vessel's envelopes down (RemoveVesselFx with
                            // onlyEnvelopes, as its part count changes) and keeps fxVessel and
                            // its airstream camera for the next CreateVesselFx, which does not
                            // switch the camera on again (AtmoFxModule, decompiled): it is
                            // switched on here, as Firefly left it.
                            if (idle.Remove(module) && fx != null)
                            {
                                Camera kept = readCamera(fx);
                                if (kept != null) kept.enabled = true;
                            }
                            continue;
                        }
                        Material material = readMaterial(fx);
                        CommandBuffer buffer = readBuffer(fx);
                        Camera airstream = readCamera(fx);
                        if (material == null || buffer == null) continue;
                        float strength = material.GetFloat(EntryStrength);
                        if (idle.Contains(module))
                        {
                            if (strength >= WakeAt)
                            {
                                Wake(module);
                                idle.Remove(module);
                                Debug.Log(Log.Tag + " Firefly idle: " + vessel.vesselName + "'s effects drawn again (strength "
                                          + strength.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ").");
                            }
                            else
                            {
                                // Firefly filled it again on its own (a reload): emptied again.
                                if (buffer.sizeInBytes > 0) buffer.Clear();
                                if (airstream != null && airstream.enabled) airstream.enabled = false;
                            }
                        }
                        else if (strength < SleepBelow)
                        {
                            buffer.Clear();
                            if (airstream != null) airstream.enabled = false;
                            idle.Add(module);
                            Debug.Log(Log.Tag + " Firefly idle: " + vessel.vesselName + "'s effects not drawn while they show nothing (strength "
                                      + strength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ").");
                        }
                    }
                }
                // Modules of vessels unloaded or destroyed.
                gone.Clear();
                foreach (object module in idle)
                    if (module == null || (module as UnityEngine.Object) == null) gone.Add(module);
                foreach (object module in gone) idle.Remove(module);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Firefly idle: stopped (" + e.GetType().Name + ": " + e.Message + "); Firefly draws as it does.");
                foreach (object module in idle) Wake(module);
                idle.Clear();
                enabled = false;
            }
        }

        private void Wake(object module)
        {
            try
            {
                if (module == null || (module as UnityEngine.Object) == null) return;
                object fx = readFxVessel(module);
                if (fx == null) return;
                populate.Invoke(module, null);
                Camera airstream = readCamera(fx);
                if (airstream != null) airstream.enabled = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Firefly idle: could not wake a vessel's effects (" + e.Message + ").");
            }
        }
    }
}
