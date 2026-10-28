using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Scenery
{
    // Firefly's airstream camera moved with the vessel while the frame is drawn between the physics steps.
    // Firefly puts the camera, an orthographic one a vessel's width across, on the vessel in its Update and takes its
    // _AirstreamVP from it there. RenderInterpolation moves the parts after every LateUpdate, by up to a step's
    // travel (60 m at 3 km/s), and the camera stayed behind. The vessel then stood outside its picture and the
    // airstream texture held nothing. The envelopes over the vessel looked up that empty texture, so the plasma
    // of a re-entry showed in a frame now and then and not in the rest.
    // Probe, Mk1 pod at 3 km/s, 2026-10-28: the envelope was 58 m from the camera with the interpolation on, 1.2 m with it off.
    // The camera now moves by the vessel's offset for the length of the drawing, and Firefly's own
    // UpdateMaterialProperties takes the matrix from the moved camera. Its Update puts both right for the next frame.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class FireflyInterpolation : MonoBehaviour
    {
        // Firefly's AtmoFxModule and the members of it and of its AtmoFxVessel this reads and calls.
        private Type moduleType;
        private FieldInfo fxVesselField;
        private Func<object, object> readFxVessel;
        private Func<object, bool> readIsLoaded;
        private Func<object, Camera> readCamera;
        private MethodInfo updateMaterialProperties;

        // The Debug switch of the probes; on at every start.
        internal static bool Enabled = true;

        // A camera moved this frame and where it stood.
        private struct Moved
        {
            internal Transform Transform;
            internal Vector3 Position;
        }

        private readonly List<Moved> moved = new List<Moved>();
        private readonly object[] noArguments = new object[0];

        // Looks up Firefly's members; anything missing, or Firefly changed (ForeignAssumptions), leaves this off.
        private void Start()
        {
            if (!ForeignAssumptions.FireflyIdle.Holds) return;
            moduleType = TypeLookup.Find("Firefly.AtmoFxModule");
            Type vesselType = TypeLookup.Find("Firefly.AtmoFxVessel");
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            fxVesselField = moduleType?.GetField("fxVessel", Any);
            FieldInfo isLoadedField = moduleType?.GetField("isLoaded", Any);
            FieldInfo cameraField = vesselType?.GetField("airstreamCamera", Any);
            updateMaterialProperties = moduleType?.GetMethod("UpdateMaterialProperties", Any, null, Type.EmptyTypes, null);
            readFxVessel = CompiledGetter.Of<object>(fxVesselField);
            readIsLoaded = CompiledGetter.Of<bool>(isLoadedField);
            readCamera = CompiledGetter.Of<Camera>(cameraField);
            if (readFxVessel == null || readIsLoaded == null || readCamera == null || updateMaterialProperties == null) return;
            RenderInterpolation.Shifted += OnShifted;
            RenderInterpolation.Restored += OnRestored;
        }

        // Takes the handlers off and puts any camera moved back.
        private void OnDestroy()
        {
            RenderInterpolation.Shifted -= OnShifted;
            RenderInterpolation.Restored -= OnRestored;
            OnRestored();
        }

        // A vessel's parts were moved by offset for this frame: its airstream camera goes along, and Firefly's matrix
        // for the camera is made again.
        private void OnShifted(Vessel vessel, Vector3 offset)
        {
            if (!Enabled || vessel == null || offset == Vector3.zero) return;
            try
            {
                foreach (VesselModule module in vessel.vesselModules)
                {
                    if (module == null || module.GetType() != moduleType || !readIsLoaded(module)) continue;
                    object fx = readFxVessel(module);
                    Camera camera = fx != null ? readCamera(fx) : null;
                    // A camera switched off is Firefly's idle one (FireflyIdle): nothing draws from it.
                    if (camera == null || !camera.enabled) continue;
                    Transform t = camera.transform;
                    moved.Add(new Moved { Transform = t, Position = t.position });
                    t.position += offset;
                    updateMaterialProperties.Invoke(module, noArguments);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Firefly's airstream camera could not follow the render interpolation (" + e.GetType().Name + ": " + e.Message + ").");
                RenderInterpolation.Shifted -= OnShifted;
                OnRestored();
            }
        }

        // The cameras back where Firefly put them, as the next frame begins.
        private void OnRestored()
        {
            for (int i = moved.Count - 1; i >= 0; i--)
                if (moved[i].Transform != null) moved[i].Transform.position = moved[i].Position;
            moved.Clear();
        }
    }
}
