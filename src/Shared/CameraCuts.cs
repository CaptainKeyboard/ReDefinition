using UnityEngine;

namespace ReDefinition.Shared
{
    // Jump cuts of KSP's flight camera, found by comparing the camera's state with
    // the previous look. AMD: set reset "to true for the first frame of the
    // discontinuous camera transformation" -- otherwise FSR blends the old view into
    // the new one, and frame generation, which gets the same flag in the frame
    // packet, interpolates between two unrelated images. SharedFrame looks when a
    // frame begins and again when the rig presents it.
    //
    // Compared rather than subscribed to. KSP's events arrive after the rig's
    // Update, twice for one cut, when nothing cut, or not at all: the IVA portrait
    // buttons move the view to another kerbal without one
    // (CameraManager.SetCameraIVA fires OnCameraChange only when the mode changes;
    // decompiled). A change of camera mode is handled by the add-on, which rebuilds
    // the rig for the new set of cameras.
    //
    // Not on a floating origin shift: above a speed threshold KSP shifts it every
    // frame (FloatingOrigin, "continuous"), and a reset every frame is an upscaler
    // without any history. What a shift does to Unity's motion vectors:
    // docs/development/shared-foundation.md, "The floating origin".
    //
    // Camera mods that take the view clear the target first -- Hullcam VDS when a
    // part camera takes over, CameraTools in SetCameraParent and SetDeathCam -- with
    // SetTargetNone, which is SetTarget(null, None) and leaves the camera's own pivot
    // as its target; they give the view back with SetTarget on the vessel. Both are
    // changes of target. A new parent of the flight camera is a cut as well: Hullcam
    // from one part to the next, CameraTools switching to its death camera or
    // stealing the camera back after KSP reverted it. KSP sets that parent itself in
    // FlightCamera.Start and OnVesselChange (its pivot), on every scene change
    // (PSystemSetup.OnSceneChange, ResetTransforms) and in the space centre
    // (SpaceCenterCamera2) -- before the rig exists, with a change of target, or at a
    // scene change, where a reset belongs anyway (all decompiled; the mods' sources).
    // Not detected are cuts that keep target and parent: Hullcam between two cameras
    // on one part without transforms of their own (hc_booster, RoverCam), CameraTools
    // switching its mode or its vessel while active, and jumps inside a CameraTools
    // mode. Such a mod can report them (ReDefinition.Api.Frame.RequestHistoryReset).
    internal static class CameraCuts
    {
        private static Transform lastTarget;
        private static Transform lastParent;
        private static Kerbal lastIvaKerbal;
        private static bool stateKnown;

        // The flight camera's target at the last look, for the rig's camera
        // instrument.
        internal static Transform Target
        {
            get { return lastTarget; }
        }

        // What changed since the last look, or null.
        internal static string Detect()
        {
            FlightCamera flight = FlightCamera.fetch;
            CameraManager manager = CameraManager.Instance;

            Transform target = flight != null ? flight.Target : null;
            Transform parent = flight != null ? flight.transform.parent : null;
            Kerbal ivaKerbal = manager != null
                               && manager.currentCameraMode == CameraManager.CameraMode.IVA
                ? manager.IVACameraActiveKerbal : null;

            string reason = null;
            if (stateKnown)
            {
                if (target != lastTarget)
                    reason = "camera target now '" + (target != null ? target.name : "none") + "'";
                else if (parent != lastParent)
                    reason = "camera parent now '" + (parent != null ? parent.name : "none") + "'";
                else if (ivaKerbal != lastIvaKerbal)
                    reason = "IVA view now at '" + (ivaKerbal != null ? ivaKerbal.name : "none") + "'";
            }

            lastTarget = target;
            lastParent = parent;
            lastIvaKerbal = ivaKerbal;
            stateKnown = true;
            return reason;
        }
    }
}
