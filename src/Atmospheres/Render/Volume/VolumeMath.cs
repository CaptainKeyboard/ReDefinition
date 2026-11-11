using System;

namespace ReDefinition.Atmospheres
{
    // Reads one tuning value: the key, the compiled default. The game passes Tuning.Get; the weather player and the tests pass their own.
    internal delegate float TuningGet(string key, float compiledDefault);

    // The sizes the volume pass runs at for one quality tier.
    internal struct VolumeTier
    {
        // The march steps of a slab ray: 16, 32 or 64.
        public int Steps;
        // The march target's size over the camera's: 1, 0.5 or 0.25 (isotropic).
        public float Resolution;
        // The local window texture: cells along east, up and north, and its half-width in metres.
        public int WindowXz;
        public int WindowY;
        public float HalfWidthM;
        // The window is rebuilt at most every this many frames, and a rebuild is spread over this many frames.
        public int RebuildFrames;
        public int SpreadFrames;
        // Whether the curl noise is on, the number of taps towards the sun.
        public bool Curl;
        public int SunTaps;
    }

    // The pure numbers of the volume pass, so that they are tested without the game and linked into the weather player. Everything the
    // shader and the passes are fed with that is a function of a setting lives here; every look constant is a knob of VolumeKnobs.
    internal static class VolumeMath
    {
        internal const int StepsLow = 16, StepsMid = 32, StepsHigh = 64;
        internal const int CurtainStepsLow = 4, CurtainStepsMid = 8, CurtainStepsHigh = 16;
        // The curtain's share S at which the step count goes up (S in (0, 0.3), (0.3, 0.7), above).
        internal const float CurtainStepEdge1 = 0.3f, CurtainStepEdge2 = 0.7f;
        // The least ray length worth marching, in metres.
        internal const float MinRayM = 1f;
        // The window texture's largest side the pass accepts, per axis.
        internal const int MaxWindowCells = 128;

        // The march step count nearest to the wanted one among 16, 32, 64 (the shader's quality levels).
        internal static int SlabSteps(int wanted)
        {
            if (wanted <= (StepsLow + StepsMid) / 2) return StepsLow;
            if (wanted <= (StepsMid + StepsHigh) / 2) return StepsMid;
            return StepsHigh;
        }

        // The curtain's step count by its share S: 4 below 0.3, 8 up to 0.7, 16 above; 0 where S is 0 (the curtain is not drawn).
        // Separate from the slab's march quality.
        internal static int CurtainSteps(float share)
        {
            if (!(share > 0f)) return 0;
            if (share < CurtainStepEdge1) return CurtainStepsLow;
            if (share < CurtainStepEdge2) return CurtainStepsMid;
            return CurtainStepsHigh;
        }

        // The resolution factor the setting volumeResolution asks for, snapped to 1, 0.5 or 0.25; anything else is full resolution.
        internal static float SnapResolution(float wanted)
        {
            if (wanted >= 0.75f) return 1f;
            if (wanted >= 0.375f) return 0.5f;
            if (wanted > 0f) return 0.25f;
            return 1f;
        }

        // The tier table (index = the numeric value of QualityTier: 1 Low, 2 Medium, 3 High, 4 Ultra, 5 Max; 0 Auto is read as Medium).
        // Resolution stays 1 in the table: volumeResolution is its own setting that is 1.0 until the half-resolution gate has passed.
        internal static VolumeTier ForTier(int tier)
        {
            switch (tier)
            {
                case 1: return new VolumeTier { Steps = StepsLow, Resolution = 1f, WindowXz = 32, WindowY = 16, HalfWidthM = 40000f, RebuildFrames = 90, SpreadFrames = 8, Curl = false, SunTaps = 2 };
                // The rebuild is spread so that one frame's share stays near 1.5 ms on the main thread (the player's Mono measured about
                // 0.28 ms per 1000 voxels per rebuild [VERIFY in the game]).
                case 3: return new VolumeTier { Steps = StepsMid, Resolution = 1f, WindowXz = 64, WindowY = 32, HalfWidthM = 60000f, RebuildFrames = 90, SpreadFrames = 32, Curl = true, SunTaps = 3 };
                case 4: return new VolumeTier { Steps = StepsHigh, Resolution = 1f, WindowXz = 64, WindowY = 32, HalfWidthM = 60000f, RebuildFrames = 60, SpreadFrames = 32, Curl = true, SunTaps = 4 };
                case 5: return new VolumeTier { Steps = StepsHigh, Resolution = 1f, WindowXz = 96, WindowY = 48, HalfWidthM = 80000f, RebuildFrames = 120, SpreadFrames = 96, Curl = true, SunTaps = 4 };
                default: return new VolumeTier { Steps = StepsMid, Resolution = 1f, WindowXz = 48, WindowY = 24, HalfWidthM = 50000f, RebuildFrames = 90, SpreadFrames = 24, Curl = false, SunTaps = 2 };
            }
        }

