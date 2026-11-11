using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The data premises: P1 (the game's temperature curves, sampled per latitude, altitude, season phase and local hour), P2 (the height
    // and ocean grids of the bodies, 360 x 180 texels) and P3 (the vertex spacing of the terrain mesh and the range of Parallax's
    // displacement). The dumps are inputs of B10, C25 and D34.
    internal sealed partial class AtmoRecon
    {
        private static readonly string[] TemperatureBodies = { "Kerbin", "Duna", "Laythe", "Eve" };
        private static readonly string[] CurveNames =
        {
            "latitudeTemperatureBiasCurve", "axialTemperatureSunMultCurve", "atmosphereTemperatureSunMultCurve", "eccentricityTemperatureBiasCurve"
        };

        private const double KscLatitude = -0.0972, KscLongitude = -74.5577, KscAltitude = 70.0;

        // ---------------------------------------------------------------- P1: temperatures

        private sealed class Slot
        {
            internal double Ut;
            internal CelestialBody Body;
            internal int Phase;
            internal double Year;
        }

        private IEnumerable<ProbeWait> TemperatureSamples()
        {
            var bodies = new List<CelestialBody>();
            foreach (string name in TemperatureBodies)
            {
                CelestialBody b = FlightGlobals.Bodies.Find(x => x.bodyName == name);
                if (b != null) bodies.Add(b);
            }
            Emit("P1.bodies_found", bodies.Count);
            int curvesPresent = 0;
            foreach (string curve in CurveNames)
            {
                if (typeof(CelestialBody).GetField(curve) != null) curvesPresent++;
            }
            Emit("P1.curves_present", curvesPresent);
            Emit("P1.method_GetFullTemperature_pos", typeof(CelestialBody).GetMethod("GetFullTemperature", new[] { typeof(Vector3d) }) != null);
            Emit("P1.method_GetTemperature_alt", typeof(CelestialBody).GetMethod("GetTemperature", new[] { typeof(double) }) != null);
            if (bodies.Count == 0) yield break;

            double baseUt = Planetarium.GetUniversalTime();
            var slots = new List<Slot>();
            foreach (CelestialBody b in bodies)
            {
                double year = YearOf(b);
                for (int k = 0; k < 4; k++) slots.Add(new Slot { Ut = baseUt + k * year / 4.0, Body = b, Phase = k, Year = year });
            }
            slots.Sort((x, y) => x.Ut.CompareTo(y.Ut));

            double[] latitudes = { -90, -75, -60, -45, -30, -15, 0, 15, 30, 45, 60, 75, 90 };
            double[] altitudes = { 0, 1000, 5000, 10000 };
            double[] longitudes = { -180, -90, 0, 90 };
            var values = new Dictionary<string, float[]>();
            var phaseUts = new Dictionary<string, double[]>();
            foreach (CelestialBody b in bodies)
            {
                values[b.bodyName] = new float[4 * longitudes.Length * altitudes.Length * latitudes.Length];
                phaseUts[b.bodyName] = new double[4];
            }
            int samples = 0, finite = 0;
            var kscValues = new List<double>();
            double kscLapse = double.NaN;
            var kscByPhase = new double[4][];

            foreach (Slot slot in slots)
            {
                Planetarium.SetUniversalTime(slot.Ut);
                yield return ProbeWait.Frames(4);
                CelestialBody b = slot.Body;
                phaseUts[b.bodyName][slot.Phase] = slot.Ut;
                float[] block = values[b.bodyName];
                int at = slot.Phase * longitudes.Length * altitudes.Length * latitudes.Length;
                for (int lo = 0; lo < longitudes.Length; lo++)
                {
                    for (int al = 0; al < altitudes.Length; al++)
                    {
                        for (int la = 0; la < latitudes.Length; la++)
                        {
                            double t = Temperature(b, latitudes[la], longitudes[lo], altitudes[al]);
                            samples++;
                            if (!double.IsNaN(t) && !double.IsInfinity(t)) finite++;
                            block[at++] = (float)t;
                        }
                    }
                }
                if (b.bodyName == "Kerbin")
                {
                    var phaseKsc = new double[4];
                    for (int hour = 0; hour < 4; hour++)
                    {
                        phaseKsc[hour] = Temperature(b, KscLatitude, KscLongitude + 90.0 * hour, KscAltitude);
                        kscValues.Add(phaseKsc[hour]);
                    }
                    kscByPhase[slot.Phase] = phaseKsc;
                    if (slot.Phase == 0)
                    {
                        kscLapse = Temperature(b, KscLatitude, KscLongitude, 0.0) - Temperature(b, KscLatitude, KscLongitude, 10000.0);
                    }
                }
            }
            Planetarium.SetUniversalTime(baseUt);
            yield return ProbeWait.Frames(4);

            Emit("P1.samples", samples);
            Emit("P1.finite_fraction", samples > 0 ? finite / (double)samples : 0);
            double kmin = double.MaxValue, kmax = double.MinValue, ksum = 0;
            foreach (double t in kscValues)
            {
                kmin = Math.Min(kmin, t);
                kmax = Math.Max(kmax, t);
                ksum += t;
            }
            CelestialBody kerbin = bodies.Find(x => x.bodyName == "Kerbin");
            if (kscValues.Count > 0 && kerbin != null)
            {
                double kmean = ksum / kscValues.Count;
                double baseCurve = kerbin.GetTemperature(KscAltitude);
                Emit("P1.ksc_min_k", kmin);
                Emit("P1.ksc_max_k", kmax);
                Emit("P1.ksc_mean_k", kmean);
                Emit("P1.ksc_base_curve_k", baseCurve);
                Emit("P1.ksc_mean_minus_base_k", Math.Abs(kmean - baseCurve));
                Emit("P1.lapse_0_10km_k", kscLapse);
                double swing = 0;
                for (int hour = 0; hour < 4; hour++)
                {
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (int ph = 0; ph < 4; ph++)
                    {
                        if (kscByPhase[ph] == null) continue;
                        lo = Math.Min(lo, kscByPhase[ph][hour]);
                        hi = Math.Max(hi, kscByPhase[ph][hour]);
                    }
                    swing = Math.Max(swing, hi - lo);
                }
                Emit("P1.ksc_seasonal_swing_k", swing);
            }

            foreach (CelestialBody b in bodies)
            {
                var json = new ReconJson().BeginObject();
                json.Text("body", b.bodyName).Number("year_s", YearOf(b)).Text("layout", "phase, longitude, altitude, latitude (row-major)").Text("unit", "kelvin");
                json.BeginArray("phase_ut");
                foreach (double ut in phaseUts[b.bodyName]) json.Number(null, ut);
                json.EndArray();
                json.Numbers("latitude_deg", ToFloats(latitudes), 2).Numbers("altitude_m", ToFloats(altitudes), 1).Numbers("longitude_deg", ToFloats(longitudes), 1);
                json.Numbers("temperature_k", values[b.bodyName], 1);
                json.BeginObject("curves");
                foreach (string curve in CurveNames) json.Flag(curve, typeof(CelestialBody).GetField(curve) != null);
                json.EndObject();
                json.EndObject();
                json.Save(DumpPath("temperature-" + b.bodyName + ".json"));
            }
            Say("P1: " + samples + " samples, " + finite + " finite, KSC " + Num(kmin) + " to " + Num(kmax) + " K");
        }

        private static double Temperature(CelestialBody body, double latitude, double longitude, double altitude)
        {
            Vector3d position = body.position + body.GetRelSurfacePosition(latitude, longitude, altitude);
            return body.GetFullTemperature(position);
        }

        // The length of the body's year: the period of the ancestor that orbits the star (a moon follows its planet's seasons).
        private static double YearOf(CelestialBody body)
        {
            CelestialBody b = body;
            int guard = 0;
            while (b.referenceBody != null && b.referenceBody != b && b.referenceBody.referenceBody != null && b.referenceBody.referenceBody != b.referenceBody && guard++ < 6)
            {
                b = b.referenceBody;
            }
            return b.orbit != null ? b.orbit.period : 0.0;
        }

        private static float[] ToFloats(double[] values)
        {
            var result = new float[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = (float)values[i];
            return result;
        }

        // ---------------------------------------------------------------- P2: grids

        private IEnumerable<ProbeWait> HeightGrid()
        {
            CelestialBody kerbin = FlightGlobals.Bodies.Find(x => x.bodyName == "Kerbin");
            if (kerbin == null) yield break;
            foreach (ProbeWait w in SampleGrid(kerbin, 90)) yield return w;
        }

        private IEnumerable<ProbeWait> HeightGridsOthers()
        {
            foreach (string name in new[] { "Duna", "Laythe", "Eve" })
            {
                CelestialBody body = FlightGlobals.Bodies.Find(x => x.bodyName == name);
                if (body == null || body.pqsController == null)
                {
                    Emit("P2." + name + "_grid_complete", 0);
                    continue;
                }
                PQS pqs = body.pqsController;
                bool started = pqs.isStarted;
                if (!started)
                {
                    try
                    {
                        pqs.ForceStart();
                    }
                    catch (Exception e)
                    {
                        Say("P2: " + name + " could not be started (" + e.GetType().Name + " " + e.Message + ")");
                    }
                    yield return ProbeWait.Frames(10);
                }
                Emit("P2." + name + "_pqs_started", pqs.isStarted);
                foreach (ProbeWait w in SampleGrid(body, 60)) yield return w;
            }
        }

        // Reads the heights of a body at the centre of each texel of a 360 x 180 grid (row 0 at the south pole, column 0 at -180 degrees),
        // a few milliseconds a frame so that the game goes on, and writes the dump when it is complete or the time is up.
        private IEnumerable<ProbeWait> SampleGrid(CelestialBody body, double seconds)
        {
            PQS pqs = body.pqsController;
            if (pqs == null)
            {
                Emit("P2." + body.bodyName + "_grid_complete", 0);
                yield break;
            }
            int width = ReconMath.GridWidth, height = ReconMath.GridHeight;
            var heights = new float[width * height];
            int done = 0;
            double end = Time.realtimeSinceStartup + seconds;
            var watch = new Stopwatch();
            double nan = 0;
            while (done < heights.Length && Time.realtimeSinceStartup < end)
            {
                watch.Reset();
                watch.Start();
                while (done < heights.Length && watch.ElapsedMilliseconds < 8)
                {
                    int row = done / width, column = done % width;
                    double latitude = -90.0 + (row + 0.5) * 180.0 / height, longitude = -180.0 + (column + 0.5) * 360.0 / width;
                    double h = pqs.GetSurfaceHeight(body.GetRelSurfaceNVector(latitude, longitude)) - pqs.radius;
                    if (double.IsNaN(h) || double.IsInfinity(h))
                    {
                        nan++;
                        h = 0;
                    }
                    heights[done++] = (float)h;
                }
                yield return ProbeWait.Frames(1);
            }
            string key = "P2." + body.bodyName;
            double complete = done / (double)heights.Length;
            Emit(key + "_grid_complete", complete);
            if (done < heights.Length) yield break;

            float min = float.MaxValue, max = float.MinValue;
            foreach (float h in heights)
            {
                min = Math.Min(min, h);
                max = Math.Max(max, h);
            }
            double ocean = body.ocean ? ReconMath.OceanFraction(heights, width, height, 0f) : 0.0;
            Emit(key + "_height_min_m", min);
            Emit(key + "_height_max_m", max);
            Emit(key + "_height_range_plausible", max - min > 50f);
            Emit(key + "_nan_samples", nan);
            Emit(key + "_ocean_fraction", ocean);

            var mask = new StringBuilder(heights.Length);
            foreach (float h in heights) mask.Append(body.ocean && h < 0f ? '1' : '0');
            var json = new ReconJson().BeginObject();
            json.Text("body", body.bodyName).Number("radius_m", body.Radius).Number("width", width).Number("height", height)
                .Text("layout", "row-major, row 0 = south pole, column 0 = longitude -180, texel centres").Flag("ocean", body.ocean)
                .Number("ocean_fraction", ocean).Number("min_m", min).Number("max_m", max)
                .Numbers("heights_m", heights, 0).Text("ocean_mask", mask.ToString()).EndObject();
            json.Save(DumpPath("heights-" + body.bodyName + ".json"));
            Say("P2: " + body.bodyName + " grid done, " + Num(min) + " to " + Num(max) + " m, ocean " + Num(ocean));
        }

        // ---------------------------------------------------------------- bodies list (P2/P1 input, schema checked)

        private void DumpBodies()
        {
            var json = new ReconJson().BeginObject().BeginArray("bodies");
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                json.BeginObject().Text("name", b.bodyName).Number("index", b.flightGlobalsIndex).Number("radius_m", b.Radius)
                    .Flag("atmosphere", b.atmosphere).Number("atmosphere_depth_m", b.atmosphere ? b.atmosphereDepth : 0.0).Flag("ocean", b.ocean)
                    .Flag("has_pqs", b.pqsController != null).Number("rotation_period_s", b.rotationPeriod).Number("gm", b.gravParameter)
                    .Number("orbit_period_s", b.orbit != null ? b.orbit.period : 0.0).Number("year_s", YearOf(b))
                    .Text("reference_body", b.referenceBody != null ? b.referenceBody.bodyName : "").EndObject();
            }
            json.EndArray().EndObject();
            json.Save(DumpPath("bodies.json"));
        }

        // ---------------------------------------------------------------- P3: vertex spacing and Parallax displacement

        private IEnumerable<ProbeWait> VertexSpacing()
        {
            DumpBodies();
            CelestialBody body = FlightGlobals.ActiveVessel.mainBody;
            PQS pqs = body.pqsController;
            Emit("P3.pqs_max_level", pqs.maxLevel);
            Emit("P3.pqs_min_level", pqs.minLevel);
            var bySubdivision = new SortedDictionary<int, List<double>>();
            int counted = 0;
            PQ[] quads = pqs.quads;
            if (quads != null)
            {
                foreach (PQ quad in quads)
                {
                    if (quad == null || quad.mesh == null) continue;
                    Vector3[] verts = quad.verts != null && quad.verts.Length > 1 ? quad.verts : quad.mesh.vertices;
                    if (verts == null || verts.Length < 2) continue;
                    double spacing = Vector3.Distance(verts[0], verts[1]);
                    List<double> list;
                    if (!bySubdivision.TryGetValue(quad.subdivision, out list)) bySubdivision[quad.subdivision] = list = new List<double>();
                    if (list.Count < 40) list.Add(spacing);
                    counted++;
                    if (counted >= 4000) break;
                }
            }
            var json = new ReconJson().BeginObject().Text("body", body.bodyName).Number("max_level", pqs.maxLevel).BeginArray("by_subdivision");
            int top = -1;
            double topSpacing = double.NaN;
            foreach (KeyValuePair<int, List<double>> pair in bySubdivision)
            {
                pair.Value.Sort();
                double median = pair.Value[pair.Value.Count / 2];
                json.BeginObject().Number("subdivision", pair.Key).Number("quads_sampled", pair.Value.Count).Number("vertex_spacing_median_m", median).EndObject();
                if (pair.Key > top)
                {
                    top = pair.Key;
                    topSpacing = median;
                }
            }
            json.EndArray();
            Emit("P3.quads_read", counted);
            Emit("P3.max_subdivision_seen", top);
            Emit("P3.vertex_spacing_m", topSpacing);
            if (quads != null && counted > 0)
            {
                foreach (PQ quad in quads)
                {
                    if (quad != null && quad.mesh != null && quad.subdivision == top)
                    {
                        Emit("P3.quad_vertex_count", quad.mesh.vertexCount);
                        break;
                    }
                }
            }

            // Parallax: the displacement range of the home body's terrain from its configuration, and the tessellation settings.
            double scale = ParallaxValue("Parallax_StockTerrainTextures", body.bodyName, "_DisplacementScale");
            double offset = ParallaxValue("Parallax_StockTerrainTextures", body.bodyName, "_DisplacementOffset");
            Emit("P3.parallax_displacement_scale", scale);
            Emit("P3.parallax_displacement_offset", offset);
            string global = ReadText(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "ParallaxContinued"),
                                                  Path.Combine("Config", "ParallaxGlobalSettings.cfg")));
            Emit("P3.parallax_max_tessellation", FirstNumber(global, "maxTessellation"));
            Emit("P3.parallax_tessellation_edge_length", FirstNumber(global, "tessellationEdgeLength"));
            Emit("P3.parallax_max_tessellation_range_m", FirstNumber(global, "maxTessellationRange"));
            json.Number("parallax_displacement_scale", scale).Number("parallax_displacement_offset", offset).EndObject();
            json.Save(DumpPath("pqs-spacing.json"));
            Say("P3: max level " + pqs.maxLevel + ", top subdivision " + top + ", spacing " + Num(topSpacing) + " m, Parallax scale " + Num(scale));
            yield break;
        }

        private static string ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static double FirstNumber(string text, string key)
        {
            Match m = Regex.Match(text, @"\b" + Regex.Escape(key) + @"\s*=\s*(-?[0-9.]+)");
            double value;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : double.NaN;
        }

        // The value of a shader property in a Parallax terrain block of a body, from the configuration file of the stock texture pack.
        private static double ParallaxValue(string pack, string bodyName, string property)
        {
            string path = Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), pack), "Terrain.cfg");
            string text = ReadText(path);
            if (text.Length == 0) return double.NaN;
            Match start = Regex.Match(text, @"^\s*name\s*=\s*" + Regex.Escape(bodyName) + @"\s*$", RegexOptions.Multiline);
            if (!start.Success) return double.NaN;
            Match next = Regex.Match(text.Substring(start.Index + start.Length), @"^\s*name\s*=\s*\w+\s*$", RegexOptions.Multiline);
            string block = next.Success ? text.Substring(start.Index, start.Length + next.Index) : text.Substring(start.Index);
            return FirstNumber(block, property);
        }
    }
}
