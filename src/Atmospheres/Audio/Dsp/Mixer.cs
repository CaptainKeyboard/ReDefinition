using System;

namespace ReDefinition.Atmospheres.Audio
{
    // FROZEN INTERFACE (for the runtime package D33): Process(float[], int, int, in AudioParams) is the one call of the audio
    // thread. The weather mixer: wind bed (synthesised, recorded or both), howl, snow hiss or grit, hull ticks, distant rumble and
    // quiet ambience, an interior muffler and a soft limiter. Sample-rate agnostic; the output does not depend on the block size
    // (the gust and the master volume step per sample, everything else and every filter coefficient on a fixed 32-sample grid
    // counted from the first sample); deterministic by seed; no allocation after construction; no UnityEngine.
    //
    // Technique: filtered-noise synthesis after J. Strout ("Procedural Audio in Unity", Game Developer), the AudioGaming
    // procedural audio tutorial and A. Farnell ("Designing Sound"); biquads after the Audio EQ Cookbook.
    public sealed class Mixer
    {
        // The most the output can reach: the soft limiter's ceiling, -3.1 dBFS.
        public const float Ceiling = 0.7f;
        // Filter coefficients and the slow parameters are renewed every this many samples (a power of two).
        public const int ControlInterval = 32;

        // Level constants (linear, before the master volume). [VERIFY] by listening: the owner's pass (T5) tunes them.
        private const double WindGain = 0.8;
        private const double HowlGain = 4.0;
        private const double HissGain = 0.35;
        private const double GritGain = 0.45;
        private const double TickGain = 0.5;
        private const double RumbleGain = 1.0;
        private const double QuietGain = 0.08;
        private const double RecordedTarget = 0.15;

        private readonly int sampleRate;
        private readonly WindSynth wind;
        private readonly HowlSynth howl;
        private readonly SnowSynth snow;
        private readonly GritSynth grit;
        private readonly HullTicks ticks;
        private readonly RumbleSynth rumble;
        private readonly QuietSnowTone quiet;
        private readonly Biquad subHighL = new Biquad(), subHighR = new Biquad();
        private readonly Biquad inL1 = new Biquad(), inL2 = new Biquad(), inR1 = new Biquad(), inR2 = new Biquad();
        private readonly Biquad tickInL = new Biquad(), tickInR = new Biquad();
        private readonly Biquad recLowL = new Biquad(), recLowR = new Biquad();

        // Per-sample smoothers.
        private Smoother sGust, sMaster;
        // Control-rate smoothers (stepped once per ControlInterval samples).
        private Smoother sSpeed, sDepth, sIntensity, sGrit, sInterior, sCloseness, sPan, sWind, sPrecip, sTicks, sRumble, sQuiet,
            sSynthWeight, sRecordedWeight;

        // The targets of the current Process call.
        private double tSpeed, tDepth, tIntensity, tGrit, tInterior, tSnowCover, tCloseness, tPan, tVolWind, tVolPrecip, tVolTicks,
            tVolRumble, tVolQuiet, tWSynth, tWRec;
        private bool ticksOn;

        // The values the control step derives and the sample loop uses.
        private double cBed, cHowl, cHiss, cGrit, cRumble, cQuiet, cTicks, cInterior, cWSynth, cWRec, cDepth, cIntensity;

        private float[] recorded;
        private double recordedStep;
        private double recordedScale;
        private double recordedPos;
        private double recordedSpeed = double.NaN;

        private long counter;

