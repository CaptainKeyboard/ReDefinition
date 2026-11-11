using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The quads of the ground the cover has seen, by quad, for all bodies. Filled when a quad is built, emptied when it is destroyed or
    // the sphere is reset. The table is behind a lock, because the PQS can build quads off the main thread; the lock is taken once per
    // quad, not once per vertex: CoverPqsMod keeps the record of the quad it is building.
    internal static class CoverQuads
    {
        private static readonly Dictionary<PQ, QuadCover> Table = new Dictionary<PQ, QuadCover>();
        private static readonly object Gate = new object();

        internal static int Count
        {
            get
            {
                lock (Gate) return Table.Count;
            }
        }

        // The record of a quad, made if it has none or has another vertex count.
        internal static QuadCover For(PQ quad, CoverBodyState body, int vertexCount)
        {
            lock (Gate)
            {
                QuadCover cover;
                if (Table.TryGetValue(quad, out cover) && cover.Length == vertexCount && ReferenceEquals(cover.Body, body)) return cover;
                cover = new QuadCover(body, vertexCount);
                Table[quad] = cover;
                return cover;
            }
        }

        internal static bool TryGet(PQ quad, out QuadCover cover)
        {
            lock (Gate) return Table.TryGetValue(quad, out cover);
        }

        internal static void Remove(PQ quad)
        {
            lock (Gate) Table.Remove(quad);
        }

        // Forgets the quads of one body (its sphere was made anew).
        internal static void RemoveBody(CoverBodyState body)
        {
            lock (Gate)
            {
                var gone = new List<PQ>();
                foreach (KeyValuePair<PQ, QuadCover> pair in Table)
                    if (ReferenceEquals(pair.Value.Body, body)) gone.Add(pair.Key);
                foreach (PQ quad in gone) Table.Remove(quad);
            }
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
