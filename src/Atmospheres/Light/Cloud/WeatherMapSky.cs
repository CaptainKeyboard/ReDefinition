using System;

namespace ReDefinition.Atmospheres
{
    // WM1 cost prototype (master plan section 26, 2026-11-20): today's layers fed by the package B column quantities (SkyColumn) instead of today's layer maps. A pure function
    // of map-sized arrays, so the map dump that feeds the weather player (tests SkyMapsExplore.DumpTheWeatherMapSky) builds both skies from the same field; the game's module
    // calls it once per map round (OwnCloudModule.MapSky.cs). The sky of the game since 2026-11-22 (the key that switched it off is removed, 2026-11-23).
    //
    // What goes where (only the cfg's own numbers are used; every place the renderer cannot say what the column says is named):
    //   cumuliform amount  -> the cover of the cumuliform layer (the layer with an alto type: Kerbin-cumulus) or, where the column's depth is more than that layer's shell is thick, of the tall
    //                   layer (the layer with an anvil type: Kerbin-cumulonimbus). The depth (top - base) picks the type value: the type table's base and top shares are interpolated
    //                   between neighbouring types (as the shader does), so the value whose interpolated thickness equals the depth is taken. APPROXIMATION: a column thinner than the
    //                   thinnest type (Stratocumulus, 0.11 of 5.7 km = 627 m; the cumuliform water without depth) is drawn that thin; a tall column thinner than Cumulonimbus calvus
    //                   (0.85 of 9.3 km) is drawn as calvus. The base: the height map's root offset (R channel) moves the layer's types by base - inner altitude, clamped to the root range
    //                   (heightRoot, the cfg's); APPROXIMATION where the base lies further from the shell's floor than that.
    //   sheet amount -> per saturated level (relative humidity >= SaturatedRh, the M1 count's definition): low -> the deck layer (the layer with a deck rule and no anvil), its precipitation
    //                   type (Stratus) or, where all three levels are saturated, it precipitates and the deck amount exceeds the cumuliform amount (the M1 proposal's nimbostratus rule R9),
    //                   its last type (Nimbostratus); middle -> the cumuliform layer's alto type (Altocumulus), drawn in the same texel as the cumuliform: the texel's cover is the union
    //                   1 - (1 - cumuliform)(1 - sheet) with the larger part's type (APPROXIMATION: one type per texel); high -> the cheap layer (Kerbin-cirrus), today's type kept.
    //                   A sheet at no saturated level goes to the level of the largest humidity (APPROXIMATION: the field's sheet is drawn at the level nearest saturation).
    //                   The sheet is put at every saturated level (the overlap rule of the M1 count's Earth comparison), so the layers' union can exceed the cover.
    //   fog          -> today's fog part of the deck rule (not a package B quantity; the same input in both skies).
    //   not drawn    -> the anvil (package B has none), the meso pattern and the combination with the field's cover (the column amounts are already the sky's shares).
    internal static class WeatherMapSky
    {
        // The level counts as saturated from here (RESULTS.md of the M1 count, section 3: the generator caps humidity at 1).
        internal const float SaturatedRh = 0.9999f;

        // The column quantities of a map (row 0 at the north pole, column 0 at -180), one value per texel.
        internal sealed class Columns
        {
            internal int Width, Height;
            internal float[] Cover, Cumuliform, Sheet, Deck, BaseM, TopM, RhLow, RhMid, RhHigh, PrecipMmH;
        }

        internal sealed class Result
        {
            // Per layer: the cover and type bytes, and the root offset bytes of the height map's R channel (null: the layer's height map is not changed).
            internal byte[][] Cover, Type, Root;
            // Per texel: the cumuliform amount the union rule dropped from the texel's type (diagnosis of the one-type-per-texel approximation), and where the sheet went (bit 1 low, 2 middle, 4 high).
            internal float[] CumuliformDropped;
            internal byte[] SheetLevels;
        }

        internal enum Role { None, Deck, Cumuliform, Tall, High }

