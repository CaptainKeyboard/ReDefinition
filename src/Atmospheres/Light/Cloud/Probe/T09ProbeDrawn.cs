using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, -t09DrawnCover: the time and the heading of the views are chosen by the share of each view's sky the clouds DRAW (.local/plans/wolken-m1-fakten.md H:
    // the field's column cover CloudCover01 at the site was 0.52 at ut 2247600 while the cumulus map over the site was 0.02 to 0.14 and the sky view looked into a clear lane).
    // T09DrawnCover.cs estimates a view's share from the layers' maps over the rays of the view's frame.
    //   1. The own pass is switched on (UseOwn) for the choice, so its stack and maps exist; the candidates are the clock step's (SampleCoverCandidates).
    //   2. Forecast: for every candidate and every heading offset (-t09DrawnHeadings, default 0,90,180,270: added to every view's heading) the views' share from the
    //      generator's layer lookup at the candidate's time without moving the clock (IT09World.ForecastLayerCover: no meso pattern, no cells, a rough deck rule). It ranks.
    //   3. Confirmation, in the ranked order, at most DrawnConfirmTries times with the sun in the band (narrow first, then wide): the clock is set, the maps settle
    //      (SettleOwnMaps: the field's maps made for the time, every layer on its own map, a snapping round), and the views' share is read from the maps the pass draws
    //      (IT09World.DrawnLayerCover) for every heading offset; with -t09TvcCover also from EVE's own layers over the same rays. A time and heading is taken where every
    //      required view (-t09DrawnViews, default ground,inside,above,sky3) shows at least -t09DrawnMin (default 0.5) of cloud on our side and, with -t09TvcCover, at least
    //      -t09TvcDrawnMin (default the same) on TVC's; of those the one nearest -t09DrawnTarget (default 0.65: a sky clearly cloudy and not closed).
    //   4. With -t09Ut (a time found once) the same estimates are read at that time and the heading -t09HeadingOffset, emitted and checked.
    // Rows: T09.heading_offset_deg; per view T09.drawn.<view>.cover / layer_fraction / picture_share / forecast / tvc_cover; T09.drawn.ok, T09.drawn.confirmed; after the pictures of
    // an own variant T09.view.<view>.picture_cloud_share (the measured share, PictureShare).
    internal sealed partial class T09Probe
    {
        private const int DrawnConfirmTries = 8, DrawnSunTries = 48;
        // A confirmed time whose required views all lie this close to the target on average ends the search.
        private const double DrawnGoodEnough = 0.1;
        private static readonly string[] DrawnViewNames = { "ground", "inside", "above", "sky3" };

        private bool DrawnCoverStart
        {
            get { return CommandLine.Has(Args(), "-t09DrawnCover", true); }
        }

        // The heading offset added to every view's heading (StandardScenario): the chosen one, or -t09HeadingOffset.
        private double headingOffset;
        private double drawnTarget = CoverTarget, drawnMin = MinCover, tvcDrawnMin = MinCover;
        private bool[] drawnRequired;
        private double[] drawnHeadings;
        // The estimates at the chosen time and heading (per DrawnViewNames; NaN where not read).
        private double[] drawnOwn, drawnFraction, drawnTvc, drawnForecast;
        private bool drawnOk;
        private string drawnDetail = "";

        private View Turned(View v)
        {
            if (headingOffset == 0.0) return v;
            v.HeadingDeg = Heading360(v.HeadingDeg + headingOffset);
            return v;
        }

        private static double Heading360(double h)
        {
            h %= 360.0;
            return h < 0.0 ? h + 360.0 : h;
        }

        private double NumberArg(string name, double fallback)
        {
            string text = CommandLine.After(Args(), name, true);
            double v;
            return !string.IsNullOrEmpty(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private void ReadDrawnArgs()
        {
            drawnTarget = NumberArg("-t09DrawnTarget", CoverTarget);
            drawnMin = NumberArg("-t09DrawnMin", MinCover);
            tvcDrawnMin = NumberArg("-t09TvcDrawnMin", drawnMin);
            string views = CommandLine.After(Args(), "-t09DrawnViews", true);
            drawnRequired = new bool[DrawnViewNames.Length];
            for (int k = 0; k < DrawnViewNames.Length; k++)
                drawnRequired[k] = string.IsNullOrEmpty(views) || Array.IndexOf(views.Split(','), DrawnViewNames[k]) >= 0;
            string fixedHeading = CommandLine.After(Args(), "-t09HeadingOffset", true);
            double h;
            if (!string.IsNullOrEmpty(fixedHeading) && double.TryParse(fixedHeading, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) drawnHeadings = new[] { Heading360(h) };
            else
            {
                var list = new List<double>();
                string text = CommandLine.After(Args(), "-t09DrawnHeadings", true);
                foreach (string part in (string.IsNullOrEmpty(text) ? "0,90,180,270" : text).Split(','))
                    if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) list.Add(Heading360(h));
                drawnHeadings = list.Count > 0 ? list.ToArray() : new[] { 0.0 };
            }
        }

        // -t09HeadingOffset without -t09DrawnCover (a start that reuses a heading): the views turn by it.
        private void ReadHeadingOffset()
        {
            headingOffset = Heading360(NumberArg("-t09HeadingOffset", 0.0));
        }

        private static View DrawnView(int k)
        {
            foreach (View v in Views)
                if (v.Name == DrawnViewNames[k]) return v;
            throw new InvalidOperationException("no view " + DrawnViewNames[k]);
        }

        private static int DrawnIndex(string viewName)
        {
            return Array.IndexOf(DrawnViewNames, viewName);
        }

        // The estimates of the views at a heading offset over the layers given, with the cover read by coverAt.
        private T09DrawnCover.Estimate[] EstimateViews(double lat, double lon, double terrainM, double offset, IList<T09DrawnLayer> layers, Func<int, double, double, double> coverAt, int columns, int rows)
        {
            double fov = 60.0;
            try { fov = Game.CameraFovDeg; } catch (Exception) { /* no camera: the flight camera's default */ }
            double aspect = Game.ScreenHeight > 0 ? (double)Game.ScreenWidth / Game.ScreenHeight : 16.0 / 9.0;
            var result = new T09DrawnCover.Estimate[DrawnViewNames.Length];
            for (int k = 0; k < DrawnViewNames.Length; k++)
            {
                View v = DrawnView(k);
                List<T09DrawnCover.Ray> rays = T09DrawnCover.Rays(Heading360(v.HeadingDeg + offset), v.PitchDeg, fov, aspect, columns, rows);
                result[k] = T09DrawnCover.Of(lat, lon, terrainM + v.AglM, Game.BodyRadius, rays, layers, coverAt);
            }
            return result;
        }

        private static double[] Covers(T09DrawnCover.Estimate[] e)
        {
            var c = new double[e.Length];
            for (int k = 0; k < e.Length; k++) c[k] = e[k].Cover;
            return c;
        }

        private static string Describe(double[] covers)
        {
            var parts = new List<string>();
            for (int k = 0; k < covers.Length; k++) parts.Add(DrawnViewNames[k] + " " + (double.IsNaN(covers[k]) ? "n/a" : covers[k].ToString("0.00", CultureInfo.InvariantCulture)));
            return string.Join(", ", parts.ToArray());
        }

        // What keeps a set of covers from qualifying (null where it does).
        private string Shortfall(double[] covers, double min, string side)
        {
            var parts = new List<string>();
            for (int k = 0; k < covers.Length; k++)
                if (drawnRequired[k] && !(covers[k] >= min)) parts.Add(side + " " + DrawnViewNames[k] + " " + (double.IsNaN(covers[k]) ? "not read" : covers[k].ToString("0.00", CultureInfo.InvariantCulture)) + " below " + min.ToString("0.00", CultureInfo.InvariantCulture));
            return parts.Count == 0 ? null : string.Join(", ", parts.ToArray());
        }

        // --- The maps ---------------------------------------------------------------------------------------------------------------------

        private bool mapsSettled;
        private string mapsWhy = "";

        // Waits until the own pass's maps stand at the clock's time (wolken-m1-fakten.md H.4: no layer on the field's shared cover in the first view): the field's maps made for
        // the clock's time (IT09World.FieldMapsAtClock), then snapping map rounds (the pass fetches them and shows them at once, no slew) until every layer is on its own map at its
        // goal after two of them. At most timeoutS; a wait that runs out is a named warning (maps_not_settled) and the start goes on.
        private IEnumerable<ProbeWait> SettleOwnMaps(string label, double timeoutS, bool warn)
        {
            double began = Elapsed, giveUp = Elapsed + timeoutS;
            mapsSettled = false;
            mapsWhy = "not looked at";
            int snapsDone = 0, roundsAtSnap = int.MinValue;
            bool snapOut = false;
            while (Elapsed < giveUp)
            {
                string why;
                if (!Game.FieldMapsAtClock(out why))
                {
                    mapsWhy = why;
                    snapOut = false;
                    snapsDone = 0;
                    yield return ProbeWait.Frames(1);
                    continue;
                }
                if (snapOut && Game.OwnMapRounds > roundsAtSnap)
                {
                    snapOut = false;
                    snapsDone++;
                }
                if (snapsDone >= 2 && Game.OwnMapsAtGoal(out why))
                {
                    mapsSettled = true;
                    break;
                }
                if (snapsDone >= 2) mapsWhy = why;
                if (!snapOut)
                {
                    roundsAtSnap = Game.OwnMapRounds;
                    Game.RequestMapSnap();
                    snapOut = true;
                }
                yield return ProbeWait.Frames(1);
            }
            double took = Elapsed - began;
            if (mapsSettled)
            {
                AtmospheresLog.Say("T09 maps settled (" + label + ") after " + took.ToString("0.0", CultureInfo.InvariantCulture) + " s: every layer on its own map at its goal");
                if (warn)
                {
                    Emit("T09.maps_settle_s", took);
                    Emit("T09.maps_settled", true);
                }
                yield break;
            }
            AtmospheresLog.Say("T09 maps not settled (" + label + ") after " + took.ToString("0.0", CultureInfo.InvariantCulture) + " s: " + mapsWhy);
            if (!warn) yield break;
            Emit("T09.maps_settle_s", took);
            Emit("T09.maps_settled", false);
            Warn("maps_not_settled", label + ": " + mapsWhy + " after " + timeoutS.ToString("0") + " s");
        }

        // --- The choice -------------------------------------------------------------------------------------------------------------------

        private struct DrawnPick
        {
            internal bool Found;
            internal double Ut, Cover, Sun, Offset, Distance;
            internal bool Narrow;
            internal double[] Own, Fraction, Tvc;
        }

        // The own pass on for the choice (its stack and maps are what is read), and its layers once there.
        private IEnumerable<ProbeWait> DrawnPrepare()
        {
            ReadDrawnArgs();
            UseOwn();
            yield return ProbeWait.Until(() => Game.OwnDrawnLayers().Count > 0, 60, "the own pass's layer stack for the drawn cover (-t09DrawnCover)");
        }

        private IEnumerable<ProbeWait> ChooseDrawn(double lat, double lon, double start, bool tvcCover)
        {
            IList<T09DrawnLayer> own = Game.OwnDrawnLayers();
            double terrain = Math.Max(0.0, Game.TerrainAltitude(lat, lon));
            // 2. The forecast ranks every candidate by its best heading.
            var ranked = new List<KeyValuePair<double, CoverCandidate>>();
            var forecasts = new Dictionary<double, double[]>();
            foreach (CoverCandidate c in coverCandidates)
            {
                double ut = c.Ut, best = double.PositiveInfinity;
                foreach (double offset in drawnHeadings)
                {
                    double[] f = Covers(EstimateViews(lat, lon, terrain, offset, own, (l, la, lo) => Game.ForecastLayerCover(l, la, lo, ut), T09DrawnCover.ForecastColumns, T09DrawnCover.ForecastRows));
                    forecasts[ut * 1000.0 + offset] = f;
                    best = Math.Min(best, T09DrawnCover.Distance(f, drawnRequired, drawnTarget, drawnMin));
                }
                ranked.Add(new KeyValuePair<double, CoverCandidate>(best, c));
                yield return ProbeWait.Frames(1);
            }
            ranked.Sort((a, b) =>
            {
                int by = a.Key.CompareTo(b.Key);
                return by != 0 ? by : a.Value.Ut.CompareTo(b.Value.Ut);
            });
            Emit("T09.drawn.forecast_candidates", ranked.Count);
            if (ranked.Count > 0) Emit("T09.drawn.forecast_best", ranked[0].Key);

            // 3. Confirmation at the clock.
            var sunAt = new Dictionary<double, double>();
            var confirmedUts = new HashSet<double>();
            DrawnPick pick = new DrawnPick();
            int confirmed = 0, sunTries = 0;
            string lastReject = "no candidate confirmed";
            bool done = false;
            for (int band = 0; band < 2 && !done && !pick.Found; band++)
            {
                double sunMin = band == 0 ? NarrowSunMinDeg : SunMinDeg, sunMax = band == 0 ? NarrowSunMaxDeg : SunMaxDeg;
                foreach (KeyValuePair<double, CoverCandidate> entry in ranked)
                {
                    if (confirmed >= DrawnConfirmTries || done) break;
                    CoverCandidate c = entry.Value;
                    if (confirmedUts.Contains(c.Ut)) continue;
                    double elevation;
                    if (!sunAt.TryGetValue(c.Ut, out elevation))
                    {
                        if (sunTries >= DrawnSunTries) break;
                        sunTries++;
                        ForceUniversalTime(c.Ut);
                        yield return ProbeWait.Frames(6);
                        elevation = Game.SunElevationDeg();
                        sunAt[c.Ut] = elevation;
                    }
                    if (elevation < sunMin || elevation > sunMax) continue;
                    confirmed++;
                    confirmedUts.Add(c.Ut);
                    if (Math.Abs(Game.UniversalTime - c.Ut) > 600.0)
                    {
                        ForceUniversalTime(c.Ut);
                        yield return ProbeWait.Frames(6);
                    }
                    foreach (ProbeWait w in SettleOwnMaps("candidate ut " + c.Ut.ToString("0", CultureInfo.InvariantCulture), MapSettleS, false)) yield return w;
                    string utText = c.Ut.ToString("0", CultureInfo.InvariantCulture);
                    if (!mapsSettled)
                    {
                        lastReject = "ut " + utText + ": the maps did not settle (" + mapsWhy + ")";
                        AtmospheresLog.Say("T09 drawn: " + lastReject);
                        continue;
                    }
                    IList<T09DrawnLayer> eve = null;
                    if (tvcCover)
                    {
                        yield return ProbeWait.Frames(TvcCoverSettleFrames);
                        eve = Game.EveDrawnLayers();
                    }
                    own = Game.OwnDrawnLayers();
                    foreach (double offset in drawnHeadings)
                    {
                        T09DrawnCover.Estimate[] ownEst = EstimateViews(lat, lon, terrain, offset, own, Game.DrawnLayerCover, T09DrawnCover.Columns, T09DrawnCover.Rows);
                        double[] ownCov = Covers(ownEst);
                        double[] tvcCov = null;
                        if (tvcCover) tvcCov = Covers(EstimateViews(lat, lon, terrain, offset, eve, Game.EveLayerCover, T09DrawnCover.Columns, T09DrawnCover.Rows));
                        string shortOwn = Shortfall(ownCov, drawnMin, "own"), shortTvc = tvcCover ? Shortfall(tvcCov, tvcDrawnMin, "TVC") : null;
                        double distance = T09DrawnCover.Distance(ownCov, drawnRequired, drawnTarget, drawnMin);
                        bool ok = shortOwn == null && shortTvc == null;
                        string head = "ut " + utText + " heading +" + offset.ToString("0", CultureInfo.InvariantCulture);
                        AtmospheresLog.Say("T09 drawn: " + head + ", sun " + elevation.ToString("0.0", CultureInfo.InvariantCulture) + ": own " + Describe(ownCov)
                                           + (tvcCover ? "; TVC " + Describe(tvcCov) : "") + (ok ? " (qualifies, distance to " + drawnTarget.ToString("0.00", CultureInfo.InvariantCulture) + ": "
                                           + distance.ToString("0.000", CultureInfo.InvariantCulture) + ")" : " (rejected: " + (shortOwn ?? shortTvc) + (shortOwn != null && shortTvc != null ? ", " + shortTvc : "") + ")"));
                        if (!ok)
                        {
                            lastReject = head + ": " + (shortOwn ?? shortTvc);
                            continue;
                        }
                        if (pick.Found && distance >= pick.Distance) continue;
                        var fraction = new double[ownEst.Length];
                        for (int k = 0; k < ownEst.Length; k++) fraction[k] = ownEst[k].LayerFraction;
                        pick = new DrawnPick { Found = true, Ut = c.Ut, Cover = c.Cover, Sun = elevation, Offset = offset, Distance = distance, Narrow = band == 0, Own = ownCov, Fraction = fraction, Tvc = tvcCov };
                    }
                    if (pick.Found && pick.Distance <= DrawnGoodEnough) done = true;
                }
            }
            Emit("T09.drawn.confirmed", confirmed);
            if (!pick.Found)
            {
                utFailure = "no universal time and heading has the clouds drawn in every required view (-t09DrawnCover: own at least " + drawnMin.ToString("0.00", CultureInfo.InvariantCulture)
                            + (tvcCover ? ", TVC at least " + tvcDrawnMin.ToString("0.00", CultureInfo.InvariantCulture) : "") + "; " + confirmed + " times confirmed of " + ranked.Count
                            + " ranked, " + sunTries + " sun checks; the last rejected: " + lastReject + ")";
                yield break;
            }
            // The clock at the chosen time, the maps settled there (the variants start from them).
            if (Math.Abs(Game.UniversalTime - pick.Ut) > 600.0)
            {
                ForceUniversalTime(pick.Ut);
                yield return ProbeWait.Frames(6);
                foreach (ProbeWait w in SettleOwnMaps("chosen ut " + pick.Ut.ToString("0", CultureInfo.InvariantCulture), MapSettleS, false)) yield return w;
            }
            double[] forecast;
            forecasts.TryGetValue(pick.Ut * 1000.0 + pick.Offset, out forecast);
            utChosen = pick.Ut;
            coverChosen = pick.Cover;
            sunChosen = Game.SunElevationDeg();
            utNarrow = pick.Narrow;
            headingOffset = pick.Offset;
            drawnOwn = pick.Own;
            drawnFraction = pick.Fraction;
            drawnTvc = pick.Tvc;
            drawnForecast = forecast;
            drawnOk = true;
            drawnDetail = "own " + Describe(pick.Own) + (pick.Tvc != null ? "; TVC " + Describe(pick.Tvc) : "") + " at heading +" + pick.Offset.ToString("0", CultureInfo.InvariantCulture);
            utOk = true;
            Emit("T09.ut_jump_s", pick.Ut - start);
        }

        // 4. A time found once (-t09Ut): the estimates at it and at -t09HeadingOffset (the clock already stands there).
        private IEnumerable<ProbeWait> DrawnAtFixed(double lat, double lon, bool tvcCover)
        {
            headingOffset = drawnHeadings[0];
            foreach (ProbeWait w in SettleOwnMaps("fixed ut " + Game.UniversalTime.ToString("0", CultureInfo.InvariantCulture), MapSettleS, false)) yield return w;
            double terrain = Math.Max(0.0, Game.TerrainAltitude(lat, lon));
            IList<T09DrawnLayer> own = Game.OwnDrawnLayers();
            T09DrawnCover.Estimate[] ownEst = EstimateViews(lat, lon, terrain, headingOffset, own, Game.DrawnLayerCover, T09DrawnCover.Columns, T09DrawnCover.Rows);
            drawnOwn = Covers(ownEst);
            drawnFraction = new double[ownEst.Length];
            for (int k = 0; k < ownEst.Length; k++) drawnFraction[k] = ownEst[k].LayerFraction;
            double ut = Game.UniversalTime;
            drawnForecast = Covers(EstimateViews(lat, lon, terrain, headingOffset, own, (l, la, lo) => Game.ForecastLayerCover(l, la, lo, ut), T09DrawnCover.ForecastColumns, T09DrawnCover.ForecastRows));
            drawnTvc = null;
            if (tvcCover)
            {
                IList<T09DrawnLayer> eve = Game.EveDrawnLayers();
                drawnTvc = Covers(EstimateViews(lat, lon, terrain, headingOffset, eve, Game.EveLayerCover, T09DrawnCover.Columns, T09DrawnCover.Rows));
            }
            string shortOwn = Shortfall(drawnOwn, drawnMin, "own"), shortTvc = drawnTvc != null ? Shortfall(drawnTvc, tvcDrawnMin, "TVC") : null;
            drawnOk = mapsSettled && shortOwn == null && shortTvc == null;
            drawnDetail = "own " + Describe(drawnOwn) + (drawnTvc != null ? "; TVC " + Describe(drawnTvc) : "") + " at heading +" + headingOffset.ToString("0", CultureInfo.InvariantCulture)
                          + (mapsSettled ? "" : "; the maps did not settle: " + mapsWhy) + (shortOwn != null ? "; " + shortOwn : "") + (shortTvc != null ? "; " + shortTvc : "");
            AtmospheresLog.Say("T09 drawn (fixed ut " + ut.ToString("0", CultureInfo.InvariantCulture) + "): " + drawnDetail);
        }

        // The rows of the chosen estimates (after the clock is chosen, before the variants).
        private void EmitDrawn()
        {
            Emit("T09.heading_offset_deg", headingOffset);
            Emit("T09.drawn.ok", drawnOk);
            if (drawnOwn == null) return;
            for (int k = 0; k < DrawnViewNames.Length; k++)
            {
                string p = "T09.drawn." + DrawnViewNames[k] + ".";
                Emit(p + "cover", drawnOwn[k]);
                if (drawnFraction != null)
                {
                    Emit(p + "layer_fraction", drawnFraction[k]);
                    Emit(p + "picture_share", double.IsNaN(drawnOwn[k]) ? double.NaN : drawnOwn[k] * drawnFraction[k]);
                }
                if (drawnForecast != null) Emit(p + "forecast", drawnForecast[k]);
                if (drawnTvc != null) Emit(p + "tvc_cover", drawnTvc[k]);
            }
        }

        // --- The picture's measured share ----------------------------------------------------------------------------------------------

        // After the pictures of an own variant's view: a baseline frame (the pass running with no layer drawn) and a frame with the pass, both raw, of the held pose; the share of the
        // picture the clouds changed by 0.05 or more (SoftLightOf, the light rows' threshold) is T09.view.<view>.picture_cloud_share, logged beside the drawn estimate. TVC and off
        // have no such baseline (the layer mask is the own pass's): a line says so.
        private IEnumerable<ProbeWait> PictureShare(View view, string variant)
        {
            if (!IsOwn(variant))
            {
                AtmospheresLog.Say("T09 picture share: " + view.Name + " " + variant + ": not measured (no baseline without clouds: the layer mask is the own pass's)");
                yield break;
            }
            if (picturesStopped || !viewPoseOk || Elapsed > MaxSeconds - PictureReserveS) yield break;
            string what = view.Name + " " + variant + " picture share";
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(NoiseSettleBaselineFrames);
            foreach (ProbeWait w in ReadRawFrame(what + " baseline")) yield return w;
            T09Image off = noiseImage;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(NoiseSettleFrames);
            foreach (ProbeWait w in ReadRawFrame(what + " clouds")) yield return w;
            T09Image on = noiseImage;
            if (off == null || on == null) yield break;
            if (off.Width != on.Width || off.Height != on.Height)
            {
                Warn("picture_share_size", what + ": the two frames differ in size");
                yield break;
            }
            SoftLightStats s;
            SoftLightOf(on.Raw, off.Raw, out s);
            Emit("T09.view." + view.Name + ".picture_cloud_share", s.CloudShare);
            int k = DrawnIndex(view.Name);
            string estimate = k >= 0 && drawnOwn != null && drawnFraction != null && !double.IsNaN(drawnOwn[k])
                ? "the drawn estimate " + (drawnOwn[k] * drawnFraction[k]).ToString("0.00", CultureInfo.InvariantCulture) + " (cover " + drawnOwn[k].ToString("0.00", CultureInfo.InvariantCulture)
                  + " over " + drawnFraction[k].ToString("0.00", CultureInfo.InvariantCulture) + " of the picture)"
                : "no drawn estimate";
            AtmospheresLog.Say("T09 picture share: " + view.Name + " " + variant + ": measured " + s.CloudShare.ToString("0.00", CultureInfo.InvariantCulture) + " of the picture changed by the clouds, " + estimate);
        }
    }
}