        public Mixer(int sampleRate, uint seed)
        {
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException("sampleRate");
            this.sampleRate = sampleRate;
            wind = new WindSynth(sampleRate, seed);
            howl = new HowlSynth(sampleRate, seed);
            snow = new SnowSynth(sampleRate, seed);
            grit = new GritSynth(sampleRate, seed);
            ticks = new HullTicks(sampleRate, seed);
            rumble = new RumbleSynth(sampleRate, seed);
            quiet = new QuietSnowTone(sampleRate, seed);
            subHighL.HighPass(sampleRate, 28.0, 0.707);
            subHighR.HighPass(sampleRate, 28.0, 0.707);
            inL1.LowPass(sampleRate, 800.0, 0.707);
            inL2.LowPass(sampleRate, 800.0, 0.707);
            inR1.LowPass(sampleRate, 800.0, 0.707);
            inR2.LowPass(sampleRate, 800.0, 0.707);
            tickInL.LowPass(sampleRate, 1800.0, 0.707);
            tickInR.LowPass(sampleRate, 1800.0, 0.707);
            sGust.Init(sampleRate, 0.04);
            sMaster.Init(sampleRate, 0.03);
            double controlRate = (double)sampleRate / ControlInterval;
            sSpeed.Init(controlRate, 0.25);
            sDepth.Init(controlRate, 0.25);
            sIntensity.Init(controlRate, 0.3);
            sGrit.Init(controlRate, 0.3);
            sInterior.Init(controlRate, 0.15);
            sCloseness.Init(controlRate, 1.0);
            sPan.Init(controlRate, 0.5);
            sWind.Init(controlRate, 0.03);
            sPrecip.Init(controlRate, 0.03);
            sTicks.Init(controlRate, 0.03);
            sRumble.Init(controlRate, 0.03);
            sQuiet.Init(controlRate, 5.0);
            sSynthWeight.Init(controlRate, 0.2);
            sRecordedWeight.Init(controlRate, 0.2);
        }

        public int SampleRate { get { return sampleRate; } }

        // Gives the mixer a looping mono texture of wind (for BedSource.Recorded and Mix). The mixer keeps a reference to the
        // array, which must not change while the mixer runs. Null removes it. Call between blocks, from the audio thread's side.
        public void SetRecordedBed(float[] mono, int textureSampleRate)
        {
            if (mono == null || mono.Length < 2 || textureSampleRate <= 0)
            {
                recorded = null;
                return;
            }
            double sum = 0.0;
            for (int i = 0; i < mono.Length; i++) sum += (double)mono[i] * mono[i];
            double rms = Math.Sqrt(sum / mono.Length);
            recordedScale = rms > 1e-9 ? RecordedTarget / rms : 0.0;
            recordedStep = (double)textureSampleRate / sampleRate;
            recordedPos = 0.0;
            recorded = mono;
        }