        internal static Role RoleOf(CloudLayerDef l)
        {
            if (AnvilMap.AnvilTypeIndex(l) >= 0) return Role.Tall;
            if (l.AltoType >= 0f) return Role.Cumuliform;
            if (l.Deck != null) return Role.Deck;
            if (l.Kind == LayerKind.Cheap) return Role.High;
            return Role.None;
        }

        // The type value (0..1) of the layer whose interpolated thickness share (top - base) is the share s, searched between the types first .. last (indices); clamped to their range.
        internal static float TypeForThickness(CloudLayerDef l, int first, int last, float s)
        {
            int n = l.Types.Count;
            if (n < 2) return 0f;
            if (s <= Thick(l, first)) return first / (float)(n - 1);
            for (int k = first; k < last; k++)
            {
                float a = Thick(l, k), b = Thick(l, k + 1);
                if (s <= b && b > a) return (k + (s - a) / (b - a)) / (n - 1);
            }
            return last / (float)(n - 1);
        }

        private static float Thick(CloudLayerDef l, int k)
        {
            return l.Types[k].TopShare - l.Types[k].BaseShare;
        }

        // The last type of a run of types whose thickness rises from the first (the cumuliform family before the alto type; the calvus-incus pair before the anvil).
        private static int RisingRun(CloudLayerDef l, int first, int stop)
        {
            int k = first;
            while (k + 1 < stop && Thick(l, k + 1) > Thick(l, k)) k++;
            return k;
        }

