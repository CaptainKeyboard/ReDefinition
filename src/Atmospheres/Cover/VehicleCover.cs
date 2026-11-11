using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Cover on vehicles (brief item 4, option c of the plan): a second renderer per part mesh, the shell, with the triangles that can
    // face up, its own shader (ReDefinition/AtmospheresShell) and an amount per vessel. The amount is VehicleCoverModel's, stepped about
    // once a second; its state between sessions is in PluginData/<save>/vehicle-cover.txt keyed by the vessel's persistent id (no
    // ScenarioModule, nothing in the save game). Only loaded vessels have shells; the shells are built lazily, a few parts a frame, and
    // the meshes are shared per source mesh and orientation. vehicleCover Off removes every shell.
    //
    // Stack: the shell is a child of the part's renderer, opaque (queue 2001), motionVectorGenerationMode = Object, no shadow caster,
    // receives shadows, depth offset -1 in the shader. The vessel motion-vector rig draws every opaque mesh renderer under a part, so a
    // shell is repaired like the part. The ray-traced scene leaves the shells out (RtFilter.LeftOut by the shader's name): vehicles are
    // bare in reflections. Kerbals on EVA carry none. Only meshes with a stock-like shader get a shell (VehicleCoverGate; RECON P8: a
    // foreign opaque shader does not look like the stock material under the shell's own lighting); the method ScreenSpace
    // (vehicleCoverMethod) paints the up-facing pixels of the vessel instead and covers every shader. [VERIFY in the player and the
    // game: the rows of series K-W3b and player-D34]
    [AtmospheresModule(67)]
    internal sealed class VehicleCover : IAtmospheresModule
    {
        internal const string ShaderName = "ReDefinition/AtmospheresShell";
        internal const string ShellName = "ReDefinitionAtmospheresShell";
        internal const string FileName = "vehicle-cover.txt";

        // Tuning keys (A01): vehicle.shellOffsetM 0.02 (0.01..0.03), vehicle.partsPerFrame 3, vehicle.maskLow 0.35, vehicle.maskHigh 0.75,
        // vehicle.minUpDot -0.25 (the lowest face-up dot product a shell keeps), vehicle.riseSeconds 1800, vehicle.meltSeconds 3600,
        // vehicle.shedSpeedMs 20, vehicle.highAltitudeM 1500, vehicle.shellMaxDistanceM 250 (50..1000: shells are drawn within this
        // distance of the flight camera plus the vessel's size), vehicle.screenGrain 40 (the speck scale of the ScreenSpace method).
        private const float StepSeconds = 1f;
        private const float SaveSeconds = 30f;

        private sealed class ShellRecord
        {
            internal Renderer Source;
            internal GameObject Shell;
            internal MeshRenderer Renderer;
            internal Vector3 BuiltUp;
        }

        private sealed class VesselRecord
        {
            internal Vessel Vessel;
            internal uint Id;
            internal float Amount;
            internal float Target;
            internal float FallPerSecond;
            internal double LastUt = double.NaN;
            internal float LastStep = -1000f;
            internal float AppliedAmount = -1f;
            internal readonly List<ShellRecord> Shells = new List<ShellRecord>();
            internal readonly List<Part> Pending = new List<Part>();
            internal HashSet<Part> Built = new HashSet<Part>();
            internal bool Wanted;
            internal bool Seeded;
            // Whether the flight camera is near enough for the shells to be drawn, and the value the shells were set for.
            internal bool Near = true;
            internal bool AppliedNear = true;
            // The screen-space method: the vessel's bounds as a region (its middle in the vessel's frame), found about once a second.
            internal CoverScreenMath.Region Region;
            internal Vector3 LocalCentre;
            internal bool HasRegion;
        }

        private readonly Dictionary<uint, VesselRecord> records = new Dictionary<uint, VesselRecord>();
        private readonly Dictionary<long, Mesh> meshes = new Dictionary<long, Mesh>();
        private readonly Dictionary<uint, VehicleCoverStored> stored = new Dictionary<uint, VehicleCoverStored>();
        private readonly List<uint> scratchIds = new List<uint>();
        private readonly List<MeshRenderer> renderersScratch = new List<MeshRenderer>();
        private readonly VehicleCoverModel.Rates rates = new VehicleCoverModel.Rates();
        private Material material;
        private bool materialTried;
        private MaterialPropertyBlock block;
        private string loadedSave;
        private float lastSave = -1000f;
        private bool dirty;

        // For the probe: the shells alive, the vessels tracked, the parts built in all and the milliseconds the last build frame took.
        internal static VehicleCover Current { get; private set; }
        internal int ShellCount { get { int n = 0; foreach (VesselRecord r in records.Values) n += r.Shells.Count; return n; } }
        internal int VesselCount { get { return records.Count; } }
        internal int PartsBuilt { get; private set; }
        internal double LastBuildMs { get; private set; }
        internal int Unreadable { get; private set; }
        // Meshes left bare in the Shell method because their shader is not a stock-like one (VehicleCoverGate), and the vessels the
        // screen-space pass painted in the last frame.
        internal int ForeignBare { get; private set; }
        internal int ScreenDrawn { get; private set; }
        internal bool ScreenAttached { get { return screenBuffer != null; } }

        private CoverScreenPass screen;
        private bool screenTried;
        private CommandBuffer screenBuffer;
        private Camera screenCamera;
        private readonly CoverScreenMath.Region[] regionScratch = new CoverScreenMath.Region[32];
        private readonly CoverScreenMath.Region[] regionsSelected = new CoverScreenMath.Region[CoverScreenMath.MaxVessels];
        private readonly HashSet<string> foreignLogged = new HashSet<string>();

        public void Init()
        {
            Current = this;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        // The amount a vessel shows (0 where it is not tracked), for the probe and the dashboard.
        internal float AmountOf(Vessel vessel)
        {
            VesselRecord r;
            return vessel != null && records.TryGetValue(vessel.persistentId, out r) ? r.Amount : 0f;
        }

        // Puts an amount on a vessel at once (a series sets the situation it measures).
        internal void Force(Vessel vessel, float amount)
        {
            VesselRecord r;
            if (vessel != null && records.TryGetValue(vessel.persistentId, out r))
            {
                r.Amount = amount;
                r.Seeded = true;
                r.AppliedAmount = -1f;
            }
        }

        public void Tick(float dt)
        {
            VehicleCoverMode mode = AtmospheresSettings.VehicleCover;
            if (!CoverModule.Enabled || mode == VehicleCoverMode.Off)
            {
                if (records.Count > 0) RemoveAll();
                DetachScreen();
                return;
            }
            List<Vessel> loaded = GameGlobals.VesselsLoaded;
            double ut;
            if (loaded == null || !CoverClock.TryUt(out ut)) return;
            EnsureSaveLoaded();
            float now = Time.realtimeSinceStartup;
            Prune(loaded, ut);
            for (int i = 0; i < loaded.Count; i++)
            {
                Vessel v = loaded[i];
                if (v == null || v.isEVA || !v.loaded || v.parts == null) continue;
                VesselRecord r;
                if (!records.TryGetValue(v.persistentId, out r))
                {
                    r = new VesselRecord { Vessel = v, Id = v.persistentId };
                    records[v.persistentId] = r;
                }
                r.Vessel = v;
                if (now - r.LastStep >= StepSeconds)
                {
                    r.LastStep = now;
                    StepVessel(r, ut, mode);
                }
            }
            if (AtmospheresSettings.VehicleCoverMethod == VehicleCoverMethod.ScreenSpace) DrawScreen();
            else
            {
                DetachScreen();
                Build(now);
            }
            if (dirty && now - lastSave >= SaveSeconds) Save(now);
        }

        public void Shutdown()
        {
            Flush();
            RemoveAll();
            DetachScreen();
            if (screen != null)
            {
                screen.Dispose();
                screen = null;
            }
            foreach (Mesh m in meshes.Values) if (m != null) UnityEngine.Object.Destroy(m);
            meshes.Clear();
            if (Current == this) Current = null;
        }

        // ---- the state of a vessel --------------------------------------------------------------------------------------------

        private void StepVessel(VesselRecord r, double ut, VehicleCoverMode mode)
        {
            Vessel v = r.Vessel;
            CoverBodyState state = CoverBodies.Of(v.mainBody);
            if (!r.Seeded)
            {
                r.Seeded = true;
                VehicleCoverStored s;
                r.Amount = stored.TryGetValue(r.Id, out s) ? (float)VehicleCoverModel.Restore(s, ut, rates) : 0f;
            }
            double dt = double.IsNaN(r.LastUt) ? 0.0 : ut - r.LastUt;
            if (dt < 0)
            {
                // The game time went back (a revert inside the session): what the vessel carried is not known any more.
                r.Amount = 0f;
                dt = 0;
            }
            r.LastUt = ut;
            rates.RiseSeconds = Tuning.Get("vehicle.riseSeconds", 1800f);
            rates.MeltSeconds = Tuning.Get("vehicle.meltSeconds", 3600f);
            rates.ShedSpeedMs = Tuning.Get("vehicle.shedSpeedMs", 20f);
            rates.HighAltitudeM = Tuning.Get("vehicle.highAltitudeM", 1500f);

            VehicleCoverInputs inputs = Inputs(v, state, ut);
            r.Wanted = state != null && VehicleCoverModel.Wanted((VehicleCoverModeLite)(int)mode, inputs.Landed, inputs.AltitudeM);
            // Near cameras only (brief item 4, U4): a shell two centimetres over a part is a z-fight at a distance, so it is drawn within
            // vehicle.shellMaxDistanceM of the flight camera (plus the vessel's own size).
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            r.Near = cam == null || (cam.transform.position - v.transform.position).magnitude <= Tuning.Get("vehicle.shellMaxDistanceM", 250f) + v.vesselSize.magnitude;
            float target, fall;
            double amount = VehicleCoverModel.Step(r.Amount, inputs, Math.Min(dt, 600.0), rates, out target, out fall);
            r.Amount = (float)amount;
            r.Target = target;
            r.FallPerSecond = fall;
            dirty = true;
            if (!r.Wanted || state == null)
            {
                if (r.Shells.Count > 0) DestroyShells(r);
                return;
            }
            if (AtmospheresSettings.VehicleCoverMethod == VehicleCoverMethod.ScreenSpace)
            {
                // No shells in this method: the vessel's bounds are the region the screen pass paints in.
                if (r.Shells.Count > 0) DestroyShells(r);
                FindRegion(r);
                return;
            }
            r.HasRegion = false;
            // Shells for parts that have none yet are built a few a frame (Build).
            if (r.Pending.Count == 0 && r.Built.Count < v.parts.Count)
                foreach (Part p in v.parts)
                    if (p != null && !r.Built.Contains(p)) r.Pending.Add(p);
            ApplyAmount(r, state);
        }

        private static VehicleCoverInputs Inputs(Vessel v, CoverBodyState state, double ut)
        {
            var i = new VehicleCoverInputs();
            i.Landed = v.Landed;
            i.SpeedMs = v.srfSpeed;
            double agl = v.heightFromTerrain;
            i.AltitudeM = agl >= 0 ? agl : v.altitude;
            i.Sunlit = v.directSunlight;
            double heating = 0;
            if (v.parts != null)
            {
                foreach (Part p in v.parts)
                {
                    if (p == null || p.skinMaxTemp <= 0) continue;
                    double ratio = p.skinTemperature / p.skinMaxTemp;
                    if (ratio > heating) heating = ratio;
                }
            }
            // Heating counts from 40 % of the skin limit and is full at 60 %: a vessel on the pad or in a parking orbit is far below.
            i.Heating01 = Math.Max(0.0, Math.Min(1.0, (heating - 0.4) / 0.2)) * (v.atmDensity > 1e-4 ? 1.0 : 0.0);
            i.TemperatureK = double.NaN;
            if (state != null)
            {
                CoverMap.Frame frame = state.Map.Current;
                double lat = v.latitude, lon = v.longitude;
                double fieldDepthM = frame.Number == 0 ? 0.0 : frame.Sample01(lat, lon) * state.Def.MaxDepthM;
                double mask = CoverDepth.Mask(state.Def, lat, Math.Max(0.0, v.altitude), 0.0, v.Splashed);
                i.Ground01 = CoverDepth.Depth01(state.Def, fieldDepthM, mask, 1.0, 0.0, AtmospheresSettings.CoverStrength);
                WeatherSample s;
                if (WeatherHub.Sample(state.Index, lat, lon, Math.Max(0.0, v.altitude), ut, out s))
                {
                    i.TemperatureK = s.TemperatureK;
                    i.Fall01 = FallOf(state.Def, s);
                }
            }
            return i;
        }

        // The field's precipitation level when it is the layer's type (snow for snow; dust, sand and ash by their own type).
        internal static double FallOf(CoverDef def, WeatherSample s)
        {
            switch (def.Kind)
            {
                case CoverKind.Snow: return s.Level01 * s.SnowFraction;
                case CoverKind.Sand: return s.Type == PrecipType.Sand ? s.Level01 : 0.0;
                case CoverKind.Dust: return s.Type == PrecipType.Dust ? s.Level01 : 0.0;
                case CoverKind.Ash: return s.Type == PrecipType.Ash ? s.Level01 : 0.0;
                default: return 0.0;
            }
        }

        // ---- the screen-space method ------------------------------------------------------------------------------------------

        // The bounds of a vessel's renderers as a region (about once a second).
        private void FindRegion(VesselRecord r)
        {
            Vessel v = r.Vessel;
            bool any = false;
            Bounds bounds = default(Bounds);
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                p.GetComponentsInChildren(false, renderersScratch);
                for (int i = 0; i < renderersScratch.Count; i++)
                {
                    MeshRenderer m = renderersScratch[i];
                    if (m == null || !m.enabled || m.gameObject.name == ShellName) continue;
                    if (!any) bounds = m.bounds;
                    else bounds.Encapsulate(m.bounds);
                    any = true;
                }
                renderersScratch.Clear();
            }
            if (!any)
            {
                r.HasRegion = false;
                return;
            }
            Vector3 up = UpOf(v);
            Vector3 e = bounds.extents;
            r.Region = CoverScreenMath.RegionOf(bounds.center.x, bounds.center.y, bounds.center.z, e.x, e.y, e.z, up.x, up.y, up.z, r.Amount);
            r.LocalCentre = v.transform.InverseTransformPoint(bounds.center);
            r.HasRegion = true;
        }

        private bool MakeScreenPass()
        {
            if (screen != null) return true;
            if (screenTried) return false;
            screenTried = true;
            string error;
            Shader shader = AtmosphereShaders.Get(CoverScreenShaderName, out error);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("vehicle cover: the screen-space shader is not available (" + error + "); vehicles stay bare.");
                return false;
            }
            screen = new CoverScreenPass(shader);
            return true;
        }

        internal const string CoverScreenShaderName = "ReDefinition/AtmospheresShellScreen";
        internal const UnityEngine.Rendering.CameraEvent ScreenEvent = UnityEngine.Rendering.CameraEvent.BeforeForwardAlpha;

        // Records the screen pass for the flight camera: the regions of the vessels near it, the light and the cover colour.
        private void DrawScreen()
        {
            Camera cam = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vessel active = GameGlobals.ActiveVessel;
            CelestialBody body = active != null ? active.mainBody : null;
            CoverBodyState state = body != null ? CoverBodies.Of(body) : null;
            if (cam == null || state == null || !MakeScreenPass())
            {
                DetachScreen();
                return;
            }
            int n = 0;
            foreach (VesselRecord r in records.Values)
            {
                if (!r.Wanted || !r.HasRegion || r.Vessel == null || n >= regionScratch.Length) continue;
                Vector3 c = r.Vessel.transform.TransformPoint(r.LocalCentre);
                CoverScreenMath.Region g = r.Region;
                g.X = c.x;
                g.Y = c.y;
                g.Z = c.z;
                g.Amount = r.Amount;
                regionScratch[n++] = g;
            }
            Vector3 camPos = cam.transform.position;
            int count = CoverScreenMath.Select(regionScratch, n, camPos.x, camPos.y, camPos.z, (float)rates.Visible, regionsSelected);
            ScreenDrawn = count;
            if (count == 0)
            {
                DetachScreen();
                return;
            }
            if (screenBuffer == null || screenCamera != cam)
            {
                DetachScreen();
                screenBuffer = new CommandBuffer { name = "ReDefinition Atmospheres vehicle cover" };
                screenCamera = cam;
                cam.AddCommandBuffer(ScreenEvent, screenBuffer);
            }
            cam.depthTextureMode |= DepthTextureMode.Depth;
            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            Vector3 up = ((Vector3)(cam.transform.position - body.transform.position)).normalized;
            var draw = new CoverScreenDraw
            {
                Regions = regionsSelected,
                Count = count,
                Up = up,
                Colour = new Color(state.Def.Red, state.Def.Green, state.Def.Blue, 1f),
                TowardsSun = sun != null ? -sun.transform.forward : up,
                SunColour = sun != null ? sun.color * Mathf.Min(sun.intensity, 1.5f) : Color.white,
                Ambient = RenderSettings.ambientLight,
                EdgeLow = Tuning.Get("vehicle.maskLow", 0.35f),
                EdgeHigh = Tuning.Get("vehicle.maskHigh", 0.75f),
                Grain = Tuning.Get("vehicle.screenGrain", 40f)
            };
            screenBuffer.Clear();
            screen.Record(screenBuffer, ref draw);
        }

        private void DetachScreen()
        {
            if (screenBuffer == null) return;
            if (screenCamera != null) screenCamera.RemoveCommandBuffer(ScreenEvent, screenBuffer);
            screenBuffer.Release();
            screenBuffer = null;
            screenCamera = null;
        }

        // ---- the shells -------------------------------------------------------------------------------------------------------

        private Material Material()
        {
            if (material != null || materialTried) return material;
            materialTried = true;
            string error;
            Shader shader = AtmosphereShaders.Get(ShaderName, out error);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("vehicle cover: the shell shader is not available (" + error + "); vehicles stay bare.");
                return null;
            }
            material = new Material(shader) { name = "ReDefinition Atmospheres shell", hideFlags = HideFlags.HideAndDontSave };
            return material;
        }

        // Builds the shells of a few parts a frame.
        private void Build(float now)
        {
            Material m = Material();
            if (m == null) return;
            int budget = Mathf.Max(1, (int)Tuning.Get("vehicle.partsPerFrame", 3f));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (VesselRecord r in records.Values)
            {
                if (!r.Wanted) continue;
                CoverBodyState state = CoverBodies.Of(r.Vessel != null ? r.Vessel.mainBody : null);
                if (state == null) continue;
                Vector3 up = UpOf(r.Vessel);
                while (r.Pending.Count > 0 && budget > 0)
                {
                    Part part = r.Pending[r.Pending.Count - 1];
                    r.Pending.RemoveAt(r.Pending.Count - 1);
                    if (part == null || r.Built.Contains(part)) continue;
                    r.Built.Add(part);
                    BuildPart(r, part, up, m, state);
                    PartsBuilt++;
                    budget--;
                }
                RebuildTurned(r, up, m, state, ref budget);
                if (budget <= 0) break;
            }
            LastBuildMs = watch.Elapsed.TotalMilliseconds;
        }

        private static Vector3 UpOf(Vessel v)
        {
            if (v == null || v.mainBody == null) return Vector3.up;
            Vector3 radial = (Vector3)(v.transform.position - v.mainBody.transform.position);
            return radial.sqrMagnitude > 1e-6f ? radial.normalized : Vector3.up;
        }

        private void BuildPart(VesselRecord r, Part part, Vector3 up, Material m, CoverBodyState state)
        {
            part.GetComponentsInChildren(false, renderersScratch);
            for (int i = 0; i < renderersScratch.Count; i++)
            {
                MeshRenderer source = renderersScratch[i];
                if (source == null || source.gameObject.name == ShellName || source.transform.parent != null && source.transform.parent.name == ShellName) continue;
                MakeShell(r, source, up, m, state);
            }
            renderersScratch.Clear();
        }

        // The shell of one mesh renderer: skipped for a renderer that is not opaque, has no readable mesh or is a helper.
        private void MakeShell(VesselRecord r, MeshRenderer source, Vector3 up, Material m, CoverBodyState state)
        {
            MeshFilter filter = source.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !source.enabled || source.sharedMaterial == null || source.sharedMaterial.renderQueue > 2500) return;
            Shader sourceShader = source.sharedMaterial.shader;
            string shaderName = sourceShader != null ? sourceShader.name : null;
            if (!VehicleCoverGate.ShellAccepts(shaderName))
            {
                // A foreign opaque shader: the shell would not light like the part (RECON P8). The part stays bare in this method.
                ForeignBare++;
                if (foreignLogged.Add(shaderName ?? "(none)"))
                    AtmospheresLog.Say("vehicle cover: no shell over shader '" + shaderName + "' (not a stock-like shader); vehicleCoverMethod ScreenSpace covers it");
                return;
            }
            if (!mesh.isReadable)
            {
                Unreadable++;
                return;
            }
            Vector3 localUp = source.transform.InverseTransformDirection(up).normalized;
            Mesh shellMesh = ShellMeshFor(mesh, localUp, source.transform.lossyScale);
            if (shellMesh == null) return;
            var go = new GameObject(ShellName);
            go.layer = source.gameObject.layer;
            Transform t = go.transform;
            t.SetParent(source.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = shellMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            mr.enabled = r.Amount >= (float)rates.Visible && r.Near;
            r.Shells.Add(new ShellRecord { Source = source, Shell = go, Renderer = mr, BuiltUp = localUp });
            ApplyTo(r, mr, state);
        }

        // The shell mesh of a source mesh for the up in its frame, shared between parts of one model.
        private Mesh ShellMeshFor(Mesh source, Vector3 localUp, Vector3 lossyScale)
        {
            long key = ShellMesh.CacheKey(source.GetInstanceID(), localUp.x, localUp.y, localUp.z);
            Mesh cached;
            if (meshes.TryGetValue(key, out cached)) return cached;
            float scale = Mathf.Max(1e-4f, (Mathf.Abs(lossyScale.x) + Mathf.Abs(lossyScale.y) + Mathf.Abs(lossyScale.z)) / 3f);
            float offset = Tuning.Get("vehicle.shellOffsetM", 0.02f) / scale;
            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;
            if (normals == null || normals.Length != vertices.Length)
            {
                source.RecalculateNormals();
                normals = source.normals;
            }
            var positions = new float[vertices.Length * 3];
            var norm = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++)
            {
                positions[i * 3] = vertices[i].x; positions[i * 3 + 1] = vertices[i].y; positions[i * 3 + 2] = vertices[i].z;
                norm[i * 3] = normals[i].x; norm[i * 3 + 1] = normals[i].y; norm[i * 3 + 2] = normals[i].z;
            }
            var indices = new List<int>();
            for (int s = 0; s < source.subMeshCount; s++) indices.AddRange(source.GetTriangles(s));
            ShellMesh.Result result = ShellMesh.Select(positions, norm, indices.ToArray(), localUp.x, localUp.y, localUp.z, Tuning.Get("vehicle.minUpDot", -0.25f), offset);
            if (result == null)
            {
                meshes[key] = null;
                return null;
            }
            var mesh = new Mesh { name = "ReDefinition shell " + source.name, hideFlags = HideFlags.HideAndDontSave };
            var v = new Vector3[result.VertexCount];
            var n = new Vector3[result.VertexCount];
            for (int i = 0; i < result.VertexCount; i++)
            {
                v[i] = new Vector3(result.Positions[i * 3], result.Positions[i * 3 + 1], result.Positions[i * 3 + 2]);
                n[i] = new Vector3(result.Normals[i * 3], result.Normals[i * 3 + 1], result.Normals[i * 3 + 2]);
            }
            mesh.vertices = v;
            mesh.normals = n;
            mesh.triangles = result.Indices;
            mesh.RecalculateBounds();
            meshes[key] = mesh;
            return mesh;
        }

        // A vessel that has turned far from the orientation a shell was built for gets that shell built anew, a few a frame.
        private void RebuildTurned(VesselRecord r, Vector3 up, Material m, CoverBodyState state, ref int budget)
        {
            for (int i = r.Shells.Count - 1; i >= 0 && budget > 0; i--)
            {
                ShellRecord s = r.Shells[i];
                if (s.Source == null || s.Shell == null)
                {
                    r.Shells.RemoveAt(i);
                    continue;
                }
                Vector3 now = s.Source.transform.InverseTransformDirection(up).normalized;
                if (!ShellMesh.Turned(s.BuiltUp.x, s.BuiltUp.y, s.BuiltUp.z, now.x, now.y, now.z)) continue;
                MeshFilter f = s.Source.GetComponent<MeshFilter>();
                Mesh shellMesh = f != null && f.sharedMesh != null ? ShellMeshFor(f.sharedMesh, now, s.Source.transform.lossyScale) : null;
                if (shellMesh != null) s.Shell.GetComponent<MeshFilter>().sharedMesh = shellMesh;
                s.BuiltUp = now;
                budget--;
            }
        }

        // The amount, the colour and the up are given to every shell of a vessel through a property block.
        private void ApplyAmount(VesselRecord r, CoverBodyState state)
        {
            if (Mathf.Abs(r.Amount - r.AppliedAmount) < 0.01f && r.AppliedAmount >= 0f && r.AppliedNear == r.Near) return;
            r.AppliedAmount = r.Amount;
            r.AppliedNear = r.Near;
            for (int i = r.Shells.Count - 1; i >= 0; i--)
            {
                ShellRecord s = r.Shells[i];
                if (s.Shell == null || s.Renderer == null)
                {
                    r.Shells.RemoveAt(i);
                    continue;
                }
                ApplyTo(r, s.Renderer, state);
            }
        }

        private void ApplyTo(VesselRecord r, MeshRenderer mr, CoverBodyState state)
        {
            if (block == null) block = new MaterialPropertyBlock();
            Vector3 up = UpOf(r.Vessel);
            block.Clear();
            block.SetFloat("_Amount", r.Amount);
            block.SetColor("_CoverColor", new Color(state.Def.Red, state.Def.Green, state.Def.Blue, 1f));
            block.SetVector("_UpWorld", new Vector4(up.x, up.y, up.z, 0f));
            block.SetFloat("_EdgeLow", Tuning.Get("vehicle.maskLow", 0.35f));
            block.SetFloat("_EdgeHigh", Tuning.Get("vehicle.maskHigh", 0.75f));
            mr.SetPropertyBlock(block);
            mr.enabled = r.Amount >= (float)rates.Visible && r.Near;
        }

        private void DestroyShells(VesselRecord r)
        {
            foreach (ShellRecord s in r.Shells)
                if (s.Shell != null) UnityEngine.Object.Destroy(s.Shell);
            r.Shells.Clear();
            r.Pending.Clear();
            r.Built.Clear();
            r.AppliedAmount = -1f;
        }

        private void RemoveAll()
        {
            foreach (VesselRecord r in records.Values) DestroyShells(r);
            records.Clear();
        }

        // Vessels that are not loaded any more give their state to the side file; a destroyed or docked part just loses its shells with it.
        private void Prune(List<Vessel> loaded, double ut)
        {
            scratchIds.Clear();
            foreach (KeyValuePair<uint, VesselRecord> pair in records)
            {
                bool still = false;
                for (int i = 0; i < loaded.Count; i++)
                    if (loaded[i] != null && loaded[i].persistentId == pair.Key) { still = true; break; }
                if (!still) scratchIds.Add(pair.Key);
            }
            foreach (uint id in scratchIds)
            {
                VesselRecord r = records[id];
                if (r.Seeded) stored[id] = new VehicleCoverStored { Amount = r.Amount, StampUt = ut, Target = r.Target, FallPerSecond = r.FallPerSecond };
                DestroyShells(r);
                records.Remove(id);
                dirty = true;
            }
        }

        // ---- the side file --------------------------------------------------------------------------------------------------------

        private static string PathOf(string save)
        {
            return ModFolder.PluginFile(AtmospheresSettings.Mod, save + "/" + FileName);
        }

        private void EnsureSaveLoaded()
        {
            string save = HighLogic.SaveFolder;
            if (string.IsNullOrEmpty(save) || save == loadedSave) return;
            if (loadedSave != null) Flush();
            loadedSave = save;
            stored.Clear();
            RemoveAll();
            try
            {
                string path = PathOf(save);
                if (File.Exists(path))
                {
                    int skipped;
                    foreach (KeyValuePair<uint, VehicleCoverStored> pair in VehicleCoverFile.Parse(File.ReadAllText(path), out skipped)) stored[pair.Key] = pair.Value;
                    AtmospheresLog.Say("vehicle cover: " + stored.Count + " vessels read from " + FileName + (skipped > 0 ? ", " + skipped + " lines skipped" : ""));
                }
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-vehicle-cover-read", "the vehicle cover file could not be read", e);
            }
        }

        private void Flush()
        {
            double ut;
            if (CoverClock.TryUt(out ut))
                foreach (VesselRecord r in records.Values)
                    if (r.Seeded) stored[r.Id] = new VehicleCoverStored { Amount = r.Amount, StampUt = ut, Target = r.Target, FallPerSecond = r.FallPerSecond };
            Save(Time.realtimeSinceStartup);
        }

        private void Save(float now)
        {
            lastSave = now;
            dirty = false;
            if (loadedSave == null || !ForcedField.AllowSideFileWrite()) return;
            try
            {
                double ut;
                if (CoverClock.TryUt(out ut))
                    foreach (VesselRecord r in records.Values)
                        if (r.Seeded) stored[r.Id] = new VehicleCoverStored { Amount = r.Amount, StampUt = ut, Target = r.Target, FallPerSecond = r.FallPerSecond };
                string path = PathOf(loadedSave);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, VehicleCoverFile.Serialize(stored));
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-vehicle-cover-write", "the vehicle cover file could not be written", e);
            }
        }
    }
}
