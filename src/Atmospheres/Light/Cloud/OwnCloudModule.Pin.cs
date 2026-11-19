using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The pinned capture of the own cloud pass (master plan phase 0 item 3, .local/plans/wolken-m1-fakten.md section F): what the T09 probe's -t09Pin needs from the module to
    // take the clouds' own output in a state a second start and the weather player can reproduce. Nothing here runs unless a probe calls it; the frame origin it uses is 0
    // by default (OwnCloudModule.FrameOrigin), so the pass draws as before.
    internal sealed partial class OwnCloudModule
    {
        // The numbers of the pinned state that pass.json carries next to the frame's (PinJson).
        internal struct PinState
        {
            internal int FrameCount, FrameOrigin, FrameNumber, FramesSinceCut, BlockIndex, CutCount;
            internal Vector3 CameraWorld, PlanetCenterWorld;
            internal Quaternion CameraRotation;
            internal float CameraFov;
            internal int CameraWidth, CameraHeight;
        }

        // A capture under way: the files land in Dir; Done once each part is written or failed. Problems names every part that failed.
        internal sealed class PinCapture
        {
            internal string Dir, Tag;
            internal bool CloudWritten, DepthWritten, DumpWritten, Finished;
            internal int CloudPending, FramesSinceCut = -1, FrameNumber = -1, CloudWidth, CloudHeight, DepthWidth, DepthHeight;
            internal readonly List<string> Problems = new List<string>();

            internal bool Done { get { return Finished && CloudPending == 0; } }

            internal void Fail(string what)
            {
                lock (Problems) Problems.Add(what);
            }
        }

        // Asks for a map round with a snap and a height build at once (the clock was just pinned: the next maps and heights are those of the pinned time).
        internal void PinRefetch()
        {
            forceMaps = snapMaps = true;
            forceHeights = true;
        }

        internal int HeightBuildsDone { get { return Stats.HeightBuilds; } }

        // Whether the maps and the heights have settled: the maps primed, no height build or upload waiting, at least one height build finished after buildsBefore (the one
        // PinRefetch asked for), and every layer's shown maps equal their goal. A map round that runs meanwhile is not a reason: at the pinned time it fetches the same maps,
        // and the probe holds the settled state over two rounds (T09ProbePin). why names the first thing that has not settled.
        internal bool PinSettled(int buildsBefore, out string why)
        {
            why = null;
            if (renderer == null || stack == null || driver == null) { why = "the pass has no renderer, stack or reconstruction"; return false; }
            if (!mapsPrimed) { why = "the maps are not primed"; return false; }
            if (heightJob.Busy) { why = "a height build is under way"; return false; }
            if (renderer.Heights.Pending) { why = "height slices wait for their upload"; return false; }
            if (Stats.HeightBuilds <= buildsBefore) { why = "no height build has finished since the clock was pinned"; return false; }
            for (int l = 0; l < stack.LayerCount; l++)
            {
                int c = Differing(renderer.Maps.CoverageBytes(l), renderer.Maps.CoverageGoalBytes(l)), t = Differing(renderer.Maps.TypeBytes(l), renderer.Maps.TypeGoalBytes(l));
                if (c != 0 || t != 0)
                {
                    why = "layer " + stack.LayerAt(l).Name + "'s maps differ from their goal (" + c + " cover, " + t + " type texels)";
                    return false;
                }
            }
            return true;
        }

        // The T09 probe's wait for the maps (T09ProbeDrawn.cs, wolken-m1-fakten.md H.4): the maps primed, every layer of the body on its own layer map (none on the field's
        // shared cover, the fallback of a layer whose map the generator has not made yet), and every layer's shown maps equal to their goal. why names the first that is not.
        // Reads only; the probe asks for a snapping round through PokeMapFetch.
        internal bool ProbeMapsAtGoal(out string why)
        {
            why = null;
            if (renderer == null || stack == null || body == null) { why = "the pass has no renderer, stack or body"; return false; }
            if (!mapsPrimed) { why = "the maps are not primed"; return false; }
            for (int l = 0; l < stack.LayerCount; l++)
            {
                string name = stack.LayerAt(l).Name;
                bool shared;
                if (!sharedCoverState.TryGetValue(body.bodyName + "/" + name, out shared)) { why = "layer " + name + " has not been fetched"; return false; }
                if (shared) { why = "layer " + name + " is on the field's shared cover"; return false; }
                int c = Differing(renderer.Maps.CoverageBytes(l), renderer.Maps.CoverageGoalBytes(l)), t = Differing(renderer.Maps.TypeBytes(l), renderer.Maps.TypeGoalBytes(l));
                if (c != 0 || t != 0)
                {
                    why = "layer " + name + "'s maps differ from their goal (" + c + " cover, " + t + " type texels)";
                    return false;
                }
            }
            return true;
        }

        // Texels that differ; -1 where one side is missing or the sizes differ.
        private static int Differing(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return -1;
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
            return n;
        }

        // Holds the noise still at fixed values: every layer's drift and rise 0, the flow clock 0, the fibre axis at its wind's direction (the axis otherwise turns towards the wind
        // over real time, so two starts would hold it at different angles); the probe also sets cloud.motionScale 0, so Advance adds nothing.
        internal bool PinNoise()
        {
            if (renderer == null || stack == null) return false;
            for (int l = 0; l < stack.LayerCount; l++)
            {
                renderer.Noise.SetState(l, Vector3.zero, 0f);
                renderer.Noise.SnapAxis(l);
            }
            renderer.Noise.FlowTimeSeconds = 0.0;
            return true;
        }

        // The universal time the module's last tick read (the maps, the heights and the frame it filled took it).
        internal double FrameUt { get { return pendingUt; } }

        internal int HistoryFramesSinceCut { get { return driver != null ? driver.FramesSinceCut : -1; } }
        internal int HistoryCuts { get { return driver != null ? driver.CutCount : -1; } }

        // Starts the capture of this frame: at its end (after the camera rendered) the clouds' own output before the composite (driver.Player.Output, linear, premultiplied
        // radiance and alpha) and the scene depth the pass read (metres along the forward axis, 0 for sky) are read back as float32 .npy (rows bottom first, as Unity
        // holds them), and the maps and pass.json of the frame are written beside them. The host is the probe's (ReconHost), which exists while a series probe runs.
        internal PinCapture BeginPinCapture(MonoBehaviour host, string dir, string tag)
        {
            var job = new PinCapture { Dir = dir, Tag = tag };
            if (host == null)
            {
                job.Fail("no host for the end-of-frame read");
                job.Finished = true;
                return job;
            }
            host.StartCoroutine(PinRoutine(job));
            return job;
        }

        private IEnumerator PinRoutine(PinCapture job)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                Directory.CreateDirectory(job.Dir);
                RenderTexture cloud = driver != null && driver.Player != null ? driver.Player.Output : null;
                job.FramesSinceCut = driver != null ? driver.FramesSinceCut : -1;
                job.FrameNumber = frame.FrameNumber;
                if (cloud == null) job.Fail("the reconstruction has no output (the pass did not record as the reconstruction)");
                else PinRead(job, cloud, TextureFormat.RGBAFloat, 4, Path.Combine(job.Dir, "cloud.npy"), true);
                if (depthTarget == null) job.Fail("the pass has no scene depth target");
                else PinRead(job, depthTarget, TextureFormat.RFloat, 1, Path.Combine(job.Dir, "depth.npy"), false);
                job.DumpWritten = WriteDump(job.Dir, job.Tag);
                if (!job.DumpWritten) job.Fail("the maps and pass.json were not written");
            }
            catch (Exception e)
            {
                job.Fail("the capture threw " + e.GetType().Name + " " + e.Message);
            }
            job.Finished = true;
        }

        private static void PinRead(PinCapture job, RenderTexture rt, TextureFormat format, int channels, string path, bool cloud)
        {
            int w = rt.width, h = rt.height;
            if (cloud) { job.CloudWidth = w; job.CloudHeight = h; }
            else { job.DepthWidth = w; job.DepthHeight = h; }
            System.Threading.Interlocked.Increment(ref job.CloudPending);
            AsyncGPUReadback.Request(rt, 0, format, r =>
            {
                try
                {
                    if (r.hasError) job.Fail(Path.GetFileName(path) + ": the readback failed");
                    else
                    {
                        float[] data = r.GetData<float>().ToArray();
                        if (data.Length != w * h * channels) job.Fail(Path.GetFileName(path) + ": " + data.Length + " values, expected " + (w * h * channels));
                        else
                        {
                            PinFiles.WriteFloat32(path, h, w, channels, data);
                            if (cloud) job.CloudWritten = true;
                            else job.DepthWritten = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    job.Fail(Path.GetFileName(path) + ": " + e.GetType().Name + " " + e.Message);
                }
                System.Threading.Interlocked.Decrement(ref job.CloudPending);
            });
        }

        // The pin block of pass.json for the frame that stands (the module's own numbers).
        private string PinJsonNow()
        {
            HashSet<string> overridden;
            List<KeyValuePair<string, float>> tuning = Tuning.Snapshot(out overridden);
            ReconSettings rs = driver != null ? driver.Settings : recon;
            LayerPassSettings.Result settings = LayerPassSettings.Collect(frame, rs, renderer != null ? renderer.Noise.Switches : null);
            Camera cam = attachedCamera;
            var p = new PinState
            {
                FrameCount = Time.frameCount, FrameOrigin = FrameOrigin, FrameNumber = frame.FrameNumber, FramesSinceCut = driver != null ? driver.FramesSinceCut : -1,
                BlockIndex = driver != null && driver.Player != null ? driver.Player.BlockIndex : -1, CutCount = driver != null ? driver.CutCount : -1,
                PlanetCenterWorld = frame.PlanetCenter
            };
            if (cam != null)
            {
                p.CameraWorld = cam.transform.position;
                p.CameraRotation = cam.transform.rotation;
                p.CameraFov = cam.fieldOfView;
                p.CameraWidth = cam.pixelWidth;
                p.CameraHeight = cam.pixelHeight;
            }
            return PinJson(p, tuning, overridden, settings, LayerPassSettings.MarchJson(frame, rs), renderer != null ? renderer.TypeTableHash() : "none");
        }

        // The keys pass.json gains for the pinned capture (each starts with ",\n"): pin (the frame numbers, the history, the camera in world space as the pass had it, so the
        // replay puts its camera and the planet's centre at the same floats), tuning (every key the tuning table names, i.e. every key not at its compiled default),
        // tuningOverrides, march (the named summary), settings (every field of the frame, the reconstruction and the noise switches), settingsSkipped, typeTableHash.
        internal static string PinJson(PinState p, IList<KeyValuePair<string, float>> tuning, ICollection<string> overridden, LayerPassSettings.Result settings, string march, string tableHash)
        {
            var s = new StringBuilder(32768);
            s.Append(",\n \"pin\": {\"frameCount\": ").Append(p.FrameCount).Append(", \"frameOrigin\": ").Append(p.FrameOrigin).Append(", \"frameNumber\": ").Append(p.FrameNumber)
             .Append(", \"framesSinceCut\": ").Append(p.FramesSinceCut).Append(", \"firstFrameNumber\": ").Append(p.FramesSinceCut > 0 ? p.FrameNumber - p.FramesSinceCut + 1 : -1)
             .Append(", \"blockIndex\": ").Append(p.BlockIndex).Append(", \"historyCuts\": ").Append(p.CutCount)
             .Append(", \"world\": {\"camera\": ");
            Vec(s, p.CameraWorld.x, p.CameraWorld.y, p.CameraWorld.z);
            s.Append(", \"rotation\": [").Append(LayerPassReadout.Num(p.CameraRotation.x)).Append(", ").Append(LayerPassReadout.Num(p.CameraRotation.y)).Append(", ")
             .Append(LayerPassReadout.Num(p.CameraRotation.z)).Append(", ").Append(LayerPassReadout.Num(p.CameraRotation.w)).Append("], \"planetCenter\": ");
            Vec(s, p.PlanetCenterWorld.x, p.PlanetCenterWorld.y, p.PlanetCenterWorld.z);
            s.Append(", \"fov\": ").Append(LayerPassReadout.Num(p.CameraFov)).Append(", \"width\": ").Append(p.CameraWidth).Append(", \"height\": ").Append(p.CameraHeight).Append("}}");
            s.Append(",\n \"tuning\": {");
            for (int i = 0; i < tuning.Count; i++)
            {
                if (i > 0) s.Append(", ");
                s.Append(LayerPassReadout.Str(tuning[i].Key)).Append(": ").Append(LayerPassReadout.Num(tuning[i].Value));
            }
            s.Append("},\n \"tuningOverrides\": [");
            var names = new List<string>(overridden ?? new string[0]);
            names.Sort(string.CompareOrdinal);
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) s.Append(", ");
                s.Append(LayerPassReadout.Str(names[i]));
            }
            s.Append("],\n \"march\": ").Append(march ?? "{}");
            s.Append(",\n \"settings\": ").Append(LayerPassSettings.Json(settings.Settings));
            s.Append(",\n \"settingsSkipped\": [");
            for (int i = 0; i < settings.Skipped.Count; i++)
            {
                if (i > 0) s.Append(", ");
                s.Append(LayerPassReadout.Str(settings.Skipped[i]));
            }
            s.Append("],\n \"settingsNotDefault\": [");
            for (int i = 0; i < settings.NotDefault.Count; i++)
            {
                if (i > 0) s.Append(", ");
                s.Append(LayerPassReadout.Str(settings.NotDefault[i]));
            }
            s.Append("],\n \"typeTableHash\": ").Append(LayerPassReadout.Str(tableHash));
            return s.ToString();
        }

        private static void Vec(StringBuilder s, float x, float y, float z)
        {
            s.Append('[').Append(LayerPassReadout.Num(x)).Append(", ").Append(LayerPassReadout.Num(y)).Append(", ").Append(LayerPassReadout.Num(z)).Append(']');
        }
    }
}
