using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using static ReDefinition.Atmospheres.Mathx;

namespace ReDefinition.Atmospheres
{
    // The state of the tracks of wheels and feet in the cover: sparse tiles of 128 m x 128 m at 0.5 m per texel (256 x 256 bytes = 64 KB),
    // anchored in the body's latitude and longitude (so the floating origin does not touch them) and made only where something marks.
    // A tile holds `pressed` (0 to 255) and one stamp time; the value shown is pressed x exp(-(UT - stamp) / tau). At most MaxLive tiles
    // stay in memory (4 MB); the others are compressed (RLE and deflate) and kept in a side file, PluginData/<save>/tracks.bin. Tracks
    // with a stamp in the future (a revert) are dropped. Pure numbers; nothing here names a game type.
    internal sealed class TrackTile
    {
        internal readonly int Tx;
        internal readonly int Ty;
        internal readonly byte[] Pressed = new byte[TrackTiles.TexelsPerSide * TrackTiles.TexelsPerSide];
        // The time the pressed values were last brought up to date, and the fade time of the tile in seconds.
        internal double StampUt;
        internal double TauSeconds;
        // Rises with every change; the drawing rebuilds a tile's quads when it moved. LastTouch orders the tiles for eviction.
        internal int Version;
        internal long LastTouch;

        internal TrackTile(int tx, int ty)
        {
            Tx = tx;
            Ty = ty;
        }
    }

    internal sealed class TrackTiles
    {
        internal const int TexelsPerSide = 256;
        internal const double TexelMetres = 0.5;
        internal const double TileMetres = TexelsPerSide * TexelMetres;
        internal const int MaxLive = 64;

        // A stamp changes nothing where the cover is thinner than this (0 to 1): tracks need something to press.
        internal const double MinCover = 0.2;

        // Below this shown value (0 to 1) a texel is gone.
        internal const double Gone = 0.02;

        // The least game time between two bakings of a tile's fade into its texels, in seconds.
        internal const double BakeSeconds = 1.0;

        private readonly double radiusM;
        private readonly Dictionary<long, TrackTile> live = new Dictionary<long, TrackTile>();
        private readonly Dictionary<long, byte[]> parked = new Dictionary<long, byte[]>();
        private long touch;

        internal TrackTiles(double bodyRadiusM)
        {
            radiusM = bodyRadiusM > 1.0 ? bodyRadiusM : 1.0;
        }

        internal int LiveCount { get { return live.Count; } }
        internal int ParkedCount { get { return parked.Count; } }
        // The memory of the live tiles in bytes.
        internal long LiveBytes { get { return (long)live.Count * TexelsPerSide * TexelsPerSide; } }
        internal IEnumerable<TrackTile> Live { get { return live.Values; } }

        // The latitude and longitude steps of a tile row in degrees: a row is TileMetres of latitude high and its columns are TileMetres
        // wide at the row's middle latitude (the row's column width does not change with the longitude). Pure.
        internal static void TileSizeDegrees(double radiusM, int ty, out double latStep, out double lonStep)
        {
            latStep = TileMetres / radiusM * 180.0 / Math.PI;
            double midLat = (ty + 0.5) * latStep - 90.0;
            double c = Math.Max(0.01, Math.Cos(midLat * Math.PI / 180.0));
            lonStep = latStep / c;
        }

        // The tile a place falls in and the place inside it in metres east and north of the tile's south-west corner.
        internal static void Locate(double radiusM, double latDeg, double lonDeg, out int tx, out int ty, out double eastM, out double northM)
        {
            double latStep, lonStep;
            double lat = Clamp(latDeg, -89.999, 89.999);
            double latStep0 = TileMetres / radiusM * 180.0 / Math.PI;
            ty = (int)Math.Floor((lat + 90.0) / latStep0);
            TileSizeDegrees(radiusM, ty, out latStep, out lonStep);
            double lon = lonDeg;
            tx = (int)Math.Floor((lon + 180.0) / lonStep);
            double southLat = ty * latStep - 90.0;
            double westLon = tx * lonStep - 180.0;
            northM = (lat - southLat) / latStep * TileMetres;
            eastM = (lon - westLon) / lonStep * TileMetres;
        }

