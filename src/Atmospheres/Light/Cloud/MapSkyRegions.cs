using System;

namespace ReDefinition.Atmospheres
{
    // The sky drawn from the weather map in the game: the layers and forms fed by the package B maps so that regions arise (a closed low stratiform layer where
    // the map's stratiform amount is high, broken cumulus where its cumuliform amount is, clear sky where its cover is 0) instead of clouds spread evenly by the old layer maps. A pure function of map-sized arrays (OwnCloudModule fetches the maps and hands the result to the layers); WM1's WeatherMapSky is the
    // weather player's cost prototype this grew from. Only cfg numbers, the maps and numbers of the M1 proposal (marked PROPOSAL) are used.
    //
    // Per map texel (bilinear in the shader, slewed as every map):
    //   cumuliform, uncapped     -> the cumuliform layer (Kerbin-cumulus), its clouds placed by the cloud field: cover = the cumuliform amount, type by the depth (top - base)
    //                         through the type table's thicknesses from the first rising type (humilis) to the last (congestus); deeper than congestus the share goes over
    //                         to the tall layer (Kerbin-cumulonimbus) between congestus' and calvus' thicknesses (cfg type heights), no hard switch.
    //   cumuliform, capped       -> (stable layer at or below the base, no cell) the cumuliform layer's stratiform type (Stratocumulus, its first type), weighted
    //                         saturate(cumuliform / 0.25) (FairShare: below that share a fair sky's clouds stand apart). It is drawn by the cloud field with the type's form
    //                         (flat, merging); the "connected sheet" this rule was written for existed only under the test lattice (removed, master plan 47.U stage 1).
    //   sheet, saturated   -> per saturated level (RH >= 0.9999, the M1 count's definition): low: the low stratiform layer (its precipitation type, Nimbostratus by the proposal's R9),
    //                         middle the cumuliform layer's Altocumulus as a connected sheet, high the cheap layer (Kerbin-cirrus, today's types kept).
    //   sheet, no level    -> "by source" (the M1 proposal's B, owner 2026-11-23 "the more realistic", PRUEFPROTOKOLL 105, 111, 112): the low stratiform layer's share (LowStratiformAmount,
    //   saturated             lift and fronts) as the middle level's Altocumulus sheet, the rest as a connected Stratocumulus layer in the cumuliform layer at the column's convective base
    //                         (the cloud-topped mixed layer, proposal T5). The other rules (the most humid level; the condensation level alone) and the calibrated depth rule were
    //                         removed with their switches (owner 2026-11-25 21:02, master plan 45).
    //   depth              -> the field's depth; a cumuliform column without depth draws the thinnest rising type (humilis), its body's height a share of the type's
    //                         by its core (CloudCells.cginc CellBody).
    //   base               -> the root offset of the cumuliform and tall layers' height maps: base - the layer's inner altitude, in a root range of the inner altitude (a base from the
    //                         ground to twice the inner altitude; clamped beyond). The top term (updraft) is zeroed for these layers: the top is the type's thickness.
    //   union              -> the cumuliform layer's parts in one texel: 1 - (1 - a)(1 - b)(1 - c), the type of the largest part (APPROXIMATION: one type per texel).
    //   storm cells        -> the tall layer draws its towers from the generator's cell list (AnvilMap.PaintCells, each cell one tower, OwnCloudModule) where the list is there: the
    //                         map holds a cell's tower only in the single texel at its centre. Without the list the tall share above is drawn.
    //   low stratiform     -> the larger of the low level's sheet and today's low stratiform rule (rain from the precipitation map, fog from the dew-point spread): the field's low level
    //                         lies below the convective base in raining fronts, so the low stratiform rain layer would be missing under them.
    //   meso pattern       -> since 2026-11-28 a layer's share is broken into fields and gaps by its MESO pattern after this (CloudMesoStage, cover only: the types stay these);
    //                         until then every share was drawn evenly, from orbit a veil where a real sky of that share is bright fields with dark gaps.
    //   not drawn          -> the combination with the field's cover (the amounts are the sky's shares).
    internal static class MapSkyRegions
    {
        internal const float SaturatedRh = WeatherMapSky.SaturatedRh;
        // The fair-sky edge of the cumuliform share (0.25; once the test lattice's own edge, kept as the rule's number): below it a capped column's stratocumulus share is
        // the square of the share over this (the clouds of a fair sky stand apart).
        internal const float FairShare = 0.25f;

