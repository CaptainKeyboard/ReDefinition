using System;

namespace ReDefinition.Atmospheres
{
    // The sky drawn from the weather map in the game: the layers and forms fed by the package B maps so that regions arise (a closed deck where
    // the map's stratiform amount is high, broken cumulus where its cumuliform amount is, clear sky where its cover is 0) instead of single clouds spread evenly by the old layer maps. A pure function of map-sized arrays (OwnCloudModule fetches the maps and hands the result to the layers); WM1's WeatherMapSky is the
    // weather player's cost prototype this grew from. Only cfg numbers, the maps and numbers of the M1 proposal (marked PROPOSAL) are used.
    //
    // Per map texel (bilinear in the shader, slewed as every map):
    //   cumuliform, uncapped     -> the cumuliform layer (Kerbin-cumulus) on its single-cloud lattice: cover = the cumuliform amount (below the lattice's fair share only a part of the cells are
    //                         alive, so the map's low cumuliform gives gaps and clear patches), type by the depth (top - base) through the type table's thicknesses from the first
    //                         lattice type (humilis) to the last rising one (congestus); deeper than congestus the share goes over to the tall layer (Kerbin-cumulonimbus) between
    //                         congestus' and calvus' thicknesses (cfg type heights), no hard switch.
    //   cumuliform, capped       -> (stable layer at or below the base, no cell) the cumuliform layer's stratiform type (Stratocumulus, its first type) as a CONNECTED sheet: the shader's _CellS
    //                         gives the type values of the stratiform types a weight that takes the map's own value instead of the lattice (CloudCells.cginc CellStratiformWeight).
    //                         Depth rule a: weighted saturate(cumuliform / CELL_ALIVE_F0), the lattice's own fair-sky edge (0.25: below it the cells are isolated); depth rule b
    //                         (PROPOSAL, wolken-m1-typwahl-vorschlag.md 6.1 line 2): always.
    //   sheet, saturated   -> per saturated level (RH >= 0.9999, the M1 count's definition): low the deck layer (its precipitation type, Nimbostratus by the proposal's R9),
    //                         middle the cumuliform layer's Altocumulus as a connected sheet, high the cheap layer (Kerbin-cirrus, today's types kept).
    //   sheet, no level    -> stratiformRule 0: WM1's "the most humid level"; 1 (PROPOSAL A, "condensation level"): a connected Stratocumulus deck in the cumuliform layer at the column's
    //   saturated             convective base (the cloud-topped mixed layer, proposal T5); 2 (PROPOSAL B, "by source"): the deck's share (DeckAmount, lift and fronts) as the
    //                         middle level's Altocumulus sheet, the rest as in A.
    //   depth              -> depthRule 1 (a): the field's depth; a cumuliform column without depth draws the thinnest lattice type (humilis), its body's height a share of the type's
    //                         by its core (CloudCells.cginc CellBody). depthRule 2 (b, PROPOSAL, proposal section 6): cells keep the field's depth, capped columns are sheets, the rest
    //                         get depth = base x (A_MIN + (A_MAX - A_MIN) q RhMid), q = clamp(vigour + NOISE_AMP (n - 0.5)), n a smooth noise on the sphere, capped at the -10 C level.
    //   base               -> the root offset of the cumuliform and tall layers' height maps: base - the layer's inner altitude, in a root range of the inner altitude (a base from the
    //                         ground to twice the inner altitude; clamped beyond). The top term (updraft) is zeroed for these layers: the top is the type's thickness.
    //   union              -> the cumuliform layer's parts in one texel: 1 - (1 - a)(1 - b)(1 - c), the type of the largest part (APPROXIMATION: one type per texel).
    //   storm cells        -> the tall layer draws its towers from the generator's cell list (AnvilMap.PaintCells, each cell one tower, OwnCloudModule) where the list is there: the
    //                         map holds a cell's tower only in the single texel at its centre. Without the list the tall share above is drawn.
    //   deck               -> the larger of the low level's sheet and today's deck rule (rain from the precipitation map, fog from the dew-point spread): the field's low level
    //                         lies below the convective base in raining fronts, so the rain deck would be missing under them.
    //   not drawn          -> the meso pattern and the combination with the field's cover (the amounts are the sky's shares).
    internal static class MapSkyRegions
    {
        internal const string TuneStratiformRule = "cloud.weatherMapSky.stratiformRule", TuneDepthRule = "cloud.weatherMapSky.depthRule";
        // Defaults of the two rules (owner 2026-11-23, "the more realistic", PRUEFPROTOKOLL 105, 111, 112): the stratiform rule B (2: by source, the deck map) and the depth rule a (1: the
        // field's own depth). B + b drew 96 % of the M0 ground view's sky as cloud where the field's cover is 0.51 (B + a: 65 %), because b replaces the field's convective depth by
        // the calibrated stand-in (up to 4.55 x the base) and makes every capped cumuliform a connected sheet.
        internal const float StratiformRuleDefault = 2f, DepthRuleDefault = 1f;
        internal const float SaturatedRh = WeatherMapSky.SaturatedRh;
        // The lattice's fair-sky edge (CloudCells.cginc CELL_ALIVE_F0): below this share only share / F0 of the cells hold a cloud.
        internal const float LatticeFairShare = 0.25f;

