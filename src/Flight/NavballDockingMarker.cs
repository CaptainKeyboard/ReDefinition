using System.Collections;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Flight
{
    // Where to point the nose to dock: while a docking port is the target, a marker
    // on the navball stands in the direction into that port, the opposite of the
    // way it faces (ModuleDockingNode.nodeTransform.forward). With the vessel's
    // nose (its control point's up) on it, the two ports face each other. KSP's
    // markers are pictures on quads under the navball's vector pivot, placed at
    // attitudeGymbal times their direction times VectorUnitScale, shown while in
    // front (z at least VectorUnitCutoff) and faded towards the ball's edge and top
    // and bottom through their material's _Opacity (NavBall.SetVectorAlphaTint;
    // decompiled). This one is a copy of the target's marker (progradeWaypoint) with
    // a picture of its own (Icons/Navball_Docking.png, tools/make_icon.py), placed
    // and faded the same way.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavballDockingMarker : MonoBehaviour
    {
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");

        private NavBall navBall;
        private Transform marker;
        private Material material;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (navBall = FindObjectOfType<NavBall>()) == null) yield return null;
            Texture2D picture = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture("ReDefinition/Icons/Navball_Docking", false) : null;
            if (navBall == null || navBall.progradeWaypoint == null || picture == null)
            {
                Debug.Log(Log.Tag + " The navball's docking marker is not shown: the navball, its target marker or the picture was not found.");
                yield break;
            }
            GameObject copy = Instantiate(navBall.progradeWaypoint.gameObject, navBall.progradeWaypoint.parent, false);
            copy.name = "ReDefinitionDockingMarker";
            marker = copy.transform;
            MeshRenderer renderer = copy.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                material = new Material(renderer.sharedMaterial) { mainTexture = picture };
                renderer.sharedMaterial = material;
            }
            copy.SetActive(false);
            Debug.Log(Log.Tag + " The navball shows where to point for a docking port as the target.");
        }

        private void LateUpdate()
        {
            if (marker == null || navBall == null) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            ModuleDockingNode port = vessel != null ? vessel.targetObject as ModuleDockingNode : null;
            if (port == null || port.nodeTransform == null || !FlightUiSwitch.On(s => s.NavballDockingMarker))
            {
                if (marker.gameObject.activeSelf) marker.gameObject.SetActive(false);
                return;
            }
            Vector3 position = navBall.attitudeGymbal * (-port.nodeTransform.forward * navBall.VectorUnitScale);
            marker.localPosition = position;
            bool front = position.z >= navBall.VectorUnitCutoff;
            if (marker.gameObject.activeSelf != front) marker.gameObject.SetActive(front);
            if (front && material != null) material.SetFloat(OpacityId, Opacity(position));
        }

        // As KSP fades its markers: by how far they face out of the ball, and more
        // near its top and bottom.
        internal static float Opacity(Vector3 position)
        {
            Vector3 direction = position.normalized;
            float opacity = Mathf.Clamp01(Vector3.Dot(direction, Vector3.forward));
            float up = Vector3.Dot(direction, Vector3.up);
            if (up >= 0.65f) opacity *= Mathf.Clamp01(Mathf.InverseLerp(0.9f, 0.65f, up));
            else if (up <= -0.75f) opacity *= Mathf.Clamp01(Mathf.InverseLerp(-0.95f, -0.75f, up));
            return opacity;
        }

        private void OnDestroy()
        {
            if (material != null) Destroy(material);
        }
    }
}
