using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the start soft60 (E40, the clouds' work after start 59; plan and review: .local/work/e40-start60-plan-review.md and e40-start60-review.md). The scenario is soft59's
    // (T09ProbeSoft.cs: both scenes, the baseline and the variants of a table per view) with another table and three additions:
    //   1. the weave diagnosis: in the fair ground view the variants soft, softr33 and the history / dither candidates read EIGHT consecutive frames (full size) and ref256 (a march of 256
    //      steps) the converged picture they are measured against (tools/weather_metrics/e40s60.py: the static-pattern measure of E1 on game frames);
    //   2. the extra looks: horizon (far view, haze), fog (fair weather, inside the fog deck) and rain (under the deck, the clock scene): one frame set each, with a baseline frame;
    //   3. the views placed by the type map: the probe reads the own layers' map and type at a ring of places around the site (Game.ReadLayers) and moves the vessel so that the
    //      nearest cell of cumulus humilis, cumulus congestus, cumulonimbus and cirrus is at the centre of the view, from the ground and from above, three frames each. A type that
    //      is absent within 300 km is logged and counted (T09s.types_absent), never a failure.
    // Every view also writes the light rows of the variant soft (SoftLightRows): the sunlit side and the base of the cloud pixels, and the deck against the sky of the baseline.
    internal sealed partial class T09Probe
    {
        private bool Soft60Start
        {
            get { return StartId != null && StartId.StartsWith("soft60", StringComparison.Ordinal) && !IsDiag && !IsK3; }
        }

        // --- The variant table -------------------------------------------------------------------------------------------------------------

        private static SoftVariant Soft60(string name, string fairCost, string clockCost, int frames, params string[] pairs)
        {
            SoftVariant v = Soft(name, fairCost, clockCost, pairs);
            v.Frames = frames;
            return v;
        }

        // g ground, i inside, a above, s sky3. WeaveFrames: the consecutive frames in the fair ground view (the weave diagnosis: the mean of eight against the converged picture).
        //   soft        the defaults: cost in every view, the grain trio, eight frames in the fair ground view (the weave of the default)
        //   softr33     recon.resolution 0.33 and cloudSkipEmpty 1 (the cost candidate that came closest to TVC): cost in every view, one frame, eight in the fair ground view
        //   softcut16   the cut span, 16 points, target 0.33: cost at the fair and the clock ground
        //   soft100s24  cloudSoftness 1.0, cloudSamples Low (24 points): the owner's second favourite, cost at the fair ground
        //   softhash    layers.ditherSequence 1 (the white hash): the weave check, eight frames, no cost
        //   softacc10   recon.accumulate 0.1 (a history of about 10 frames), softacc05 0.05 (about 20): the history's share in the static pattern; the history needs frames to settle
        //   ref256      layers.gridFine 5.3333 = 256 grid points: the converged picture of the pose, the reference of the static-pattern measure (the 48-step bias leaves with the high-pass)
        // (There is no key for the history's clip: ReconSettings.ClipSigma is no tuning key; the player series e40-wv-temporal measured clip1000 and found no case separated from the default.)
        internal static readonly SoftVariant[] Soft60Variants =
        {
            Soft60("soft", "ga", "gias", 3).Weave(8),
            Soft60("softr33", "ga", "gias", 1, "t:" + OwnCloudOptions.TuneReconResolution + "=0.33", "s:" + AtmospheresSettings.Keys.CloudSkipEmpty + "=1").Weave(8),
            Soft60("softcut16", "g", "g", 1, "t:" + OwnCloudOptions.TuneSpanTight + "=1", "t:" + OwnCloudOptions.TuneGridFine + "=0.3333", "t:" + OwnCloudOptions.TuneReconResolution + "=0.33").In("g", "g"),
            Soft60("soft100s24", "g", "", 1, "s:" + AtmospheresSettings.Keys.CloudSoftness + "=1", "s:" + AtmospheresSettings.Keys.CloudSamples + "=0").In("g", "-"),
            Soft60("softhash", "", "", 1, "t:" + OwnCloudOptions.TuneDitherSequence + "=1").Weave(8).In("g", "-"),
            Soft60("softacc10", "", "", 1, "t:" + OwnCloudOptions.TuneAccumulate + "=0.1").Weave(8).Settle(60).Extra(1.5).In("g", "-"),
            Soft60("softacc05", "", "", 1, "t:" + OwnCloudOptions.TuneAccumulate + "=0.05").Weave(8).Settle(120).Extra(3.0).In("g", "-"),
            Soft60("ref256", "", "", 1, "t:" + OwnCloudOptions.TuneGridFine + "=5.3333").Weave(8).Extra(4.0).In("g", "-")
        };

        // The extra looks have the variant soft alone and no cost window.
        internal static readonly SoftVariant[] Soft60ExtraTable = { Soft60("soft", "", "", 3) };

        // Far view and horizon haze (the horizon in the middle of the picture), the fog deck of a fair day (inside it: the deck stands from the ground to 210 m), rain under the deck.
        internal static readonly View[] Soft60FairExtras =
        {
            new View { Name = "horizon", AglM = 200.0, PitchDeg = 2.0, HeadingDeg = 90.0, CameraDistM = 100.0 },
            new View { Name = "fog", AglM = 100.0, PitchDeg = 0.5, HeadingDeg = 90.0, CameraDistM = 100.0 }
        };

        internal static readonly View[] Soft60ClockExtras =
        {
            new View { Name = "rain", AglM = 200.0, PitchDeg = 5.0, HeadingDeg = 90.0, CameraDistM = 100.0 }
        };

        // --- The plan ----------------------------------------------------------------------------------------------------------------------

        // The type scan reads the maps at SoftScanCells places, 24 a frame; the plan counts the seconds it holds the main thread over.
        internal const double Soft60ScanS = 1.5;
        // After the frames of the baseline of a type view (the mask settles a few frames).
        internal const double Soft60BaselineS = 1.0;

        // The seconds one type-placed view takes: the placement, the switch of the variant soft, its three frames and, for the cumulus bodies, the baseline frame.
        internal static double Soft60TypeViewS(Soft60Target t)
        {
            return SoftPlaceS + SoftSwitchS + 3.0 * SoftFrameReadS + (t.Light ? SoftFrameReadS + Soft60BaselineS : 0.0);
        }

        internal static double Soft60EstimateS(IList<SoftVariant> table)
        {
            double s = SoftPickCoverS + SoftPickClockS;
            foreach (char c in SoftFairViews) s += SoftEstimateViewS(true, c, table);
            foreach (char c in SoftClockViews) s += SoftEstimateViewS(false, c, table);
            s += Soft60Estimate(Soft60FairExtras.Length, true) + Soft60Estimate(Soft60ClockExtras.Length, false);
            foreach (Soft60Target t in Soft60Targets) s += 2.0 * Soft60TypeViewS(t);
            return s + 2.0 * Soft60ScanS;
        }

        private static double Soft60Estimate(int views, bool fair)
        {
            return views * SoftEstimateViewS(fair, '?', Soft60ExtraTable, false);
        }

        private void LogSoft60Plan()
        {
            var sb = new System.Text.StringBuilder();
            foreach (SoftVariant v in Soft60Variants)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(v.Name);
                SoftExpectation e = SoftExpect(v);
                sb.Append(" [softness ").Append(e.Softness.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(e.GridPoints).Append(" grid points, march target ").Append(e.Resolution.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append(", history ").Append(e.Accumulate.ToString("0.###", CultureInfo.InvariantCulture)).Append(", frames ").Append(v.Frames).Append("/").Append(v.WeaveFrames).Append(", in fair '").Append(v.FairViews)
                  .Append("' clock '").Append(v.ClockViews).Append("', cost fair '").Append(v.FairCost).Append("' clock '").Append(v.ClockCost).Append("']");
                string problem = v.TableProblem();
                if (problem != null) Check("T09s.table_" + v.Name, false, problem);
            }
            double estimate = Soft60EstimateS(Soft60Variants);
            AtmospheresLog.Say("T09 soft plan (soft60): " + sb + "; extra looks " + Soft60FairExtras.Length + " fair, " + Soft60ClockExtras.Length + " clock; type views " + 2 * Soft60Targets.Length + " at most; estimated "
                               + estimate.ToString("0") + " s of " + SoftMaxSeconds.ToString("0") + " s");
            EmitText("T09s.plan", sb.ToString());
            Emit("T09s.plan_estimate_s", estimate);
        }

        // --- The light rows ----------------------------------------------------------------------------------------------------------------

        // Linear luminance (Rec. 709) of a value the camera target held.
        internal static float SoftLuma(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        internal const float SoftLightCloudChange = 0.05f, SoftLightBodyChange = 0.15f;
        private const int SoftLightBins = 4096;
        private const float SoftLightMax = 4f;

        internal struct SoftLightStats
        {
            // The pixels the clouds changed against the baseline by 0.05 or more, and by 0.15 or more (the body: pixels the cloud mostly replaced).
            internal int Cloud, Body;
            // Luminance over the body pixels: the sunlit side (the 90th percentile), the base (the 10th) and the median; the baseline's median over the same pixels (the sky there).
            internal double TopLuma, BaseLuma, MedianLuma, SkyLuma;
            internal double CloudShare, BodyShare;
            // Top over base, and the body's median over the sky behind it.
            internal double TopOverBase { get { return BaseLuma > 1e-6 ? TopLuma / BaseLuma : double.NaN; } }
            internal double DeckOverSky { get { return SkyLuma > 1e-6 ? MedianLuma / SkyLuma : double.NaN; } }
        }

        private static double SoftPercentile(int[] bins, int total, double fraction)
        {
            if (total <= 0) return double.NaN;
            long target = (long)Math.Ceiling(fraction * total), seen = 0;
            for (int i = 0; i < bins.Length; i++)
            {
                seen += bins[i];
                if (seen >= target) return (i + 0.5) * SoftLightMax / bins.Length;
            }
            return SoftLightMax;
        }

        // The light of the cloud pixels of a frame: raw is the picture with the pass drawn, off the same pose with no layer drawn (T09Image.Raw of both). Percentiles from a histogram
        // of 4096 bins over 0 to 4 (a bin is 0.001 wide). False where the pictures differ in size or the clouds changed no pixel by the body threshold.
        internal static bool SoftLightOf(Color[] raw, Color[] off, out SoftLightStats stats)
        {
            stats = new SoftLightStats();
            if (raw == null || off == null || raw.Length != off.Length || raw.Length == 0) return false;
            var top = new int[SoftLightBins];
            var sky = new int[SoftLightBins];
            int cloud = 0, body = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                float d = Mathf.Max(Mathf.Abs(raw[i].r - off[i].r), Mathf.Max(Mathf.Abs(raw[i].g - off[i].g), Mathf.Abs(raw[i].b - off[i].b)));
                if (d >= SoftLightCloudChange) cloud++;
                if (d < SoftLightBodyChange) continue;
                body++;
                top[Mathf.Clamp((int)(SoftLuma(raw[i]) / SoftLightMax * SoftLightBins), 0, SoftLightBins - 1)]++;
                sky[Mathf.Clamp((int)(SoftLuma(off[i]) / SoftLightMax * SoftLightBins), 0, SoftLightBins - 1)]++;
            }
            stats.Cloud = cloud;
            stats.Body = body;
            stats.CloudShare = (double)cloud / raw.Length;
            stats.BodyShare = (double)body / raw.Length;
            if (body < 100) return false;
            stats.TopLuma = SoftPercentile(top, body, 0.90);
            stats.BaseLuma = SoftPercentile(top, body, 0.10);
            stats.MedianLuma = SoftPercentile(top, body, 0.50);
            stats.SkyLuma = SoftPercentile(sky, body, 0.50);
            return true;
        }

        // Rows and one line per view: the light of the variant soft in the game's own light (the player has no ambient sky). The ratio top over base is the proxy of the base / top check
        // of the light refit (a cumulus body: 2 to 6); deck over sky is the deck's median over the clear sky of the baseline at the same pixels.
        private void SoftLightRows(string head, Color[] off, Color[] raw, int w, int h)
        {
            SoftLightStats s;
            if (off == null || off.Length != w * h || !SoftLightOf(raw, off, out s))
            {
                AtmospheresLog.Say("T09 light " + head + ": no body pixels against the baseline (or no baseline): no light rows");
                return;
            }
            Emit(head + "light_cloud_share", s.CloudShare);
            Emit(head + "light_body_share", s.BodyShare);
            Emit(head + "light_top", s.TopLuma);
            Emit(head + "light_base", s.BaseLuma);
            Emit(head + "light_median", s.MedianLuma);
            Emit(head + "light_sky", s.SkyLuma);
            Emit(head + "light_top_over_base", s.TopOverBase);
            Emit(head + "light_deck_over_sky", s.DeckOverSky);
            AtmospheresLog.Say("T09 light " + head + ": cloud " + (100.0 * s.CloudShare).ToString("0.0", CultureInfo.InvariantCulture) + " percent of the picture, body " + (100.0 * s.BodyShare).ToString("0.0", CultureInfo.InvariantCulture)
                               + " percent; sunlit side (P90) " + s.TopLuma.ToString("0.000", CultureInfo.InvariantCulture) + ", base (P10) " + s.BaseLuma.ToString("0.000", CultureInfo.InvariantCulture) + ", top over base "
                               + s.TopOverBase.ToString("0.00", CultureInfo.InvariantCulture) + "; median " + s.MedianLuma.ToString("0.000", CultureInfo.InvariantCulture) + " over the baseline's sky "
                               + s.SkyLuma.ToString("0.000", CultureInfo.InvariantCulture) + " = " + s.DeckOverSky.ToString("0.00", CultureInfo.InvariantCulture));
        }

        // --- The type map ------------------------------------------------------------------------------------------------------------------

        // What a type view looks at: the layer and type it needs in the map, how strong the cover must be (Min: a cell the nearest of which is taken; Weak: the strongest cell at or above
        // it when none reaches Min), and the two views: from the ground (the pitch up, the height the view centre meets the cloud at) and from above (the height over the terrain,
        // the pitch down, the height of the cloud the centre meets). The heights are Kerbin's cfg: humilis 1.7 to 3 km, congestus to 7.4 km, cumulonimbus 1.7 to 11 km, cirrus 9 to 11 km.
        internal sealed class Soft60Target
        {
            internal string Name, LayerPart, TypePart;
            internal float MinCover, WeakCover;
            internal double GroundPitchDeg, GroundMidM, AboveAglM, AbovePitchDeg, AboveMidM;
            // The cumulus bodies get a baseline frame (the light rows need one).
            internal bool Light;

            internal bool Matches(string layerName, string typeName)
            {
                return layerName != null && typeName != null && layerName.IndexOf(LayerPart, StringComparison.OrdinalIgnoreCase) >= 0 && typeName.IndexOf(TypePart, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        internal static readonly Soft60Target[] Soft60Targets =
        {
            new Soft60Target { Name = "humilis", LayerPart = "cumulus", TypePart = "humilis", MinCover = 0.55f, WeakCover = 0.30f, GroundPitchDeg = 20.0, GroundMidM = 2300.0, AboveAglM = 8300.0, AbovePitchDeg = 25.0, AboveMidM = 2300.0, Light = true },
            new Soft60Target { Name = "congestus", LayerPart = "cumulus", TypePart = "congestus", MinCover = 0.55f, WeakCover = 0.30f, GroundPitchDeg = 25.0, GroundMidM = 4500.0, AboveAglM = 10500.0, AbovePitchDeg = 25.0, AboveMidM = 5000.0, Light = true },
            new Soft60Target { Name = "cumulonimbus", LayerPart = "cumulonimbus", TypePart = "cumulonimbus", MinCover = 0.35f, WeakCover = 0.15f, GroundPitchDeg = 28.0, GroundMidM = 6000.0, AboveAglM = 14000.0, AbovePitchDeg = 25.0, AboveMidM = 8000.0 },
            new Soft60Target { Name = "cirrus", LayerPart = "cirrus", TypePart = "cirrus", MinCover = 0.45f, WeakCover = 0.25f, GroundPitchDeg = 35.0, GroundMidM = 10000.0, AboveAglM = 12500.0, AbovePitchDeg = 30.0, AboveMidM = 10000.0 }
        };

        // The rings of the scan around the site (km) and the bearings on each (every 15 degrees); 1 + 11 x 24 = 265 places. Kerbin's map is 512 x 256 texels: a texel is 7.4 km at the equator.
        internal static readonly double[] Soft60RingsKm = { 0.0, 6.0, 12.0, 20.0, 32.0, 48.0, 70.0, 100.0, 140.0, 190.0, 245.0, 300.0 };
        internal const int Soft60Bearings = 24, Soft60ScanPerFrame = 24;
        internal const double Soft60MaxViewKm = 40.0, Soft60MinViewKm = 1.0;

        // A place of the scan where a target's type stands: its distance and bearing from the site, the cover of the layer there, and the names the map gave.
        internal sealed class Soft60Cell
        {
            internal double LatDeg, LonDeg, DistKm, BearingDeg;
            internal float Cover;
            internal string LayerName = "", TypeName = "";
            internal bool Weak;
        }

        internal static double Soft60Rad(double deg) { return deg * Math.PI / 180.0; }

        // The point at km (negative: behind) along the bearing from a place, on a sphere of the body's radius.
        internal static void Soft60Destination(double latDeg, double lonDeg, double bearingDeg, double km, double radiusM, out double lat2, out double lon2)
        {
            double d = km * 1000.0 / radiusM, phi = Soft60Rad(latDeg), lam = Soft60Rad(lonDeg), th = Soft60Rad(bearingDeg);
            double sinPhi2 = Math.Sin(phi) * Math.Cos(d) + Math.Cos(phi) * Math.Sin(d) * Math.Cos(th);
            double phi2 = Math.Asin(Math.Max(-1.0, Math.Min(1.0, sinPhi2)));
            double lam2 = lam + Math.Atan2(Math.Sin(th) * Math.Sin(d) * Math.Cos(phi), Math.Cos(d) - Math.Sin(phi) * sinPhi2);
            lat2 = phi2 * 180.0 / Math.PI;
            lon2 = WrapDeg(lam2 * 180.0 / Math.PI);
        }

        // The initial bearing from the first place to the second, degrees clockwise from north, 0 to 360.
        internal static double Soft60Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = Soft60Rad(lat1), p2 = Soft60Rad(lat2), dl = Soft60Rad(lon2 - lon1);
            double y = Math.Sin(dl) * Math.Cos(p2), x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            double b = Math.Atan2(y, x) * 180.0 / Math.PI;
            return b < 0.0 ? b + 360.0 : b;
        }

        internal static double Soft60DistanceKm(double lat1, double lon1, double lat2, double lon2, double radiusM)
        {
            double p1 = Soft60Rad(lat1), p2 = Soft60Rad(lat2), dp = p2 - p1, dl = Soft60Rad(lon2 - lon1);
            double a = Math.Sin(dp / 2.0) * Math.Sin(dp / 2.0) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2.0) * Math.Sin(dl / 2.0);
            return 2.0 * radiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a))) / 1000.0;
        }

        // Where the vessel and the camera must be for a target's view to have the cell at its centre: the view's horizontal reach is the height difference over the tangent of the pitch
        // (the cloud's height against the camera's, the terrain at the cell counted), the vessel stands that far from the cell on the side of the site, the camera looks along the line to the cell.
        internal struct Soft60Pose
        {
            internal View View;
            internal double VesselLatDeg, VesselLonDeg, CentreLatDeg, CentreLonDeg, ReachKm, MovedKm;
        }

        internal static Soft60Pose Soft60PoseFor(Soft60Target t, bool above, Soft60Cell cell, double siteLat, double siteLon, double radiusM, double terrainM)
        {
            double agl = above ? t.AboveAglM : 200.0, pitch = above ? t.AbovePitchDeg : t.GroundPitchDeg, mid = above ? t.AboveMidM : t.GroundMidM;
            double camera = Math.Max(0.0, terrainM) + agl;
            double reachKm = Math.Abs(mid - camera) / Math.Tan(Soft60Rad(Math.Max(5.0, pitch))) / 1000.0;
            reachKm = Math.Max(Soft60MinViewKm, Math.Min(Soft60MaxViewKm, reachKm));
            // From the cell towards the site (a cell on the site: towards the west, so that the camera looks east as the other views do).
            double back = cell.DistKm < 0.5 ? 270.0 : Soft60Bearing(cell.LatDeg, cell.LonDeg, siteLat, siteLon);
            double vlat, vlon;
            Soft60Destination(cell.LatDeg, cell.LonDeg, back, reachKm, radiusM, out vlat, out vlon);
            double heading = Soft60Bearing(vlat, vlon, cell.LatDeg, cell.LonDeg);
            var view = new View { Name = "ty_" + t.Name + (above ? "_a" : "_g"), AglM = agl, PitchDeg = above ? -pitch : pitch, HeadingDeg = heading, CameraDistM = above ? 150.0 : 100.0 };
            return new Soft60Pose
            {
                View = view, VesselLatDeg = vlat, VesselLonDeg = vlon, CentreLatDeg = cell.LatDeg, CentreLonDeg = cell.LonDeg, ReachKm = reachKm, MovedKm = Soft60DistanceKm(siteLat, siteLon, vlat, vlon, radiusM)
            };
        }

        private static readonly float[] Soft60NoShifts = new float[0];
        private int soft60TypeViews, soft60TypesAbsent;
        private readonly bool[] soft60Done = new bool[Soft60Targets.Length];

        // A cell that reaches the target's cover beats one that does not; among those that do, the nearer ring wins (the stronger cover within one ring); among the weak ones the stronger.
        internal static bool Soft60Better(Soft60Cell old, double km, float cover, bool good)
        {
            if (old == null) return true;
            bool oldGood = !old.Weak;
            if (oldGood != good) return good;
            if (good) return km < old.DistKm - 1e-6 || (Math.Abs(km - old.DistKm) < 1e-6 && cover > old.Cover);
            return cover > old.Cover;
        }

        // Reads the layers' maps at the places of the rings and keeps, per target, the nearest cell that reaches its cover (the strongest of the weaker ones where none does).
        private IEnumerable<ProbeWait> Soft60Scan(string scene, double lat, double lon, Soft60Cell[] found)
        {
            double radiusM = Game.BodyRadius;
            int cells = 0, failed = 0;
            for (int r = 0; r < Soft60RingsKm.Length; r++)
            {
                double km = Soft60RingsKm[r];
                int bearings = km <= 0.0 ? 1 : Soft60Bearings;
                for (int b = 0; b < bearings; b++)
                {
                    double bearing = b * 360.0 / Soft60Bearings, la = lat, lo = lon;
                    if (km > 0.0) Soft60Destination(lat, lon, bearing, km, radiusM, out la, out lo);
                    T09LayerReport report = null;
                    try { report = Game.ReadLayers(la, lo, 1.0, Soft60NoShifts); }
                    catch (Exception e)
                    {
                        if (failed++ == 0) Warn("type_scan_fault", scene + ": " + e.GetType().Name + " " + e.Message);
                    }
                    cells++;
                    if (report != null)
                    {
                        foreach (T09LayerInfo info in report.Layers)
                        {
                            if (float.IsNaN(info.CpuCover)) continue;
                            for (int t = 0; t < Soft60Targets.Length; t++)
                            {
                                Soft60Target target = Soft60Targets[t];
                                if (!target.Matches(info.Name, info.TypeName)) continue;
                                bool good = info.CpuCover >= target.MinCover;
                                if (!good && info.CpuCover < target.WeakCover) continue;
                                if (!Soft60Better(found[t], km, info.CpuCover, good)) continue;
                                found[t] = new Soft60Cell { LatDeg = la, LonDeg = lo, DistKm = km, BearingDeg = km > 0.0 ? bearing : 0.0, Cover = info.CpuCover, LayerName = info.Name, TypeName = info.TypeName, Weak = !good };
                            }
                        }
                    }
                    if (cells % Soft60ScanPerFrame == 0) yield return ProbeWait.Frames(1);
                }
            }
            Emit("T09s.types." + scene + ".cells", cells);
            Emit("T09s.types." + scene + ".scan_failed", failed);
        }

        // The type views of a scene: the scan, then every target that has not been shown yet and has a cell is shown from the ground and from above (three frames each).
        private IEnumerable<ProbeWait> Soft60Types(bool fair, double lat, double lon)
        {
            string scene = fair ? "fair" : "clock";
            var found = new Soft60Cell[Soft60Targets.Length];
            foreach (ProbeWait w in Soft60Scan(scene, lat, lon, found)) yield return w;
            var line = new System.Text.StringBuilder();
            for (int t = 0; t < Soft60Targets.Length; t++)
            {
                Soft60Target target = Soft60Targets[t];
                string head = "T09s.types." + scene + "." + target.Name + ".";
                if (line.Length > 0) line.Append("; ");
                Soft60Cell c = found[t];
                Emit(head + "found", c != null);
                if (c == null)
                {
                    line.Append(target.Name).Append(" absent within ").Append(Soft60RingsKm[Soft60RingsKm.Length - 1].ToString("0", CultureInfo.InvariantCulture)).Append(" km");
                    continue;
                }
                Emit(head + "dist_km", c.DistKm);
                Emit(head + "bearing_deg", c.BearingDeg);
                Emit(head + "cover", c.Cover);
                Emit(head + "weak", c.Weak);
                EmitText(head + "type", c.TypeName);
                line.Append(target.Name).Append(" ").Append(c.DistKm.ToString("0", CultureInfo.InvariantCulture)).Append(" km at ").Append(c.BearingDeg.ToString("0", CultureInfo.InvariantCulture)).Append(" degrees, cover ")
                    .Append(c.Cover.ToString("0.00", CultureInfo.InvariantCulture)).Append(" [").Append(c.TypeName).Append(c.Weak ? ", weak" : "").Append("]");
            }
            AtmospheresLog.Say("T09 types (" + scene + "): " + line);
            for (int t = 0; t < Soft60Targets.Length; t++)
            {
                if (soft60Done[t] || found[t] == null) continue;
                soft60Done[t] = true;
                foreach (bool above in new[] { false, true })
                    foreach (ProbeWait w in Soft60TypeView(fair, Soft60Targets[t], above, found[t], lat, lon)) yield return w;
            }
            if (!fair)
            {
                for (int t = 0; t < Soft60Targets.Length; t++)
                {
                    if (soft60Done[t]) continue;
                    soft60TypesAbsent++;
                    AtmospheresLog.Say("T09 types: " + Soft60Targets[t].Name + " was absent within " + Soft60RingsKm[Soft60RingsKm.Length - 1].ToString("0", CultureInfo.InvariantCulture) + " km in both scenes: no view of it (logged, not a failure)");
                }
            }
        }

        // One type-placed view: moved, held, the baseline frame for the cumulus bodies, the variant soft, the type under the view centre, three frames.
        private IEnumerable<ProbeWait> Soft60TypeView(bool fair, Soft60Target target, bool above, Soft60Cell cell, double siteLat, double siteLon)
        {
            string scene = fair ? "fair" : "clock";
            double terrain = 0.0;
            try { terrain = Game.TerrainAltitude(cell.LatDeg, cell.LonDeg); } catch (Exception) { terrain = 0.0; }
            Soft60Pose pose = Soft60PoseFor(target, above, cell, siteLat, siteLon, Game.BodyRadius, terrain);
            View view = pose.View;
            string head = "T09s." + scene + "." + view.Name + ".";
            double estimate = Soft60TypeViewS(target);
            if (Elapsed + estimate > MaxSeconds - SoftReserveS)
            {
                softSectionsSkipped++;
                Check("T09s.section_" + scene + "_" + view.Name, false, "the scenario is at " + Elapsed.ToString("0") + " s of " + MaxSeconds.ToString("0") + " s and the view needs about " + estimate.ToString("0") + " s: left out");
                yield break;
            }
            softSections++;
            soft60TypeViews++;
            AtmospheresLog.Say("T09 type view " + view.Name + " (" + scene + "): " + target.Name + " at " + cell.DistKm.ToString("0.0", CultureInfo.InvariantCulture) + " km from the site, cover " + cell.Cover.ToString("0.00", CultureInfo.InvariantCulture)
                               + " [" + cell.TypeName + "], the vessel " + pose.MovedKm.ToString("0.0", CultureInfo.InvariantCulture) + " km from the site, the view's reach " + pose.ReachKm.ToString("0.0", CultureInfo.InvariantCulture)
                               + " km, heading " + view.HeadingDeg.ToString("0", CultureInfo.InvariantCulture));
            Emit(head + "cell_dist_km", cell.DistKm);
            Emit(head + "vessel_moved_km", pose.MovedKm);
            Emit(head + "reach_km", pose.ReachKm);
            foreach (ProbeWait w in PlaceChecked(pose.VesselLatDeg, pose.VesselLonDeg, view)) yield return w;
            Emit(head + "placed_ok", placeOk);
            if (!placeOk) yield break;
            foreach (ProbeWait w in StableGate(view, "soft")) yield return w;
            string pictureView = view.Name + (fair ? "_fair" : "");
            SoftVariant soft = Soft60Variants[0];
            softOffRaw = null;
            if (target.Light)
            {
                SoftBase();
                Game.SetLayerMask(NoLayersMask);
                yield return ProbeWait.Frames(DiagSettleFrames);
                foreach (ProbeWait w in ReadRawFrame(view.Name + " " + scene + " baseline")) yield return w;
                softOffRaw = noiseImage != null ? noiseImage.Raw : null;
                if (noiseImage != null) SoftSave(pictureView, "off_a", noiseImage);
                Game.SetLayerMask(0);
            }
            SoftApply(soft);
            foreach (ProbeWait w in SoftSettle(soft)) yield return w;
            SoftVerify(head, soft);
            Soft60CentreType(head, target, pose);
            foreach (ProbeWait w in SoftGrainRead(view, head + soft.Name + ".", pictureView, soft.Name, soft.Frames, target.Light && softOffRaw != null)) yield return w;
            SoftBase();
            Game.SetLayerMask(0);
        }

        // What the map holds under the view's centre: the layers' types and covers, and whether the target's own type is among them.
        private void Soft60CentreType(string head, Soft60Target target, Soft60Pose pose)
        {
            T09LayerReport report = null;
            try { report = Game.ReadLayers(pose.CentreLatDeg, pose.CentreLonDeg, 1.0, Soft60NoShifts); }
            catch (Exception e) { Warn("type_centre_fault", head + e.GetType().Name + " " + e.Message); }
            if (report == null) return;
            var sb = new System.Text.StringBuilder();
            bool match = false;
            foreach (T09LayerInfo info in report.Layers)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(info.Name).Append(" ").Append(info.TypeName).Append(" cover ").Append(info.CpuCover.ToString("0.00", CultureInfo.InvariantCulture));
                if (target.Matches(info.Name, info.TypeName) && info.CpuCover >= target.WeakCover) match = true;
            }
            Emit(head + "centre_match", match);
            EmitText(head + "centre_types", sb.ToString());
            AtmospheresLog.Say("T09 type centre " + head + ": " + sb + (match ? " (the " + target.Name + " is under the view's centre)" : " (NOT the " + target.Name + ": the map has moved or the cell was weak)"));
        }
    }

}