        // The calibrated depth rule (wolken-m1-typwahl-vorschlag.md section 6.1, PROPOSAL, not decided): its levers as the proposal's script has them (calibrated_depth.py).
        internal const double CalAMin = 0.25, CalAMax = 4.55, CalNoiseAmp = 0.5, CalPatchRad = 0.2, CalIceK = 263.15;
        internal const uint CalSeed = 0x5EEDC10Du;

        // The deck's base from the weather field (owner 2026-11-23 "Ja, Feld-Basis", after "Warum sind die Regenwolken so niedrig?"): 1 lifts the rain and snow deck's types to the
        // field's cloud base (DeckRoots; the fog type stays on the ground), 0 the deck at its cfg type shares (about 170 m at KSC where the field says 899 m; the condensation
        // physics and the place variation pushed it onto the ground there, the owner's "Nebel"). On by default since the owner chose the height factor on pictures that all
        // carried it (2026-11-23 16:02, PRUEFPROTOKOLL 139; his "Ja, Feld-Basis" asked for pictures first, then the default).
        internal const string TuneDeckBase = "cloud.weatherMapSky.deckBase";
        internal const float DeckBaseDefault = 1f;
        internal static bool DeckBase { get { return Tuning.Get(TuneDeckBase, DeckBaseDefault) > 0.5f; } }

        internal static int StratiformRule { get { return (int)Math.Round(Tuning.Get(TuneStratiformRule, StratiformRuleDefault)); } }
        internal static int DepthRule { get { return (int)Math.Round(Tuning.Get(TuneDepthRule, DepthRuleDefault)); } }

        // The maps, one value per texel (row 0 at the north pole, column 0 at -180). Heights as shares of the troposphere, as the field gives them.
        internal sealed class Inputs
        {
            internal int Width, Height;
            internal float[] Cover, Cumuliform, Stratiform, Deck, BaseShare, TopShare, StableShare, RhLow, RhMid, RhHigh, Descent, Instability, Uplift, Reach, PrecipLevel, SurfaceTempK;
            internal double TropopauseM, LapseKPerM, DayS, Ut;
        }

        // Per texel, the parts the layers take (set by Plan).
        internal sealed class Parts
        {
            internal int N;
            // The cumuliform on the lattice (cumuliform layer) and in the tall layer, the depth they are drawn with (m), the base (m).
            internal float[] Lattice, Tall, DepthM, BaseM;
            // The cumuliform layer's connected sheets: Stratocumulus (capped cumuliform and the unsaturated sheet of rules A / B) and Altocumulus (the middle level).
            internal float[] Sc, Ac;
            // The deck layer's sheet (low level saturated) and whether it is nimbostratus; the cheap layer's sheet.
            internal float[] Low, High;
            internal bool[] Nimbo;
            // The rules in force when the parts were made.
            internal int StratiformRule, DepthRule;
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