        // The low stratiform layer's base is the weather field's (owner 2026-11-23 "Ja, Feld-Basis", after "Warum sind die Regenwolken so niedrig?"): the low stratiform rain and snow layer's types
        // are lifted to the field's cloud base (LowStratiformRoots; the fog type stays on the ground); at its cfg type shares it stood about 170 m high at KSC where the field says 899 m.
        // The default since 2026-11-23 16:02 (PRUEFPROTOKOLL 139); its switch cloud.weatherMapSky.lowStratiformBase was removed 2026-11-25 (master plan 45).

        // The maps, one value per texel (row 0 at the north pole, column 0 at -180). Heights as shares of the troposphere, as the field gives them.
        internal sealed class Inputs
        {
            internal int Width, Height;
            internal float[] Cover, Cumuliform, Stratiform, LowStratiform, BaseShare, TopShare, StableShare, RhLow, RhMid, RhHigh, Reach, PrecipLevel;
            internal double TropopauseM, Ut;
        }

        // Per texel, the parts the layers take (set by Plan).
        internal sealed class Parts
        {
            internal int N;
            // The cumuliform as rising cumulus (cumuliform layer) and in the tall layer, the depth they are drawn with (m), the base (m).
            internal float[] Rising, Tall, DepthM, BaseM;
            // The cumuliform layer's connected sheets: Stratocumulus (capped cumuliform and the unsaturated sheet of rules A / B) and Altocumulus (the middle level).
            internal float[] Sc, Ac;
            // The low stratiform layer's sheet (low level saturated) and whether it is nimbostratus; the cheap layer's sheet.
            internal float[] Low, High;
            internal bool[] Nimbo;
            // Copies of the maps the parts were made from, for the pinned capture's map patches (a dump never reads the fetch buffers a later round refills): cover, cumuliform,
            // sheet, base and top shares, the cells' reach; and the time.
            internal float[] MapCover, MapCumuliform, MapStratiform, MapBase, MapTop, MapReach;
            internal double Ut, TropopauseM;
        }

        private static float[] CopyOf(float[] source, float[] spare)
        {
            if (source == null) return null;
            float[] c = spare != null && spare.Length == source.Length ? spare : new float[source.Length];
            Array.Copy(source, c, source.Length);
            return c;
        }

        // The type value (0..1) of a layer's type index.
        private static float Value(CloudLayerDef l, int index)
        {
            return l.Types.Count > 1 ? index / (float)(l.Types.Count - 1) : 0f;
        }

        private static float ThickM(CloudLayerDef l, int k)
        {
            return (l.Types[k].TopShare - l.Types[k].BaseShare) * l.ThicknessM;
        }

        private static int RisingRun(CloudLayerDef l, int first, int stop)
        {
            int k = first;
            while (k + 1 < stop && l.Types[k + 1].TopShare - l.Types[k + 1].BaseShare > l.Types[k].TopShare - l.Types[k].BaseShare) k++;
            return k;
        }

        internal static int AltoIndex(CloudLayerDef cumuliform)
        {
            return cumuliform.AltoType >= 0f ? (int)Math.Round(Math.Min(1f, cumuliform.AltoType) * (cumuliform.Types.Count - 1)) : -1;
        }

        // The cumuliform layer's last rising type (the end of the rising run before the alto type) and the tall layer's last tower type (before its anvil).
        internal static int CumuliformLast(CloudLayerDef cumuliform)
        {
            int alto = AltoIndex(cumuliform);
            return RisingRun(cumuliform, 0, alto >= 0 ? alto : cumuliform.Types.Count);
        }

        internal static int TallLast(CloudLayerDef tall)
        {
            int anvil = AnvilMap.AnvilTypeIndex(tall);
            return RisingRun(tall, 0, anvil >= 0 ? anvil : tall.Types.Count);
        }

        private static float Saturate(double v)
        {
            return (float)(v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v));
        }

