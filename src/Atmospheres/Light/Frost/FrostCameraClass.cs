using System;

namespace ReDefinition.Atmospheres
{
    // The game's camera modes as the frost tells them apart (CameraManager.CameraMode mapped by the renderer).
    internal enum FrostCameraMode
    {
        Other = 0,
        Flight = 1,
        Iva = 2,
        Internal = 3,
        Map = 4
    }

    // Decides where the frost can be seen. Pure.
    //   Eva  the flight camera is at the head of a kerbal outside (the stock first-person view or ThroughTheEyes), or the IVA/internal
    //        camera is a kerbal's: the frost covers the whole picture, as there is no geometry of a visor to frost [VERIFY how the stock
    //        first-person view is reported on KSP 1.12; both routes are handled]
    //   Iva  a cabin: only the windows are frosted (the interior is not), so this view needs the interior mask or the quad fallback
    //   Off  the external camera, the map view, any camera but the flight camera's
    internal static class FrostCameraClass
    {
        // The distance from the camera to the kerbal's centre inside which the flight camera counts as first person.
        internal const float FirstPersonM = 0.9f;

        internal static FrostView Classify(FrostCameraMode mode, bool mapView, bool vesselIsEva, float cameraToVesselM, float firstPersonM)
        {
            if (mapView || mode == FrostCameraMode.Map || mode == FrostCameraMode.Other) return FrostView.Off;
            if (mode == FrostCameraMode.Iva || mode == FrostCameraMode.Internal) return vesselIsEva ? FrostView.Eva : FrostView.Iva;
            if (mode == FrostCameraMode.Flight && vesselIsEva && cameraToVesselM >= 0f && cameraToVesselM <= firstPersonM) return FrostView.Eva;
            return FrostView.Off;
        }
    }

    // How much light there is to scatter in the frost, 0 to 1: the ambient share always, the sun's share when it is up and its light
    // reaches the camera.
    internal static class FrostLight
    {
        internal static float Factor(float sunElevationRad, float sunTransmission, ref FrostTuning t)
        {
            float up = FrostShape.Smooth(-0.05f, 0.25f, (float)Math.Sin(sunElevationRad));
            float f = t.Ambient + t.SunShare * up * FrostShape.Clamp01(sunTransmission);
            return FrostShape.Clamp01(f);
        }
    }
}