        // The cumuliform layer's last lattice type (the end of the rising run before the alto type) and the tall layer's last tower type (before its anvil).
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
        internal static Parts Plan(Inputs m, CloudLayerDef cumuliform, CloudLayerDef tall, int stratiformRule, int depthRule, Parts into = null)
        {
            int n = m.Width * m.Height;
            Parts p = into != null && into.N == n ? into : new Parts
            {
                N = n, Lattice = new float[n], Tall = new float[n], DepthM = new float[n], BaseM = new float[n], Sc = new float[n], Ac = new float[n], Low = new float[n],
                High = new float[n], Nimbo = new bool[n]
            };
            p.StratiformRule = stratiformRule;
            p.DepthRule = depthRule;
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
                // the cumuliform's split between the lattice and the stratocumulus sheet
                float scCumuliform = 0f;
                if (capped) scCumuliform = depthRule == 2 ? hp : hp * Saturate(hp / LatticeFairShare);
                float onLattice = hp - scCumuliform;
                if (depthRule == 2 && !cell && !capped && onLattice > 0f) depth = CalibratedDepthM(m, i, zb);
                p.DepthM[i] = (float)depth;
                float toTall = tall != null ? (cumuliform == null ? 1f : Saturate((depth - handFrom) / (handTo - handFrom))) : 0f;
                p.Tall[i] = onLattice * toTall;
                p.Lattice[i] = onLattice * (1f - toTall);
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
                        if (stratiformRule == 0)
                        {
                            if (m.RhLow[i] >= m.RhMid[i] && m.RhLow[i] >= m.RhHigh[i]) lowS = s;
                            else if (m.RhMid[i] >= m.RhHigh[i]) ac = s;
                            else highS = s;
                        }
                        else if (stratiformRule == 2 && m.Deck != null)
                        {
                            float d = Math.Min(s, Math.Max(0f, m.Deck[i]));
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
                p.Nimbo[i] = low && mid && hi && m.PrecipLevel != null && m.PrecipLevel[i] > 0f && m.Deck != null && m.Deck[i] > hp;
                if (cover <= 0f)
                {
                    p.Lattice[i] = p.Tall[i] = p.Sc[i] = p.Ac[i] = p.Low[i] = p.High[i] = 0f;
                    p.Nimbo[i] = false;
                }
            }
            return p;
        }

        // The depth of the calibrated rule (proposal 6.1 lines 3-4): vigour from the instability and the net ascent against the descent, a smooth noise, the middle level's humidity;
        // the top at most at the -10 C level (a tower without a cell stays a cumulus) and the tropopause.
        internal static double CalibratedDepthM(Inputs m, int i, double zb)
        {
            double up = m.Uplift != null ? m.Uplift[i] : 0.0, down = m.Descent != null ? m.Descent[i] : 0.0, inst = m.Instability != null ? m.Instability[i] : 0.0;
            double asc = Math.Max(0.0, Math.Min(1.0, up - down));
            double vig = Math.Max(0.0, Math.Min(1.0, inst + asc - down));
            int x = i % m.Width, y = i / m.Width;
            double latDeg = 90.0 - (y + 0.5) * 180.0 / m.Height, lonDeg = -180.0 + (x + 0.5) * 360.0 / m.Width;
            double noise = SmoothNoise(latDeg, lonDeg, m.Ut, m.DayS);
            double q = Math.Max(0.0, Math.Min(1.0, vig + CalNoiseAmp * (noise - 0.5)));
            double a = CalAMin + (CalAMax - CalAMin) * q * Math.Max(0.0, Math.Min(1.0, m.RhMid[i]));
            double dep = a * zb;
            double tK = m.SurfaceTempK != null ? m.SurfaceTempK[i] : 288.0;
            double zIce = m.LapseKPerM > 0.0 ? Math.Max((tK - CalIceK) / m.LapseKPerM, 0.0) : double.PositiveInfinity;
            double cap = Math.Max(zIce - zb, CalAMin * zb);
            return Math.Max(0.0, Math.Min(Math.Min(dep, cap), m.TropopauseM - zb));
        }

        // The proposal script's hash and value noise (calibrated_depth.py _hash, _value3, smooth_noise): a hashed value per lattice corner of spacing CalPatchRad on the unit
        // sphere, blended trilinearly, cross-faded to a new pattern every body day.
        internal static double Hash(long i, long j, long k, long s)
        {
            uint h = (uint)((i * 0x8DA6B343L + j * 0xD8163841L + k * 0xCB1AB31FL + s) & 0xFFFFFFFFL);
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h / 4294967295.0;
        }

        private static double Value3(double x, double y, double z, long s)
        {
            double xi = Math.Floor(x), yi = Math.Floor(y), zi = Math.Floor(z);
            double fx = x - xi, fy = y - yi, fz = z - zi;
            double ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy), uz = fz * fz * (3 - 2 * fz);
            long ix = (long)xi, iy = (long)yi, iz = (long)zi;
            double r = 0.0;
            for (int dx = 0; dx < 2; dx++)
            for (int dy = 0; dy < 2; dy++)
            for (int dz = 0; dz < 2; dz++)
            {
                double w = (dx == 1 ? ux : 1 - ux) * (dy == 1 ? uy : 1 - uy) * (dz == 1 ? uz : 1 - uz);
                r += w * Hash(ix + dx, iy + dy, iz + dz, s);
            }
            return r;
        }

