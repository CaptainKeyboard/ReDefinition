using System.Collections.Generic;

namespace ReDefinition.Seasons
{
    // The quads of Kerbin's ground the snow mod has seen, by quad. Filled when a quad is built, emptied when it is destroyed or the
    // sphere is reset. The quad builder and the refresher both run on the main thread.
    internal static class SnowQuads
    {
        private static readonly Dictionary<PQ, QuadSnow> Table = new Dictionary<PQ, QuadSnow>();

        internal static int Count
        {
            get { return Table.Count; }
        }

        // The record of a quad, made if it has none or has another vertex count.
        internal static QuadSnow For(PQ quad, int vertexCount)
        {
            QuadSnow snow;
            if (Table.TryGetValue(quad, out snow) && snow.Length == vertexCount) return snow;
            snow = new QuadSnow(vertexCount);
            Table[quad] = snow;
            return snow;
        }

        internal static bool TryGet(PQ quad, out QuadSnow snow)
        {
            return Table.TryGetValue(quad, out snow);
        }

        internal static void Remove(PQ quad)
        {
            Table.Remove(quad);
        }

        internal static void Clear()
        {
            Table.Clear();
        }

        // The quads now, in a list that stays valid while the table changes.
        internal static void Snapshot(List<PQ> into)
        {
            into.Clear();
            into.AddRange(Table.Keys);
        }
    }
}
