using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Finds the components other mods' filters put on cameras, and lists the enabled cameras, without allocating
    // in the once-a-second passes.
    internal static class HostCameras
    {
        // The components of a type that the antialiasing passes of Scatterer and Deferred
        // put on cameras (Scatterer.cs: AddComponent on the near, far, scaled space and
        // internal cameras). The whole scene is searched in the first ten seconds of a
        // scene, while they are being set up. After that the enabled cameras are asked:
        // FindObjectsOfType walks every object, and once a second it held a frame for
        // 3 to 7 ms (measured 2026-10-06).
        private static GameScenes searchedScene = GameScenes.LOADING;
        private static float sceneSeenAt;

        // The enabled cameras, as Camera.allCameras lists them, and one camera's components of a type: kept between
        // the once-a-second passes so that each makes no array.
        private static Camera[] cameraScratch = new Camera[8];
        private static readonly List<Component> componentScratch = new List<Component>();

        // Fills `found` (emptied first) with the components of `type` on the cameras: the whole scene searched in
        // the first ten seconds, then the enabled cameras asked.
        internal static void OnCameras(Type type, List<Component> found)
        {
            found.Clear();
            if (type == null) return;
            if (searchedScene != HighLogic.LoadedScene)
            {
                searchedScene = HighLogic.LoadedScene;
                sceneSeenAt = Time.unscaledTime;
            }
            if (Time.unscaledTime - sceneSeenAt < 10f)
            {
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(type))
                    if (o is Component c) found.Add(c);
                return;
            }
            int count = EnabledCameras();
            for (int i = 0; i < count; i++)
            {
                Camera camera = cameraScratch[i];
                if (camera == null) continue;
                camera.GetComponents(type, componentScratch);
                foreach (Component c in componentScratch)
                    if (c != null && !found.Contains(c)) found.Add(c);
            }
            componentScratch.Clear();
        }

        // The enabled cameras into the scratch array, in the order of Camera.allCameras; how many there are.
        // CameraAt reads them until the next call.
        internal static int EnabledCameras()
        {
            int count = Camera.allCamerasCount;
            if (cameraScratch.Length < count) cameraScratch = new Camera[count];
            return Camera.GetAllCameras(cameraScratch);
        }

        // The camera at that index of the last EnabledCameras.
        internal static Camera CameraAt(int index)
        {
            return cameraScratch[index];
        }
    }
}
