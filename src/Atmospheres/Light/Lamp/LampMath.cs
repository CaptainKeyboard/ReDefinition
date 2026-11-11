using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // A three-component vector for the lamp maths, so that the maths stands without the engine (the unit tests and the weather player
    // link it as it is).
    internal struct LampVec
    {
        internal float X, Y, Z;

        internal LampVec(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static LampVec operator +(LampVec a, LampVec b)
        {
            return new LampVec(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static LampVec operator -(LampVec a, LampVec b)
        {
            return new LampVec(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static LampVec operator *(LampVec a, float s)
        {
            return new LampVec(a.X * s, a.Y * s, a.Z * s);
        }

        internal static float Dot(LampVec a, LampVec b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        internal float Length
        {
            get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); }
        }

        internal LampVec Normalized
        {
            get
            {
                float l = Length;
                return l > 1e-12f ? new LampVec(X / l, Y / l, Z / l) : new LampVec(0f, 0f, 1f);
            }
        }
    }

    // Which medium sources count. Each one is switched on or off on its own (the probe's flags lamp.src.fog, .precip, .cloud, .dust).
    internal struct LampSources
    {
        internal bool Fog, Precip, Cloud, Dust;

        internal static LampSources All
        {
            get { return new LampSources { Fog = true, Precip = true, Cloud = true, Dust = true }; }
        }
    }

    // The scattering coefficient of the air at one place, in 1/m, split by source. Total is their sum.
    internal struct LampSigma
    {
        internal float Fog, Precip, Cloud, Dust;

        internal float Total
        {
            get { return Fog + Precip + Cloud + Dust; }
        }
    }

    // Every look constant of the lamp scatter, with its default. LampScatter.ReadTuning fills it through Tuning.Get; the keys, the
    // defaults and the tuning ranges (proposed, not enforced) are listed there for E40.
    internal struct LampTuning
    {
        // The factor on each source's sigma.
        internal float FogGain, PrecipGain, CloudGain, DustGain;
        // The extinction of a cloud at density 1, in 1/m, and the density an EVE layer gives per unit of coverage.
        internal float CloudSigmaFull, EveCoverageDensity;
        // The share of the extinction that scatters (single-scattering albedo).
        internal float Albedo;
        // The Henyey-Greenstein anisotropy (forward scattering).
        internal float PhaseG;
        // The factor on the transmittance exponent (the multiple-scattering stand-in: thick air keeps the beam brighter for longer).
        internal float AttenScale;
        // Steps of the march; the spot's inner cone as a share of its outer half angle; the lens fade distance in metres.
        internal float Steps, SpotInner, NearFadeM;
        // The radiance scale of one lamp at intensity 1 and colour white, in scene units.
        internal float Gain;
        // The least sigma at which anything is drawn, and the distance from the camera within which a lamp counts, in metres.
        internal float MinSigma, ActiveRangeM;
        // The sigma slew: relative change per second, and the sigma below which the slew has its floor.
        internal float SigmaSlewPerS, SigmaSlewFloor;
        // The brightness lag and the fade in/out of a lamp entering or leaving the set, in seconds; the rescoring rate in Hz.
        internal float BrightnessLagS, FadeS, RescoreHz;
        // The discovery limits: least intensity, least range in metres.
        internal float MinIntensity, MinRangeM;
        // The depth of the flake glints (0 = smooth beam), and their size in metres.
        internal float GlintAmp, GlintSizeM;
        // The precipitation sigma at level 1 is the table of PrecipSigmaFull times this; the exponent of the level.
        internal float PrecipLevelExponent;
        // Sigma maximum (a bound for every source).
        internal float MaxSigma;

        // The tuning held to values the maths and the shader can take: an anisotropy of 1 or more would divide by zero in the phase
        // function and put infinity into an additive HDR target, so g stays below 0.95, and so on for every key.
        internal LampTuning Clamped()
        {
            LampTuning t = this;
            t.FogGain = Clamp(t.FogGain, 0f, 10f);
            t.PrecipGain = Clamp(t.PrecipGain, 0f, 10f);
            t.CloudGain = Clamp(t.CloudGain, 0f, 10f);
            t.DustGain = Clamp(t.DustGain, 0f, 10f);
            t.CloudSigmaFull = Clamp(t.CloudSigmaFull, 0f, 1f);
            t.EveCoverageDensity = Clamp(t.EveCoverageDensity, 0f, 1f);
            t.Albedo = Clamp(t.Albedo, 0f, 1f);
            t.PhaseG = Clamp(t.PhaseG, 0f, 0.95f);
            t.AttenScale = Clamp(t.AttenScale, 0f, 2f);
            t.Steps = Clamp(t.Steps, 4f, 16f);
            t.SpotInner = Clamp(t.SpotInner, 0.1f, 0.99f);
            t.NearFadeM = Clamp(t.NearFadeM, 0f, 5f);
            t.Gain = Clamp(t.Gain, 0f, 200f);
            t.MinSigma = Clamp(t.MinSigma, 1e-6f, 1f);
            t.ActiveRangeM = Clamp(t.ActiveRangeM, 1f, 2000f);
            t.SigmaSlewPerS = Clamp(t.SigmaSlewPerS, 0.01f, 100f);
            t.SigmaSlewFloor = Clamp(t.SigmaSlewFloor, 1e-4f, 1f);
            t.BrightnessLagS = Clamp(t.BrightnessLagS, 0f, 5f);
            t.FadeS = Clamp(t.FadeS, 0.01f, 5f);
            t.RescoreHz = Clamp(t.RescoreHz, 0.1f, 60f);
            t.MinIntensity = Clamp(t.MinIntensity, 0f, 100f);
            t.MinRangeM = Clamp(t.MinRangeM, 0f, 1000f);
            t.GlintAmp = Clamp(t.GlintAmp, 0f, 1f);
            t.GlintSizeM = Clamp(t.GlintSizeM, 0.05f, 50f);
            t.PrecipLevelExponent = Clamp(t.PrecipLevelExponent, 0.5f, 4f);
            t.MaxSigma = Clamp(t.MaxSigma, 0.001f, 2f);
            return t;
        }

        // A value held between a least and a largest; NaN becomes the least.
        private static float Clamp(float v, float min, float max)
        {
            if (float.IsNaN(v)) return min;
            return v < min ? min : (v > max ? max : v);
        }

        internal static LampTuning Defaults()
        {
            return new LampTuning
            {
                FogGain = 1f, PrecipGain = 1f, CloudGain = 1f, DustGain = 1f,
                CloudSigmaFull = 0.05f, EveCoverageDensity = 0.5f,
                Albedo = 0.95f, PhaseG = 0.6f, AttenScale = 0.6f,
                Steps = 10f, SpotInner = 0.7f, NearFadeM = 0.3f,
                Gain = 6f,
                MinSigma = 1e-4f, ActiveRangeM = 150f,
                SigmaSlewPerS = 1f, SigmaSlewFloor = 0.01f,
                BrightnessLagS = 0.1f, FadeS = 0.3f, RescoreHz = 5f,
                MinIntensity = 0.1f, MinRangeM = 2f,
                GlintAmp = 0.35f, GlintSizeM = 0.5f,
                PrecipLevelExponent = 1.3f,
                MaxSigma = 0.5f
            };
        }
    }

    // The pure numbers of the lamp scatter: the medium's sigma from its sources, the phase function, the slews, the choice of lamps and
    // the geometry of the beam. No engine type, no allocation in the per-frame functions, deterministic. The shader repeats BeamRadiance
    // (ReDefinitionAtmospheresLamp.shader); the tests and the player compare the two.
    internal static partial class LampMath
    {
        // Visibility is the distance at which a contrast of 2 % is left (Koschmieder): extinction = 3.912 / visibility.
        internal const float KoschmiederNumerator = 3.912f;

        // The extinction an ExponentialSquared fog of density d is given: the extinction a homogeneous medium of the same visibility
        // has. Unity's fog transmits exp(-(d r)^2), whose 2 % distance is sqrt(3.912) / d, so the equal-visibility extinction is
        // 3.912 / (sqrt(3.912) / d) = sqrt(3.912) d. (The local extinction 2 d^2 r grows with the distance from the camera; a lamp
        // beam lies in one place and needs one number.)
        internal static readonly float FogSigmaFactor = (float)Math.Sqrt(KoschmiederNumerator);

        // The extinction of fog density d, in 1/m; 0 for no fog.
        internal static float FogSigma(float expSquaredDensity)
        {
            return expSquaredDensity > 0f ? FogSigmaFactor * expSquaredDensity : 0f;
        }

        // The visibility an ExponentialSquared fog of density d gives, in m (infinite for no fog).
        internal static float FogVisibility(float expSquaredDensity)
        {
            return expSquaredDensity > 0f ? FogSigmaFactor / expSquaredDensity : float.PositiveInfinity;
        }

        // The extinction at level 1 of each precipitation type, in 1/m: flakes per cubic metre times the mean cross-section, as a
        // visibility of the type's heaviest fall (snow 50 m, sleet 80 m, rain 250 m, freezing rain 200 m, dust 70 m, sand 60 m, ash 55 m,
        // methane 130 m). Starting values for T1 [VERIFY against the look of the flakes of C22].
        internal static float PrecipSigmaFull(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow: return 0.078f;
                case PrecipType.Sleet: return 0.049f;
                case PrecipType.Rain: return 0.016f;
                case PrecipType.FreezingRain: return 0.020f;
                case PrecipType.Dust: return 0.056f;
                case PrecipType.Sand: return 0.065f;
                case PrecipType.Ash: return 0.071f;
                case PrecipType.Methane: return 0.030f;
                default: return 0f;
            }
        }

        // The extinction of the precipitation at a level from 0 to 1: the table times level^exponent (the flake count of C22 rises with
        // level^1.3 and the cross-section stays).
        internal static float PrecipSigma(PrecipType type, float level01, float exponent)
        {
            if (!(level01 > 0f)) return 0f;
            float full = PrecipSigmaFull(type);
            if (full <= 0f) return 0f;
            return full * (float)Math.Pow(Math.Min(level01, 1f), exponent);
        }

        // The extinction inside a cloud of the given density (0 to 1; the share of the volume's cloud water or the layer's coverage
        // times its type density), in 1/m.
        internal static float CloudSigma(float density01, float sigmaFull)
        {
            return density01 > 0f ? Math.Min(density01, 1f) * sigmaFull : 0f;
        }

        // The medium at one place. fogDensity is Unity's ExponentialSquared density, level01 and type the precipitation there,
        // cloudDensity01 the cloud density (0 to 1), dustSigma the body's constant haze in 1/m, bodyScale the body's scale of the whole
        // scatter (LightMediumRules.Density). A source that is off adds nothing; every source is bounded by MaxSigma.
        internal static LampSigma Sigma(ref LampTuning t, LampSources on, float fogDensity, PrecipType type, float level01, float cloudDensity01,
            float dustSigma, float bodyScale)
        {
            float scale = bodyScale > 0f ? bodyScale : 0f;
            var s = new LampSigma();
            if (on.Fog) s.Fog = Bound(FogSigma(fogDensity) * t.FogGain * scale, t.MaxSigma);
            if (on.Precip) s.Precip = Bound(PrecipSigma(type, level01, t.PrecipLevelExponent) * t.PrecipGain * scale, t.MaxSigma);
            if (on.Cloud) s.Cloud = Bound(CloudSigma(cloudDensity01, t.CloudSigmaFull) * t.CloudGain * scale, t.MaxSigma);
            if (on.Dust) s.Dust = Bound(dustSigma > 0f ? dustSigma * t.DustGain * scale : 0f, t.MaxSigma);
            return s;
        }

        private static float Bound(float v, float max)
        {
            if (float.IsNaN(v) || v < 0f) return 0f;
            return v > max ? max : v;
        }

        // The Henyey-Greenstein phase function for the cosine of the angle between the light's travel direction and the direction the
        // light is scattered to; integrates to 1 over the sphere.
        internal static float HenyeyGreenstein(float cosTheta, float g)
        {
            float g2 = g * g;
            float denom = 1f + g2 - 2f * g * cosTheta;
            return (1f - g2) / (4f * (float)Math.PI * denom * (float)Math.Sqrt(denom));
        }

        // The sigma a value moves to in dt seconds: at most SigmaSlewPerS of the larger of the current value and the floor per second,
        // so that a beam never pops and a rising medium still grows from zero.
        internal static float SlewSigma(float current, float target, float relPerS, float floor, float dt)
        {
            if (!(dt > 0f)) return current;
            float step = relPerS * Math.Max(current, floor) * dt;
            if (target > current) return Math.Min(target, current + step);
            return Math.Max(target, current - step);
        }

        // An exponential lag towards a target with time constant tau seconds.
        internal static float Lag(float current, float target, float tauS, float dt)
        {
            if (!(dt > 0f)) return current;
            if (!(tauS > 1e-4f)) return target;
            return current + (target - current) * (1f - (float)Math.Exp(-dt / tauS));
        }

        // The least sigma and the distance within which anything is drawn. False means: no command buffer, no copy of the depth, no
        // mesh (zero cost).
        internal static bool Worth(float maxSigma, float nearestLampDistanceM, ref LampTuning t)
        {
            return maxSigma >= t.MinSigma && nearestLampDistanceM <= t.ActiveRangeM;
        }

        // The lowbias32 integer mixer of Chris Wellons' hash-prospector (The Unlicense), as PrecipMath.Hash: it fills the glints' noise table.
        internal static uint LowBias32(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }

        // The value of the glints' noise table at a texel (wrapping at 256), 0 up to but not including 1: the same for the same texel.
        internal static float NoiseValue(int x, int y)
        {
            unchecked
            {
                uint h = LowBias32((uint)(x & 255) + 256u * (uint)(y & 255) + 0x9e3779b9U);
                return (h >> 8) * (1f / 16777216f);
            }
        }

        // The depth of the flake glints: the tuning depth times the precipitation level and times the share of the extinction that is the
        // precipitation's, so that a static fog or a cloud keeps a smooth beam (the noise scales with the level only).
        internal static float GlintDepth(float glintAmp, float level01, float precipSigma, float totalSigma)
        {
            if (!(totalSigma > 0f) || !(precipSigma > 0f) || !(level01 > 0f)) return 0f;
            float share = Math.Min(precipSigma / totalSigma, 1f);
            return Math.Max(0f, glintAmp) * Math.Min(level01, 1f) * share;
        }

        // The same decision with a hold: once drawing, the lamp scatter keeps drawing until the sigma falls below 0.8 of the least or the
        // nearest lamp is more than 1.1 times the range away, so that patchy snow or a lamp at the edge of the range does not switch the
        // buffers on and off.
        internal static bool WorthHolding(bool wasDrawing, float maxSigma, float nearestLampDistanceM, ref LampTuning t)
        {
            if (!wasDrawing) return Worth(maxSigma, nearestLampDistanceM, ref t);
            return maxSigma >= 0.8f * t.MinSigma && nearestLampDistanceM <= 1.1f * t.ActiveRangeM;
        }

        // The factor on the body's dust haze for the air at an altitude: the pressure there over the pressure at the surface (0 for a body
        // without air, and in vacuum), so that the haze is not drawn round a lamp in orbit.
        internal static float AirShare(double pressureHere, double pressureSurface)
        {
            if (!(pressureSurface > 0.0) || !(pressureHere > 0.0)) return 0f;
            double share = pressureHere / pressureSurface;
            return share >= 1.0 ? 1f : (float)share;
        }

        // --- The choice of lamps ---

        // The score of a lamp for the choice: intensity x range^2 / (distance^2 + 1).
        internal static float Score(float intensity, float rangeM, float distanceSquaredM)
        {
            return intensity * rangeM * rangeM / (distanceSquaredM + 1f);
        }

        // Whether a Light is a lamp: a spot or point light (the caller says), intensity above the least and range above the least.
        internal static bool IsLamp(float intensity, float rangeM, ref LampTuning t)
        {
            return intensity > t.MinIntensity && rangeM > t.MinRangeM;
        }

        // --- The geometry of a beam ---

        // The ray from `origin` along the unit direction `dir` against a lamp at `pos` with unit axis `axis`, range `range` and, for a
        // spot, the cosine of the outer half angle (cosOuter <= 0 or a point light: the whole sphere). Gives the interval [t0, t1] of
        // the ray, clipped to [0, tMax], where the light can reach; false where there is none.
        internal static bool BeamInterval(LampVec origin, LampVec dir, float tMax, LampVec pos, LampVec axis, float range, float cosOuter,
            out float t0, out float t1)
        {
            t0 = 0f;
            t1 = 0f;
            LampVec w = origin - pos;
            // The sphere of the light's range.
            float b = LampVec.Dot(w, dir);
            float c = LampVec.Dot(w, w) - range * range;
            float disc = b * b - c;
            if (disc <= 0f) return false;
            float root = (float)Math.Sqrt(disc);
            float lo = Math.Max(0f, -b - root), hi = Math.Min(tMax, -b + root);
            if (!(hi > lo)) return false;
            if (!(cosOuter > 0f))
            {
                t0 = lo;
                t1 = hi;
                return true;
            }
            // The forward cone: (axis . x)^2 >= cos^2 |x|^2 with x = w + t dir, and axis . x >= 0.
            float c2 = cosOuter * cosOuter;
            float ad = LampVec.Dot(axis, dir), aw = LampVec.Dot(axis, w);
            float qa = ad * ad - c2;
            float qb = 2f * (aw * ad - c2 * LampVec.Dot(w, dir));
            float qc = aw * aw - c2 * LampVec.Dot(w, w);
            // Candidate intervals of f(t) = qa t^2 + qb t + qc >= 0.
            float aLo = 0f, aHi = 0f, bLo = 0f, bHi = 0f;
            bool haveA = false, haveB = false;
            if (Math.Abs(qa) < 1e-9f)
            {
                if (Math.Abs(qb) < 1e-12f)
                {
                    if (qc >= 0f)
                    {
                        aLo = lo;
                        aHi = hi;
                        haveA = true;
                    }
                }
                else if (qb > 0f)
                {
                    aLo = -qc / qb;
                    aHi = float.PositiveInfinity;
                    haveA = true;
                }
                else
                {
                    aLo = float.NegativeInfinity;
                    aHi = -qc / qb;
                    haveA = true;
                }
            }
            else
            {
                float d = qb * qb - 4f * qa * qc;
                if (d < 0f)
                {
                    // No root: f has the sign of qa everywhere.
                    if (qa > 0f)
                    {
                        aLo = float.NegativeInfinity;
                        aHi = float.PositiveInfinity;
                        haveA = true;
                    }
                }
                else
                {
                    float sq = (float)Math.Sqrt(d);
                    float r1 = (-qb - sq) / (2f * qa), r2 = (-qb + sq) / (2f * qa);
                    if (r1 > r2)
                    {
                        float tmp = r1;
                        r1 = r2;
                        r2 = tmp;
                    }
                    if (qa < 0f)
                    {
                        aLo = r1;
                        aHi = r2;
                        haveA = true;
                    }
                    else
                    {
                        aLo = float.NegativeInfinity;
                        aHi = r1;
                        haveA = true;
                        bLo = r2;
                        bHi = float.PositiveInfinity;
                        haveB = true;
                    }
                }
            }
            // The forward half: axis . (w + t dir) >= 0.
            float gLo = float.NegativeInfinity, gHi = float.PositiveInfinity;
            if (Math.Abs(ad) < 1e-9f)
            {
                if (aw < 0f) return false;
            }
            else if (ad > 0f) gLo = -aw / ad;
            else gHi = -aw / ad;
            float outLo = float.PositiveInfinity, outHi = float.NegativeInfinity;
            if (haveA) Clip(aLo, aHi, lo, hi, gLo, gHi, ref outLo, ref outHi);
            if (haveB) Clip(bLo, bHi, lo, hi, gLo, gHi, ref outLo, ref outHi);
            if (!(outHi > outLo)) return false;
            t0 = outLo;
            t1 = outHi;
            return true;
        }

        private static void Clip(float aLo, float aHi, float lo, float hi, float gLo, float gHi, ref float outLo, ref float outHi)
        {
            float l = Math.Max(Math.Max(aLo, lo), gLo), h = Math.Min(Math.Min(aHi, hi), gHi);
            if (!(h > l)) return;
            if (l < outLo) outLo = l;
            if (h > outHi) outHi = h;
        }

        // The light a lamp gives at a point: the range window, the falloff 1 / (1 + 25 (d / range)^2) that Unity's own light attenuation
        // approximates (so that the beam is as long as the ground the lamp lights), the spot's edge and the fade near the lens. dist is
        // the distance from the lamp, cosAngle the cosine of the angle to the axis.
        internal static float Emission(float dist, float cosAngle, float range, float cosOuter, float cosInner, float nearFadeM)
        {
            float ratio = dist / range;
            float r2 = ratio * ratio;
            float window = 1f - r2 * r2;
            if (window <= 0f) return 0f;
            window *= window;
            float spot = 1f;
            if (cosOuter > 0f)
            {
                float u = (cosAngle - cosOuter) / Math.Max(cosInner - cosOuter, 1e-4f);
                if (u <= 0f) return 0f;
                u = Math.Min(u, 1f);
                spot = u * u * (3f - 2f * u);
            }
            float near = nearFadeM > 0f ? Math.Min(dist / nearFadeM, 1f) : 1f;
            near = near * near * (3f - 2f * near);
            return window * spot * near / (1f + 25f * r2);
        }

        // The single-scattering radiance of one lamp along a ray, for an intensity-1 white lamp (the colour and the gain multiply it):
        // the integral from the start of the lamp's reach to its end (or the scene depth tMax) of
        // albedo x sigma x emission x phase x exp(-attenScale x (sigmaPath x t + sigma x d)),
        // with t the distance from the camera, d the distance from the lamp and sigmaPath the camera-side sigma. steps samples are
        // taken at the places jitter (0 to 1) shifts inside their cells. The shader does exactly this.
        internal static float BeamRadiance(LampVec origin, LampVec dir, float tMax, LampVec pos, LampVec axis, float range, float cosOuter,
            float sigma, float sigmaCamera, ref LampTuning t, int steps, float jitter)
        {
            if (!(sigma > 0f)) return 0f;
            float t0, t1;
            if (!BeamInterval(origin, dir, tMax, pos, axis, range, cosOuter, out t0, out t1)) return 0f;
            float cosInner = cosOuter > 0f ? (float)Math.Cos(Math.Acos(Math.Min(cosOuter, 1f)) * t.SpotInner) : 1f;
            float h = (t1 - t0) / steps;
            float sum = 0f;
            for (int i = 0; i < steps; i++)
            {
                float tt = t0 + h * (i + jitter);
                LampVec x = origin + dir * tt;
                LampVec l = x - pos;
                float d = l.Length;
                if (d < 1e-4f) continue;
                LampVec travel = l * (1f / d);
                float cosAngle = LampVec.Dot(travel, axis);
                float e = Emission(d, cosAngle, range, cosOuter, cosInner, t.NearFadeM);
                if (e <= 0f) continue;
                float phase = HenyeyGreenstein(LampVec.Dot(travel, dir * -1f), t.PhaseG);
                float tau = t.AttenScale * (sigmaCamera * tt + sigma * d);
                sum += e * phase * (float)Math.Exp(-tau);
            }
            return t.Albedo * sigma * sum * h;
        }
    }
}
