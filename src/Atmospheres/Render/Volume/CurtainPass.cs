using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The precipitation curtain under the storm cores: the raymarched streak volume of ReDefinition/AtmospheresStormVolume (the shader
    // StormVolume draws), placed on the cores the volume pass found in the weather field's window instead of on hand-made storm cells,
    // and gated by the level the frame driver wrote into WeatherHooks.View: drawn only from level 0.45 up (with a hysteresis of 0.05, the
    // same gate as the veil), none for light levels. The step count follows the gate share S: 4 below 0.3, 8 up to 0.7, 16 above
    // (VolumeMath.CurtainSteps), separate from the slab's march quality. A fixed pool of cylinders is made once; nothing is allocated
    // per frame. Cloud shapes are the volume pass's; the curtain is the falling precipitation below their bases.
    internal sealed class CurtainPass
    {
        // Cores farther than this from the camera, measured from the edge, are not drawn, m. The most cores drawn at once.
        internal const double RangeM = 60000;
        internal const int PoolSize = 3;
        // The share of a core's radius the curtain covers: the rain shaft is narrower than the tower above it.
        internal const float RadiusShare = 0.7f;
        // The darkest the base is made by a full-strength curtain.
        internal const float BaseDark = 0.6f;

        // The property ids are read when the pass is made, not in a static initialiser: Shader.PropertyToID needs the engine, and the
        // module is also created where there is none (the assembly smoke test).
        private static int IdCell, IdUp, IdRight, IdHeights, IdWind, IdLook, IdSun, IdSunColour, IdAmbient, IdNoise, IdSnowBoost, IdVeilTint;

        private static void ReadIds()
        {
            IdCell = Shader.PropertyToID("_Cell");
            IdUp = Shader.PropertyToID("_Up");
            IdRight = Shader.PropertyToID("_Right");
            IdHeights = Shader.PropertyToID("_Heights");
            IdWind = Shader.PropertyToID("_Wind");
            IdLook = Shader.PropertyToID("_Look");
            IdSun = Shader.PropertyToID("_Sun");
            IdSunColour = Shader.PropertyToID("_SunColour");
            IdAmbient = Shader.PropertyToID("_AmbientColour");
            IdNoise = Shader.PropertyToID("_Noise");
            IdSnowBoost = Shader.PropertyToID("_SnowBoost");
            IdVeilTint = Shader.PropertyToID("_VeilTint");
        }

        private sealed class Piece
        {
            internal GameObject Root;
            internal Transform Transform;
            internal Material Material;
            internal Vector3 Position;
            internal float BaseAgl, Radius, BelowM;
        }

        private Piece[] pool;
        private Mesh mesh;
        private Texture3D ownNoise;
        private bool madeNoise;
        private int shown;
        private float share;
        private int steps;

        // The cores drawn now, and the gate share and step count of the last Update.
        internal int Shown { get { return shown; } }
        internal float Share { get { return share; } }
        internal int Steps { get { return steps; } }
        internal bool GateOn { get; private set; }
        internal string Problem { get; private set; }

        // Makes the pool with the curtain shader; noise may be shared with the volume pass (null: made here).
        internal bool Init(Shader shader, Texture3D noise, int layer)
        {
            if (shader == null)
            {
                Problem = "the storm volume shader is missing";
                return false;
            }
            ReadIds();
            var test = new Material(shader);
            bool compiles = test.passCount > 0;
            UnityEngine.Object.Destroy(test);
            if (!compiles)
            {
                Problem = "the storm volume shader does not compile";
                return false;
            }
            if (noise == null)
            {
                int size = StormVolumeMath.NoiseSize;
                byte[] voxels = StormNoise.Volume(size, StormVolumeMath.NoisePeriod, StormVolumeMath.NoiseOctaves, StormVolumeMath.NoiseSeed);
                noise = new Texture3D(size, size, size, TextureFormat.RGBA32, true)
                {
                    name = "curtain noise",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear
                };
                var colours = new Color32[voxels.Length];
                for (int i = 0; i < voxels.Length; i++) colours[i] = new Color32(voxels[i], voxels[i], voxels[i], 255);
                noise.SetPixels32(colours);
                noise.Apply(true, true);
                madeNoise = true;
            }
            ownNoise = noise;
            float[] v = StormVolumeMath.CylinderVertices(StormVolumeMath.Segments);
            var vertices = new Vector3[v.Length / 3];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
            mesh = new Mesh { name = "curtain cylinder" };
            mesh.vertices = vertices;
            mesh.triangles = StormVolumeMath.CylinderTriangles(StormVolumeMath.Segments);
            mesh.RecalculateBounds();
            pool = new Piece[PoolSize];
            for (int p = 0; p < PoolSize; p++)
            {
                var piece = new Piece();
                piece.Root = new GameObject("ReDefinition Atmospheres curtain " + p) { layer = layer };
                piece.Transform = piece.Root.transform;
                piece.Material = new Material(shader) { name = "ReDefinition Atmospheres curtain", renderQueue = 2995 };
                piece.Material.SetTexture(IdNoise, ownNoise);
                piece.Material.EnableKeyword("STORM_DEPTH");
                piece.Material.SetVector(IdVeilTint, new Vector4(0f, 0f, 0f, 0f));
                piece.Material.SetFloat(IdSnowBoost, 1f);
                piece.Root.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = piece.Root.AddComponent<MeshRenderer>();
                r.sharedMaterial = piece.Material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                piece.Root.SetActive(false);
                pool[p] = piece;
            }
            return true;
        }

        internal bool Ready { get { return pool != null; } }

        internal void HideAll()
        {
            shown = 0;
            if (pool == null) return;
            for (int p = 0; p < pool.Length; p++)
                if (pool[p].Root.activeSelf) pool[p].Root.SetActive(false);
        }

        internal void Dispose()
        {
            HideAll();
            if (pool != null)
            {
                for (int p = 0; p < pool.Length; p++)
                {
                    UnityEngine.Object.Destroy(pool[p].Root);
                    UnityEngine.Object.Destroy(pool[p].Material);
                }
            }
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (madeNoise && ownNoise != null) UnityEngine.Object.Destroy(ownNoise);
            pool = null;
        }

        // Takes the gate share S of this frame (0 where the curtain is not drawn; VolumePass computes it from the level with the hysteresis
        // of the veil's gate) and sets the step count from it.
        internal void SetShare(float gateShare, bool gateOn)
        {
            GateOn = gateOn;
            share = gateOn ? gateShare : 0f;
            steps = VolumeMath.CurtainSteps(share);
        }

        // Puts a piece on a core. position is the world position of the ground under the core's centre, up the local vertical, right a
        // horizontal axis, baseAgl the cloud base above that ground, radius the core's radius, m.
        internal void Place(int slot, Vector3 position, Vector3 up, Vector3 right, float baseAgl, float radius, float curvature, double planetRadius)
        {
            if (pool == null || slot < 0 || slot >= pool.Length) return;
            Piece p = pool[slot];
            float below = StormVolumeMath.BelowM(radius, planetRadius);
            float top = StormVolumeMath.TopM(baseAgl);
            p.Position = position;
            p.BaseAgl = baseAgl;
            p.Radius = radius;
            p.BelowM = below;
            Quaternion rotation = Quaternion.LookRotation(Vector3.Cross(right, up), up);
            p.Transform.SetPositionAndRotation(position, rotation);
            p.Transform.localScale = new Vector3(radius * StormVolumeMath.Widening, below + top, radius * StormVolumeMath.Widening);
            // The cylinder's origin is its bottom: move it down by BelowM along the local vertical.
            p.Transform.position = position - up * below;
            Material m = p.Material;
            m.SetVector(IdCell, new Vector4(position.x, position.y, position.z, radius));
            m.SetVector(IdUp, new Vector4(up.x, up.y, up.z, curvature));
            m.SetVector(IdRight, new Vector4(right.x, right.y, right.z, 0f));
            m.SetVector(IdHeights, new Vector4(baseAgl, top, below, BaseDark * (float)Mathx.SmoothStep(0.35, 0.8, share)));
            if (!p.Root.activeSelf) p.Root.SetActive(true);
            if (slot + 1 > shown) shown = slot + 1;
        }

        // Light, wind and the camera-dependent numbers of the pieces in use; called once a frame after the places are set.
        internal void Light(Vector3 towardsSun, Color sunColour, Color ambientColour, float seconds, float yawDegrees, float longestM, int layer)
        {
            if (pool == null) return;
            float fade = (float)Mathx.SmoothStep(0, 0.35, share);
            float offsetX, offsetZ, shearX, shearZ, fall;
            StormVolumeMath.Wind(yawDegrees, seconds, out offsetX, out offsetZ, out shearX, out shearZ, out fall);
            for (int p = 0; p < pool.Length; p++)
            {
                Piece piece = pool[p];
                if (!piece.Root.activeSelf) continue;
                if (piece.Root.layer != layer) piece.Root.layer = layer;
                Material m = piece.Material;
                m.SetVector(IdWind, new Vector4(offsetX, offsetZ, shearX, fall));
                m.SetVector(IdLook, new Vector4(fade, steps, shearZ, longestM));
                m.SetVector(IdSun, new Vector4(towardsSun.x, towardsSun.y, towardsSun.z, 0f));
                m.SetVector(IdSunColour, sunColour);
                m.SetVector(IdAmbient, ambientColour);
            }
        }

        // Hides the pieces from slot onward (the cores this frame are fewer than the pool).
        internal void HideFrom(int slot)
        {
            if (pool == null) return;
            for (int p = Math.Max(0, slot); p < pool.Length; p++)
                if (pool[p].Root.activeSelf) pool[p].Root.SetActive(false);
            shown = Math.Min(shown, Math.Max(0, slot));
        }

        internal void CameraDepth(Camera camera)
        {
            if (camera != null) camera.depthTextureMode |= DepthTextureMode.Depth;
        }
    }
}