        // The size of the march target for the camera's pixel size and the resolution factor (at least 16 by 16).
        internal static void TargetSize(int cameraWidth, int cameraHeight, float resolution, out int width, out int height)
        {
            float r = SnapResolution(resolution);
            width = Math.Max(16, (int)Math.Round(cameraWidth * r));
            height = Math.Max(16, (int)Math.Round(cameraHeight * r));
        }

        // The ray's interval inside the window's volume, the shader's tight interval: the cylinder of the window's radius around the
        // up axis through the centre (a, b, c of the horizontal quadratic), and the heights from lowH to highH above the centre along up
        // (the window's own curvature drop is allowed for by lowH being negative). Local frame: y is up, the ray starts at (ox, oy, oz)
        // with the direction (dx, dy, dz) of unit length. False where the ray misses; t0 and t1 are distances, t0 at least tMin.
        internal static bool ClipRay(float ox, float oy, float oz, float dx, float dy, float dz, float radius, float lowH, float highH,
                                     float tMin, float tMax, out float t0, out float t1)
        {
            t0 = tMin;
            t1 = tMax;
            float a = dx * dx + dz * dz;
            float b = ox * dx + oz * dz;
            float c = ox * ox + oz * oz - radius * radius;
            if (a > 1e-8f)
            {
                float disc = b * b - a * c;
                if (disc <= 0f) return false;
                float root = (float)Math.Sqrt(disc);
                t0 = Math.Max(t0, (-b - root) / a);
                t1 = Math.Min(t1, (-b + root) / a);
            }
            else if (c > 0f)
            {
                return false;
            }
            if (Math.Abs(dy) > 1e-5f)
            {
                float ta = (lowH - oy) / dy, tb = (highH - oy) / dy;
                t0 = Math.Max(t0, Math.Min(ta, tb));
                t1 = Math.Min(t1, Math.Max(ta, tb));
            }
            else if (oy < lowH || oy > highH)
            {
                return false;
            }
            return t1 - t0 > MinRayM;
        }

        // The height above the window's plane of a point at the horizontal distance r2 (squared) from the centre, with the planet's
        // curvature c = 1 / (2 R): the plane height plus the ground's fall.
        internal static float CurvedHeight(float planeHeight, float horizontalSquared, float curvature)
        {
            return planeHeight + curvature * horizontalSquared;
        }

        // The first step of a ray of length L marched in n steps that grow by the ratio g (g near 1 is uniform): dt0 * (g^n - 1) / (g - 1) = L.
        internal static float FirstStep(float length, int steps, float growth)
        {
            if (steps < 1) return length;
            if (growth < 1.0001f) return length / steps;
            double sum = (Math.Pow(growth, steps) - 1.0) / (growth - 1.0);
            return (float)(length / sum);
        }

        // The window's lookup coordinates for a local position: u and w across (0 to 1 over the full width), v up (0 to 1 over the height).
        internal static void WindowUv(float lx, float lz, float h, float halfWidth, float height, out float u, out float v, out float w)
        {
            float inv = 0.5f / halfWidth;
            u = lx * inv + 0.5f;
            w = lz * inv + 0.5f;
            v = h / height;
        }

        // The temporal frame offset of the dither keyed to the jitter index: the same index gives the same offset, consecutive indices
        // of an eight-phase jitter spread over the pattern (the golden-ratio step of the interleaved gradient noise).
        internal static float JitterKeyedOffset(int jitterIndex)
        {
            return 5.588238f * (jitterIndex % 64);
        }
    }