        private static byte Byte(double v)
        {
            return (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
        }

        // today: per layer today's cover and type bytes (the fog part of the deck and the cirrus types are taken from them); fogCover / fogType: the deck rule's fog part alone for the
        // deck layer (null: none). heightRootM: per layer the root range of its height map in m (the cfg's heightRoot; 0: no root offset).
        internal static Result Build(CloudLayerDef[] layers, Columns c, byte[][] todayType, byte[] fogCover, byte[] fogType, float[] heightRootM)
        {
            int n = c.Width * c.Height, count = layers.Length;
            var r = new Result { Cover = new byte[count][], Type = new byte[count][], Root = new byte[count][], CumuliformDropped = new float[n], SheetLevels = new byte[n] };
            int deck = -1, cumuliform = -1, tall = -1, high = -1;
            for (int l = 0; l < count; l++)
            {
                r.Cover[l] = new byte[n];
                r.Type[l] = todayType[l] != null ? (byte[])todayType[l].Clone() : new byte[n];
                switch (RoleOf(layers[l]))
                {
                    case Role.Deck: if (deck < 0) deck = l; break;
                    case Role.Cumuliform: if (cumuliform < 0) cumuliform = l; break;
                    case Role.Tall: if (tall < 0) tall = l; break;
                    case Role.High: if (high < 0) high = l; break;
                }
            }
            CloudLayerDef hl = cumuliform >= 0 ? layers[cumuliform] : null, tl = tall >= 0 ? layers[tall] : null, dl = deck >= 0 ? layers[deck] : null;
            int cumuliformAlto = hl != null ? (int)Math.Round(Math.Min(1f, hl.AltoType) * (hl.Types.Count - 1)) : -1;
            int cumuliformLast = hl != null ? RisingRun(hl, 0, cumuliformAlto >= 0 ? cumuliformAlto : hl.Types.Count) : -1;
            int tallAnvil = tl != null ? AnvilMap.AnvilTypeIndex(tl) : -1;
            int tallLast = tl != null ? RisingRun(tl, 0, tallAnvil >= 0 ? tallAnvil : tl.Types.Count) : -1;
            float cumuliformThick = hl != null ? hl.ThicknessM : 0f;
            float cumuliformAltoValue = hl != null && cumuliformAlto >= 0 ? cumuliformAlto / (float)(hl.Types.Count - 1) : 0f;
            float deckLast = 1f, deckSheet = dl != null && dl.Deck != null ? dl.Deck.PrecipType : 0.5f;
            if (cumuliform >= 0 && heightRootM[cumuliform] > 0f) r.Root[cumuliform] = new byte[n];
            if (tall >= 0 && heightRootM[tall] > 0f) r.Root[tall] = new byte[n];
            for (int i = 0; i < n; i++)
            {
                float cover = c.Cover[i], h = Math.Max(0f, c.Cumuliform[i]), s = Math.Max(0f, c.Sheet[i]);
                float baseM = c.BaseM[i], depth = Math.Max(0f, c.TopM[i] - baseM);
                // The cumuliform amount: the cumuliform layer or, deeper than its shell, the tall layer.
                bool toTall = tl != null && (hl == null || depth > cumuliformThick);
                double cumuliformCover = 0.0, cumuliformType = 0.0;
                if (h > 0f && toTall)
                {
                    r.Cover[tall][i] = Byte(h);
                    r.Type[tall][i] = Byte(TypeForThickness(tl, 0, tallLast, depth / tl.ThicknessM));
                }
                else if (h > 0f && hl != null)
                {
                    cumuliformCover = h;
                    cumuliformType = TypeForThickness(hl, 0, cumuliformLast, depth / cumuliformThick);
                }
                if (tall >= 0 && r.Root[tall] != null)
                    r.Root[tall][i] = (byte)Math.Round(128.0 + 127.0 * Math.Max(-1.0, Math.Min(1.0, (baseM - tl.InnerAltitudeM) / heightRootM[tall])));
                if (cumuliform >= 0 && r.Root[cumuliform] != null)
                    r.Root[cumuliform][i] = (byte)Math.Round(128.0 + 127.0 * Math.Max(-1.0, Math.Min(1.0, (baseM - hl.InnerAltitudeM) / heightRootM[cumuliform])));
                // The sheet: every saturated level, else the level of the largest humidity.
                bool low = c.RhLow[i] >= SaturatedRh, mid = c.RhMid[i] >= SaturatedRh, hi = c.RhHigh[i] >= SaturatedRh;
                if (s > 0f && !low && !mid && !hi)
                {
                    if (c.RhLow[i] >= c.RhMid[i] && c.RhLow[i] >= c.RhHigh[i]) low = true;
                    else if (c.RhMid[i] >= c.RhHigh[i]) mid = true;
                    else hi = true;
                }
                if (s <= 0f) low = mid = hi = false;
                r.SheetLevels[i] = (byte)((low ? 1 : 0) | (mid ? 2 : 0) | (hi ? 4 : 0));
                if (deck >= 0)
                {
                    double dc = 0.0, dt = fogType != null ? fogType[i] / 255.0 : 0.0;
                    if (low)
                    {
                        bool nimbo = c.RhLow[i] >= SaturatedRh && c.RhMid[i] >= SaturatedRh && c.RhHigh[i] >= SaturatedRh && c.PrecipMmH[i] > 0f && c.Deck[i] > h;
                        dc = s;
                        dt = nimbo ? deckLast : deckSheet;
                    }
                    double fog = fogCover != null ? fogCover[i] / 255.0 : 0.0;
                    if (fog > dc)
                    {
                        dc = fog;
                        dt = fogType[i] / 255.0;
                    }
                    r.Cover[deck][i] = Byte(dc);
                    r.Type[deck][i] = Byte(dt);
                }
                if (cumuliform >= 0)
                {
                    double sc = mid ? s : 0.0;
                    double union = 1.0 - (1.0 - cumuliformCover) * (1.0 - sc);
                    r.Cover[cumuliform][i] = Byte(union);
                    if (sc > cumuliformCover)
                    {
                        r.Type[cumuliform][i] = Byte(cumuliformAltoValue);
                        r.CumuliformDropped[i] = (float)cumuliformCover;
                    }
                    else
                    {
                        r.Type[cumuliform][i] = Byte(cumuliformType);
                    }
                }
                if (high >= 0) r.Cover[high][i] = Byte(hi ? s : 0.0);
            }
            return r;
        }
    }
}
