using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Ui;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The camera path of the probe's default measurement (SceneProbeRunner): a variant's two passes, the sweep, the
    // path's segments and the capture of each stage with its read-back.
    internal sealed partial class SceneProbeRunner
    {
        // One variant: the camera path with the vessel, then without.
        private IEnumerator Run(string name)
        {
            variant = name;
            Directory.CreateDirectory(Path.Combine(folder, name));
            capturePrefix = "";
            // Both passes from the same heading: the sky behind the vessel the same.
            if (float.IsNaN(startHeading)) startHeading = FlightCamera.fetch.camHdg;
            // -redefinitionProbeFreeze: game time stopped for both passes, so that
            // clouds and water stand still between them and only the vessel differs.
            float timeScale = Time.timeScale;
            if (Freeze) Time.timeScale = 0f;
            yield return Turn(name);
            List<Renderer> hidden = HideVessel();
            capturePrefix = "h";
            yield return Turn(name + " without the vessel");
            foreach (Renderer r in hidden)
                if (r != null) r.enabled = true;
            Time.timeScale = timeScale;
        }

        // Whether -redefinitionProbeFreeze stops game time for both passes of a variant.
        private static bool Freeze
        {
            get { return CommandLine.Has("-redefinitionProbeFreeze"); }
        }

        // The active vessel's enabled renderers disabled; those it disabled, to enable again.
        private static List<Renderer> HideVessel()
        {
            List<Renderer> hidden = new List<Renderer>();
            foreach (Part part in FlightGlobals.ActiveVessel.parts)
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>())
                    if (r.enabled)
                    {
                        r.enabled = false;
                        hidden.Add(r);
                    }
            return hidden;
        }

        // For the vendors' checks: from below the vessel, the sky moving past it left
        // and right, without captures of its own.
        private IEnumerator Sweep(string name)
        {
            FlightCamera camera = FlightCamera.fetch;
            float hdg = startHeading;
            camera.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
            int n = sweepSubdivide;
            // -redefinitionProbeSweepRight: the same path the other way round.
            float sign = CommandLine.Has("-redefinitionProbeSweepRight") ? -1f : 1f;
            int captureStep = CaptureStep;
            for (int i = 0; i < 60 * n; i++)
            {
                hdg += sign * (i < 20 * n || i >= 40 * n ? 1.5f : -1.5f) / n * Mathf.Deg2Rad;
                camera.camHdg = hdg;
                camera.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
                // The capture without the vessel spans the frames either side of the
                // one with it, of which one shows the same step.
                if (captureDuringSweep && name == "renderdoc" && i == captureStep)
                    RenderDocCapture.Capture("camera at sweep step " + captureStep);
                if (captureDuringSweep && name != "renderdoc" && i == captureStep - 1)
                    RenderDocCapture.CaptureFrames(4, "camera at sweep step " + captureStep + ", " + name);
                yield return null;
            }
        }

        // A stretch of the path: per frame the change of heading and pitch, in degrees.
        private struct Segment
        {
            // Its name, a capture's phase in meta.txt; the heading's and the pitch's change a frame; its frames.
            internal string Name;
            internal float Heading;
            internal float Pitch;
            internal int Frames;
        }

        // From below the vessel, the sky behind it, the camera moves the sky past it to
        // each side: left, right, down, up, each followed by a steady stretch.
        private static readonly Segment[] Segments =
        {
            new Segment { Name = "steady", Frames = SteadyFrames },
            new Segment { Name = "left", Heading = 1.5f, Frames = 20 },
            new Segment { Name = "after-left", Frames = 20 },
            new Segment { Name = "right", Heading = -1.5f, Frames = 40 },
            new Segment { Name = "after-right", Frames = 20 },
            new Segment { Name = "down", Pitch = 1f, Frames = 20 },
            new Segment { Name = "after-down", Frames = 20 },
            new Segment { Name = "up", Pitch = -1f, Frames = 20 },
            new Segment { Name = "after-up", Frames = 20 },
        };

        // The camera path, with captures in the middle of each move and at the end of
        // each steady stretch.
        private IEnumerator Turn(string name)
        {
            captureIndex = 0;
            FlightCamera camera = FlightCamera.fetch;
            float hdg = startHeading;
            float pitch = StartPitchDegrees;
            foreach (Segment segment in Segments)
            {
                bool moving = segment.Heading != 0f || segment.Pitch != 0f;
                for (int i = 1; i <= segment.Frames; i++)
                {
                    hdg += segment.Heading * Mathf.Deg2Rad;
                    pitch += segment.Pitch;
                    camera.camPitch = pitch * Mathf.Deg2Rad;
                    camera.camHdg = hdg;
                    bool capture = moving ? i == segment.Frames / 2 || i == segment.Frames / 2 + 1 : i == segment.Frames;
                    if (capture)
                    {
                        phase = segment.Name;
                        yield return Capture();
                    }
                    else
                    {
                        ReadNext();
                        yield return null;
                    }
                }
            }
            Debug.Log(Log.Tag + " Probe: " + name + " measured.");
        }

        // The stages of this frame's scene camera, around the vessel.
        private IEnumerator Capture()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            // In the camera's pixels: its screen points are the screen's (ScreenCoordinates).
            int width = scene.pixelWidth, height = scene.pixelHeight;
            Vector3 viewport = scene.WorldToViewportPoint(vessel.CoMD);
            Vector3 screen = new Vector3(viewport.x * width, viewport.y * height, viewport.z);
            int x = Mathf.Clamp(Mathf.RoundToInt(screen.x) - Crop / 2, 0, Mathf.Max(0, width - Crop));
            int y = Mathf.Clamp(Mathf.RoundToInt(screen.y) - Crop / 2, 0, Mathf.Max(0, height - Crop));
            Vector2 scale = new Vector2((float)Crop / width, (float)Crop / height);
            Vector2 offset = new Vector2((float)x / width, (float)y / height);
            int n = captureIndex++;
            string capture = capturePrefix + n.ToString("00", CultureInfo.InvariantCulture);
            string prefix = Path.Combine(Path.Combine(folder, variant), capture);
            meta.Append(variant).Append(' ').Append(Time.frameCount).Append(' ').Append(capture).Append(' ')
                .Append(x).Append(' ').Append(y).Append(' ').Append(phase).Append('\n');

            foreach (Station station in stations)
            {
                RenderTexture crop = Target(RenderTextureFormat.ARGBHalf);
                CommandBuffer cb = Buffer(station.Event);
                cb.Blit(BuiltinRenderTextureType.CameraTarget, crop, scale, offset);
                reads.Enqueue(new Read { Path = prefix + "-" + station.Name + ".bin", Texture = crop });
            }
            CommandBuffer last = Buffer(CameraEvent.AfterEverything);
            RenderTexture depth = Target(RenderTextureFormat.RFloat);
            last.Blit(new RenderTargetIdentifier("_CameraDepthTexture"), depth, scale, offset);
            reads.Enqueue(new Read { Path = prefix + "-depth.bin", Texture = depth });
            if (fullMask == null || fullMask.width != width || fullMask.height != height)
            {
                if (fullMask != null) fullMask.Release();
                fullMask = new RenderTexture(width, height, 24, RenderTextureFormat.R8);
                fullMask.Create();
            }
            last.SetRenderTarget(fullMask);
            last.ClearRenderTarget(true, true, Color.clear);
            last.SetViewProjectionMatrices(scene.worldToCameraMatrix, scene.projectionMatrix);
            foreach (Part part in vessel.parts)
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.gameObject.activeInHierarchy)
                        for (int s = 0; s < r.sharedMaterials.Length; s++)
                            last.DrawRenderer(r, maskMaterial, s, 0);
            RenderTexture mask = Target(RenderTextureFormat.R8);
            last.Blit(fullMask, mask, scale, offset);
            reads.Enqueue(new Read { Path = prefix + "-mask.bin", Texture = mask });

            // The finished frame, as it goes to the screen: the scene camera's image
            // reaches it only after its own last event (measured: AfterEverything
            // holds the frame without the vessel).
            yield return new WaitForEndOfFrame();
            // Around the same point of the screen, Crop screen pixels wide whatever the
            // render size, so that runs at different render sizes compare. The
            // screenshot's rows run the other way from the camera's buffers: counted
            // from the other edge.
            int screenX = Mathf.Clamp(Mathf.RoundToInt(screen.x * Screen.width / width) - Crop / 2, 0,
                                      Mathf.Max(0, Screen.width - Crop));
            int screenY = Mathf.Clamp(Screen.height - Mathf.RoundToInt(screen.y * Screen.height / height) - Crop / 2, 0,
                                      Mathf.Max(0, Screen.height - Crop));
            RenderTexture full = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
            RenderTexture screenCrop = Target(RenderTextureFormat.ARGB32);
            Graphics.Blit(full, screenCrop, new Vector2((float)Crop / Screen.width, (float)Crop / Screen.height),
                          new Vector2((float)screenX / Screen.width, (float)screenY / Screen.height));
            RenderTexture.ReleaseTemporary(full);
            reads.Enqueue(new Read { Path = prefix + "-screen.bin", Texture = screenCrop });
            // The buffers ran in that frame: emptied for the frames after.
            foreach (CommandBuffer cb in buffers.Values) cb.Clear();
        }

        // A new linear crop texture, Crop pixels square.
        private static RenderTexture Target(RenderTextureFormat format)
        {
            RenderTexture texture = new RenderTexture(Crop, Crop, 0, format, RenderTextureReadWrite.Linear);
            texture.Create();
            return texture;
        }

        // The probe's command buffer on the scene camera's event, added the first time it is asked for.
        private CommandBuffer Buffer(CameraEvent e)
        {
            CommandBuffer cb;
            if (!buffers.TryGetValue(e, out cb))
            {
                cb = new CommandBuffer { name = "ReDefinition probe " + e };
                scene.AddCommandBuffer(e, cb);
                buffers[e] = cb;
            }
            return cb;
        }

        // The probe's command buffers taken off the scene camera.
        private void Detach()
        {
            foreach (KeyValuePair<CameraEvent, CommandBuffer> pair in buffers)
                if (scene != null) scene.RemoveCommandBuffer(pair.Key, pair.Value);
            buffers.Clear();
        }

        // One read-back at a time, written by a thread.
        private void ReadNext()
        {
            if (Volatile.Read(ref busy) != 0 || reads.Count == 0) return;
            Read next = reads.Dequeue();
            Volatile.Write(ref busy, 1);
            AsyncGPUReadback.Request(next.Texture, 0, request =>
            {
                next.Texture.Release();
                Destroy(next.Texture);
                if (request.hasError)
                {
                    Volatile.Write(ref busy, 0);
                    return;
                }
                NativeArray<byte> data = request.GetData<byte>();
                byte[] bytes = data.ToArray();
                string path = next.Path;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        File.WriteAllBytes(path, bytes);
                    }
                    catch (Exception)
                    {
                        // A diagnostic: a file not written is a file missing.
                    }
                    Volatile.Write(ref busy, 0);
                });
            });
        }
    }
}