        internal static double SmoothNoise(double latDeg, double lonDeg, double t, double periodS)
        {
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            double x = Math.Cos(la) * Math.Cos(lo) / CalPatchRad, y = Math.Cos(la) * Math.Sin(lo) / CalPatchRad, z = Math.Sin(la) / CalPatchRad;
            double tt = periodS > 0.0 ? t / periodS : 0.0;
            double k = Math.Floor(tt);
            double f = tt - k;
            f = f * f * (3 - 2 * f);
            long kk = (long)k;
            double n0 = Value3(x, y, z, (CalSeed + kk * 7919L) & 0xFFFFFFFFL);
            double n1 = Value3(x, y, z, (CalSeed + (kk + 1) * 7919L) & 0xFFFFFFFFL);
            double n = (1 - f) * n0 + f * n1;
            double sd = Math.Sqrt((1 - f) * (1 - f) + f * f);
            return Math.Max(0.0, Math.Min(1.0, 0.5 + (n - 0.5) / sd));
        }

        private static byte Byte(double v)
        {
            return (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
        }

        // The type value of a depth in the cumuliform layer's lattice types (humilis .. congestus) and in the tall layer's towers.
        internal static float LatticeType(CloudLayerDef cumuliform, float depthM)
        {
            int last = CumuliformLast(cumuliform);
            int first = Math.Min(1, last);
            return WeatherMapSky.TypeForThickness(cumuliform, first, last, depthM / cumuliform.ThicknessM);
        }

        // The cover and type bytes of a layer from the parts (role: WeatherMapSky.RoleOf). todayType: the cheap layer's types of today's chain; fogCover / fogType: the deck rule's
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
                        float a = p.Lattice[i], b = p.Sc[i], c = p.Ac[i];
                        cover[i] = Byte(1.0 - (1.0 - a) * (1.0 - b) * (1.0 - c));
                        float t = a >= b && a >= c ? (a > 0f ? LatticeType(l, p.DepthM[i]) : scValue) : (b >= c ? scValue : acValue);
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
                case WeatherMapSky.Role.Deck:
                {
                    float stratiformType = l.Deck != null ? l.Deck.PrecipType : 0.5f;
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

        // The range the deck's root offsets are encoded in: its depth (the deck starts at the ground, so the cumuliform layers' inner altitude would allow 1 m).
        internal static float DeckRootRangeM(CloudLayerDef l)
        {
            return Math.Max(1f, l.OuterAltitudeM - l.InnerAltitudeM);
        }

        // The deck's root offsets (m, TuneDeckBase): each texel's drawn type (typeBytes, the deck's type map of the round; null: the deck rule's precipitation type) is lifted from
        // its cfg base (its interpolated base share of the layer's depth) to the field's cloud base, never lowered, at most the range; the nearer the texel's type is to the deck
        // rule's fog type, the less it is lifted (the fog type itself stays on the ground, where the field says fog).
        internal static float[] DeckRoots(CloudLayerDef l, Parts p, byte[] typeBytes)
        {
            var r = new float[p.N];
            int count = l.Types.Count;
            if (count == 0) return r;
            float depth = l.OuterAltitudeM - l.InnerAltitudeM, range = DeckRootRangeM(l);
            float precip = l.Deck != null ? l.Deck.PrecipType : 0.5f, fogX = (l.Deck != null ? l.Deck.FogType : 0f) * (count - 1);
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

        // The shader's stratiform types of the cumuliform layer (_CellS, CloudCells.cginc CellStratiformWeight): x 1 on, y the stratiform type at the low end (Stratocumulus), z the one at the
        // high end (Altocumulus), w the width of a type step (1 / (count - 1)). Zero for every other layer and while the switch is off.
        internal static UnityEngine.Vector4 StratiformTypes(CloudLayerDef l)
        {
            if (l == null || WeatherMapSky.RoleOf(l) != WeatherMapSky.Role.Cumuliform || l.Types.Count < 2) return UnityEngine.Vector4.zero;
            int alto = AltoIndex(l);
            return new UnityEngine.Vector4(1f, Value(l, 0), alto >= 0 ? Value(l, alto) : 2f, 1f / (l.Types.Count - 1));
        }
    }
}
