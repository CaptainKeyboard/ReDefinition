using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The drawing of the tracks, stage 2 of the plan: strips just above the ground, one rectangle for each run of equally dark texels in
    // a row of a tile, collected for the tiles near a place into one list that becomes one mesh and one draw. Stage 1 (vertex colours)
    // cannot show a track of a wheel's width: the PQS vertices of Kerbin lie about 66 to 93 m apart (RECON.md P3), so a vertex colour
    // can only tint a patch of that size. Pure numbers; the game part (TrackDraw) puts heights on the corners and builds the mesh.
    internal static class TrackRibbons
    {
        // The most rectangles in the list (the pool of the plan), and the number of darkness levels a run is cut at.
        internal const int PoolSize = 2000;
        internal const int Levels = 6;

        internal struct Strip
        {
            // The tile and the corner of the strip inside it in metres east and north of the tile's south-west corner.
            internal int Tx, Ty;
            internal float East, North;
            // The length (east) and the height (north) of the strip in metres.
            internal float Length, Height;
            // The darkness 0 to 1 (a level of Levels).
            internal float Alpha;
        }

        // The strips of the tiles within rangeM of a place, nearest tiles first, at most `limit` of them. nowUt decides the fade.
        internal static int Collect(TrackTiles tiles, double radiusM, double centreLat, double centreLon, double rangeM, double nowUt,
            List<Strip> into, int limit)
        {
            into.Clear();
            int ctx, cty;
            double ce, cn;
            TrackTiles.Locate(radiusM, centreLat, centreLon, out ctx, out cty, out ce, out cn);
            // The tiles in range ordered by their distance from the centre tile (a square ring after ring): the nearest tiles get the pool.
            int ring = (int)Math.Ceiling(rangeM / TrackTiles.TileMetres) + 1;
            var near = new List<TrackTile>();
            foreach (TrackTile t in tiles.Live)
            {
                if (Math.Abs(t.Ty - cty) > ring) continue;
                near.Add(t);
            }
            near.Sort((a, b) => Cost(a, ctx, cty).CompareTo(Cost(b, ctx, cty)));
            foreach (TrackTile tile in near)
            {
                if (into.Count >= limit) break;
                if (nowUt < tile.StampUt) continue;
                AddTile(tile, nowUt, into, limit);
            }
            return into.Count;
        }

        private static double Cost(TrackTile t, int cx, int cy)
        {
            // Tiles at one latitude have columns of the same width, so the column difference is a distance in tiles there; at other
            // latitudes it is a good order all the same.
            double dx = t.Tx - cx, dy = t.Ty - cy;
            return dx * dx + dy * dy;
        }

        // The strips of one tile at nowUt, appended to the list until it holds `limit`.
        internal static void AddTile(TrackTile tile, double nowUt, List<Strip> into, int limit)
        {
            double fade = Math.Exp(-(nowUt - tile.StampUt) / Math.Max(1.0, tile.TauSeconds));
            int n = TrackTiles.TexelsPerSide;
            byte[] p = tile.Pressed;
            for (int j = 0; j < n; j++)
            {
                int i = 0;
                while (i < n)
                {
                    int level = LevelOf(p[j * n + i] / 255.0 * fade);
                    if (level == 0)
                    {
                        i++;
                        continue;
                    }
                    int start = i;
                    i++;
                    while (i < n && LevelOf(p[j * n + i] / 255.0 * fade) == level) i++;
                    if (into.Count >= limit) return;
                    into.Add(new Strip
                    {
                        Tx = tile.Tx,
                        Ty = tile.Ty,
                        East = (float)(start * TrackTiles.TexelMetres),
                        North = (float)(j * TrackTiles.TexelMetres),
                        Length = (float)((i - start) * TrackTiles.TexelMetres),
                        Height = (float)TrackTiles.TexelMetres,
                        Alpha = level / (float)Levels
                    });
                }
            }
        }

        // The level 0 (gone) to Levels of a shown value.
        internal static int LevelOf(double shown)
        {
            if (shown < TrackTiles.Gone) return 0;
            int level = (int)Math.Ceiling(shown * Levels);
            return level < 1 ? 1 : level > Levels ? Levels : level;
        }
    }
}
