using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that takes Unity's own reflections out while the camera draws and gives them back to the
    // cameras after it.
    internal sealed partial class RtCamera
    {
        // Unity's reflections back for the cameras after this one.
        private void OnPostRender()
        {
            HoldReflections(false);
        }

        // Unity's own reflections are taken out while this camera draws, where the raytraced
        // reflections come into the frame without Deferred's reflections pass (the forward
        // light): the surfaces would shine with both. They are the reflection probes', KSP's on
        // the vessel among them, and the sky's. They are set back after this camera, for the
        // others. The probes are looked for once a second.
        private ReflectionProbe[] probes = new ReflectionProbe[0];
        private float[] probeIntensities = new float[0];
        private float probesFound = float.NegativeInfinity;
        private float heldReflectionIntensity = -1f;

        // Holds or gives back Unity's reflections as described above; a call that changes nothing does nothing.
        private void HoldReflections(bool hold)
        {
            if (hold == (heldReflectionIntensity >= 0f)) return;
            if (hold)
            {
                if (Time.unscaledTime - probesFound >= 1f)
                {
                    probesFound = Time.unscaledTime;
                    probes = FindObjectsOfType<ReflectionProbe>();
                    if (probeIntensities.Length < probes.Length) probeIntensities = new float[probes.Length];
                }
                for (int i = 0; i < probes.Length; i++)
                {
                    ReflectionProbe probe = probes[i];
                    if (probe == null) continue;
                    probeIntensities[i] = probe.intensity;
                    probe.intensity = 0f;
                }
                heldReflectionIntensity = RenderSettings.reflectionIntensity;
                RenderSettings.reflectionIntensity = 0f;
                return;
            }
            for (int i = 0; i < probes.Length; i++)
                if (probes[i] != null && probes[i].intensity == 0f) probes[i].intensity = probeIntensities[i];
            if (RenderSettings.reflectionIntensity == 0f) RenderSettings.reflectionIntensity = heldReflectionIntensity;
            heldReflectionIntensity = -1f;
        }
    }
}