        // The latitude and longitude of a point inside a tile (metres east and north of its south-west corner).
        internal static void PlaceOf(double radiusM, int tx, int ty, double eastM, double northM, out double latDeg, out double lonDeg)
        {
            double latStep, lonStep;
            TileSizeDegrees(radiusM, ty, out latStep, out lonStep);
            latDeg = ty * latStep - 90.0 + northM / TileMetres * latStep;
            lonDeg = tx * lonStep - 180.0 + eastM / TileMetres * lonStep;
        }

        private static long Key(int tx, int ty)
        {
            return ((long)ty << 32) ^ (uint)tx;
        }

        // The tile at a key; a parked one is brought back, and none is made unless create is set. May evict the oldest live tile.
        internal TrackTile Get(int tx, int ty, bool create)
        {
            long key = Key(tx, ty);
            TrackTile tile;
            if (live.TryGetValue(key, out tile))
            {
                tile.LastTouch = ++touch;
                return tile;
            }
            byte[] packed;
            if (parked.TryGetValue(key, out packed))
            {
                tile = Decode(packed, tx, ty);
                parked.Remove(key);
                if (tile != null)
                {
                    Add(key, tile);
                    return tile;
                }
            }
            if (!create) return null;
            tile = new TrackTile(tx, ty);
            Add(key, tile);
            return tile;
        }

        private void Add(long key, TrackTile tile)
        {
            tile.LastTouch = ++touch;
            live[key] = tile;
            while (live.Count > MaxLive) EvictOldest(key);
        }

        // Parks the least recently touched live tile (never the one with the keep key).
        private void EvictOldest(long keep)
        {
            long oldestKey = 0;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<long, TrackTile> pair in live)
            {
                if (pair.Key != keep && pair.Value.LastTouch < oldest)
                {
                    oldest = pair.Value.LastTouch;
                    oldestKey = pair.Key;
                }
            }
            TrackTile tile;
            if (!live.TryGetValue(oldestKey, out tile) || oldestKey == keep) return;
            live.Remove(oldestKey);
            byte[] packed = Encode(tile);
            if (packed != null) parked[oldestKey] = packed;
        }

