using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The sea ice in the orbit and map view: an overlay layer (IOrbitOverlayLayer, order 20: after the surface cover at 10, before the
    // cloud shell at 40) that shows a sphere in scaled space slightly above the planet with the ice picture on it. The picture is made
    // from the field's ice fraction and thickness by IceOrbitPicture on a worker thread; the shell (ReDefinition/AtmospheresIceShell)
    // draws it per pixel, so the ice is a pale region with a diffuse edge, nothing on land, and the opaque ice hides the planet's sun
    // glint under it. The planet's own colour map (which the surface cover rewrites) is never touched, so the two layers do not meet.
    //
    // Per body: the shell and a Texture2D of 1024 x 512 (2 MB; mips off) while the body is drawn; both are given back 20 s after the host
    // last asked for the body.
    internal sealed class IceOrbitLayer : IOrbitOverlayLayer
    {
        internal const int LayerOrder = 20;
        internal const string ShaderName = "ReDefinition/AtmospheresIceShell";
        // The shell stands this much above the planet's radius, as a share of it: above the terrain, below the cloud shell (1.0012).
        internal const float RadiusShare = 1.0005f;
        // Real seconds after the last request when a body's shell is hidden, and when its picture is freed.
        internal const float HideSeconds = 1.5f;
        internal const float FreeSeconds = 20f;
        // Real seconds between two tries to fit the map layout while the shell is not calibrated.
        internal const float CalibrationRetryS = 0.5f;

        private readonly Dictionary<int, BodyShell> shells = new Dictionary<int, BodyShell>();
        private readonly List<int> scratch = new List<int>();

        public string Name { get { return "ReDefinition Atmospheres sea ice"; } }

        public int Order { get { return LayerOrder; } }

        // The number of bodies with a picture now, and the pictures made in total, for the probe.
        internal int ActiveBodies { get { return shells.Count; } }

        internal int ComposedCount(CelestialBody body)
        {
            BodyShell s;
            return body != null && shells.TryGetValue(body.flightGlobalsIndex, out s) ? s.ComposedCount : 0;
        }

        internal bool ShellVisible(CelestialBody body)
        {
            BodyShell s;
            return body != null && shells.TryGetValue(body.flightGlobalsIndex, out s) && s.Visible;
        }

        internal BodyShell ShellOf(CelestialBody body)
        {
            BodyShell s;
            return body != null && shells.TryGetValue(body.flightGlobalsIndex, out s) ? s : null;
        }

        public bool Wants(CelestialBody body)
        {
            return body != null && body.scaledBody != null && IceTuning.OrbitWanted && AtmospheresSettings.OrbitOverlay && IceBodies.Of(body) != null;
        }

        public void Draw(CelestialBody body, bool focused, double ut)
        {
            IceBodyState state = IceBodies.Of(body);
            if (state == null) return;
            IceModule.Request(body);
            BodyShell s;
            if (!shells.TryGetValue(body.flightGlobalsIndex, out s)) shells[body.flightGlobalsIndex] = s = new BodyShell(body, state);
            s.Draw(ut);
        }

        // Hides the shells the host has not asked for lately and frees the pictures it has not asked for for long; called every frame.
        internal void Housekeeping()
        {
            float now = Time.realtimeSinceStartup;
            scratch.Clear();
            foreach (KeyValuePair<int, BodyShell> pair in shells)
            {
                float idle = now - pair.Value.LastDrawn;
                if (idle > HideSeconds || !IceTuning.OrbitWanted) pair.Value.Hide();
                if (idle > FreeSeconds || !IceTuning.OrbitWanted && idle > HideSeconds) scratch.Add(pair.Key);
            }
            foreach (int key in scratch)
            {
                shells[key].Destroy();
                shells.Remove(key);
            }
        }

        internal void ReleaseAll()
        {
            foreach (BodyShell s in shells.Values) s.Destroy();
            shells.Clear();
        }

        // The shell and the picture of one body.
        internal sealed class BodyShell
        {
            private readonly CelestialBody body;
            private readonly IceBodyState state;
            private GameObject shell;
            private Material material;
            private Texture2D picture;
            private bool calibrated;
            private int calibrationTries;
            private float calibrationTriedAt;
            private double calibrationWorstDeg;
            private double shiftDeg;
            private bool mirrored;
            private bool broken;

            private readonly IceInputs inputs = new IceInputs();
            private byte[] southFirst;
            private volatile bool workerBusy;
            private volatile bool resultReady;
            private volatile string workerError;
            private IceOrbitPicture.Stats stats;
            private long workerMilliseconds;
            private int composedStamp = int.MinValue;
            private double composedUt = double.NaN;
            private float composedAt = -1000f;
            private int workerStamp;
            private double workerUt;

            internal float LastDrawn { get; private set; }
            internal bool Visible { get; private set; }
            internal int ComposedCount { get; private set; }
            internal IceOrbitPicture.Stats LastStats { get { return stats; } }
            internal long LastWorkerMilliseconds { get { return workerMilliseconds; } }
            internal Texture2D Picture { get { return picture; } }
            internal double ShiftDeg { get { return shiftDeg; } }
            internal bool Mirrored { get { return mirrored; } }
            internal bool Calibrated { get { return calibrated; } }
            internal int CalibrationTries { get { return calibrationTries; } }
            internal double CalibrationWorstDeg { get { return calibrationWorstDeg; } }

            internal BodyShell(CelestialBody body, IceBodyState state)
            {
                this.body = body;
                this.state = state;
            }

            internal void Draw(double ut)
            {
                if (broken) return;
                LastDrawn = Time.realtimeSinceStartup;
                try
                {
                    if (workerError != null)
                    {
                        Fail("the picture worker failed (" + workerError + ")");
                        return;
                    }
                    if (shell == null && !Build()) return;
                    RetryCalibration();
                    if (resultReady) Upload();
                    Schedule(ut);
                    bool show = calibrated && picture != null && ComposedCount > 0;
                    if (show)
                    {
                        material.SetTexture("_Picture", picture);
                        material.SetFloat("_Shift", (float)(shiftDeg / 360.0));
                        material.SetFloat("_Mirror", mirrored ? 1f : 0f);
                        material.SetVector("_SunDir", SunDirection());
                        if (!shell.activeSelf) shell.SetActive(true);
                    }
                    else if (shell.activeSelf)
                    {
                        shell.SetActive(false);
                    }
                    Visible = show;
                }
                catch (Exception e)
                {
                    Fail(CompatibilityLog.Reason(e));
                }
            }

            private void Fail(string reason)
            {
                broken = true;
                AtmospheresLog.WarnOnce("sea ice of " + body.name + " in the orbit view failed and is switched off for this session (" + reason + ").");
                Destroy();
            }

            private bool Build()
            {
                string error;
                Shader shader = AtmosphereShaders.Get(ShaderName, out error);
                if (shader == null)
                {
                    AtmospheresLog.WarnOnce("sea ice: the shell shader " + ShaderName + " is not in the bundle (" + error + "); no ice in the orbit view.");
                    broken = true;
                    return false;
                }
                shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                shell.name = "ReDefinition Atmospheres sea ice " + body.name;
                UnityEngine.Object.Destroy(shell.GetComponent("Collider"));
                shell.layer = 10;
                Transform parent = body.scaledBody.transform;
                shell.transform.SetParent(parent, false);
                shell.transform.localPosition = Vector3.zero;
                float planetScaled = (float)(body.Radius * ScaledSpace.InverseScaleFactor);
                float parentScale = parent.lossyScale.x > 0f ? parent.lossyScale.x : 1f;
                shell.transform.localScale = Vector3.one * (2f * planetScaled * RadiusShare / parentScale);
                material = new Material(shader) { name = "ReDefinition Atmospheres sea ice" };
                var renderer = shell.GetComponent<Renderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                picture = new Texture2D(IceOrbitPicture.Width, IceOrbitPicture.Height, TextureFormat.RGBA32, false)
                {
                    name = "ReDefinition Atmospheres sea ice picture",
                    filterMode = FilterMode.Bilinear,
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Clamp
                };
                calibrated = Calibrate(out shiftDeg, out mirrored);
                calibrationTries = 1;
                calibrationTriedAt = Time.realtimeSinceStartup;
                AtmospheresLog.Say("sea ice: the orbit shell of " + body.name + " is built (picture " + IceOrbitPicture.Width + "x" + IceOrbitPicture.Height + ", map layout shift "
                                   + shiftDeg.ToString("0.#", CultureInfo.InvariantCulture) + " deg, " + (mirrored ? "mirrored" : "not mirrored")
                                   + (calibrated ? "" : ", NOT calibrated yet: worst error " + calibrationWorstDeg.ToString("0.#", CultureInfo.InvariantCulture)
                                                        + " deg, tried again every " + CalibrationRetryS.ToString("0.#", CultureInfo.InvariantCulture) + " s") + ").");
                shell.SetActive(false);
                return true;
            }

            // The shell can be built while the scene is still loading (K-W3b: built at the start of the flight scene's load, the scaled
            // body not yet placed, the three places missed by far and the shell stayed off for the whole session). The layout is tried
            // again until the three places fit; the shell shows only when they do.
            private void RetryCalibration()
            {
                if (calibrated || shell == null) return;
                float now = Time.realtimeSinceStartup;
                if (now - calibrationTriedAt < CalibrationRetryS) return;
                calibrationTriedAt = now;
                calibrationTries++;
                calibrated = Calibrate(out shiftDeg, out mirrored);
                if (calibrated)
                    AtmospheresLog.Say("sea ice: the orbit shell of " + body.name + " is calibrated after " + calibrationTries + " tries (map layout shift "
                                       + shiftDeg.ToString("0.#", CultureInfo.InvariantCulture) + " deg, " + (mirrored ? "mirrored" : "not mirrored") + ").");
            }

            // Where the field's map lies on the scaled body's sphere, from three places (EveMapLayout, the way the cloud shell does it).
            private bool Calibrate(out double shift, out bool mirror)
            {
                double worst;
                Transform scaled = body.scaledBody.transform;
                EveMapLayout.LayerOfPlace layerOf = delegate (double lat, double lon, out double u, out double v)
                {
                    Vector3d world = body.GetWorldSurfacePosition(lat, lon, 0.0);
                    Vector3 scaledPosition = ScaledSpace.LocalToScaledSpace(world);
                    Vector3 local = scaled.InverseTransformPoint(scaledPosition).normalized;
                    EveCoverageMath.Uv(local.x, local.y, local.z, out u, out v);
                };
                bool fits = EveMapLayout.Calibrate(layerOf, 1.0, out shift, out mirror, out worst);
                calibrationWorstDeg = worst;
                return fits;
            }

            private Vector3 SunDirection()
            {
                CelestialBody sun = GameGlobals.Sun;
                if (sun == null || sun.scaledBody == null) return Vector3.up;
                return ((Vector3)(sun.scaledBody.transform.position - body.scaledBody.transform.position)).normalized;
            }

            // Starts a worker for a new picture when the inputs changed, or enough game time has passed.
            private void Schedule(double ut)
            {
                if (workerBusy || resultReady || !state.MapsValid && !state.Params.Permanent && IceTuning.ForcedFraction < 0f) return;
                if (state.Shore == null) return;
                float now = Time.realtimeSinceStartup;
                int stamp = state.PictureStamp();
                bool first = ComposedCount == 0;
                bool inputsChanged = stamp != composedStamp;
                bool timePassed = double.IsNaN(composedUt) || Math.Abs(ut - composedUt) >= IceTuning.PictureGameStepSeconds;
                if (!first && now - composedAt < IceTuning.PictureStepSeconds) return;
                if (!first && !inputsChanged && !timePassed) return;
                state.FillInputs(inputs);
                workerBusy = true;
                workerStamp = stamp;
                workerUt = ut;
                if (southFirst == null) southFirst = new byte[IceOrbitPicture.Width * IceOrbitPicture.Height * 4];
                byte[] output = southFirst;
                IceInputs copy = inputs;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var north = new byte[output.Length];
                        IceOrbitPicture.Stats s = IceOrbitPicture.Fill(copy, north, IceOrbitPicture.Width, IceOrbitPicture.Height);
                        FlipRows(north, output, IceOrbitPicture.Width, IceOrbitPicture.Height);
                        stats = s;
                    }
                    catch (Exception e)
                    {
                        workerError = "picture: " + e.GetType().Name + " " + e.Message;
                    }
                    finally
                    {
                        workerMilliseconds = watch.ElapsedMilliseconds;
                        resultReady = true;
                    }
                });
            }

            // The texture's first row is the south: the picture is made with the north first, like the field's maps.
            internal static void FlipRows(byte[] north, byte[] south, int width, int height)
            {
                int stride = width * 4;
                for (int row = 0; row < height; row++) Buffer.BlockCopy(north, row * stride, south, (height - 1 - row) * stride, stride);
            }

            private void Upload()
            {
                resultReady = false;
                workerBusy = false;
                if (workerError != null || picture == null || southFirst == null) return;
                picture.LoadRawTextureData(southFirst);
                picture.Apply(false, false);
                composedStamp = workerStamp;
                composedUt = workerUt;
                composedAt = Time.realtimeSinceStartup;
                ComposedCount++;
                AtmospheresLog.Say("sea ice: the orbit picture of " + body.name + " made " + ComposedCount + " (sea texels " + stats.SeaTexels + ", ice texels " + stats.IceTexels
                                   + ", worker " + workerMilliseconds + " ms).");
            }

            internal void Hide()
            {
                if (shell != null && shell.activeSelf) shell.SetActive(false);
                Visible = false;
            }

            internal void Destroy()
            {
                if (shell != null) UnityEngine.Object.Destroy(shell);
                if (material != null) UnityEngine.Object.Destroy(material);
                if (picture != null) UnityEngine.Object.Destroy(picture);
                shell = null;
                material = null;
                picture = null;
                Visible = false;
                calibrated = false;
                calibrationTries = 0;
                ComposedCount = 0;
                composedStamp = int.MinValue;
                composedUt = double.NaN;
            }
        }
    }
}