        // Fills frames x channels interleaved samples (channel 1: both channels averaged; more than 2: the others carry the average).
        public void Process(float[] interleaved, int frames, int channels, in AudioParams p)
        {
            if (interleaved == null) throw new ArgumentNullException("interleaved");
            if (channels < 1) throw new ArgumentOutOfRangeException("channels");
            if (frames < 0 || (long)frames * channels > interleaved.Length) throw new ArgumentOutOfRangeException("frames");

            tSpeed = Clamp(p.windSpeed, 0.0, 60.0);
            double gustWave = Clamp(p.gust, -1.0, 1.0);
            tDepth = Clamp(p.gustDepth, 0.0, 1.0);
            tIntensity = Clamp(p.intensity, 0.0, 1.0);
            tGrit = Clamp(p.grit, 0.0, 1.0);
            tInterior = Clamp(p.interior, 0.0, 1.0);
            tSnowCover = Clamp(p.snowCover, 0.0, 1.0);
            tCloseness = Clamp(p.cellCloseness, 0.0, 1.0);
            tPan = Clamp(p.cellPan, -1.0, 1.0);
            double master = Clamp(p.volume, 0.0, 1.0);
            tVolWind = Clamp(p.volWind, 0.0, 1.0);
            tVolPrecip = Clamp(p.volPrecip, 0.0, 1.0);
            tVolTicks = Clamp(p.volTicks, 0.0, 1.0);
            tVolRumble = Clamp(p.volRumble, 0.0, 1.0);
            tVolQuiet = Clamp(p.volQuiet, 0.0, 1.0);
            ticksOn = tVolTicks > 0.0;
            int source = p.bedSource;
            bool haveTexture = recorded != null && recordedScale > 0.0;
            tWSynth = 1.0;
            tWRec = 0.0;
            if (haveTexture)
            {
                if (source == BedSource.Recorded) { tWSynth = 0.0; tWRec = 1.0; }
                else if (source == BedSource.Mix) { tWSynth = 0.70710678; tWRec = 0.70710678; }
            }

            for (int f = 0; f < frames; f++)
            {
                if ((counter & (ControlInterval - 1)) == 0) Control();
                counter++;

                double g = sGust.Next(gustWave);
                double mst = sMaster.Next(master);
                double gm = 1.0 + cDepth * g;
                if (gm < 0.0) gm = 0.0;

                double l = 0.0, r = 0.0;
                float al, ar;

                double bedAmp = cBed * gm;
                if (bedAmp > 1e-6)
                {
                    if (cWSynth > 1e-4)
                    {
                        wind.Next(out al, out ar);
                        double a = bedAmp * WindGain * cWSynth;
                        l += al * a;
                        r += ar * a;
                    }
                    if (cWRec > 1e-4 && haveTexture)
                    {
                        double a = bedAmp * cWRec;
                        double s0 = ReadRecorded(recordedPos);
                        double s1 = ReadRecorded(recordedPos + recorded.Length * 0.5);
                        l += recLowL.Process(s0 * recordedScale) * a;
                        r += recLowR.Process(s1 * recordedScale) * a;
                    }
                    double howlAmp = cHowl * gm;
                    if (howlAmp > 1e-6)
                    {
                        howl.Next(out al, out ar);
                        double a = howlAmp * HowlGain;
                        l += al * a;
                        r += ar * a;
                    }
                }
                if (haveTexture)
                {
                    recordedPos += recordedStep;
                    if (recordedPos >= recorded.Length) recordedPos -= recorded.Length;
                }

                if (cHiss > 1e-6)
                {
                    if (cGrit < 0.999)
                    {
                        snow.Next(out al, out ar);
                        double a = cHiss * HissGain * (1.0 - cGrit);
                        l += al * a;
                        r += ar * a;
                    }
                    if (cGrit > 0.001)
                    {
                        grit.Next(out al, out ar);
                        double a = cHiss * GritGain * cGrit;
                        l += al * a;
                        r += ar * a;
                    }
                }

                if (cRumble > 1e-6)
                {
                    rumble.Next(out al, out ar);
                    double a = cRumble * RumbleGain;
                    l += subHighL.Process(al) * a;
                    r += subHighR.Process(ar) * a;
                }

                if (cQuiet > 1e-7)
                {
                    quiet.Next(out al, out ar);
                    l += al * cQuiet;
                    r += ar * cQuiet;
                }

                // The hull muffles the bed; the ticks become duller and louder.
                if (cInterior > 1e-4)
                {
                    double wetL = inL2.Process(inL1.Process(l)) * 0.45;
                    double wetR = inR2.Process(inR1.Process(r)) * 0.45;
                    l += (wetL - l) * cInterior;
                    r += (wetR - r) * cInterior;
                }

                ticks.Next(ticksOn ? cIntensity : 0.0, out al, out ar);
                double tl = al * cTicks;
                double tr = ar * cTicks;
                if (cInterior > 1e-4)
                {
                    double twl = tickInL.Process(tl) * 1.4;
                    double twr = tickInR.Process(tr) * 1.4;
                    tl += (twl - tl) * cInterior;
                    tr += (twr - tr) * cInterior;
                }
                l += tl;
                r += tr;

                l = Limit(l * mst);
                r = Limit(r * mst);
                int o = f * channels;
                if (channels == 1)
                {
                    interleaved[o] = (float)(0.5 * (l + r));
                }
                else
                {
                    interleaved[o] = (float)l;
                    interleaved[o + 1] = (float)r;
                    if (channels > 2)
                    {
                        float m = (float)(0.5 * (l + r));
                        for (int c = 2; c < channels; c++) interleaved[o + c] = m;
                    }
                }
            }
        }

