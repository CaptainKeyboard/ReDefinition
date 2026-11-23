using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The sky drawn from the weather map. Where a round has no weather data the layers take the layer maps of today's chain until the next round. Once per map round the package B
    // maps are fetched and cut into the layers' parts (MapSkyRegions.Plan); each layer the map drives then takes its cover and type from them instead of today's chain (no meso stage,
    // no combination with the field's cover), and the next height build puts the convective base into the heap and tall layers' root offsets.
    internal sealed partial class OwnCloudModule
    {
        // The maps of a round, in this order (MapSkyKinds); a map the field cannot give yet ends the round's map sky (today's chain draws until the next round).
        private static readonly WeatherMapKind[] MapSkyKinds =
        {
            WeatherMapKind.ConvectiveAmount, WeatherMapKind.StratiformAmount, WeatherMapKind.DeckAmount, WeatherMapKind.ConvectiveBase, WeatherMapKind.ConvectiveTop,
            WeatherMapKind.StableShare, WeatherMapKind.HumidityLow, WeatherMapKind.HumidityMid, WeatherMapKind.HumidityHigh, WeatherMapKind.Descent, WeatherMapKind.Instability,
            WeatherMapKind.Uplift, WeatherMapKind.CellReach, WeatherMapKind.SurfaceTemperature
        };

        private readonly WeatherMap[] mapSkyMaps = new WeatherMap[14];
        private readonly MapSkyRegions.Inputs mapSkyInputs = new MapSkyRegions.Inputs();
        private MapSkyRegions.Parts mapSkyParts;
        // Whether this round draws the map sky (decided at its fog step), and the parts' time.
        private bool mapSkyRound;
        private double mapSkyUt = double.NaN;
        private readonly byte[][] mapSkyCover = new byte[CloudLayerDef.MaxLayers][], mapSkyType = new byte[CloudLayerDef.MaxLayers][], mapSkyFogCover = new byte[CloudLayerDef.MaxLayers][],
            mapSkyFogType = new byte[CloudLayerDef.MaxLayers][];
        private readonly WeatherMap[] mapSkyCoverMaps = new WeatherMap[CloudLayerDef.MaxLayers];
        // The last parts and their layers' root offsets for the height build (null: today's heights).
        private volatile MapSkyRegions.Parts mapSkyPartsForHeights;
        private string mapSkyState;
        // Whether the last height build began with the map sky's bases (BuildHeights builds again when that changes), and whether the last APPLIED one had them.
        private bool heightsHadMapSky, heightsBuildingMapSky, heightsAppliedMapSky;

        // The proof of what the pass drew (pass.json "mapSky", the probe's row T09.pin.<label>.map_sky): the number of finished map rounds that published the map sky, and the
        // last finished round's state: -1 today's chain, otherwise sheet rule x 10 + depth rule.
        internal int MapSkyRounds { get; private set; }
        private int lastRoundMapSky = -1;
        internal int LastRoundMapSky { get { return lastRoundMapSky; } private set { lastRoundMapSky = value; } }

        // The state the rule keys ask for now, in the form of LastRoundMapSky.
        internal static int MapSkyWanted
        {
            get { return MapSkyRegions.SheetRule * 10 + MapSkyRegions.DepthRule; }
        }

        private void MapSkyRoundEnded()
        {
            LastRoundMapSky = mapSkyRound && mapSkyParts != null ? mapSkyParts.SheetRule * 10 + mapSkyParts.DepthRule : -1;
            if (LastRoundMapSky >= 0) MapSkyRounds++;
        }

        // Whether the last map round and the applied heights are those the keys ask for (why: the first that is not).
        internal bool MapSkySettled(out string why)
        {
            why = null;
            int wanted = MapSkyWanted;
            if (LastRoundMapSky != wanted) why = "the last map round drew sky state " + LastRoundMapSky + ", the keys ask for " + wanted + " (-1: today's chain; else sheet rule x 10 + depth rule)";
            else if (heightsAppliedMapSky != (wanted >= 0)) why = "the applied height maps were built " + (heightsAppliedMapSky ? "with" : "without") + " the map's bases";
            return why == null;
        }

        private string MapSkyJson()
        {
            return ",\n \"mapSky\": {\"wanted\": " + MapSkyWanted + ", \"lastRound\": " + LastRoundMapSky + ", \"rounds\": " + MapSkyRounds + ", \"heightsWithBases\": "
                   + (heightsAppliedMapSky ? "true" : "false") + ", \"partsUt\": " + (double.IsNaN(mapSkyUt) ? "null" : mapSkyUt.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) + "}";
        }

        // The round's map sky: the maps and the parts. Called at the round's fog step, after the coverage and the precipitation level were fetched.
        private void FetchMapSky(CelestialBody b, IWeatherField field, double ut)
        {
            mapSkyRound = false;
            if (!fetchHaveShared || !fetchHaveType) return;
            GeneratorField gf = GeneratorModule.Current;
            GeneratorState gs = gf != null ? gf.StateOf(b.flightGlobalsIndex) : null;
            string why = null;
            if (gs == null || gs.Broken) why = "no generator state for " + b.bodyName;
            for (int k = 0; why == null && k < MapSkyKinds.Length; k++)
            {
                WeatherMap m = mapSkyMaps[k] ?? (mapSkyMaps[k] = new WeatherMap());
                if (!field.GetMap(b.flightGlobalsIndex, MapSkyKinds[k], ut, m) || m.Width != MapWidth || m.Height != MapHeight) why = "the field has no " + MapSkyKinds[k] + " map yet";
            }
            if (why != null)
            {
                if (mapSkyState != why) AtmospheresLog.Say("own clouds: the sky from the weather map waits (" + why + "); today's maps draw meanwhile");
                mapSkyState = why;
                return;
            }
            MapSkyRegions.Inputs m0 = mapSkyInputs;
            m0.Width = MapWidth;
            m0.Height = MapHeight;
            m0.Cover = coverageMap.Values;
            m0.Heap = mapSkyMaps[0].Values;
            m0.Sheet = mapSkyMaps[1].Values;
            m0.Deck = mapSkyMaps[2].Values;
            m0.BaseShare = mapSkyMaps[3].Values;
            m0.TopShare = mapSkyMaps[4].Values;
            m0.StableShare = mapSkyMaps[5].Values;
            m0.RhLow = mapSkyMaps[6].Values;
            m0.RhMid = mapSkyMaps[7].Values;
            m0.RhHigh = mapSkyMaps[8].Values;
            m0.Descent = mapSkyMaps[9].Values;
            m0.Instability = mapSkyMaps[10].Values;
            m0.Uplift = mapSkyMaps[11].Values;
            m0.Reach = mapSkyMaps[12].Values;
            m0.SurfaceTempK = mapSkyMaps[13].Values;
            m0.PrecipLevel = typeMap.Values;
            m0.TropopauseM = gs.Parameters.TropopauseM;
            m0.LapseKPerM = gs.Parameters.LapseKPerM;
            m0.DayS = b.rotationPeriod;
            m0.Ut = ut;
            CloudLayerDef heap = null, tall = null;
            for (int l = 0; l < stack.LayerCount; l++)
            {
                WeatherMapSky.Role role = WeatherMapSky.RoleOf(stack.LayerAt(l));
                if (role == WeatherMapSky.Role.Heap && heap == null) heap = stack.LayerAt(l);
                if (role == WeatherMapSky.Role.Tall && tall == null) tall = stack.LayerAt(l);
            }
            // A new Parts each round: the height build of the last round may still read the one before on its worker.
            mapSkyParts = MapSkyRegions.Plan(m0, heap, tall, MapSkyRegions.SheetRule, MapSkyRegions.DepthRule);
            mapSkyPartsForHeights = mapSkyParts;
            mapSkyUt = ut;
            mapSkyRound = true;
            string state = "on (sheet rule " + mapSkyParts.SheetRule + ", depth rule " + mapSkyParts.DepthRule + ")";
            if (mapSkyState != state) AtmospheresLog.Say("own clouds: the sky from the weather map " + state + " at ut " + ut.ToString("0"));
            mapSkyState = state;
        }

        // A layer of a map-sky round: its cover and type from the parts (the deck keeps today's fog part). False where the map does not drive the layer (it keeps today's chain).
        private bool MapSkyLayer(int l, CloudLayerDef def, WeatherDeckRule deck, byte[] rawCover, byte[] todayType, ref byte[] cover, ref byte[] typeBytes, ref WeatherMap coverSource)
        {
            if (!mapSkyRound || mapSkyParts == null) return false;
            WeatherMapSky.Role role = WeatherMapSky.RoleOf(def);
            if (role == WeatherMapSky.Role.None) return false;
            // The tall layer with the cell list: each cell painted as one tower (AnvilMap.PaintCells), not the map's single tower texel; the map's tall share comes from the same cells.
            if (role == WeatherMapSky.Role.Tall && cellLayerNow[l]) return false;
            int n = MapWidth * MapHeight;
            byte[] c = mapSkyCover[l] ?? (mapSkyCover[l] = new byte[n]);
            byte[] t = mapSkyType[l] ?? (mapSkyType[l] = new byte[n]);
            byte[] fogC = null, fogT = null;
            if (role == WeatherMapSky.Role.Deck && deck != null && rawCover != null)
            {
                fogC = mapSkyFogCover[l] ?? (mapSkyFogCover[l] = new byte[n]);
                fogT = mapSkyFogType[l] ?? (mapSkyFogType[l] = new byte[n]);
                // The deck rule as today, its rain and fog parts (from the field's precipitation level and dew-point spread maps): the field's low level lies below the convective base
                // in the raining fronts (RhLow 0.96 under a saturated middle and high level at KSC), so without this the rain would fall from a sky with no low deck.
                deck.Apply(rawCover, typeMap.Values, fogSpreadK, fogWind, fogC, fogT, CloudLookTuning.DeckPrecipOf(def.Name), CloudLookTuning.DeckFogOf(def.Name));
                float deckRain = def.RainType;
                if (deckRain < 1f)
                    for (int i = 0; i < n; i++) fogT[i] = (byte)Math.Round(fogT[i] * deckRain);
            }
            if (!MapSkyRegions.Layer(def, role, mapSkyParts, todayType, fogC, fogT, c, t)) return false;
            WeatherMap cm = mapSkyCoverMaps[l] ?? (mapSkyCoverMaps[l] = new WeatherMap());
            cm.Resize(MapWidth, MapHeight);
            for (int i = 0; i < n; i++) cm.Values[i] = c[i] / 255f;
            cover = c;
            typeBytes = t;
            coverSource = cm;
            return true;
        }

        // The height build's root offsets of a layer from the last map-sky parts (null: the layer keeps today's heights), and the range they are encoded in.
        // l: the layer's index in the stack (the deck's type map of the round is read from it).
        internal float[] MapSkyRoots(int l, CloudLayerDef def, out float rangeM)
        {
            rangeM = 0f;
            MapSkyRegions.Parts p = mapSkyPartsForHeights;
            if (p == null || def == null) return null;
            WeatherMapSky.Role role = WeatherMapSky.RoleOf(def);
            // The deck follows the field's base too (owner 2026-11-23, MapSkyRegions.TuneDeckBase).
            if (role == WeatherMapSky.Role.Deck && MapSkyRegions.DeckBase)
            {
                rangeM = MapSkyRegions.DeckRootRangeM(def);
                byte[] deckType = l >= 0 && l < mapSkyType.Length ? mapSkyType[l] : null;
                return MapSkyRegions.DeckRoots(def, p, deckType);
            }
            if (role != WeatherMapSky.Role.Heap && role != WeatherMapSky.Role.Tall) return null;
            rangeM = MapSkyRegions.RootRangeM(def);
            return MapSkyRegions.Roots(def, p);
        }

        // The pinned capture's map patches: the field's cover, heap and sheet amounts and the base, top and cells' reach as bytes (0..1 of their range) into the dump folder.
        private void WriteMapSkyDump(string dir)
        {
            // Only the published parts and the map copies they hold: the fetch buffers may be refilled by a round in flight.
            MapSkyRegions.Parts p = mapSkyParts;
            if (p == null) return;
            try
            {
                if (p.MapCover != null) WriteScaled(dir, "wms-cover.u8", p.MapCover, 1f);
                if (p.MapHeap != null) WriteScaled(dir, "wms-ConvectiveAmount.u8", p.MapHeap, 1f);
                if (p.MapSheet != null) WriteScaled(dir, "wms-StratiformAmount.u8", p.MapSheet, 1f);
                if (p.MapBase != null) WriteScaled(dir, "wms-ConvectiveBase.u8", p.MapBase, 1f);
                if (p.MapTop != null) WriteScaled(dir, "wms-ConvectiveTop.u8", p.MapTop, 1f);
                if (p.MapReach != null) WriteScaled(dir, "wms-CellReach.u8", p.MapReach, 1f);
                {
                    WriteScaled(dir, "wms-part-lattice.u8", p.Lattice, 1f);
                    WriteScaled(dir, "wms-part-tall.u8", p.Tall, 1f);
                    WriteScaled(dir, "wms-part-sc.u8", p.Sc, 1f);
                    WriteScaled(dir, "wms-part-ac.u8", p.Ac, 1f);
                    WriteScaled(dir, "wms-part-low.u8", p.Low, 1f);
                    WriteScaled(dir, "wms-part-high.u8", p.High, 1f);
                    WriteScaled(dir, "wms-part-depth.u8", p.DepthM, 1f / 12000f);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "wms.json"), "{\"ut\": " + p.Ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + ", \"sheetRule\": " + p.SheetRule + ", \"depthRule\": " + p.DepthRule + ", \"tropopauseM\": "
                        + p.TropopauseM.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ", \"depthScaleM\": 12000, \"lastRound\": " + LastRoundMapSky
                        + ", \"rounds\": " + MapSkyRounds + "}\n");
                }
            }
            catch (Exception e)
            {
                AtmospheresLog.Say("own clouds: the map sky dump failed: " + e.Message);
            }
        }

        private static void WriteScaled(string dir, string name, float[] values, float scale)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(values[i] * scale * 255.0)));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), bytes);
        }
    }
}
