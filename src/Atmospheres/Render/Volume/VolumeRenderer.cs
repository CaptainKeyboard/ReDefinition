using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Everything the shader needs for one frame that is not the look: where the window stands in the world, the light, the wind and the
    // camera's jitter. A plain object the caller keeps and rewrites each frame (no allocation).
    internal sealed class VolumeFrame
    {
        // The window's centre at the ground, world space, and its three axes (east, up, north); the planet's curvature, 1 / (2 R).
        public Vector3 Centre, Up = Vector3.up, East = Vector3.right, North = Vector3.forward;
        public float Curvature;
        // The window's half-width and height, m.
        public float HalfWidthM, HeightM;
        // The pattern's offset east and north (wrapped), the window's drift since its snapshot (east, north), m, and the pattern's velocity
        // in world space, m/s (the analytic motion).
        public Vector2 WindOffset, Drift;
        public Vector3 AdvectVelocity;
        // The light: towards the sun, the sun's colour (already scaled by the scene's brightness) and the sky's colour.
        public Vector3 TowardsSun = Vector3.up;
        public Color SunColour = Color.white, AmbientColour = Color.gray;
        // The march: steps (16, 32 or 64), the render scale (1, 0.5 or 0.25), the longest ray, the frame number, the upscaler's jitter index
        // and the jitter in render pixels.
        public int Steps = 32;
        public float Resolution = 1f;
        // Halves only the height of the march target (width stays full): measured once against the isotropic half, not a setting.
        public bool VerticalHalf;
        public float MaxDistanceM = 120000f;
        public int FrameNumber, JitterIndex;
        public Vector2 JitterPixels;
        // The sun taps of the tier.
        public int SunTaps = 3;
        // The shader variants.
        public bool Powder = true, Curl = true, Deck = true, KeyedDither = true;
        // The deck table: base and thickness above the ground for stratus, a cumulus layer and a high deck, and the cloud water that fills a deck.
        public Vector4 DeckA = new Vector4(700f, 700f, 1600f, 1600f), DeckB = new Vector4(5500f, 900f, 0f, 0f);
        public float DeckWater = 0.8f;
    }

    // The volume pass's drawing: the window texture and the deck map on the GPU, the march target at the chosen resolution, and the command
    // buffer that marches and composites (and, for the analytic motion and depth, the passes the host or the player calls). It uses nothing
    // of the game, so the weather player links it as it is. Nothing is allocated per frame once the target is made.
    internal sealed class VolumeRenderer
    {
        private static readonly int IdWindow = Shader.PropertyToID("_Window"), IdDeck = Shader.PropertyToID("_Deck"),
            IdNoise = Shader.PropertyToID("_Noise"), IdCell = Shader.PropertyToID("_Cell"), IdUp = Shader.PropertyToID("_Up"),
            IdRight = Shader.PropertyToID("_Right"), IdNorth = Shader.PropertyToID("_North"), IdWinSize = Shader.PropertyToID("_WinSize"),
            IdWind = Shader.PropertyToID("_Wind"), IdDens = Shader.PropertyToID("_Dens"), IdLight = Shader.PropertyToID("_Light"),
            IdLook = Shader.PropertyToID("_Look"), IdStep = Shader.PropertyToID("_Step"), IdDeckA = Shader.PropertyToID("_DeckA"),
            IdDeckB = Shader.PropertyToID("_DeckB"), IdSun = Shader.PropertyToID("_Sun"), IdSunColour = Shader.PropertyToID("_SunColour"),
            IdAmbient = Shader.PropertyToID("_AmbientColour"), IdCamRight = Shader.PropertyToID("_CamRight"),
            IdCamUp = Shader.PropertyToID("_CamUp"), IdCamForward = Shader.PropertyToID("_CamForward"),
            IdHitTex = Shader.PropertyToID("_HitTex"), IdCloudTex = Shader.PropertyToID("_CloudTex"),
            IdSceneMotion = Shader.PropertyToID("_SceneMotion"), IdSceneDepth = Shader.PropertyToID("_SceneDepth"),
            IdAdvect = Shader.PropertyToID("_AdvectVel"), IdCur = Shader.PropertyToID("_CurViewProj"), IdPrev = Shader.PropertyToID("_PrevViewProj"),
            IdGate = Shader.PropertyToID("_Gate");

        // The extinction at full density, per m (the cloud's optical thickness: 100 m of core is opaque).
        internal const float Sigma = 0.010f;

        private const string KeywordPowder = "VOLUME_POWDER", KeywordCurl = "VOLUME_CURL", KeywordDeck = "VOLUME_DECK",
            KeywordJitter = "VOLUME_JITTER_IGN";

        private Material material;
        private Texture3D windowTexture, noiseTexture;
        private Texture2D deckTexture;
        private RenderTexture target, hit;
        private CommandBuffer commands;
        private Camera attached;
        private CameraEvent attachedEvent;
        private int uploadedBuilds = -1;
        private bool madeNoise;

        internal string Problem { get; private set; }
        internal bool Ready { get { return material != null && windowTexture != null; } }
        // The march target, valid after the first Prepare (the player reads it for the analytic motion pass).
        internal RenderTexture Target { get { return target; } }
        internal RenderTexture Hit { get { return hit; } }
        internal CommandBuffer Commands { get { return commands; } }

        // Makes the material and the textures. noise is the 64^3 tileable noise of the storm volume (null: made here from StormNoise).
        internal bool Init(Shader shader, int windowAcross, int windowUp, Texture3D noise)
        {
            if (shader == null)
            {
                Problem = "the volume shader is missing";
                return false;
            }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (material.passCount < 5)
            {
                Problem = "the volume shader does not compile (" + material.passCount + " passes)";
                return false;
            }
            windowTexture = new Texture3D(windowAcross, windowUp, windowAcross, TextureFormat.RGBA32, false)
            {
                name = "volume window",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            deckTexture = new Texture2D(windowAcross, windowAcross, TextureFormat.RG16, false)
            {
                name = "volume deck",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            if (noise == null)
            {
                int size = CurtainMath.NoiseSize;
                byte[] voxels = StormNoise.Volume(size, CurtainMath.NoisePeriod, CurtainMath.NoiseOctaves, CurtainMath.NoiseSeed);
                noise = new Texture3D(size, size, size, TextureFormat.RGBA32, true)
                {
                    name = "volume noise",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear
                };
                var colours = new Color32[voxels.Length];
                for (int i = 0; i < voxels.Length; i++) colours[i] = new Color32(voxels[i], voxels[i], voxels[i], 255);
                noise.SetPixels32(colours);
                noise.Apply(true, true);
                madeNoise = true;
            }
            noiseTexture = noise;
            material.SetTexture(IdWindow, windowTexture);
            material.SetTexture(IdDeck, deckTexture);
            material.SetTexture(IdNoise, noiseTexture);
            commands = new CommandBuffer { name = "ReDefinition volume pass" };
            return true;
        }

        // Puts the buffer on the camera at that event.
        internal void Attach(Camera camera, CameraEvent cameraEvent)
        {
            Detach();
            if (camera == null || commands == null) return;
            camera.AddCommandBuffer(cameraEvent, commands);
            attached = camera;
            attachedEvent = cameraEvent;
        }

        internal void Detach()
        {
            if (attached != null && commands != null) attached.RemoveCommandBuffer(attachedEvent, commands);
            attached = null;
        }

        internal void Dispose()
        {
            Detach();
            if (commands != null) commands.Release();
            if (target != null) target.Release();
            if (hit != null) hit.Release();
            UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(windowTexture);
            UnityEngine.Object.Destroy(deckTexture);
            if (madeNoise) UnityEngine.Object.Destroy(noiseTexture);
            material = null;
            windowTexture = null;
        }

        // Uploads the resampler's voxels and deck map after a completed rebuild.
        internal void Upload(VolumeWindowResampler resampler)
        {
            if (!Ready || resampler.Builds == uploadedBuilds || !resampler.HasData) return;
            windowTexture.SetPixelData(resampler.Data, 0);
            windowTexture.Apply(false);
            deckTexture.SetPixelData(resampler.DeckMap, 0);
            deckTexture.Apply(false);
            uploadedBuilds = resampler.Builds;
        }

        private void EnsureTarget(Camera camera, float resolution, bool verticalHalf)
        {
            int w, h;
            VolumeMath.TargetSize(camera.pixelWidth, camera.pixelHeight, resolution, out w, out h);
            if (verticalHalf)
            {
                VolumeMath.TargetSize(camera.pixelWidth, camera.pixelHeight, 1f, out w, out h);
                h = Math.Max(16, h / 2);
            }
            if (target != null && target.width == w && target.height == h) return;
            if (target != null) target.Release();
            if (hit != null) hit.Release();
            target = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "volume march",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            target.Create();
            hit = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "volume hit",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            hit.Create();
        }

        private void SetKeyword(string keyword, bool on)
        {
            if (on) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        // Writes the frame's numbers and records the march and the composite into the buffer. markStart/markEnd (may be null) are called
        // around the passes so that the player can time them.
        internal void Prepare(Camera camera, VolumeFrame f, VolumeLook look, Action<CommandBuffer> markStart, Action<CommandBuffer> markMarched,
                              Action<CommandBuffer> markEnd, bool analytic)
        {
            if (!Ready) return;
            EnsureTarget(camera, f.Resolution, f.VerticalHalf);
            Material m = material;
            SetKeyword(KeywordPowder, f.Powder);
            SetKeyword(KeywordCurl, f.Curl);
            SetKeyword(KeywordDeck, f.Deck);
            SetKeyword(KeywordJitter, f.KeyedDither);
            m.SetVector(IdCell, new Vector4(f.Centre.x, f.Centre.y, f.Centre.z, f.HalfWidthM));
            m.SetVector(IdUp, new Vector4(f.Up.x, f.Up.y, f.Up.z, f.Curvature));
            m.SetVector(IdRight, new Vector4(f.East.x, f.East.y, f.East.z, 0f));
            m.SetVector(IdNorth, new Vector4(f.North.x, f.North.y, f.North.z, 0f));
            m.SetVector(IdWinSize, new Vector4(f.HeightM, look.ErosionTileM, look.ShapeTileM, look.WindSlant));
            m.SetVector(IdWind, new Vector4(f.WindOffset.x, f.WindOffset.y, f.Drift.x, f.Drift.y));
            m.SetVector(IdDens, new Vector4(Sigma * look.Density, look.Erosion, look.CurlStrength, look.Powder));
            m.SetVector(IdLight, new Vector4(look.SunShare, look.Ambient, look.BaseDark, look.TransmittanceCut));
            m.SetVector(IdLook, new Vector4(VolumeMath.SlabSteps(f.Steps), f.MaxDistanceM, f.FrameNumber, f.JitterIndex));
            m.SetVector(IdStep, new Vector4(look.StepGrowth, f.SunTaps, look.DeckCoverage, f.DeckWater));
            m.SetVector(IdDeckA, f.DeckA);
            m.SetVector(IdDeckB, f.DeckB);
            m.SetVector(IdSun, new Vector4(f.TowardsSun.x, f.TowardsSun.y, f.TowardsSun.z, 0f));
            m.SetVector(IdSunColour, f.SunColour);
            m.SetVector(IdAmbient, f.AmbientColour);
            float tanY = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * camera.aspect;
            Transform t = camera.transform;
            Vector3 right = t.right * tanX, up = t.up * tanY;
            m.SetVector(IdCamRight, new Vector4(right.x, right.y, right.z, 2f * f.JitterPixels.x / camera.pixelWidth));
            m.SetVector(IdCamUp, new Vector4(up.x, up.y, up.z, 2f * f.JitterPixels.y / camera.pixelHeight));
            Vector3 forward = t.forward;
            m.SetVector(IdCamForward, new Vector4(forward.x, forward.y, forward.z, 0f));
            m.SetVector(IdAdvect, new Vector4(f.AdvectVelocity.x, f.AdvectVelocity.y, f.AdvectVelocity.z, 0.35f));

            commands.Clear();
            if (markStart != null) markStart(commands);
            commands.Blit(BuiltinRenderTextureType.None, target, m, 0);
            if (markMarched != null) markMarched(commands);
            commands.Blit(target, BuiltinRenderTextureType.CameraTarget, m, 1);
            if (markEnd != null) markEnd(commands);
            // The hit distance for the analytic motion and depth comes after the timed passes: it belongs to the motion path.
            if (analytic) commands.Blit(BuiltinRenderTextureType.None, hit, m, 2);
        }

        // The analytic motion and depth, laid over the scene's: where the cloud covers a pixel it takes the motion and depth of the point
        // where its ray meets the cloud. Recorded into command (the host's post-upscale hook or the player's pass). The matrices are the
        // unjittered view-projection now and before, dt is the frame time and flip turns the motion's signs.
        internal void AppendAnalytic(CommandBuffer command, RenderTargetIdentifier motionIn, RenderTargetIdentifier motionOut,
                                     RenderTargetIdentifier depthIn, RenderTargetIdentifier depthOut, Matrix4x4 current, Matrix4x4 previous,
                                     float dt, bool flip)
        {
            if (!Ready || target == null) return;
            material.SetMatrix(IdCur, current);
            material.SetMatrix(IdPrev, previous);
            material.SetVector(IdGate, new Vector4(dt, flip ? 1f : 0f, 0f, 0f));
            command.SetGlobalTexture(IdHitTex, hit);
            command.SetGlobalTexture(IdCloudTex, target);
            command.SetGlobalTexture(IdSceneMotion, motionIn);
            command.SetGlobalTexture(IdSceneDepth, depthIn);
            command.Blit(motionIn, motionOut, material, 3);
            command.Blit(depthIn, depthOut, material, 4);
        }
    }
}
