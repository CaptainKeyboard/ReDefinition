using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The sea ice near the ground: a clipmap sheet (IceClipmap) at the sea level of the body under the flight camera, drawn with the
    // material ReDefinition/AtmospheresIce. It is switched by the tuning key ice.groundOverlay (IceTuning) and by the setting seaIce
    // (OrbitAndGround or Physical); the orbit picture is the default until the series has measured the 5 km case of RECON P10 and P11.
    //
    // The sheet is on layer 15 (the local scenery, where Scatterer's ocean and the terrain are), at queue 2504, writes depth, and is raised
    // by a few centimetres plus the wave lift along the normal. Every frame (before the flight cameras cull) the uniforms are set from the
    // camera: the foot of the camera on the sea, the east and north vectors there, the body's centre, the frame of latitude and longitude,
    // the drift and the sun. The ice itself is worked out per pixel by the shader from the field's maps (two small textures). Nothing
    // here touches the water: open water and leads are discarded pixels.
    //
    // Not done in this package [VERIFY / requested]: the registration of the sheet in the ray-traced scene (the Atmospheres assembly does
    // not reference the main mod's RtScene), the Deferred G-buffer variant, the Scatterer wave damping, physical ice.
    internal sealed class IceGroundOverlay
    {
        internal const string ShaderName = "ReDefinition/AtmospheresIce";
        internal const int SheetLayer = 15;
        internal const int SheetQueue = 2504;

        private GameObject sheet;
        private Material material;
        private Mesh mesh;
        private float meshRadius = -1f, meshFar = -1f;
        private Texture2D iceMap;
        private Texture2D shoreMap;
        private int iceMapStamp = int.MinValue;
        private byte[] shoreMapSource;
        private bool broken;
        private bool subscribed;
        private int lastUniformFrame = -1;
        private IceBodyState state;

        // For the probe and the log.
        internal int Draws { get; private set; }
        internal bool Active { get; private set; }
        internal long Triangles { get; private set; }
        internal long Cells { get; private set; }
        internal int Builds { get; private set; }
        internal double LastBuildMs { get; private set; }
        internal double AltitudeM { get; private set; }
        internal Material Material { get { return material; } }
        internal GameObject Sheet { get { return sheet; } }
        internal CelestialBody Body { get { return state != null ? state.Body : null; } }
        internal string WhyOff { get; private set; }

        // Called every frame by the module: decides whether the sheet is drawn and keeps its textures and mesh.
        internal void Tick()
        {
            if (broken) return;
            try
            {
                string why;
                IceBodyState s = Wanted(out why);
                WhyOff = why;
                if (s == null)
                {
                    SetActive(false);
                    return;
                }
                if (!ReferenceEquals(s, state))
                {
                    state = s;
                    iceMapStamp = int.MinValue;
                    shoreMapSource = null;
                }
                if (!EnsureMaterial()) return;
                EnsureMesh(s);
                EnsureTextures(s);
                if (!subscribed)
                {
                    Camera.onPreCull += OnPreCull;
                    subscribed = true;
                }
                SetActive(mesh != null && iceMap != null && shoreMap != null);
            }
            catch (Exception e)
            {
                broken = true;
                AtmospheresLog.Failed("atmospheres-ice-ground", "sea ice: the ground overlay failed and is switched off for this session", e);
                Release();
            }
        }

        // The body whose sheet is drawn now: the flight camera's body, with a sea, a coast map and the field's maps, camera not too high.
        private IceBodyState Wanted(out string why)
        {
            why = null;
            if (!IceTuning.GroundOverlayWanted) { why = "switch off"; return null; }
            if (!HighLogic.LoadedSceneIsFlight) { why = "not in flight"; return null; }
            Vessel vessel = GameGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody == null) { why = "no vessel"; return null; }
            try
            {
                if (MapView.MapIsEnabled) { why = "map view"; return null; }
            }
            catch (Exception)
            {
                // The map view is not there (a scene without it): the flight view counts.
            }
            IceBodyState s = IceBodies.Of(vessel.mainBody);
            if (s == null) { why = "no sea"; return null; }
            IceModule.Request(vessel.mainBody);
            if (s.Shore == null) { why = "coast map not ready"; return null; }
            if (!s.MapsValid && !s.Params.Permanent && IceTuning.ForcedFraction < 0f) { why = "no field maps"; return null; }
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) { why = "no flight camera"; return null; }
            double alt = (cam.transform.position - (Vector3)s.Body.position).magnitude - s.SeaRadiusM;
            AltitudeM = alt;
            if (alt < 0.2) { why = "camera under the sea"; return null; }
            if (alt > Tuning.Get(IceTuning.MaxAltitudeKey, IceTuning.MaxAltitudeDefault) * 1000.0) { why = "camera too high"; return null; }
            return s;
        }

        private bool EnsureMaterial()
        {
            if (material != null) return true;
            string error;
            Shader shader = AtmosphereShaders.Get(ShaderName, out error);
            if (shader == null)
            {
                broken = true;
                AtmospheresLog.WarnOnce("sea ice: the overlay shader " + ShaderName + " is not in the bundle (" + error + "); no ice near the ground.");
                return false;
            }
            material = new Material(shader) { name = "ReDefinition Atmospheres sea ice sheet", renderQueue = SheetQueue };
            return true;
        }

        private void EnsureMesh(IceBodyState s)
        {
            float far = Math.Max(1f, Tuning.Get(IceTuning.FarKey, IceTuning.FarDefault)) * 1000f;
            float radius = (float)s.Body.Radius;
            if (mesh != null && Math.Abs(meshRadius - radius) < 1f && Math.Abs(meshFar - far) < 1f) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            List<IceClipmap.Ring> rings = IceClipmap.Rings(radius, far);
            float[] xz;
            int[] indices;
            IceClipmap.Build(rings, out xz, out indices);
            var vertices = new Vector3[xz.Length / 2];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(xz[2 * i], 0f, xz[2 * i + 1]);
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            mesh = new Mesh { name = "ReDefinition Atmospheres sea ice clipmap", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            // The vertex shader moves the vertices; the bounds only have to contain the camera.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
            meshRadius = radius;
            meshFar = far;
            Triangles = indices.Length / 3;
            Cells = IceClipmap.CellCount(rings);
            Builds++;
            LastBuildMs = watch.Elapsed.TotalMilliseconds;
            AtmospheresLog.Say("sea ice: the overlay clipmap of " + s.Body.name + " built, " + rings.Count + " rings, " + Cells + " cells, " + Triangles + " triangles, largest cell "
                               + IceClipmap.LargestCell(radius).ToString("0", CultureInfo.InvariantCulture) + " m, reach " + (far / 1000f).ToString("0.#", CultureInfo.InvariantCulture)
                               + " km, " + LastBuildMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms.");
            if (sheet != null) sheet.GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        private void EnsureSheet()
        {
            if (sheet != null) return;
            sheet = new GameObject("ReDefinition Atmospheres sea ice sheet") { layer = SheetLayer };
            sheet.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = sheet.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            sheet.SetActive(false);
        }

        private void EnsureTextures(IceBodyState s)
        {
            if (iceMap == null)
                iceMap = new Texture2D(256, 128, TextureFormat.RGBA32, false, true)
                {
                    name = "ReDefinition Atmospheres sea ice field",
                    filterMode = FilterMode.Bilinear,
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Clamp
                };
            int stamp = s.PictureStamp();
            if (stamp != iceMapStamp)
            {
                iceMapStamp = stamp;
                FillIceMap(s);
            }
            if (shoreMap == null)
                shoreMap = new Texture2D(ShoreGrid.Width, ShoreGrid.Height, TextureFormat.RGBA32, false, true)
                {
                    name = "ReDefinition Atmospheres sea ice coast",
                    filterMode = FilterMode.Bilinear,
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Clamp
                };
            if (!ReferenceEquals(shoreMapSource, s.Shore))
            {
                shoreMapSource = s.Shore;
                shoreMap.LoadRawTextureData(s.Shore);
                shoreMap.Apply(false, false);
            }
            EnsureSheet();
        }

        // The field's maps into one small texture: R the fraction, G the square root of the thickness over 3 m, B the square root of the
        // snow depth over 0.1 m. The texture's first row is the south; the field's maps start at the north.
        private void FillIceMap(IceBodyState s)
        {
            var inputs = new IceMapCopy();
            var thick = new IceMapCopy();
            var snow = new IceMapCopy();
            if (s.MapsValid)
            {
                inputs.CopyFrom(s.FractionMap);
                thick.CopyFrom(s.ThicknessMap);
                if (s.SnowMap.Width > 0) snow.CopyFrom(s.SnowMap);
            }
            FillIceMapTexture(iceMap, inputs, thick, snow);
        }

        // Writes the three maps into the small texture of the shader (the series probe makes one per season phase).
        internal static void FillIceMapTexture(Texture2D tex, IceMapCopy inputs, IceMapCopy thick, IceMapCopy snow)
        {
            int w = tex.width, h = tex.height;
            var bytes = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
            {
                double lat = -90.0 + (row + 0.5) / h * 180.0;
                for (int col = 0; col < w; col++)
                {
                    double lon = (col + 0.5) / w * 360.0 - 180.0;
                    int i = (row * w + col) * 4;
                    bytes[i] = Quant(inputs.Sample(lat, lon));
                    bytes[i + 1] = Quant((float)Math.Sqrt(Math.Max(0f, thick.Sample(lat, lon)) / 3f));
                    bytes[i + 2] = Quant((float)Math.Sqrt(Math.Max(0f, snow.Sample(lat, lon)) / 0.1f));
                    bytes[i + 3] = 255;
                }
            }
            tex.LoadRawTextureData(bytes);
            tex.Apply(false, false);
        }

        private static byte Quant(float v)
        {
            v = float.IsNaN(v) ? 0f : v < 0f ? 0f : v > 1f ? 1f : v;
            return (byte)(v * 255f + 0.5f);
        }

        private void SetActive(bool on)
        {
            Active = on;
            if (sheet != null && sheet.activeSelf != on) sheet.SetActive(on);
        }

        // Before a camera culls: the uniforms from the camera, once per frame.
        private void OnPreCull(Camera cam)
        {
            if (!Active || broken || state == null || material == null || cam == null) return;
            if ((cam.cullingMask & (1 << SheetLayer)) == 0 || cam.cameraType != CameraType.Game) return;
            if (lastUniformFrame == Time.frameCount) return;
            try
            {
                lastUniformFrame = Time.frameCount;
                Apply(cam);
                Draws++;
            }
            catch (Exception e)
            {
                broken = true;
                AtmospheresLog.Failed("atmospheres-ice-ground", "sea ice: the ground overlay's uniforms failed; switched off for this session", e);
            }
        }

        private void Apply(Camera cam)
        {
            IceBodyState s = state;
            CelestialBody body = s.Body;
            Vector3d camPos = cam.transform.position;
            Vector3d centre = body.position;
            Vector3d fromCentre = camPos - centre;
            double dist = fromCentre.magnitude;
            Vector3d up = dist > 0.0 ? fromCentre / dist : Vector3d.up;
            double seaR = s.SeaRadiusM;
            Vector3d pole = (body.GetWorldSurfacePosition(90.0, 0.0, 0.0) - centre).normalized;
            Vector3d east = Vector3d.Cross(pole, up);
            if (east.magnitude < 1e-4) east = Vector3d.Cross((Vector3d)cam.transform.forward, up);
            east = east.normalized;
            Vector3d north = Vector3d.Cross(up, east).normalized;
            Matrix4x4 worldToSphere = Matrix4x4.Rotate(Quaternion.Inverse(body.bodyTransform != null ? body.bodyTransform.rotation : body.transform.rotation));
            float far = Math.Max(1f, Tuning.Get(IceTuning.FarKey, IceTuning.FarDefault)) * 1000f;
            CelestialBody sun = GameGlobals.Sun;
            Vector3 sunDir = sun != null ? (Vector3)(sun.position - camPos).normalized : Vector3.up;

            Material m = material;
            SetCommon(m, s, iceMap, shoreMap);
            m.SetMatrix("_WorldToSphere", worldToSphere);
            m.SetVector("_CenterRel", (Vector3)(centre - camPos));
            m.SetVector("_NadirRel", (Vector3)(up * (seaR - dist)));
            m.SetVector("_East", (Vector3)east);
            m.SetVector("_North", (Vector3)north);
            m.SetVector("_Sea", new Vector4((float)seaR, Tuning.Get(IceTuning.LiftKey, IceTuning.LiftDefault), Tuning.Get(IceTuning.WaveLiftKey, IceTuning.WaveLiftDefault), 0f));
            m.SetFloat("_Aid", Tuning.Get(IceTuning.AidKey, 0f) > 0.5f ? 1f : 0f);
            m.SetVector("_Fade", new Vector4(far * IceTuning.FadeStartShare, far, AtmospheresSettings.IceDrawDistanceKm * 1000f, s.Params.RadiusM * IceTuning.DitherCellsPerMetre));
            m.SetVector("_SunDir", sunDir);
            m.SetColor("_SunColour", new Color(1f, 0.98f, 0.94f, 1f));
            Color ambient = RenderSettings.ambientLight;
            m.SetColor("_AmbientColour", new Color(Mathf.Max(0.08f, ambient.r), Mathf.Max(0.08f, ambient.g), Mathf.Max(0.08f, ambient.b), 1f));
        }

        // The uniforms that do not depend on the camera: the two textures, the frame of latitude and longitude, the pattern, the drift, the
        // forced values and the look. The series probe sets them on a material it blits with (passes 1 to 3 need no camera).
        internal static void SetCommon(Material m, IceBodyState s, Texture iceMap, Texture shoreMap)
        {
            double ax, ay, az, bx, by, bz, px, py, pz;
            s.Frame.Direction(0.0, 0.0, out ax, out ay, out az);
            s.Frame.Direction(0.0, 90.0, out bx, out by, out bz);
            s.Frame.Direction(90.0, 0.0, out px, out py, out pz);
            IceParams p = s.Params;
            float lead = Tuning.Get(IceTuning.LeadOverrideKey, -1f);
            m.SetTexture("_IceMap", iceMap);
            m.SetTexture("_ShoreMap", shoreMap);
            m.SetVector("_FrameA", new Vector4((float)ax, (float)ay, (float)az, 0f));
            m.SetVector("_FrameB", new Vector4((float)bx, (float)by, (float)bz, 0f));
            m.SetVector("_FrameP", new Vector4((float)px, (float)py, (float)pz, 0f));
            m.SetFloat("_Freq", p.Frequency);
            m.SetVector("_Drift", new Vector4((float)s.Drift.X, (float)s.Drift.Y, (float)s.Drift.Z, 0f));
            m.SetFloat("_SeedLo", p.Seed & 0xFFFFu);
            m.SetFloat("_SeedHi", p.Seed >> 16);
            m.SetFloat("_LeadFraction", lead >= 0f ? lead : p.LeadFraction);
            m.SetFloat("_ShoreGain", p.ShoreGain);
            m.SetFloat("_EdgeNoise", p.EdgeNoise);
            m.SetFloat("_Permanent", p.Permanent ? 1f : 0f);
            m.SetFloat("_PermanentThickness", p.PermanentThicknessM);
            m.SetFloat("_Forced", IceTuning.ForcedFraction);
            m.SetColor("_IceColour", new Color(s.Climate.Ice.Colour.r, s.Climate.Ice.Colour.g, s.Climate.Ice.Colour.b, 1f));
            m.SetFloat("_Roughness", s.Climate.Ice.Roughness);
        }

        // Frees everything the overlay made.
        internal void Release()
        {
            if (subscribed)
            {
                Camera.onPreCull -= OnPreCull;
                subscribed = false;
            }
            if (sheet != null) UnityEngine.Object.Destroy(sheet);
            if (material != null) UnityEngine.Object.Destroy(material);
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (iceMap != null) UnityEngine.Object.Destroy(iceMap);
            if (shoreMap != null) UnityEngine.Object.Destroy(shoreMap);
            sheet = null;
            material = null;
            mesh = null;
            iceMap = null;
            shoreMap = null;
            state = null;
            shoreMapSource = null;
            meshRadius = -1f;
            meshFar = -1f;
            iceMapStamp = int.MinValue;
            Active = false;
        }

        // Forgets that the overlay failed (another game was loaded).
        internal void ResetFailure()
        {
            broken = false;
        }
    }
}
