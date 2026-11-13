using System;
using AtmoSim.Core;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The share of the sky each cloud layer covers, from the weather generator's own physical quantities (E40, decision D1 of the cover-meaning design).
    //
    // The generator makes ONE cloud water per column, cw = bg + xdyn + 0.7 cs + cu (humid background, dynamic lift and front, convective cells, fair-weather cumulus), and its field
    // cover is Coverage = 1 - exp(-3 cw). That is the cover of independent components: 1 - Coverage = exp(-3 bg) exp(-3 xdyn) exp(-3 0.7 cs) exp(-3 cu). So the cover of a part of the
    // cloud water x is 1 - exp(-3 x), and the parts overlap at random (the product of the clear shares) exactly as the field already defines its total. The rule is therefore
    // random overlap, and the combination is exact with no scaling of the maps: split cw among the layers, give each layer 1 - exp(-3 x_layer), and the union is the field cover.
    // (Maximum-minimum overlap for neighbouring layers would also be a rule; it gives a smaller union for the same shares, so the field's total would not be met.)
    //
    // The split follows what rains and what does not (E40 rain coherence, feb90e4f), with the precipitation level of the field as the one source of truth:
    //   rain deck       the part of the stratiform amount (bg + xdyn) that falls as rain, la * active * (1 - convective share of the rain)
    //   cumulonimbus    the cell water above the cell rain threshold, 0.7 (cs - th), where it rains: the cells that rain are the towers
    //   cirrus          a fixed fraction of the stratiform amount that does not rain: the upper, humid layer ([UNKNOWN] starting value HighFraction)
    //   cumulus         everything else: the fair-weather cumulus field cu, the weak cells (cs up to th), and the non-raining stratiform remainder
    // active = the share of the precipitation level that counts as rain (PrecipScale.FlurriesFrom to LightFrom, the same smooth step as WeatherDeckRule.PrecipShare): below the band None
    // of the HUD there is no rain deck and no cumulonimbus, which is the rain-coherence rule (a rain cloud only where it rains), and the water that is not rain-bearing goes to the
    // cumulus layer, so the total stays the field's.
    // Fog air (WeatherDeckRule.FogShare) is not part of cw and stays the deck rule's own path.
    internal static class LayerShares
    {
        // The generator's exponent of Coverage = 1 - exp(-K cw) (Generator.cs, WeatherGenerator.Sample) and of the tower cover in LayerCoupling.Coverage.
        internal const double CoverExponent = 3.0;
        // The weight of the cell water in cw (Generator.cs: cw = bg + xdyn + 0.7 cs).
        internal const double CellWaterWeight = 0.7;

        internal struct Shares
        {
            // Cloud water per layer (the same units as Weather.CloudWater).
            internal double DeckWater, CumulusWater, CumulonimbusWater, CirrusWater;
            // The share of the sky each layer covers looking up, 0 to 1: 1 - exp(-3 water).
            internal double Deck, Cumulus, Cumulonimbus, Cirrus;
            // The union of the four under random overlap; equals the field's Coverage.
            internal double Union;
        }

        internal sealed class Settings
        {
            // [UNKNOWN] starting value: the part of the non-raining stratiform cloud water that is cirrus (the high, humid layer). The generator has no height structure for it
            // (its humidity is one column value), so the split is a constant; the literature figure for the high-cloud part of the cloud amount is of this order (about a quarter),
            // not verified here. A tuning value, not a fact.
            internal double HighFraction = 0.25;
            // The cell rain threshold of the body (BodyParams.CellRainThreshold): cells weaker than this do not rain and are cumulus.
            internal double CellRainThreshold = 0.02;
            // The precipitation level where rain begins and where it is complete (PrecipScale.FlurriesFrom and LightFrom; the band None of the HUD, and the end of Flurries).
            // The rain-bearing share (deck and towers together) that rain itself guarantees, 0 to 1 (0: none, the pure split). 0.9 is the shipped deckRainFloor; it is reached at a
            // quarter of the way from PrecipFrom to PrecipFullAt (level 0.08), as WeatherDeckRule.FloorOf. It is the owner's rule "no rain from a clear sky" against the generator's
            // own cover, which is under 0.9 for part of the rain (LayerSharesTests prints how much).
            internal double RainFloor = 0.0;
            internal float PrecipFrom =PrecipScale.FlurriesFrom, PrecipFullAt = PrecipScale.LightFrom;
        }

        private static double Step(double edge0, double edge1, double x)
        {
            if (!(edge1 > edge0)) return x >= edge1 ? 1.0 : 0.0;
            double t = (x - edge0) / (edge1 - edge0);
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            return t * t * (3.0 - 2.0 * t);
        }

        // The share of a texel that counts as raining, from the precipitation level (the smooth step of WeatherDeckRule.PrecipShare).
        internal static double Active(double level, Settings s)
        {
            return Step(s.PrecipFrom, s.PrecipFullAt, level);
        }

        internal static double CoverOf(double water)
        {
            return water <= 0.0 ? 0.0 : 1.0 - DMath.Exp(-CoverExponent * water);
        }

        // The layer shares of one generator evaluation. level is the precipitation level of the column (PrecipScale.LevelFromRate of w.PrecipMmH).
        internal static void Compute(ref Weather w, double level, Settings s, out Shares r)
        {
            Compute(ref w, level, w.ConvectiveShare, s, out r);
        }

        // The same with the rain taken from another evaluation than the cloud water: a layer carried by its own wind keeps the water of where it has been carried to, but rain
        // falls where it falls, so the level and the convective share of the rain are those of the point itself (the rain-coherence rule stays true at the point).
        internal static void Compute(ref Weather w, double level, double convectiveShare, Settings s, out Shares r)
        {
            double cw = w.CloudWater < 0.0 ? 0.0 : w.CloudWater;
            double la = w.LayerAmount < 0.0 ? 0.0 : w.LayerAmount;
            double cs = w.CellStrength < 0.0 ? 0.0 : w.CellStrength;
            double cell = CellWaterWeight * cs;
            // the generator clamps cw at 1.5; when it did, the parts are scaled so they still add to cw
            double parts = la + cell;
            double scale = parts > cw && parts > 0.0 ? cw / parts : 1.0;
            la *= scale;
            double active = Active(level, s);
            double conv = convectiveShare < 0.0 ? 0.0 : convectiveShare > 1.0 ? 1.0 : convectiveShare;
            double deck = la * active * (1.0 - conv);
            double cb = CellWaterWeight * Math.Max(0.0, cs - s.CellRainThreshold) * scale * active;
            double rest = la - deck;
            double ci = s.HighFraction * rest;
            double cu = cw - deck - cb - ci;
            if (cu < 0.0) cu = 0.0;
            if (s.RainFloor > 0.0)
            {
                // Rain implies a rain-bearing cloud (the owner's rule, WeatherDeckRule.RainFloor): where the rain-bearing share is under the floor the deck takes water from the
                // cumulus layer, then from cirrus. Only where the whole cloud water is too little does the total rise above the field's cover.
                double f = s.RainFloor > 0.999 ? 0.999 : s.RainFloor;
                double ramp = Step(s.PrecipFrom, s.PrecipFrom + 0.25 * (s.PrecipFullAt - s.PrecipFrom), level);
                double want = -DMath.Log(1.0 - f) / CoverExponent * ramp, have = deck + cb;
                if (want > have)
                {
                    double need = want - have;
                    deck += need;
                    double fromCu = need < cu ? need : cu;
                    cu -= fromCu; need -= fromCu;
                    double fromCi = need < ci ? need : ci;
                    ci -= fromCi;
                }
            }
            r = default(Shares);
            r.DeckWater = deck; r.CumulonimbusWater = cb; r.CirrusWater = ci; r.CumulusWater = cu;
            r.Deck = CoverOf(deck); r.Cumulonimbus = CoverOf(cb); r.Cirrus = CoverOf(ci); r.Cumulus = CoverOf(cu);
            r.Union = 1.0 - (1.0 - r.Deck) * (1.0 - r.Cumulonimbus) * (1.0 - r.Cirrus) * (1.0 - r.Cumulus);
        }

        // The four layer maps at one point as the module would hand them to the layers, with each layer looked up where its own wind has carried it (LayerCoupling.LayerLon,
        // as LayerCoupling.Coverage does): the rain deck, the cumulus layer and cirrus at the longitude of their mid height; the cumulonimbus layer at the point itself (the cells
        // are not moved, LayerCoupling.Column). mid heights are the layers' mid heights in metres. levelOf maps a rate in mm/h to the precipitation level.
        internal static void AtPoint(WeatherGenerator g, double lat, double lon, double ut, double deckMidM, double cumulusMidM, double cirrusMidM, LayerOptions o,
            Func<double, double> levelOf, Settings s, out double deck, out double cumulus, out double cumulonimbus, out double cirrus)
        {
            Weather w0, wd, wc, wi;
            g.Sample(lat, lon, 0.0, ut, out w0);
            wd = Moved(g, w0, lat, lon, ut, deckMidM, o);
            wc = Moved(g, w0, lat, lon, ut, cumulusMidM, o);
            wi = Moved(g, w0, lat, lon, ut, cirrusMidM, o);
            Shares sd, sc, sb, si;
            double level = levelOf(w0.PrecipMmH);
            Compute(ref wd, level, w0.ConvectiveShare, s, out sd);
            Compute(ref wc, level, w0.ConvectiveShare, s, out sc);
            Compute(ref w0, level, w0.ConvectiveShare, s, out sb);
            Compute(ref wi, level, w0.ConvectiveShare, s, out si);
            deck = sd.Deck; cumulus = sc.Cumulus; cumulonimbus = sb.Cumulonimbus; cirrus = si.Cirrus;
        }

        internal enum Role
        {
            RainDeck = 0,
            Cumulus = 1,
            Cumulonimbus = 2,
            Cirrus = 3
        }

        // The share map of one layer role at a height, equirectangular as SubGrid.LayerMap (row 0 north, texel centres): the replacement of SubGrid.LayerMap in the layer renders
        // of GeneratorMaps (E40 D1). The layer is looked up where its own wind has carried it (the cumulonimbus layer is not moved), the rain from the point itself.
        internal static void Map(WeatherGenerator g, double ut, int nx, int ny, Role role, double midM, LayerOptions o, Func<double, double> levelOf, Settings s, float[] target)
        {
            for (int j = 0; j < ny; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / ny) * DMath.Pi / 180.0;
                for (int i = 0; i < nx; i++)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / nx) * DMath.Pi / 180.0;
                    Weather w0;
                    g.Sample(lat, lon, 0.0, ut, out w0);
                    Weather w = role == Role.Cumulonimbus ? w0 : Moved(g, w0, lat, lon, ut, midM, o);
                    Shares r;
                    Compute(ref w, levelOf(w0.PrecipMmH), w0.ConvectiveShare, s, out r);
                    double v = role == Role.RainDeck ? r.Deck : role == Role.Cumulus ? r.Cumulus : role == Role.Cumulonimbus ? r.Cumulonimbus : r.Cirrus;
                    target[j * nx + i] = (float)v;
                }
            }
        }

        private static Weather Moved(WeatherGenerator g, Weather w0, double lat, double lon, double ut, double midM, LayerOptions o)
        {
            double lonL = LayerCoupling.LayerLon(g, lon, ut, midM, o);
            if (lonL == lon) return w0;
            Weather w;
            g.Sample(lat, lonL, 0.0, ut, out w);
            return w;
        }
    }
}
