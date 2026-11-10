using System.Collections.Generic;

namespace ReDefinition.Seasons
{
    // The quads of Kerbin's ground the snow mod has seen, by quad. Filled when a quad is built, emptied when it is destroyed or the
    // sphere is reset. The quad builder and the refresher run on the main thread in the game checked. The table is behind a lock
    // all the same, because the PQS can build quads off it. The lock is taken once per quad, not once per vertex: SnowPqsMod keeps
    // the record of the quad it is building.
    internal static class SnowQuads
    {
        private static readonly Dictionary<PQ, QuadSnow> Table = new Dictionary<PQ, QuadSnow>();
        private static readonly object Gate = new object();

        internal static int Count
        {
            get
            {
                lock (Gate) return Table.Count;
            }
        }

        // The record of a quad, made if it has none or has another vertex count.
        internal static QuadSnow For(PQ quad, int vertexCount)
        {
            lock (Gate)
            {
                QuadSnow snow;
                if (Table.TryGetValue(quad, out snow) && snow.Length == vertexCount) return snow;
                snow = new QuadSnow(vertexCount);
                Table[quad] = snow;
                return snow;
            }
        }

        internal static bool TryGet(PQ quad, out QuadSnow snow)
        {
            lock (Gate) return Table.TryGetValue(quad, out snow);
        }

        internal static void Remove(PQ quad)
        {
            lock (Gate) Table.Remove(quad);
        }

        internal static void Clear()
        {
            lock (Gate) Table.Clear();
        }

        // The quads now, in a list that stays valid while the table changes.
        internal static void Snapshot(List<PQ> into)
        {
            into.Clear();
            lock (Gate) into.AddRange(Table.Keys);
        }
    }
}
