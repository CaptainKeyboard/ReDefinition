using System;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The snow veil of the storm cells (StormCells) in the world. For each cell near the camera it is a translucent curtain of falling
    // snow from the base of EVE's cloud to the ground, so that a storm can be seen from afar and entered. The cloud is EVE's
    // (EveLayerStorm, EveCellLayer). The veil is a transparent object of the forward queue, with no image effect.
    //
    // Raymarched: one bounding cylinder per cell, drawn with the shader ReDefinition/AtmospheresStormVolume from the Atmospheres' shader bundle.
    // The shader marches the view ray through a 3D noise texture made here once. Camera.onPreCull places the cylinder and hands the
    // cell's frame, heights, wind and light to its material. Without the bundle's shader no cell is drawn.
    //
    // The pieces are made once, in a pool of StormMaxCells cells. A cell is anchored to the planet with GetWorldSurfacePosition.
    // Nothing is allocated per frame.
    [Obsolete("Replaced by CurtainPass (Render/Volume); StormEffect, its only caller, goes when C25 removes the cell scheduler.")]
    internal sealed class StormVolume
    {
        // Cells farther than this from the camera, measured from the edge of the cell, are not drawn, in m.
        internal const double RangeM = 60000;

        // Above this height of the camera nothing is drawn, in m above sea level.
        internal const double MaxCameraAltitudeM = 40000;

        // The colours of the volume before the sun's brightness: the sky's light on the snow and the sun's.
        private static readonly Color RayAmbient = new Color(0.72f, 0.76f, 0.82f, 1f);
        private static readonly Color RaySun = new Color(1.00f, 0.96f, 0.88f, 1f);

        // The sun's colour when it is at the horizon, and the sky's light at night (blue-grey).
        private static readonly Color RaySunLow = new Color(1.00f, 0.74f, 0.52f, 1f);
        private static readonly Color RayAmbientNight = new Color(0.55f, 0.66f, 0.90f, 1f);

        // A diagnostics cap on the strength of the cells' veil, to look at a weak storm: 1 leaves it alone.
        internal static float StrengthCap = 1f;

        // The strength of the dark cloud base in the shader, 0 to 1. Diagnostics set it to compare pictures.
        internal static float BaseDark = 0.6f;

        // The render queue of the volume: before the veil (3000) and the flakes (3100).
        private const int QueueRay = 2995;

        private static readonly int IdCell = Shader.PropertyToID("_Cell"), IdUp = Shader.PropertyToID("_Up"), IdRight = Shader.PropertyToID("_Right"),
            IdHeights = Shader.PropertyToID("_Heights"), IdWind = Shader.PropertyToID("_Wind"), IdLook = Shader.PropertyToID("_Look"),
            IdSun = Shader.PropertyToID("_Sun"), IdSunColour = Shader.PropertyToID("_SunColour"), IdAmbient = Shader.PropertyToID("_AmbientColour"),
            IdNoise = Shader.PropertyToID("_Noise"), IdSnowBoost = Shader.PropertyToID("_SnowBoost"), IdVeilTint = Shader.PropertyToID("_VeilTint");

        // A diagnostics switch for screenshots: the veil is magenta and VeilProbeBoost times as dense, so that it can be told from the sky.
        internal static bool VeilProbe;
        private const float VeilProbeBoost = 12f;

        // Another diagnostics value: a factor on the veil's extinction, to compare densities in screenshots. 1 is the shader's own.
        internal static float VeilScale = 1f;

        // The storm's visibility at the camera now, in m, set by StormEffect; 0 where no storm runs. Inside a cell the rays are marched
        // no farther than the fog shows (CurtainMath.InsideRayM). ClampRays is a diagnostics switch to compare the frame time.
        internal static double CameraVisibilityM;
        internal static bool ClampRays = true;

        // A change of strength or brightness that is written to the materials again.
        private const float WriteStep = 0.004f;

        private sealed class Entry
        {
            internal GameObject Root;
            internal Transform RootTransform;
            internal bool Used;
            internal ulong Key;
            internal double Latitude, Longitude, RadiusM, BaseAglM;
            internal double Ground = double.NaN, GroundLatitude, GroundLongitude;
            internal float Strength, Brightness, WrittenStrength = -1f, WrittenBrightness = -1f;
            internal double SinSun = 1.0;
            internal float Yaw;
            internal float BelowM;
            internal Transform RayTransform;
            internal Material RayMaterial;
            internal float Fade;
        }

        private Entry[] pool;
        private Mesh cylinderMesh;
        private Texture3D noiseVolume;
        private Camera mainCamera;
        private CelestialBody body;
        private bool built, failed, hooked;
        private int shown;

        // The number of cells drawn now.
        internal int Shown
        {
            get { return shown; }
        }

        // Updates the cells that are to be seen: places the pieces in the pool on the cells (nearest first) and sets their colours.
        // cells are the cells that Collect found around the camera; the pieces of the others are hidden.
        internal void Update(Camera cam, CelestialBody planet, StormCells.Cell[] cells, int count, double cameraAltitude)
        {
            int wanted = AtmospheresSettings.StormVolume && count > 0 && cam != null && planet != null && cameraAltitude < MaxCameraAltitudeM
                ? Math.Min(count, AtmospheresSettings.StormMaxCells) : 0;
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
                    if (e.RayMaterial != null) UnityEngine.Object.Destroy(e.RayMaterial);
                }
            }
            if (cylinderMesh != null) UnityEngine.Object.Destroy(cylinderMesh);
            if (noiseVolume != null) UnityEngine.Object.Destroy(noiseVolume);
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
            e.Strength = Mathf.Min((float)cell.Strength, StrengthCap);
            if (double.IsNaN(e.Ground) || StormCells.Distance(cell.Latitude, cell.Longitude, e.GroundLatitude, e.GroundLongitude, body.Radius) > 300)
            {
                e.Ground = GroundHeight(cell.Latitude, cell.Longitude);
                e.GroundLatitude = cell.Latitude;
                e.GroundLongitude = cell.Longitude;
            }
            // The curtain starts at the base of EVE's cloud, which is a height above sea level.
            e.BaseAglM = EveLayerStorm.Supplying ? EveLayerStorm.BaseAboveGroundM(e.Ground) : EveCellLayer.BaseAboveGroundM(e.Ground);
            // The bounding cylinder from the lowest point of the volume to just over the cloud base.
            e.BelowM = CurtainMath.BelowM(cell.RadiusM, body.Radius);
            float rx = (float)cell.RadiusM;
            e.RayTransform.localPosition = new Vector3(0f, -e.BelowM, 0f);
            e.RayTransform.localScale = new Vector3(rx * CurtainMath.Widening, e.BelowM + CurtainMath.TopM(e.BaseAglM), rx * CurtainMath.Widening);
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

        // Colour and strength of a cell for the sun's height at its place. Written only when they changed.
        private void Colour(Entry e)
        {
            float brightness = Brightness(e);
            e.Brightness = brightness;
            if (Mathf.Abs(e.Strength - e.WrittenStrength) < WriteStep && Mathf.Abs(brightness - e.WrittenBrightness) < WriteStep) return;
            e.WrittenStrength = e.Strength;
            e.WrittenBrightness = brightness;
            e.Fade = (float)Mathx.SmoothStep(0, 0.35, e.Strength);
            // The sun's light is the sun's visibility (none at night) and warm while the sun is low; the ambient light is the sky's, which
            // goes blue-grey at night.
            double sin = e.SinSun;
            Color sun = Color.Lerp(RaySun, RaySunLow, (float)StormLook.WarmShare(sin));
            e.RayMaterial.SetVector(IdSunColour, Scale(sun, brightness * (float)StormLook.SunVisibility(sin)));
            e.RayMaterial.SetVector(IdAmbient, Scale(Color.Lerp(RayAmbient, RayAmbientNight, (float)StormLook.NightShare(sin)), brightness));
        }

        // The brightness of the sun at the cell's place: the sine of its elevation through StormLook.FogBrightness.
        private float Brightness(Entry e)
        {
            CelestialBody sunBody = GameGlobals.Sun;
            if (sunBody == null) return 1f;
            Vector3d ground = body.GetWorldSurfacePosition(e.Latitude, e.Longitude, e.Ground);
            Vector3d toSun = (sunBody.position - ground).normalized;
            double sin = Vector3d.Dot(toSun, body.GetSurfaceNVector(e.Latitude, e.Longitude));
            e.SinSun = sin;
            return (float)StormLook.FogBrightness(sin);
        }

        private static Color Scale(Color c, float f)
        {
            return new Color(c.r * f, c.g * f, c.b * f, 1f);
        }

        // Runs for every camera before it culls; the flight camera's pass places the pieces and hands the material its numbers.
        private void OnPreCull(Camera camera)
        {
            if (camera != mainCamera || body == null || pool == null) return;
            float time = Time.time;
            int layer = StormShaders.LayerOf(camera);
            // The volume reads the scene's depth to end its rays at what is drawn; the deferred path has the depth already.
            camera.depthTextureMode |= DepthTextureMode.Depth;
            CelestialBody sunBody = GameGlobals.Sun;
            Vector3 sunPosition = sunBody != null ? (Vector3)sunBody.position : Vector3.zero;
            float curvature = CurtainMath.Curvature(body.Radius);
            int steps = StormVolumeMath.Steps(AtmospheresSettings.StormVolumeSteps);
            for (int p = 0; p < pool.Length; p++)
            {
                Entry e = pool[p];
                if (!e.Used) continue;
                Vector3 position = (Vector3)body.GetWorldSurfacePosition(e.Latitude, e.Longitude, e.Ground);
                Vector3 radial = (Vector3)body.GetSurfaceNVector(e.Latitude, e.Longitude);
                Quaternion rotation = Quaternion.AngleAxis(e.Yaw, radial) * Quaternion.FromToRotation(Vector3.up, radial);
                e.RootTransform.SetPositionAndRotation(position, rotation);
                if (e.Root.layer != layer) SetLayer(e, layer);
                Vector3 right = rotation * Vector3.right;
                Vector3 toSun = sunPosition - position;
                toSun = toSun.sqrMagnitude > 1f ? toSun.normalized : radial;
                float offsetX, offsetZ, shearX, shearZ, fall;
                CurtainMath.Wind(e.Yaw + 90.0, time, out offsetX, out offsetZ, out shearX, out shearZ, out fall);
                Material m = e.RayMaterial;
                m.SetVector(IdCell, new Vector4(position.x, position.y, position.z, (float)e.RadiusM));
                m.SetVector(IdUp, new Vector4(radial.x, radial.y, radial.z, curvature));
                m.SetVector(IdRight, new Vector4(right.x, right.y, right.z, 0f));
                m.SetVector(IdHeights, new Vector4((float)e.BaseAglM, CurtainMath.TopM(e.BaseAglM), e.BelowM, BaseDark * (float)Mathx.SmoothStep(0.35, 0.8, e.Strength)));
                m.SetFloat(IdSnowBoost, VeilProbe ? VeilProbeBoost : VeilScale);
                m.SetColor(IdVeilTint, VeilProbe ? new Color(1f, 0f, 1f, 1f) : new Color(0f, 0f, 0f, 0f));
                m.SetVector(IdWind, new Vector4(offsetX, offsetZ, shearX, fall));
                double longest = RangeM + e.RadiusM * 2.0;
                if (ClampRays && CameraVisibilityM > 0)
                {
                    Vector3 relative = camera.transform.position - position;
                    float height = Vector3.Dot(relative, radial);
                    if (Mathf.Sqrt(Mathf.Max(0f, relative.sqrMagnitude - height * height)) < e.RadiusM)
                        longest = CurtainMath.InsideRayM(CameraVisibilityM, e.BaseAglM, longest);
                }
                m.SetVector(IdLook, new Vector4(e.Fade, steps, shearZ, (float)longest));
                m.SetVector(IdSun, new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            }
        }

        private static void SetLayer(Entry e, int layer)
        {
            e.Root.layer = layer;
            e.RayTransform.gameObject.layer = layer;
        }

        // Makes the pool of cells where the bundle's shader is there; logs it, or why not (then no cell is drawn).
        private void Build(Camera cam)
        {
            string reason;
            UnityEngine.Shader volume = StormShaders.LoadVolumeShader(out reason);
            if (volume != null && BuildRaymarched(cam, volume)) return;
            if (volume != null) reason = "the volume material or its noise texture could not be made";
            failed = true;
            AtmospheresLog.WarnOnce("storm volume: " + reason + "; the snow veil of the storm cells is not drawn");
        }

        // The pool of cells with one bounding cylinder each, drawn by the volume shader, and the noise texture they share. The cylinder
        // has 48 segments and is 2 % wider than the cell, from BelowM under the centre to the cloud base plus 450 m (the dark band of the cloud's underside). It is drawn at queue
        // 2995 with Cull Front and ZWrite Off. The ray stops at the far plane and at _CameraDepthTexture. The noise is 64^3 with 4
        // octaves, and the streaks are stretched 9 times along the vertical.
        private bool BuildRaymarched(Camera cam, UnityEngine.Shader shader)
        {
            var test = new Material(shader);
            bool compiles = test.passCount > 0;
            UnityEngine.Object.Destroy(test);
            if (!compiles) return false;

            noiseVolume = MakeNoiseVolume();
            cylinderMesh = CylinderMesh();
            int cells = Math.Max(1, AtmospheresSettings.StormMaxCells);
            pool = new Entry[cells];
            int layer = StormShaders.LayerOf(cam);
            for (int c = 0; c < cells; c++)
            {
                var e = new Entry();
                e.Root = new GameObject("ReDefinition Atmospheres storm cell " + c) { layer = layer };
                e.RootTransform = e.Root.transform;
                e.RayMaterial = new Material(shader) { name = "ReDefinition Atmospheres storm volume", renderQueue = QueueRay };
                e.RayMaterial.SetTexture(IdNoise, noiseVolume);
                e.RayMaterial.EnableKeyword("STORM_DEPTH");
                e.RayMaterial.SetVector(IdLook, new Vector4(0f, StormVolumeMath.Steps(AtmospheresSettings.StormVolumeSteps), 0f, 0f));
                e.RayTransform = Piece(e, "volume", cylinderMesh, e.RayMaterial, layer).transform;
                e.Root.SetActive(false);
                pool[c] = e;
            }
            built = true;
            AtmospheresLog.Say("storm volume: raymarched (bundle " + StormShaders.BundlePath + ", shader " + shader.name + ", noise "
                      + CurtainMath.NoiseSize.ToString(CultureInfo.InvariantCulture) + "^3, " + AtmospheresSettings.StormVolumeSteps.ToString(CultureInfo.InvariantCulture)
                      + " steps, " + cells.ToString(CultureInfo.InvariantCulture) + " cells in the pool, depth texture on), camera " + cam.name + " near "
                      + cam.nearClipPlane.ToString("0.###", CultureInfo.InvariantCulture) + " far " + cam.farClipPlane.ToString("0", CultureInfo.InvariantCulture)
                      + ", range " + (RangeM / 1000).ToString("0", CultureInfo.InvariantCulture) + " km");
            return true;
        }

        // The 3D noise the volume shader reads: tileable, bilinear, with mip levels for the far steps.
        private static Texture3D MakeNoiseVolume()
        {
            int size = CurtainMath.NoiseSize;
            byte[] voxels = StormNoise.Volume(size, CurtainMath.NoisePeriod, CurtainMath.NoiseOctaves, CurtainMath.NoiseSeed);
            var texture = new Texture3D(size, size, size, TextureFormat.RGBA32, true)
            {
                name = "ReDefinition Atmospheres storm noise",
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
            float[] v = CurtainMath.CylinderVertices(CurtainMath.Segments);
            var vertices = new Vector3[v.Length / 3];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
            var mesh = new Mesh { name = "ReDefinition Atmospheres storm cylinder" };
            mesh.vertices = vertices;
            mesh.triangles = CurtainMath.CylinderTriangles(CurtainMath.Segments);
            mesh.RecalculateBounds();
            return mesh;
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
    }
}