        // The parts of every texel for the stack's cumuliform and tall layers (either may be null).
        internal static Parts Plan(Inputs m, CloudLayerDef cumuliform, CloudLayerDef tall, Parts into = null)
        {
            int n = m.Width * m.Height;
            Parts p = into != null && into.N == n ? into : new Parts
            {
                N = n, Rising = new float[n], Tall = new float[n], DepthM = new float[n], BaseM = new float[n], Sc = new float[n], Ac = new float[n], Low = new float[n],
                High = new float[n], Nimbo = new bool[n]
            };
            p.MapCover = CopyOf(m.Cover, p.MapCover);
            p.MapCumuliform = CopyOf(m.Cumuliform, p.MapCumuliform);
            p.MapStratiform = CopyOf(m.Stratiform, p.MapStratiform);
            p.MapBase = CopyOf(m.BaseShare, p.MapBase);
            p.MapTop = CopyOf(m.TopShare, p.MapTop);
            p.MapReach = CopyOf(m.Reach, p.MapReach);
            p.Ut = m.Ut;
            p.TropopauseM = m.TropopauseM;
            double h = m.TropopauseM;
            // The cumuliform's hand-over from the cumuliform layer to the tall one: from congestus' thickness to calvus' (the cfg's type heights).
            double congM = cumuliform != null ? ThickM(cumuliform, CumuliformLast(cumuliform)) : 0.0;
            double calvusM = tall != null ? ThickM(tall, 0) : 0.0;
            double handFrom = congM, handTo = Math.Max(calvusM, congM + 1.0);
            for (int i = 0; i < n; i++)
            {
                float cover = Math.Max(0f, m.Cover[i]), hp = Math.Max(0f, m.Cumuliform[i]), s = Math.Max(0f, m.Stratiform[i]);
                double zb = m.BaseShare[i] * h, zt = m.TopShare[i] * h, zi = m.StableShare != null ? m.StableShare[i] * h : double.PositiveInfinity;
                double depth = Math.Max(0.0, zt - zb);
                bool cell = m.Reach != null && m.Reach[i] > 0f;
                bool capped = !cell && zi <= zb;
                p.BaseM[i] = (float)zb;
                // the cumuliform's split between the rising cumulus and the stratocumulus
                float scCumuliform = 0f;
                if (capped) scCumuliform = hp * Saturate(hp / FairShare);
                float rising = hp - scCumuliform;
                p.DepthM[i] = (float)depth;
                float toTall = tall != null ? (cumuliform == null ? 1f : Saturate((depth - handFrom) / (handTo - handFrom))) : 0f;
                p.Tall[i] = rising * toTall;
                p.Rising[i] = rising * (1f - toTall);
                // the sheet: every saturated level, else by the rule
                bool low = m.RhLow[i] >= SaturatedRh, mid = m.RhMid[i] >= SaturatedRh, hi = m.RhHigh[i] >= SaturatedRh;
                float sc = scCumuliform, ac = 0f, lowS = 0f, highS = 0f;
                if (s > 0f)
                {
                    if (low) lowS = s;
                    if (mid) ac = s;
                    if (hi) highS = s;
                    if (!low && !mid && !hi)
                    {
                        if (m.LowStratiform != null)
                        {
                            float d = Math.Min(s, Math.Max(0f, m.LowStratiform[i]));
                            ac = d;
                            sc = 1f - (1f - sc) * (1f - (s - d));
                        }
                        else sc = 1f - (1f - sc) * (1f - s);
                    }
                }
                p.Sc[i] = sc;
                p.Ac[i] = ac;
                p.Low[i] = lowS;
                p.High[i] = highS;
                p.Nimbo[i] = low && mid && hi && m.PrecipLevel != null && m.PrecipLevel[i] > 0f && m.LowStratiform != null && m.LowStratiform[i] > hp;
                if (cover <= 0f)
                {
                    p.Rising[i] = p.Tall[i] = p.Sc[i] = p.Ac[i] = p.Low[i] = p.High[i] = 0f;
                    p.Nimbo[i] = false;
                }
            }
            return p;
        }