    // The tuning keys of the volume pass with their compiled defaults and ranges, read through Tuning.Get (A01). The same list goes to the
    // tuning package (E40).
    internal struct VolumeKnob
    {
        internal readonly string Key;
        internal readonly float Default, Min, Max;

        internal VolumeKnob(string key, float def, float min, float max)
        {
            Key = key;
            Default = def;
            Min = min;
            Max = max;
        }
    }

    // The look of the volume pass for one frame, read from the tuning table.
    internal struct VolumeLook
    {
        // The sun's share of the light, the sky's light, the extinction scale, the erosion depth, the dark base, the powder strength.
        public float SunShare, Ambient, Density, Erosion, BaseDark, Powder;
        // How high the anvil spreads, as a share of the core's depth, and how far (a factor on the core's radius).
        public float AnvilHeight, AnvilSpread;
        // The reference cloud water for a full byte of the window, g per m3; the curl noise's strength; the growth of the step.
        public float CloudWaterRef, CurlStrength, StepGrowth;
        // The noise tile of the erosion and of the base shape, m; the wind slant (m of shift per m of height).
        public float ErosionTileM, ShapeTileM, WindSlant;
        // The transmittance below which the march ends, and the deck's coverage multiplier.
        public float TransmittanceCut, DeckCoverage;

        // Every knob with its default and range; the tuning package lists the same.
        internal static readonly VolumeKnob[] Knobs =
        {
            new VolumeKnob("volume.sunShare", 1.3f, 0f, 2.5f),
            new VolumeKnob("volume.ambient", 0.9f, 0f, 2f),
            new VolumeKnob("volume.density", 0.25f, 0.05f, 2f),
            new VolumeKnob("volume.erosion", 0.6f, 0f, 0.9f),
            new VolumeKnob("volume.baseDark", 0.9f, 0f, 1f),
            new VolumeKnob("volume.powder", 1f, 0f, 2f),
            new VolumeKnob("volume.anvilHeight", 0.18f, 0.05f, 0.4f),
            new VolumeKnob("volume.anvilSpread", 1.8f, 1f, 3f),
            new VolumeKnob("volume.cloudWaterRef", 1.2f, 0.2f, 6f),
            new VolumeKnob("volume.curl", 0.35f, 0f, 1f),
            new VolumeKnob("volume.stepGrowth", 1.03f, 1f, 1.1f),
            new VolumeKnob("volume.erosionTileM", 700f, 150f, 3000f),
            new VolumeKnob("volume.shapeTileM", 9000f, 2000f, 30000f),
            new VolumeKnob("volume.windSlant", 0.25f, 0f, 1f),
            new VolumeKnob("volume.transmittanceCut", 0.03f, 0.005f, 0.2f),
            new VolumeKnob("volume.deckCoverage", 1f, 0f, 1.5f),
        };

        internal static VolumeLook Read(TuningGet get)
        {
            var look = new VolumeLook();
            look.SunShare = get("volume.sunShare", 1.3f);
            look.Ambient = get("volume.ambient", 0.9f);
            look.Density = get("volume.density", 0.25f);
            look.Erosion = get("volume.erosion", 0.6f);
            look.BaseDark = get("volume.baseDark", 0.9f);
            look.Powder = get("volume.powder", 1f);
            look.AnvilHeight = get("volume.anvilHeight", 0.18f);
            look.AnvilSpread = get("volume.anvilSpread", 1.8f);
            look.CloudWaterRef = get("volume.cloudWaterRef", 1.2f);
            look.CurlStrength = get("volume.curl", 0.35f);
            look.StepGrowth = get("volume.stepGrowth", 1.03f);
            look.ErosionTileM = get("volume.erosionTileM", 700f);
            look.ShapeTileM = get("volume.shapeTileM", 9000f);
            look.WindSlant = get("volume.windSlant", 0.25f);
            look.TransmittanceCut = get("volume.transmittanceCut", 0.03f);
            look.DeckCoverage = get("volume.deckCoverage", 1f);
            return look;
        }
    }
}