        // Presses a disc of radius halfWidthM at a place at time ut where the cover is at least MinCover: the texels in the disc go to
        // full. A tile older than BakeSeconds first takes its fade into the texels, so that a new stamp does not freshen old tracks.
        // Returns false where nothing was pressed (thin cover or a bad number).
        internal bool Stamp(double latDeg, double lonDeg, double halfWidthM, double ut, double tauSeconds, double cover01)
        {
            if (cover01 < MinCover || double.IsNaN(latDeg) || double.IsNaN(lonDeg) || double.IsNaN(ut) || !(tauSeconds > 0)) return false;
            if (!(halfWidthM > 0)) halfWidthM = TexelMetres * 0.5;
            int tx, ty;
            double east, north;
            Locate(radiusM, latDeg, lonDeg, out tx, out ty, out east, out north);
            bool any = false;
            // The disc may reach into the neighbouring tiles at an edge: visit the tiles its bounding box touches.
            int reach = (int)Math.Ceiling(halfWidthM / TileMetres);
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    double e = east - dx * TileMetres, n = north - dy * TileMetres;
                    if (e + halfWidthM < 0 || e - halfWidthM > TileMetres || n + halfWidthM < 0 || n - halfWidthM > TileMetres) continue;
                    TrackTile tile = Get(tx + dx, ty + dy, true);
                    Bake(tile, ut);
                    tile.TauSeconds = tauSeconds;
                    if (PressDisc(tile, e, n, halfWidthM)) any = true;
                    tile.StampUt = Math.Max(tile.StampUt, ut);
                    tile.Version++;
                }
            }
            return any;
        }

        private static bool PressDisc(TrackTile tile, double eastM, double northM, double radius)
        {
            int i0 = (int)Math.Floor((eastM - radius) / TexelMetres), i1 = (int)Math.Floor((eastM + radius) / TexelMetres);
            int j0 = (int)Math.Floor((northM - radius) / TexelMetres), j1 = (int)Math.Floor((northM + radius) / TexelMetres);
            bool any = false;
            for (int j = Math.Max(0, j0); j <= Math.Min(TexelsPerSide - 1, j1); j++)
            {
                for (int i = Math.Max(0, i0); i <= Math.Min(TexelsPerSide - 1, i1); i++)
                {
                    double cx = (i + 0.5) * TexelMetres - eastM, cy = (j + 0.5) * TexelMetres - northM;
                    if (cx * cx + cy * cy > radius * radius + 0.25 * TexelMetres * TexelMetres) continue;
                    tile.Pressed[j * TexelsPerSide + i] = 255;
                    any = true;
                }
            }
            return any;
        }

        // Takes the fade between the tile's stamp and ut into the texels (when it is old enough) and sets the stamp to ut.
        private static void Bake(TrackTile tile, double ut)
        {
            if (tile.TauSeconds <= 0 || ut - tile.StampUt < BakeSeconds)
            {
                if (tile.TauSeconds <= 0) tile.StampUt = ut;
                return;
            }
            double f = Math.Exp(-(ut - tile.StampUt) / tile.TauSeconds);
            byte[] p = tile.Pressed;
            for (int k = 0; k < p.Length; k++)
                if (p[k] != 0) p[k] = (byte)(p[k] * f + 0.5);
            tile.StampUt = ut;
        }

        // The value shown at a place, 0 to 1: pressed x exp(-(ut - stamp) / tau); 0 where there is no tile or the stamp lies in the
        // future of ut.
        internal double Shown(double latDeg, double lonDeg, double ut)
        {
            int tx, ty;
            double east, north;
            Locate(radiusM, latDeg, lonDeg, out tx, out ty, out east, out north);
            TrackTile tile = Get(tx, ty, false);
            if (tile == null) return 0;
            return ShownAt(tile, (int)Math.Floor(east / TexelMetres), (int)Math.Floor(north / TexelMetres), ut);
        }

        internal static double ShownAt(TrackTile tile, int i, int j, double ut)
        {
            if (i < 0 || j < 0 || i >= TexelsPerSide || j >= TexelsPerSide) return 0;
            byte p = tile.Pressed[j * TexelsPerSide + i];
            if (p == 0 || ut < tile.StampUt) return 0;
            double v = p / 255.0 * Math.Exp(-(ut - tile.StampUt) / Math.Max(1.0, tile.TauSeconds));
            return v < Gone ? 0 : v;
        }

        // New cover has fallen on the tracks: every live tile and every parked one loses the share (0 to 1) of what it shows, as a
        // snowfall fills the ruts in. Parked tiles are not touched (they are decoded when they come back; the caller attenuates the
        // live ones it draws).
        internal void Attenuate(double share)
        {
            if (!(share > 0)) return;
            double keep = 1.0 - Clamp01(share);
            foreach (TrackTile tile in live.Values)
            {
                byte[] p = tile.Pressed;
                for (int k = 0; k < p.Length; k++)
                    if (p[k] != 0) p[k] = (byte)(p[k] * keep + 0.5);
                tile.Version++;
            }
        }

        // Drops the tiles with a stamp after ut (a revert), the live ones and the parked ones; returns how many. Texels cannot be told
        // apart by time inside a tile, so the whole tile goes.
        internal int DropAfter(double ut)
        {
            int dropped = 0;
            var gone = new List<long>();
            foreach (KeyValuePair<long, TrackTile> pair in live)
                if (pair.Value.StampUt > ut) gone.Add(pair.Key);
            foreach (long key in gone)
            {
                live.Remove(key);
                dropped++;
            }
            gone.Clear();
            foreach (KeyValuePair<long, byte[]> pair in parked)
            {
                double stamp;
                if (!TryStamp(pair.Value, out stamp) || stamp > ut) gone.Add(pair.Key);
            }
            foreach (long key in gone)
            {
                parked.Remove(key);
                dropped++;
            }
            return dropped;
        }

        // Drops the tiles that have faded out completely at ut (nothing shows and 12 tau have passed); returns how many.
        internal int DropFaded(double ut)
        {
            var gone = new List<long>();
            foreach (KeyValuePair<long, TrackTile> pair in live)
                if (ut - pair.Value.StampUt > 12.0 * Math.Max(1.0, pair.Value.TauSeconds)) gone.Add(pair.Key);
            foreach (long key in gone) live.Remove(key);
            return gone.Count;
        }

        // ---- the side file -------------------------------------------------------------------------------------------------

        private const string Magic = "RDTRK1";

        // One tile as bytes: tx, ty, stamp, tau, then the texels as run lengths (value, count up to 255) deflated.
        internal static byte[] Encode(TrackTile tile)
        {
            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms))
                {
                    bw.Write(tile.StampUt);
                    bw.Write(tile.TauSeconds);
                    using (var raw = new MemoryStream())
                    {
                        byte[] p = tile.Pressed;
                        int k = 0;
                        while (k < p.Length)
                        {
                            byte v = p[k];
                            int run = 1;
                            while (k + run < p.Length && p[k + run] == v && run < 255) run++;
                            raw.WriteByte(v);
                            raw.WriteByte((byte)run);
                            k += run;
                        }
                        byte[] rle = raw.ToArray();
                        using (var packed = new MemoryStream())
                        {
                            using (var ds = new DeflateStream(packed, CompressionMode.Compress, true)) ds.Write(rle, 0, rle.Length);
                            byte[] deflated = packed.ToArray();
                            bw.Write(deflated.Length);
                            bw.Write(deflated);
                        }
                    }
                }
                return ms.ToArray();
            }
        }

        private static bool TryStamp(byte[] packed, out double stamp)
        {
            stamp = 0;
            if (packed == null || packed.Length < 16) return false;
            stamp = BitConverter.ToDouble(packed, 0);
            return !double.IsNaN(stamp);
        }

        // A tile from Encode's bytes; null where they do not fit (damaged).
        internal static TrackTile Decode(byte[] packed, int tx, int ty)
        {
            try
            {
                using (var ms = new MemoryStream(packed))
                using (var br = new BinaryReader(ms))
                {
                    var tile = new TrackTile(tx, ty);
                    tile.StampUt = br.ReadDouble();
                    tile.TauSeconds = br.ReadDouble();
                    int length = br.ReadInt32();
                    if (length < 0 || length > packed.Length || double.IsNaN(tile.StampUt) || double.IsNaN(tile.TauSeconds)) return null;
                    byte[] deflated = br.ReadBytes(length);
                    using (var input = new MemoryStream(deflated))
                    using (var ds = new DeflateStream(input, CompressionMode.Decompress))
                    using (var rle = new MemoryStream())
                    {
                        ds.CopyTo(rle);
                        byte[] r = rle.ToArray();
                        int at = 0;
                        for (int k = 0; k + 1 < r.Length; k += 2)
                        {
                            int run = r[k + 1];
                            if (at + run > tile.Pressed.Length) return null;
                            if (r[k] != 0) for (int n = 0; n < run; n++) tile.Pressed[at + n] = r[k];
                            at += run;
                        }
                        if (at != tile.Pressed.Length) return null;
                    }
                    return tile;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Every tile, live and parked, as the content of tracks.bin: the magic, the body radius, the count, then per tile its tx, ty and
        // its bytes. A tile that has faded out (nothing shows at ut) is left out.
        internal byte[] SaveAll(double ut)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Magic);
                bw.Write(radiusM);
                var entries = new List<KeyValuePair<long, byte[]>>();
                foreach (KeyValuePair<long, TrackTile> pair in live)
                    if (ut - pair.Value.StampUt <= 12.0 * Math.Max(1.0, pair.Value.TauSeconds)) entries.Add(new KeyValuePair<long, byte[]>(pair.Key, Encode(pair.Value)));
                foreach (KeyValuePair<long, byte[]> pair in parked) entries.Add(pair);
                bw.Write(entries.Count);
                foreach (KeyValuePair<long, byte[]> e in entries)
                {
                    bw.Write(e.Key);
                    bw.Write(e.Value.Length);
                    bw.Write(e.Value);
                }
                return ms.ToArray();
            }
        }

        // Reads a SaveAll content into this store as parked tiles; tiles with a stamp after nowUt (a revert) are dropped. Returns the
        // number kept, or -1 where the content is not ours (a body of another radius, a damaged file).
        internal int LoadAll(byte[] data, double nowUt)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var br = new BinaryReader(ms))
                {
                    if (br.ReadString() != Magic) return -1;
                    double radius = br.ReadDouble();
                    if (Math.Abs(radius - radiusM) > 1.0) return -1;
                    int count = br.ReadInt32();
                    if (count < 0 || count > 1000000) return -1;
                    int kept = 0;
                    for (int n = 0; n < count; n++)
                    {
                        long key = br.ReadInt64();
                        int length = br.ReadInt32();
                        if (length < 16 || length > ms.Length - ms.Position) return -1;
                        byte[] packed = br.ReadBytes(length);
                        double stamp;
                        if (!TryStamp(packed, out stamp) || stamp > nowUt) continue;
                        parked[key] = packed;
                        live.Remove(key);
                        kept++;
                    }
                    return kept;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
