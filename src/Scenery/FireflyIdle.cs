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
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class FireflyIdle : MonoBehaviour
    {
        private const float WakeAt = 1f;
        private const float SleepBelow = 0.5f;
        private static readonly int EntryStrength = Shader.PropertyToID("_EntryStrength");

        private Type moduleType;
        private FieldInfo fxVesselField, isLoadedField, bufferField, cameraField, materialField;
        private MethodInfo populate;
        // Modules whose effects are idle now.
        private readonly HashSet<object> idle = new HashSet<object>();
        private readonly List<object> gone = new List<object>();

        private void Start()
        {
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
            if (fxVesselField == null || isLoadedField == null || populate == null || bufferField == null || cameraField == null
                || materialField == null)
                enabled = false;
        }

        private void OnDestroy()
        {
            foreach (object module in idle) Wake(module);
            idle.Clear();
        }

        private void LateUpdate()
        {
            try
            {
                foreach (Vessel vessel in FlightGlobals.VesselsLoaded)
                {
                    if (vessel == null) continue;
                    foreach (VesselModule module in vessel.vesselModules)
                    {
                        if (module == null || module.GetType() != moduleType) continue;
                        object fx = fxVesselField.GetValue(module);
                        if (fx == null || !(bool)isLoadedField.GetValue(module))
                        {
                            idle.Remove(module);
                            continue;
                        }
                        var material = materialField.GetValue(fx) as Material;
                        var buffer = bufferField.GetValue(fx) as CommandBuffer;
                        var airstream = cameraField.GetValue(fx) as Camera;
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
                object fx = fxVesselField.GetValue(module);
                if (fx == null) return;
                populate.Invoke(module, null);
                var airstream = cameraField.GetValue(fx) as Camera;
                if (airstream != null) airstream.enabled = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Firefly idle: could not wake a vessel's effects (" + e.Message + ").");
            }
        }
    }
}
