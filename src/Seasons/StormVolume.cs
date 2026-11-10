using System;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // The storm cells (StormCells) in the world: a veil of falling snow under a low snow cloud for each cell near the camera, so that a
    // storm can be seen from afar and entered. It is a transparent object of the forward queue, with no image effect. Where EVE's
    // volumetric clouds carry the cell's cloud (EveCellLayer.Supplying), the raymarched volume draws the snow only.
    //
    // Raymarched, where the Seasons' shader bundle loads: one bounding cylinder per cell, drawn with the shader
    // ReDefinition/SeasonsStormVolume. The shader marches the view ray through a 3D noise texture made here once. Camera.onPreCull
    // places the cylinder and hands the cell's frame, heights, wind and light to its material.
    //
    // Simple, as the fallback or by setting: three open cylinders (shells) per cell, a dome at the cloud base and a few camera-facing
    // puffs, all with the game's alpha-blended particle shader (StormShaders).
    //
    // The pieces are made once, in a pool of StormMaxCells cells. A cell is anchored to the planet with GetWorldSurfacePosition.
    // Nothing is allocated per frame.
    internal sealed class StormVolume
    {
        // Cells farther than this from the camera, measured from the edge of the cell, are not drawn, in m.
        internal const double RangeM = 60000;

        // Above this height of the camera nothing is drawn, in m above sea level.
        internal const double MaxCameraAltitudeM = 40000;

        // The shells: radius as a share of the cell's, peak alpha, texture repeats around, scroll speed (repeats a second down, sideways).
        private static readonly float[] ShellRadius = { 1.00f, 0.80f, 0.60f };
        private static readonly float[] ShellAlpha = { 0.30f, 0.45f, 0.55f };
        private static readonly int[] ShellTiles = { 18, 14, 10 };
        private static readonly float[] ShellFall = { 0.020f, 0.030f, 0.045f };
        private static readonly float[] ShellSide = { 0.004f, -0.003f, 0.005f };

        // The vertical rings of a shell: height as a share of the shell (0 at the bottom, below the ground), and the alpha of the ring.
        private static readonly float[] RingY = { 0f, 0.04f, 0.08f, 0.25f, 0.50f, 0.75f, 0.90f, 1f };
        private static readonly float[] RingAlpha = { 0f, 0.8f, 1f, 1f, 1f, 0.9f, 0.4f, 0f };

        // The dome: rings (radius share) and their alpha.
        private static readonly float[] DomeRho = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] DomeAlpha = { 1f, 0.95f, 0.8f, 0.4f, 0f };

        private const int Segments = 40;
        private const int ShellsPerCell = 3;

        // Puffs: the lower ring along the rim (dark, big) and the upper ring above the base (light, smaller).
        private const int PuffsLow = 8, PuffsHigh = 5, PuffsPerCell = PuffsLow + PuffsHigh;

        // Where the puffs of the simple meshes sit, as a part of the cell's radius and in metres above the cloud base. The low ring is
        // near the edge: 0.78 to 0.95 out, 100 to 400 m up, 0.45 to 0.65 across. The high ring is nearer the middle: 0.25 to 0.70 out,
        // 500 to 1100 m up, 0.50 to 0.70 across. Each puff takes a value in its range from a seeded key.
        private const double LowRhoMin = 0.78, LowRhoMax = 0.95, HighRhoMin = 0.25, HighRhoMax = 0.70;
        private const double LowAboveMinM = 100, LowAboveMaxM = 400, HighAboveMinM = 500, HighAboveMaxM = 1100;
        private const double LowSizeMin = 0.45, LowSizeMax = 0.65, HighSizeMin = 0.50, HighSizeMax = 0.70;

        // Render queues: the dome first, the shells, the puffs; all before the veil (3000) and the flakes (3100).
        private const int QueueDome = 2994, QueueShell = 2996, QueuePuff = 2998;

        // The colours before the sun's brightness (StormLook.FogBrightness).
        private static readonly Color CurtainColour = new Color(0.80f, 0.83f, 0.88f, 1f);
        private static readonly Color DomeColour = new Color(0.50f, 0.52f, 0.57f, 1f);
        private static readonly Color PuffLowColour = new Color(0.58f, 0.60f, 0.65f, 1f);
        private static readonly Color PuffHighColour = new Color(0.78f, 0.80f, 0.85f, 1f);

        // How opaque the dome and the puffs are at full strength (0 to 1): the puffs are few, big and faint, so that they do not show as dark rings.
        private const float DomeAlphaFull = 0.90f, PuffAlphaFull = 0.42f;

        // The colours of the raymarched volume before the sun's brightness: the sky's light on the snow and the cloud, and the sun's.
        private static readonly Color RayAmbient = new Color(0.72f, 0.76f, 0.82f, 1f);
        private static readonly Color RaySun = new Color(1.00f, 0.96f, 0.88f, 1f);

        // The render queue of the raymarched volume: before the veil (3000) and the flakes (3100).
        private const int QueueRay = 2995;

        private static readonly int IdCell = Shader.PropertyToID("_Cell"), IdUp = Shader.PropertyToID("_Up"), IdRight = Shader.PropertyToID("_Right"),
            IdHeights = Shader.PropertyToID("_Heights"), IdWind = Shader.PropertyToID("_Wind"), IdLook = Shader.PropertyToID("_Look"),
            IdSun = Shader.PropertyToID("_Sun"), IdSunColour = Shader.PropertyToID("_SunColour"), IdAmbient = Shader.PropertyToID("_AmbientColour"),
            IdNoise = Shader.PropertyToID("_Noise"), IdCloudWeight = Shader.PropertyToID("_CloudWeight");

        // A change of strength or brightness that is written to the materials again.
        private const float WriteStep = 0.004f;

        private sealed class Puff
        {
            internal Transform Transform;
            internal float X, Z, Above, Size;
            internal bool High;
        }

        private sealed class Entry
        {
            internal GameObject Root;
            internal Transform RootTransform;
            internal Transform[] Shells = new Transform[ShellsPerCell];
            internal Material[] ShellMaterials = new Material[ShellsPerCell];
            internal Transform Dome;
            internal Material DomeMaterial, PuffLowMaterial, PuffHighMaterial;
            internal Puff[] Puffs = new Puff[PuffsPerCell];
            internal bool Used;
            internal ulong Key;
            internal double Latitude, Longitude, RadiusM, BaseAglM;
            internal double Ground = double.NaN, GroundLatitude, GroundLongitude;
            internal float Strength, Brightness, WrittenStrength = -1f, WrittenBrightness = -1f;
            internal float Yaw;
            internal float BelowM, HeightM, BulgeM;
            internal Transform RayTransform;
            internal Material RayMaterial;
            internal float Fade;
            // Whether EVE's layer supplies the cloud of this cell, so that the volume draws the snow only.
            internal bool EveCloud;
        }

        private Entry[] pool;
        private Mesh[] shellMeshes;
        private Mesh domeMesh, quadMesh, cylinderMesh;
        private Texture2D cloudTexture, puffTexture;
        private Texture3D noiseVolume;
        private Camera mainCamera;
        private CelestialBody body;
        private bool built, failed, hooked, raymarch;
        private string shaderUsed = "none";
        private int shown;

        // Whether the cells are drawn raymarched (false: the simple meshes, or nothing yet).
        internal bool Raymarched
        {
            get { return raymarch; }
        }

        // The number of cells drawn now.
        internal int Shown
        {
            get { return shown; }
        }

        // The shader the pieces use, once they are made.
        internal string ShaderName
        {
            get { return shaderUsed; }
        }

        // Updates the cells that are to be seen: places the pieces in the pool on the cells (nearest first) and sets their colours.
        // cells are the cells that Collect found around the camera; the pieces of the others are hidden.
        internal void Update(Camera cam, CelestialBody planet, StormCells.Cell[] cells, int count, double cameraAltitude)
        {
            int wanted = SeasonsSettings.StormVolume && count > 0 && cam != null && planet != null && cameraAltitude < MaxCameraAltitudeM
                ? Math.Min(count, SeasonsSettings.StormMaxCells) : 0;
            if (wanted > 0 && !built && !failed) Build(cam);
            if (!built) wanted = 0;
            if (wanted > 0 && wanted > pool.Length) wanted = pool.Length;
            if (wanted == 0)
            {
                HideAll();
                return;
            }
            mainCamera = cam;
            body = planet;

            // Keep the pieces of the cells that go on, then give free pieces to the new ones.
            for (int p = 0; p < pool.Length; p++) pool[p].Used = false;
            for (int i = 0; i < wanted; i++)
            {
                Entry e = Find(cells[i].Key);
                if (e != null) e.Used = true;
            }
            for (int i = 0; i < wanted; i++)
            {
                Entry e = Find(cells[i].Key);
                if (e == null)
                {
                    e = Free();
                    if (e == null) continue;
                    e.Used = true;
                    e.Key = cells[i].Key;
                    e.Ground = double.NaN;
                    e.WrittenStrength = -1f;
                    e.Yaw = (float)(StormModel.Unit(cells[i].Key, 12) * 360.0);
                }
                Set(e, ref cells[i]);
            }
            shown = 0;
            for (int p = 0; p < pool.Length; p++)
            {
                Entry e = pool[p];
                if (e.Used)
                {
                    shown++;
                    if (!e.Root.activeSelf) e.Root.SetActive(true);
                    Colour(e);
                }
                else if (e.Root.activeSelf)
                {
                    e.Root.SetActive(false);
                    e.Key = 0;
                }
            }
            if (shown > 0 && !hooked)
            {
                Camera.onPreCull += OnPreCull;
                hooked = true;
            }
        }

        internal void HideAll()
        {
            shown = 0;
            if (pool != null)
            {
                for (int p = 0; p < pool.Length; p++)
                {
                    if (pool[p].Root != null && pool[p].Root.activeSelf) pool[p].Root.SetActive(false);
                    pool[p].Key = 0;
                    pool[p].Used = false;
                }
            }
            if (hooked)
            {
                Camera.onPreCull -= OnPreCull;
                hooked = false;
            }
        }

        internal void Dispose()
        {
            HideAll();
            if (pool != null)
            {
                for (int p = 0; p < pool.Length; p++)
                {
                    Entry e = pool[p];
                    if (e.Root != null) UnityEngine.Object.Destroy(e.Root);
                    for (int i = 0; i < ShellsPerCell; i++) if (e.ShellMaterials[i] != null) UnityEngine.Object.Destroy(e.ShellMaterials[i]);
                    if (e.DomeMaterial != null) UnityEngine.Object.Destroy(e.DomeMaterial);
                    if (e.PuffLowMaterial != null) UnityEngine.Object.Destroy(e.PuffLowMaterial);
                    if (e.PuffHighMaterial != null) UnityEngine.Object.Destroy(e.PuffHighMaterial);
                    if (e.RayMaterial != null) UnityEngine.Object.Destroy(e.RayMaterial);
                }
            }
            if (cylinderMesh != null) UnityEngine.Object.Destroy(cylinderMesh);
            if (noiseVolume != null) UnityEngine.Object.Destroy(noiseVolume);
            if (shellMeshes != null) for (int i = 0; i < shellMeshes.Length; i++) if (shellMeshes[i] != null) UnityEngine.Object.Destroy(shellMeshes[i]);
            if (domeMesh != null) UnityEngine.Object.Destroy(domeMesh);
            if (quadMesh != null) UnityEngine.Object.Destroy(quadMesh);
            if (cloudTexture != null) UnityEngine.Object.Destroy(cloudTexture);
            if (puffTexture != null) UnityEngine.Object.Destroy(puffTexture);
            pool = null;
            built = false;
        }

        private Entry Find(ulong key)
        {
            for (int p = 0; p < pool.Length; p++) if (pool[p].Key == key && pool[p].Key != 0 && pool[p].Root.activeSelf) return pool[p];
            return null;
        }

        private Entry Free()
        {
            for (int p = 0; p < pool.Length; p++) if (!pool[p].Used) return pool[p];
            return null;
        }

        // Takes the cell's place, size and strength into the piece, and the ground height under its centre (looked up again when the
        // centre has moved by more than 300 m since).
        private void Set(Entry e, ref StormCells.Cell cell)
        {
            e.Latitude = cell.Latitude;
            e.Longitude = cell.Longitude;
            e.RadiusM = cell.RadiusM;
            e.Strength = (float)cell.Strength;
            if (double.IsNaN(e.Ground) || StormCells.Distance(cell.Latitude, cell.Longitude, e.GroundLatitude, e.GroundLongitude, body.Radius) > 300)
            {
                e.Ground = GroundHeight(cell.Latitude, cell.Longitude);
                e.GroundLatitude = cell.Latitude;
                e.GroundLongitude = cell.Longitude;
            }
            // Where EVE's layer supplies the cloud, the curtain starts at its base, which is a height above sea level.
            e.EveCloud = raymarch && EveCellLayer.Supplying;
            e.BaseAglM = e.EveCloud ? EveCellLayer.BaseAboveGroundM(e.Ground) : cell.BaseAglM;
            // The planet curves away from the tangent plane of the centre by r^2 / 2R at the rim; the shells reach that far below.
            double sagitta = cell.RadiusM * cell.RadiusM / (2 * body.Radius);
            e.BelowM = (float)(sagitta + 150);
            e.HeightM = (float)(cell.BaseAglM + e.BelowM);
            e.BulgeM = (float)Math.Max(300, Math.Min(800, cell.RadiusM * 0.05));
            float rx = (float)cell.RadiusM;
            if (raymarch)
            {
                // The bounding cylinder from the lowest point of the volume to its top.
                e.BelowM = StormVolumeMath.BelowM(cell.RadiusM, body.Radius);
                e.RayTransform.localPosition = new Vector3(0f, -e.BelowM, 0f);
                e.RayTransform.localScale = new Vector3(rx * StormVolumeMath.Widening, e.BelowM + StormVolumeMath.TopM(e.BaseAglM, !e.EveCloud), rx * StormVolumeMath.Widening);
                return;
            }
            for (int i = 0; i < ShellsPerCell; i++)
            {
                Transform t = e.Shells[i];
                t.localPosition = new Vector3(0f, -e.BelowM, 0f);
                t.localScale = new Vector3(rx * ShellRadius[i], e.HeightM, rx * ShellRadius[i]);
            }
            e.Dome.localPosition = new Vector3(0f, (float)cell.BaseAglM, 0f);
            e.Dome.localScale = new Vector3(rx, e.BulgeM, rx);
        }

        // The terrain height above sea level at a place, 0 over the sea; 0 where the planet cannot say.
        private double GroundHeight(double latitude, double longitude)
        {
            try
            {
                double h = body.TerrainAltitude(latitude, longitude, false);
                return double.IsNaN(h) || double.IsInfinity(h) ? 0 : h;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Colour and alpha of the pieces of a cell for its strength and the sun's height at its place. Written only when they changed.
        private void Colour(Entry e)
        {
            float brightness = Brightness(e);
            e.Brightness = brightness;
            if (Mathf.Abs(e.Strength - e.WrittenStrength) < WriteStep && Mathf.Abs(brightness - e.WrittenBrightness) < WriteStep) return;
            e.WrittenStrength = e.Strength;
            e.WrittenBrightness = brightness;
            float fade = (float)Mathx.SmoothStep(0, 0.35, e.Strength);
            e.Fade = fade;
            if (raymarch)
            {
                e.RayMaterial.SetVector(IdSunColour, Scale(RaySun, brightness));
                e.RayMaterial.SetVector(IdAmbient, Scale(RayAmbient, brightness));
                return;
            }
            for (int i = 0; i < ShellsPerCell; i++) StormShaders.Tint(e.ShellMaterials[i], Scale(CurtainColour, brightness), ShellAlpha[i] * fade);
            StormShaders.Tint(e.DomeMaterial, Scale(DomeColour, brightness), DomeAlphaFull * fade);
            StormShaders.Tint(e.PuffLowMaterial, Scale(PuffLowColour, brightness), PuffAlphaFull * fade);
            StormShaders.Tint(e.PuffHighMaterial, Scale(PuffHighColour, brightness), PuffAlphaFull * fade);
        }

        // The brightness of the sun at the cell's place: the sine of its elevation through StormLook.FogBrightness.
        private float Brightness(Entry e)
        {
            if (FlightGlobals.Bodies == null || FlightGlobals.Bodies.Count == 0 || FlightGlobals.Bodies[0] == null) return 1f;
            Vector3d ground = body.GetWorldSurfacePosition(e.Latitude, e.Longitude, e.Ground);
            Vector3d toSun = (FlightGlobals.Bodies[0].position - ground).normalized;
            double sin = Vector3d.Dot(toSun, body.GetSurfaceNVector(e.Latitude, e.Longitude));
            return (float)StormLook.FogBrightness(sin);
        }

        private static Color Scale(Color c, float f)
        {
            return new Color(c.r * f, c.g * f, c.b * f, 1f);
        }

        // Runs for every camera before it culls; the flight camera's pass places the pieces and moves the textures.
        private void OnPreCull(Camera camera)
        {
            if (camera != mainCamera || body == null || pool == null) return;
            Quaternion facing = camera.transform.rotation;
            float time = Time.time;
            int layer = StormShaders.LayerOf(camera);
            // The raymarched volume reads the scene's depth to end its rays at what is drawn; the deferred path has the depth already.
            if (raymarch) camera.depthTextureMode |= DepthTextureMode.Depth;
            Vector3 sunPosition = FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 && FlightGlobals.Bodies[0] != null
                ? (Vector3)FlightGlobals.Bodies[0].position : Vector3.zero;
            float curvature = StormVolumeMath.Curvature(body.Radius);
            int steps = StormVolumeMath.Steps(SeasonsSettings.StormVolumeSteps);
            for (int p = 0; p < pool.Length; p++)
            {
                Entry e = pool[p];
                if (!e.Used) continue;
                Vector3 position = (Vector3)body.GetWorldSurfacePosition(e.Latitude, e.Longitude, e.Ground);
                Vector3 radial = (Vector3)body.GetSurfaceNVector(e.Latitude, e.Longitude);
                Quaternion rotation = Quaternion.AngleAxis(e.Yaw, radial) * Quaternion.FromToRotation(Vector3.up, radial);
                e.RootTransform.SetPositionAndRotation(position, rotation);
                if (e.Root.layer != layer) SetLayer(e, layer);
                if (raymarch)
                {
                    Vector3 right = rotation * Vector3.right;
                    Vector3 toSun = sunPosition - position;
                    toSun = toSun.sqrMagnitude > 1f ? toSun.normalized : radial;
                    float offsetX, offsetZ, shearX, shearZ, fall;
                    StormVolumeMath.Wind(e.Yaw + 90.0, time, out offsetX, out offsetZ, out shearX, out shearZ, out fall);
                    Material m = e.RayMaterial;
                    m.SetVector(IdCell, new Vector4(position.x, position.y, position.z, (float)e.RadiusM));
                    m.SetVector(IdUp, new Vector4(radial.x, radial.y, radial.z, curvature));
                    m.SetVector(IdRight, new Vector4(right.x, right.y, right.z, 0f));
                    m.SetVector(IdHeights, new Vector4((float)e.BaseAglM, StormVolumeMath.CloudThicknessM(e.BaseAglM), StormVolumeMath.TopM(e.BaseAglM, !e.EveCloud), e.BelowM));
                    m.SetFloat(IdCloudWeight, e.EveCloud ? 0f : 1f);
                    m.SetVector(IdWind, new Vector4(offsetX, offsetZ, shearX, fall));
                    m.SetVector(IdLook, new Vector4(e.Fade, steps, shearZ, (float)(RangeM + e.RadiusM * 2.0)));
                    m.SetVector(IdSun, new Vector4(toSun.x, toSun.y, toSun.z, 0f));
                    continue;
                }
                for (int i = 0; i < ShellsPerCell; i++)
                    e.ShellMaterials[i].mainTextureOffset = new Vector2(Mathx.Frac(time * ShellSide[i]), Mathx.Frac(time * ShellFall[i]));
                float r = (float)e.RadiusM;
                for (int i = 0; i < PuffsPerCell; i++)
                {
                    Puff puff = e.Puffs[i];
                    Vector3 local = new Vector3(puff.X * r, (float)e.BaseAglM + puff.Above, puff.Z * r);
                    puff.Transform.SetPositionAndRotation(position + rotation * local, facing);
                    float size = puff.Size * r;
                    puff.Transform.localScale = new Vector3(size, size, 1f);
                }
            }
        }

        private static void SetLayer(Entry e, int layer)
        {
            e.Root.layer = layer;
            if (e.RayTransform != null)
            {
                e.RayTransform.gameObject.layer = layer;
                return;
            }
            for (int i = 0; i < ShellsPerCell; i++) e.Shells[i].gameObject.layer = layer;
            e.Dome.gameObject.layer = layer;
            for (int i = 0; i < PuffsPerCell; i++) e.Puffs[i].Transform.gameObject.layer = layer;
        }

        // Makes the pool of cells the raymarched way where the bundle's shader is there, else the simple way (or none where the setting
        // demands the raymarched one); logs which, and why not.
        private void Build(Camera cam)
        {
            SeasonsSettings.StormVolumeModes mode = SeasonsSettings.StormVolumeMode;
            string reason = "the setting stormVolumeMode is simple";
            if (mode != SeasonsSettings.StormVolumeModes.Simple)
            {
                UnityEngine.Shader volume = StormShaders.LoadVolumeShader(out reason);
                if (volume != null && BuildRaymarched(cam, volume)) return;
                if (volume != null) reason = "the volume material or its noise texture could not be made";
            }
            if (mode == SeasonsSettings.StormVolumeModes.Raymarch)
            {
                failed = true;
                SeasonsLog.WarnOnce("storm volume: the raymarched volume is required (stormVolumeMode raymarch) but " + reason
                                    + "; the storm cells are not drawn");
                return;
            }
            if (mode == SeasonsSettings.StormVolumeModes.Auto)
                SeasonsLog.WarnOnce("storm volume: fallback to plain meshes, " + reason);
            BuildSimple(cam, mode == SeasonsSettings.StormVolumeModes.Simple ? reason : "fallback: " + reason);
        }

        // The pool of cells with one bounding cylinder each, drawn by the volume shader, and the noise texture they share. The cylinder
        // has 48 segments and is 2 % wider than the cell, from BelowM under the centre to the cloud top plus 120 m. It is drawn at queue
        // 2995 with Cull Front and ZWrite Off. The ray stops at the far plane and at _CameraDepthTexture. The noise is 64^3 with 4
        // octaves, the snow cloud 1300 m thick (StormVolumeMath.CloudThicknessM), and the veil is stretched 9 times along the vertical.
        private bool BuildRaymarched(Camera cam, UnityEngine.Shader shader)
        {
            var test = new Material(shader);
            bool compiles = test.passCount > 0;
            UnityEngine.Object.Destroy(test);
            if (!compiles) return false;

            noiseVolume = MakeNoiseVolume();
            cylinderMesh = CylinderMesh();
            int cells = Math.Max(1, SeasonsSettings.StormMaxCells);
            pool = new Entry[cells];
            int layer = StormShaders.LayerOf(cam);
            for (int c = 0; c < cells; c++)
            {
                var e = new Entry();
                e.Root = new GameObject("ReDefinition Seasons storm cell " + c) { layer = layer };
                e.RootTransform = e.Root.transform;
                e.RayMaterial = new Material(shader) { name = "ReDefinition Seasons storm volume", renderQueue = QueueRay };
                e.RayMaterial.SetTexture(IdNoise, noiseVolume);
                e.RayMaterial.EnableKeyword("STORM_DEPTH");
                e.RayMaterial.SetVector(IdLook, new Vector4(0f, StormVolumeMath.Steps(SeasonsSettings.StormVolumeSteps), 0f, 0f));
                e.RayTransform = Piece(e, "volume", cylinderMesh, e.RayMaterial, layer).transform;
                e.Root.SetActive(false);
                pool[c] = e;
            }
            raymarch = true;
            built = true;
            shaderUsed = shader.name;
            SeasonsLog.Say("storm volume: raymarched (bundle " + StormShaders.BundlePath + ", shader " + shader.name + ", noise "
                      + StormVolumeMath.NoiseSize.ToString(CultureInfo.InvariantCulture) + "^3, " + SeasonsSettings.StormVolumeSteps.ToString(CultureInfo.InvariantCulture)
                      + " steps, " + cells.ToString(CultureInfo.InvariantCulture) + " cells in the pool, depth texture on), camera " + cam.name + " near "
                      + cam.nearClipPlane.ToString("0.###", CultureInfo.InvariantCulture) + " far " + cam.farClipPlane.ToString("0", CultureInfo.InvariantCulture)
                      + ", range " + (RangeM / 1000).ToString("0", CultureInfo.InvariantCulture) + " km");
            return true;
        }

        // The 3D noise the volume shader reads: tileable, bilinear, with mip levels for the far steps.
        private static Texture3D MakeNoiseVolume()
        {
            int size = StormVolumeMath.NoiseSize;
            byte[] voxels = StormNoise.Volume(size, StormVolumeMath.NoisePeriod, StormVolumeMath.NoiseOctaves, StormVolumeMath.NoiseSeed);
            var texture = new Texture3D(size, size, size, TextureFormat.RGBA32, true)
            {
                name = "ReDefinition Seasons storm noise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear
            };
            var colours = new Color32[voxels.Length];
            for (int i = 0; i < voxels.Length; i++) colours[i] = new Color32(voxels[i], voxels[i], voxels[i], 255);
            texture.SetPixels32(colours);
            texture.Apply(true, true);
            return texture;
        }

        // The bounding cylinder of radius 1 and height 1 (bottom at y 0), with caps.
        private static Mesh CylinderMesh()
        {
            float[] v = StormVolumeMath.CylinderVertices(StormVolumeMath.Segments);
            var vertices = new Vector3[v.Length / 3];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
            var mesh = new Mesh { name = "ReDefinition Seasons storm cylinder" };
            mesh.vertices = vertices;
            mesh.triangles = StormVolumeMath.CylinderTriangles(StormVolumeMath.Segments);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Makes the textures, the three kinds of mesh and the pool of cells; says what it made and which shader it uses. Three shells
        // stand at 1.0, 0.8 and 0.6 of the radius, with 40 segments and 8 rings. A dome 300 to 800 m high sits at the cloud base.
        // There are 13 puffs, 8 on the rim and 5 above the base, with alpha 0.42 at full strength. The queues are 2994 (dome), 2996 (shells) and 2998 (puffs). The
        // cloud texture is 128 x 128, the puff 64 x 64. 22 darker puffs showed as dark rings in the game.
        private void BuildSimple(Camera cam, string why)
        {
            UnityEngine.Shader shader = StormShaders.Find(StormShaders.BlendedNames);
            if (shader == null)
            {
                failed = true;
                SeasonsLog.WarnOnce("storm volume: no alpha-blended shader found (" + string.Join(", ", StormShaders.BlendedNames)
                                    + "); the storm cells are not drawn");
                return;
            }
            shaderUsed = shader.name;
            cloudTexture = MakeTexture("ReDefinition Seasons storm cloud", StormNoise.CloudAlpha(128, 4, 4, 7), 128, TextureWrapMode.Repeat, true);
            puffTexture = MakeTexture("ReDefinition Seasons storm puff", StormNoise.PuffAlpha(64, 11), 64, TextureWrapMode.Clamp, false);
            shellMeshes = new Mesh[ShellsPerCell];
            for (int i = 0; i < ShellsPerCell; i++) shellMeshes[i] = ShellMesh(ShellTiles[i]);
            domeMesh = DomeMesh();
            quadMesh = QuadMesh();

            int cells = Math.Max(1, SeasonsSettings.StormMaxCells);
            pool = new Entry[cells];
            int layer = StormShaders.LayerOf(cam);
            int puffs = 0;
            for (int c = 0; c < cells; c++)
            {
                var e = new Entry();
                e.Root = new GameObject("ReDefinition Seasons storm cell " + c) { layer = layer };
                e.RootTransform = e.Root.transform;
                for (int i = 0; i < ShellsPerCell; i++)
                {
                    e.ShellMaterials[i] = MakeMaterial(shader, cloudTexture, QueueShell);
                    e.Shells[i] = Piece(e, "shell " + i, shellMeshes[i], e.ShellMaterials[i], layer).transform;
                }
                e.DomeMaterial = MakeMaterial(shader, cloudTexture, QueueDome);
                e.Dome = Piece(e, "base", domeMesh, e.DomeMaterial, layer).transform;
                e.PuffLowMaterial = MakeMaterial(shader, puffTexture, QueuePuff);
                e.PuffHighMaterial = MakeMaterial(shader, puffTexture, QueuePuff);
                for (int i = 0; i < PuffsPerCell; i++)
                {
                    bool high = i >= PuffsLow;
                    GameObject go = Piece(e, "puff " + i, quadMesh, high ? e.PuffHighMaterial : e.PuffLowMaterial, layer);
                    e.Puffs[i] = MakePuff(i, high, go.transform);
                    puffs++;
                }
                e.Root.SetActive(false);
                pool[c] = e;
            }
            built = true;
            SeasonsLog.Say("storm volume: simple meshes (" + why + "), shells " + (cells * ShellsPerCell).ToString(CultureInfo.InvariantCulture) + ", puffs "
                      + puffs.ToString(CultureInfo.InvariantCulture) + " (" + cells.ToString(CultureInfo.InvariantCulture) + " cells in the pool), shader "
                      + shaderUsed + ", camera " + cam.name + " near " + cam.nearClipPlane.ToString("0.###", CultureInfo.InvariantCulture)
                      + " far " + cam.farClipPlane.ToString("0", CultureInfo.InvariantCulture) + ", range "
                      + (RangeM / 1000).ToString("0", CultureInfo.InvariantCulture) + " km, texture scroll via the material offset ("
                      + (shader.name.Contains("Sprites") ? "not supported by this shader" : "supported") + ")");
        }

        private static Puff MakePuff(int index, bool high, Transform transform)
        {
            ulong key = StormModel.Mix(0xC10D5UL, (ulong)index + 1UL);
            int n = high ? index - PuffsLow : index;
            int count = high ? PuffsHigh : PuffsLow;
            double angle = (n + 0.35 * StormModel.Unit(key, 0)) / count * 2 * Math.PI + (high ? 0.4 : 0.0);
            double rho = high ? Mathx.Lerp(HighRhoMin, HighRhoMax, StormModel.Unit(key, 1)) : Mathx.Lerp(LowRhoMin, LowRhoMax, StormModel.Unit(key, 1));
            return new Puff
            {
                Transform = transform,
                High = high,
                X = (float)(Math.Cos(angle) * rho),
                Z = (float)(Math.Sin(angle) * rho),
                Above = (float)(high ? Mathx.Lerp(HighAboveMinM, HighAboveMaxM, StormModel.Unit(key, 2)) : Mathx.Lerp(LowAboveMinM, LowAboveMaxM, StormModel.Unit(key, 2))),
                Size = (float)(high ? Mathx.Lerp(HighSizeMin, HighSizeMax, StormModel.Unit(key, 3)) : Mathx.Lerp(LowSizeMin, LowSizeMax, StormModel.Unit(key, 3)))
            };
        }

        private static Material MakeMaterial(UnityEngine.Shader shader, Texture2D texture, int queue)
        {
            var m = new Material(shader) { name = "ReDefinition Seasons storm cell" };
            m.mainTexture = texture;
            m.renderQueue = queue;
            StormShaders.Tint(m, Color.white, 0f);
            return m;
        }

        private static GameObject Piece(Entry e, string name, Mesh mesh, Material material, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(e.RootTransform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go;
        }

        private static Texture2D MakeTexture(string name, byte[] alpha, int size, TextureWrapMode wrap, bool mips)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mips) { name = name, wrapMode = wrap, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, alpha[i]);
            texture.SetPixels32(pixels);
            texture.Apply(mips, true);
            return texture;
        }

        // An open cylinder of radius 1 and height 1 (bottom at y 0), Segments around, with rings at RingY. The vertex alpha is
        // the vertical profile; the texture coordinates repeat tiles times around and once and a bit up.
        private static Mesh ShellMesh(int tiles)
        {
            int rings = RingY.Length, columns = Segments + 1;
            var vertices = new Vector3[rings * columns];
            var uv = new Vector2[rings * columns];
            var colours = new Color32[rings * columns];
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < columns; s++)
                {
                    float a = s / (float)Segments * 2f * Mathf.PI;
                    int i = r * columns + s;
                    vertices[i] = new Vector3(Mathf.Cos(a), RingY[r], Mathf.Sin(a));
                    uv[i] = new Vector2(s / (float)Segments * tiles, RingY[r] * 1.2f);
                    colours[i] = new Color32(255, 255, 255, (byte)(RingAlpha[r] * 255f + 0.5f));
                }
            }
            var triangles = new int[(rings - 1) * Segments * 6];
            int t = 0;
            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < Segments; s++)
                {
                    int a = r * columns + s, b = a + 1, c = a + columns, d = c + 1;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }
            return Finish(new Mesh { name = "ReDefinition Seasons storm shell" }, vertices, uv, colours, triangles);
        }

        // A dome of radius 1: height 1 in the middle, 0 at the rim (1 - rho^2), alpha from the middle to the rim as DomeAlpha.
        private static Mesh DomeMesh()
        {
            int rings = DomeRho.Length, columns = Segments + 1;
            var vertices = new Vector3[rings * columns];
            var uv = new Vector2[rings * columns];
            var colours = new Color32[rings * columns];
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < columns; s++)
                {
                    float a = s / (float)Segments * 2f * Mathf.PI;
                    float rho = DomeRho[r];
                    int i = r * columns + s;
                    vertices[i] = new Vector3(Mathf.Cos(a) * rho, 1f - rho * rho, Mathf.Sin(a) * rho);
                    uv[i] = new Vector2(0.5f + 0.5f * Mathf.Cos(a) * rho, 0.5f + 0.5f * Mathf.Sin(a) * rho) * 3f;
                    colours[i] = new Color32(255, 255, 255, (byte)(DomeAlpha[r] * 255f + 0.5f));
                }
            }
            var triangles = new int[(rings - 1) * Segments * 6];
            int t = 0;
            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < Segments; s++)
                {
                    int a = r * columns + s, b = a + 1, c = a + columns, d = c + 1;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }
            return Finish(new Mesh { name = "ReDefinition Seasons storm base" }, vertices, uv, colours, triangles);
        }

        // A square of side 1 in the plane of x and y, for the puffs.
        private static Mesh QuadMesh()
        {
            var vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            var uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            var colours = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            return Finish(new Mesh { name = "ReDefinition Seasons storm puff" }, vertices, uv, colours, new[] { 0, 2, 1, 1, 2, 3 });
        }

        private static Mesh Finish(Mesh mesh, Vector3[] vertices, Vector2[] uv, Color32[] colours, int[] triangles)
        {
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors32 = colours;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