        private static byte Byte(double v)
        {
            return (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
        }

        // The type value of a depth in the cumuliform layer's rising types (humilis .. congestus) and in the tall layer's towers.
        internal static float RisingType(CloudLayerDef cumuliform, float depthM)
        {
            int last = CumuliformLast(cumuliform);
            int first = Math.Min(1, last);
            return WeatherMapSky.TypeForThickness(cumuliform, first, last, depthM / cumuliform.ThicknessM);
        }

        // The cover and type bytes of a layer from the parts (role: WeatherMapSky.RoleOf). todayType: the cheap layer's types of today's chain; fogCover / fogType: the low stratiform rule's
        // output, rain and fog (null: none). False for a layer the map does not drive (it keeps today's maps).
        internal static bool Layer(CloudLayerDef l, WeatherMapSky.Role role, Parts p, byte[] todayType, byte[] fogCover, byte[] fogType, byte[] cover, byte[] type)
        {
            int n = p.N;
            switch (role)
            {
                case WeatherMapSky.Role.Cumuliform:
                {
                    float scValue = Value(l, 0);
                    int alto = AltoIndex(l);
                    float acValue = alto >= 0 ? Value(l, alto) : scValue;
                    for (int i = 0; i < n; i++)
                    {
                        float a = p.Rising[i], b = p.Sc[i], c = p.Ac[i];
                        cover[i] = Byte(1.0 - (1.0 - a) * (1.0 - b) * (1.0 - c));
                        float t = a >= b && a >= c ? (a > 0f ? RisingType(l, p.DepthM[i]) : scValue) : (b >= c ? scValue : acValue);
                        type[i] = Byte(t);
                    }
                    return true;
                }
                case WeatherMapSky.Role.Tall:
                {
                    int last = TallLast(l);
                    for (int i = 0; i < n; i++)
                    {
                        cover[i] = Byte(p.Tall[i]);
                        type[i] = Byte(WeatherMapSky.TypeForThickness(l, 0, last, p.DepthM[i] / l.ThicknessM));
                    }
                    return true;
                }
                case WeatherMapSky.Role.LowStratiform:
                {
                    float stratiformType = l.LowStratiform != null ? l.LowStratiform.PrecipType : 0.5f;
                    for (int i = 0; i < n; i++)
                    {
                        double dc = p.Low[i], dt = p.Nimbo[i] ? 1.0 : stratiformType;
                        double fog = fogCover != null ? fogCover[i] / 255.0 : 0.0;
                        if (fog > dc)
                        {
                            dc = fog;
                            dt = fogType != null ? fogType[i] / 255.0 : dt;
                        }
                        cover[i] = Byte(dc);
                        type[i] = Byte(dt);
                    }
                    return true;
                }
                case WeatherMapSky.Role.High:
                {
                    for (int i = 0; i < n; i++)
                    {
                        cover[i] = Byte(p.High[i]);
                        type[i] = todayType != null ? todayType[i] : (byte)0;
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        // The root offsets (m) of the cumuliform and tall layers' height maps: the base against the layer's inner altitude; the range they are encoded in is RootRangeM.
        internal static float RootRangeM(CloudLayerDef l)
        {
            return Math.Max(1f, l.InnerAltitudeM);
        }

        internal static float[] Roots(CloudLayerDef l, Parts p)
        {
            var r = new float[p.N];
            float range = RootRangeM(l);
            for (int i = 0; i < p.N; i++) r[i] = Math.Max(-range, Math.Min(range, p.BaseM[i] - l.InnerAltitudeM));
            return r;
        }

        // The range the low stratiform layer's root offsets are encoded in: its depth (the low stratiform layer starts at the ground, so the cumuliform layers' inner altitude would allow 1 m).
        internal static float LowStratiformRootRangeM(CloudLayerDef l)
        {
            return Math.Max(1f, l.OuterAltitudeM - l.InnerAltitudeM);
        }

        // The low stratiform layer's root offsets (m): each texel's drawn type (typeBytes, the low stratiform layer's type map of the round; null: the low stratiform rule's precipitation type) is lifted from
        // its cfg base (its interpolated base share of the layer's depth) to the field's cloud base, never lowered, at most the range; the nearer the texel's type is to the low stratiform layer
        // rule's fog type, the less it is lifted (the fog type itself stays on the ground, where the field says fog).
        internal static float[] LowStratiformRoots(CloudLayerDef l, Parts p, byte[] typeBytes)
        {
            var r = new float[p.N];
            int count = l.Types.Count;
            if (count == 0) return r;
            float depth = l.OuterAltitudeM - l.InnerAltitudeM, range = LowStratiformRootRangeM(l);
            float precip = l.LowStratiform != null ? l.LowStratiform.PrecipType : 0.5f, fogX = (l.LowStratiform != null ? l.LowStratiform.FogType : 0f) * (count - 1);
            for (int i = 0; i < p.N; i++)
            {
                float tv = typeBytes != null && i < typeBytes.Length ? typeBytes[i] / 255f : precip;
                float x = Math.Max(0f, Math.Min(count - 1, tv * (count - 1)));
                int ta = Math.Min((int)Math.Floor(x), count - 1), tb = Math.Min(ta + 1, count - 1);
                float f = x - ta;
                float baseShare = l.Types[ta].BaseShare + (l.Types[tb].BaseShare - l.Types[ta].BaseShare) * f;
                float fogWeight = Math.Max(0f, 1f - Math.Abs(x - fogX));
                float lift = (p.BaseM[i] - (l.InnerAltitudeM + baseShare * depth)) * (1f - fogWeight);
                r[i] = Math.Max(0f, Math.Min(range, lift));
            }
            return r;
        }
    }
}
