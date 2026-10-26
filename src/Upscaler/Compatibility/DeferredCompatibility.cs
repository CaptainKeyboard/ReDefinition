using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReDefinition.Upscaler
{
    // Deferred's screen-space reflections size their textures and command
    // buffers once, by their camera, and again only when HDR or VR change
    // (ScreenSpaceReflections.OnPreRender, "also need to detect width/height
    // changes here"). The rig changes that size whenever it attaches, detaches
    // or changes the mode. RecreateTexturesAndBuffers(1, false), which
    // OnPreRender calls on an HDR change, sizes them anew by
    // RenderingUtils.GetCameraRenderDimensions: the camera's activeTexture, the
    // screen where it has none.
    //
    // A component is sized anew only where its size -- its screenColor, made at
    // the camera's full size -- is not its camera's any more (pixelWidth, for a
    // redirected camera the rig texture's): a rebuild throws its history away.
    // At the end of the frame, after every camera has rendered: OnPreRender adds
    // the command buffers and OnPostRender takes those same ones off again, so a
    // rebuild between the two would leave the old ones on the camera. Where
    // Deferred would not read the camera's size there -- no activeTexture
    // outside the camera's render -- or the component has not started yet, it
    // is sized as its camera next culls (Camera.onPreCull), still before its
    // OnPreRender.
    internal static class DeferredCompatibility
    {
        // The full name of Deferred's screen-space reflections component.
        private const string SsrTypeName = "Deferred.ScreenSpaceReflections";

        // Deferred's members, looked up once by Resolve: the component's type, its camera and screenColor
        // history fields, the history's indexer (found on first use), and RecreateTexturesAndBuffers.
        private static bool resolved;
        private static Type ssrType;
        private static FieldInfo targetCamera;
        private static FieldInfo screenColor;
        private static PropertyInfo historyItem;
        private static MethodInfo recreate;

        // The components to size as their camera next culls, whether OnPreCull is subscribed for them, and the
        // reason of the latest change, for the log.
        private static readonly List<Behaviour> pending = new List<Behaviour>();
        private static bool listening;
        private static string pendingReason;

        // Whether Deferred is installed with the members this is written against.
        public static bool Present
        {
            get
            {
                Resolve();
                return ssrType != null && targetCamera != null && screenColor != null && recreate != null;
            }
        }

        // Runs at the end of a frame in which the render size changed (ReDefinitionAddon's follow-up). Every
        // reflections component whose size is not its camera's any more is sized anew, or as its camera
        // next culls where Deferred could not read the size now.
        public static void AfterRenderSizeChange(string reason)
        {
            if (!Present) return;

            int resized = 0;
            foreach (Object found in Object.FindObjectsOfType(ssrType))
            {
                Behaviour ssr = found as Behaviour;
                if (ssr == null) continue;
                // One at a time: one failing must not keep the others.
                try
                {
                    Camera camera = targetCamera.GetValue(ssr) as Camera;
                    Vector2Int current;
                    if (camera == null || !Size(ssr, out current))
                    {
                        Defer(ssr, reason);
                        continue;
                    }
                    if (current == PixelSize(camera)) continue;
                    if (DeferredReads(camera) != PixelSize(camera))
                    {
                        Defer(ssr, reason);
                        continue;
                    }
                    recreate.Invoke(ssr, new object[] { 1, false });
                    resized++;
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("deferred-ssr", "Deferred's screen-space reflections could not be sized anew ("
                                                          + CompatibilityLog.Reason(e) + ").");
                }
            }

            if (resized > 0)
                Debug.Log(Log.Tag + " Deferred's screen-space reflections sized anew for " + resized + " camera(s) ("
                          + reason + ").");
        }

        // Queues a component to be sized as its camera next culls, subscribing to Camera.onPreCull once.
        private static void Defer(Behaviour ssr, string reason)
        {
            if (!pending.Contains(ssr)) pending.Add(ssr);
            pendingReason = reason;
            if (listening) return;
            Camera.onPreCull += OnPreCull;
            listening = true;
        }

        // Sizes the queued components of the camera culling now, where Deferred reads that camera's size (a
        // warning where not); unsubscribes once the queue is empty.
        private static void OnPreCull(Camera culling)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Behaviour ssr = pending[i];
                if (ssr == null)
                {
                    pending.RemoveAt(i);
                    continue;
                }
                try
                {
                    Camera camera = targetCamera.GetValue(ssr) as Camera;
                    if (camera == null)
                    {
                        // Not started yet: its Start takes the camera, before it renders.
                        if (ssr.GetComponent<Camera>() != culling) continue;
                        pending.RemoveAt(i);
                        continue;
                    }
                    if (camera != culling) continue;
                    pending.RemoveAt(i);

                    Vector2Int current;
                    Vector2Int wanted = PixelSize(camera);
                    if (!Size(ssr, out current) || current == wanted) continue;
                    Vector2Int read = DeferredReads(camera);
                    if (read != wanted)
                    {
                        CompatibilityLog.Warn("deferred-ssr-size", "Deferred's screen-space reflections on " + camera.name
                                                                   + " stay at " + current.x + "x" + current.y
                                                                   + ": Deferred reads " + read.x + "x" + read.y
                                                                   + " for a camera of " + wanted.x + "x" + wanted.y + ".");
                        continue;
                    }
                    recreate.Invoke(ssr, new object[] { 1, false });
                    Debug.Log(Log.Tag + " Deferred's screen-space reflections on " + camera.name + " sized anew as it"
                              + " culled (" + pendingReason + ").");
                }
                catch (Exception e)
                {
                    pending.RemoveAt(i);
                    CompatibilityLog.Warn("deferred-ssr", "Deferred's screen-space reflections could not be sized anew ("
                                                          + CompatibilityLog.Reason(e) + ").");
                }
            }
            if (pending.Count > 0) return;
            Camera.onPreCull -= OnPreCull;
            listening = false;
        }

        // The size the camera renders at: for a redirected camera the rig texture's.
        private static Vector2Int PixelSize(Camera camera)
        {
            return new Vector2Int(camera.pixelWidth, camera.pixelHeight);
        }

        // What GetCameraRenderDimensions reads now, VR aside.
        private static Vector2Int DeferredReads(Camera camera)
        {
            RenderTexture active = camera.activeTexture;
            return active != null ? new Vector2Int(active.width, active.height) : new Vector2Int(Screen.width, Screen.height);
        }

        // The size the component was last made for: its screenColor history at
        // the camera's full size (CreateOrResizeRenderTextures). False before
        // Start has made it.
        private static bool Size(Behaviour ssr, out Vector2Int size)
        {
            size = Vector2Int.zero;
            object history = screenColor.GetValue(ssr);
            if (history == null) return false;
            PropertyInfo item = historyItem ?? (historyItem = history.GetType().GetProperty("Item", TypeLookup.Any, null,
                null, new[] { typeof(bool), typeof(bool), typeof(int) }, null));
            if (item == null) return false;
            RenderTexture texture = item.GetValue(history, new object[] { true, true, 0 }) as RenderTexture;
            if (texture == null) return false;
            size = new Vector2Int(texture.width, texture.height);
            return true;
        }

        // Looks Deferred's type and members up once; warns where Deferred is there but its members are not.
        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            ssrType = TypeLookup.Find(SsrTypeName);
            if (ssrType == null) return;
            targetCamera = ssrType.GetField("targetCamera", TypeLookup.Any);
            screenColor = ssrType.GetField("screenColor", TypeLookup.Any);
            recreate = ssrType.GetMethod("RecreateTexturesAndBuffers", TypeLookup.Any, null,
                                         new[] { typeof(int), typeof(bool) }, null);

            if (!Present)
                Debug.LogWarning(Log.Tag + " Deferred is installed, but not the one this mod was written against; its"
                                 + " screen-space reflections stay at the size they were made for.");
        }
    }
}
