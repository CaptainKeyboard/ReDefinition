using System.Collections;
using KSP.UI.Screens.Flight;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Hud
{
    // Where the navigation asks the nose to point in a climb or a descent: a green marker on the navball in that direction. It is made
    // in the way of the docking marker, a copy of the target's marker with a picture of its own, placed and faded the same way. It is shown
    // while the navigation has a direction to ask for. With the nose on it the vessel points as asked.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavballGuideMarker : MonoBehaviour
    {
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");

        private NavBall navBall;
        private Transform marker;
        private Material material;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (navBall = FindObjectOfType<NavBall>()) == null) yield return null;
            Texture2D picture = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture("ReDefinition/Icons/Navball_Guide", false) : null;
            if (navBall == null || navBall.progradeWaypoint == null || picture == null)
            {
                Debug.Log(Log.Tag + " The navball's guidance marker is not shown: the navball, its target marker or the picture was not found.");
                yield break;
            }
            GameObject copy = Instantiate(navBall.progradeWaypoint.gameObject, navBall.progradeWaypoint.parent, false);
            copy.name = "ReDefinitionGuideMarker";
            marker = copy.transform;
            MeshRenderer renderer = copy.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                material = new Material(renderer.sharedMaterial) { mainTexture = picture };
                renderer.sharedMaterial = material;
            }
            copy.SetActive(false);
        }

        // While the navigation has a direction: the marker placed and faded there; hidden otherwise.
        private void LateUpdate()
        {
            if (marker == null || navBall == null) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!GuidanceBoard.Current(vessel) || !GuidanceBoard.HasDirection)
            {
                if (marker.gameObject.activeSelf) marker.gameObject.SetActive(false);
                return;
            }
            Vector3d direction = GuidanceBoard.Direction;
            Vector3 position = navBall.attitudeGymbal * ((Vector3)direction * navBall.VectorUnitScale);
            marker.localPosition = position;
            bool front = position.z >= navBall.VectorUnitCutoff;
            if (marker.gameObject.activeSelf != front) marker.gameObject.SetActive(front);
            if (front && material != null) material.SetFloat(OpacityId, NavballDockingMarker.Opacity(position));
        }

        private void OnDestroy()
        {
            if (material != null) Destroy(material);
        }
    }
}