        // The control step on the fixed grid: the slow parameters move towards their targets, the amplitudes and the filter
        // coefficients follow.
        private void Control()
        {
            double spd = sSpeed.Next(tSpeed);
            cDepth = sDepth.Next(tDepth);
            double inten = sIntensity.Next(tIntensity);
            cIntensity = inten;
            cGrit = sGrit.Next(tGrit);
            cInterior = sInterior.Next(tInterior);
            double close = sCloseness.Next(tCloseness);
            double pan = sPan.Next(tPan);
            double vWind = sWind.Next(tVolWind);
            double vPrecip = sPrecip.Next(tVolPrecip);
            cTicks = sTicks.Next(tVolTicks) * TickGain;
            double vRumble = sRumble.Next(tVolRumble);
            cWSynth = sSynthWeight.Next(tWSynth);
            cWRec = sRecordedWeight.Next(tWRec);

            double sn = spd / WindSynth.TopSpeedMs;
            sn = sn < 0.0 ? 0.0 : sn > 1.0 ? 1.0 : sn;
            // Powers of 1.5 as x times its root.
            cBed = sn * Math.Sqrt(sn) * vWind;
            cHowl = sn * sn * sn * Smooth(8.0, 14.0, spd) * vWind;
            cHiss = inten * Math.Sqrt(inten) * vPrecip;
            cRumble = close * Math.Sqrt(close) * vRumble;

            // Calm air over deep snow: the quiet ambience, faded in over seconds.
            double calm = (1.0 - Clamp(spd / 8.0, 0.0, 1.0)) * (1.0 - Clamp(inten * 5.0, 0.0, 1.0));
            cQuiet = sQuiet.Next(tSnowCover * calm * tVolQuiet) * QuietGain;

            wind.SetSpeed(spd);
            howl.SetControl(spd, sGust.Value);
            howl.Advance(ControlInterval);
            rumble.SetControl(close, pan, ControlInterval);
            if (recorded != null && cWRec > 1e-4 && !(Math.Abs(spd - recordedSpeed) < 0.01))
            {
                recordedSpeed = spd;
                double t = spd / WindSynth.TopSpeedMs;
                t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
                double fc = 400.0 + 3600.0 * t;
                recLowL.LowPass(sampleRate, fc, 0.707);
                recLowR.LowPass(sampleRate, fc, 0.707);
            }
            // Filters that can see silence lose their denormal tails here.
            subHighL.Flush(); subHighR.Flush();
            inL1.Flush(); inL2.Flush(); inR1.Flush(); inR2.Flush();
            tickInL.Flush(); tickInR.Flush();
            recLowL.Flush(); recLowR.Flush();
        }

        private double ReadRecorded(double pos)
        {
            double len = recorded.Length;
            while (pos >= len) pos -= len;
            int i = (int)pos;
            double frac = pos - i;
            int j = i + 1 >= recorded.Length ? 0 : i + 1;
            return recorded[i] + (recorded[j] - recorded[i]) * frac;
        }

        // The soft limiter: Ceiling x tanh(u) with u = x / Ceiling, as the rational form u (27 + u^2) / (27 + 9 u^2) up to |u| = 3 and
        // the ceiling beyond (it is monotone and flat at 3; its largest deviation from tanh is about 2 %). It costs a quarter of
        // Math.Tanh. A result too small to be a normal float is zero.
        private static double Limit(double x)
        {
            double u = x * (1.0 / Ceiling);
            double y;
            if (u >= 3.0) y = Ceiling;
            else if (u <= -3.0) y = -Ceiling;
            else
            {
                double u2 = u * u;
                y = Ceiling * u * (27.0 + u2) / (27.0 + 9.0 * u2);
            }
            return y < 1e-20 && y > -1e-20 ? 0.0 : y;
        }

        private static double Smooth(double edge0, double edge1, double x)
        {
            double t = (x - edge0) / (edge1 - edge0);
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (!(v >= lo)) return lo;
            return v > hi ? hi : v;
        }
    }
}
