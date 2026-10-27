using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The light probe's shots at the space centre at night: the runway's light pools and the launch pad's smoke.
    internal static partial class LightProbe
    {
        // At night on the runway, 3 m over its middle line a little in from one end, along
        // it: its lights as KSP draws them, without them, and dimmed (Scenery.RunwayLights).
        // The runway's line from its light meshes: their middle and the direction between
        // the two farthest apart.
        private static IEnumerator RunwayShots(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            CelestialBody body = vessel.mainBody;
            var runway = new List<Renderer>();
            foreach (Renderer light in Scenery.RunwayLights.Lights)
                if (light != null && light.enabled && light.gameObject.activeInHierarchy) runway.Add(light);
            if (runway.Count < 2)
            {
                Debug.LogWarning(Log.Tag + " Probe: light: no runway lights found; no runway shots.");
                yield break;
            }
            Vector3 middle = Vector3.zero;
            foreach (Renderer light in runway) middle += light.bounds.center;
            middle /= runway.Count;
            Vector3 a = runway[0].bounds.center, b = a;
            foreach (Renderer p in runway)
                foreach (Renderer q in runway)
                    if ((p.bounds.center - q.bounds.center).sqrMagnitude > (b - a).sqrMagnitude)
                    {
                        a = p.bounds.center;
                        b = q.bounds.center;
                    }
            Vector3 up = (middle - (Vector3)body.position).normalized;
            Vector3 direction = Vector3.ProjectOnPlane(b - a, up).normalized;
            float half = (b - a).magnitude * 0.5f;
            Vector3 eye = middle - direction * (half * 0.9f) + up * 3f;
            Debug.Log(Log.Tag + " Probe: light: runway " + (half * 2f).ToString("0", CultureInfo.InvariantCulture) + " m by its "
                      + runway.Count + " light meshes.");
            // The flight camera's own placing held (FlightCamera.DeactivateUpdate) and the
            // camera put at the runway; back afterwards.
            FlightCamera flight = FlightCamera.fetch;
            Vector3 position = flight.transform.position;
            Quaternion rotation = flight.transform.rotation;
            flight.DeactivateUpdate();
            float before = Scenery.RunwayLights.Brightness;
            try
            {
                flight.transform.position = eye;
                flight.transform.rotation = Quaternion.LookRotation(direction, up);
                Scenery.RunwayLights.Brightness = 1f;
                yield return Shot(folder, "runway-ksp");
                foreach (Renderer light in runway) light.enabled = false;
                yield return Shot(folder, "runway-without");
                foreach (Renderer light in runway) light.enabled = true;
                Scenery.RunwayLights.Brightness = before;
                yield return Shot(folder, "runway-dimmed");
            }
            finally
            {
                foreach (Renderer light in runway) light.enabled = true;
                Scenery.RunwayLights.Brightness = before;
                flight.transform.position = position;
                flight.transform.rotation = rotation;
                flight.ActivateUpdate();
            }
        }

        // At night, the camera beside the pad, 60 m off and 15 m up, the vessel's next stage
        // activated: the pad's smoke (PadFXReceiver, PadSmokeLvl3) after 1, 2 and 4 seconds.
        private static IEnumerator PadFxShots(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            Vector3 up = ((Vector3)(vessel.CoMD - vessel.mainBody.position)).normalized;
            Vector3 side = Vector3.ProjectOnPlane(Vector3.Cross(up, Vector3.forward), up).normalized;
            Vector3 pad = vessel.CoMD - up * 10f;
            Vector3 eye = pad + side * 60f + up * 15f;
            FlightCamera flight = FlightCamera.fetch;
            flight.DeactivateUpdate();
            try
            {
                flight.transform.position = eye;
                flight.transform.rotation = Quaternion.LookRotation(pad - eye, up);
                yield return Shot(folder, "padfx-before");
                KSP.UI.Screens.StageManager.ActivateNextStage();
                float started = Time.time;
                foreach (float at in new[] { 1f, 2f, 4f })
                {
                    while (Time.time - started < at) yield return null;
                    flight.transform.position = eye;
                    flight.transform.rotation = Quaternion.LookRotation(pad - eye, up);
                    yield return new WaitForEndOfFrame();
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, "padfx-" + at.ToString("0", CultureInfo.InvariantCulture) + "s.png"));
                }
                // Each pad smoke system's particles and its renderer's settings.
                foreach (ParticleSystem system in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                {
                    if (!system.name.StartsWith("PadSmoke", StringComparison.Ordinal)) continue;
                    var renderer = system.GetComponent<ParticleSystemRenderer>();
                    Material m = renderer != null ? renderer.sharedMaterial : null;
                    Debug.Log(Log.Tag + " Probe: light: pad smoke '" + system.name + "' " + system.particleCount + " particles, mode "
                              + (renderer != null ? renderer.renderMode.ToString() : "-") + ", material " + (m != null ? m.name + " (" + m.shader.name + ")" : "-")
                              + (m != null && m.mainTexture != null ? ", texture " + m.mainTexture.name : ""));
                }
            }
            finally
            {
                flight.ActivateUpdate();
            }
        }
    }
}
