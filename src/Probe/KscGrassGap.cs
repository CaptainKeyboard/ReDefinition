using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // With -redefinitionKscGrassGap, in a flight at the space centre, the probe logs how far the space
    // centre's grass surfaces (its ground meshes' grass materials) stand above the terrain
    // under them (PQS.GetSurfaceHeight with the KSC's flattening). Parallax's grass grows
    // on that terrain. The probe takes random points on the grass triangles of every space centre renderer
    // in range, per facility. Whether Parallax's blades reach through the
    // grass surfaces depends on this gap alone.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class KscGrassGap : MonoBehaviour
    {
        // The command-line switch, and the random points measured on each grass submesh.
        internal const string Argument = "-redefinitionKscGrassGap";
        private const int PointsPerMesh = 40;

        // With the switch, the measurement eight seconds into the flight.
        private IEnumerator Start()
        {
            if (!CommandLine.Has(Argument)) yield break;
            yield return new WaitForSeconds(8f);
            try
            {
                Measure();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " KSC grass gap: " + e);
            }
        }

        // The gaps measured per facility's grass submesh, then KSP's flattening decals and Parallax's blade size, into the log.
        private static void Measure()
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            PQS pqs = body != null ? body.pqsController : null;
            if (pqs == null) return;
            var random = new System.Random(1);
            var perFacility = new Dictionary<string, List<float>>();
            var materials = new List<Material>();
            foreach (PQSCity city in FindObjectsOfType<PQSCity>())
                foreach (MeshFilter filter in city.GetComponentsInChildren<MeshFilter>(false))
                {
                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    Mesh mesh = filter.sharedMesh;
                    if (renderer == null || mesh == null || !mesh.isReadable) continue;
                    renderer.GetSharedMaterials(materials);
                    Vector3[] vertices = mesh.vertices;
                    for (int s = 0; s < mesh.subMeshCount && s < materials.Count; s++)
                    {
                        Material material = materials[s];
                        if (material == null || material.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        int[] triangles = mesh.GetTriangles(s);
                        if (triangles.Length < 3) continue;
                        string facility = city.name + "/" + filter.name + " [" + material.name.Replace(" (Instance)", "") + "]";
                        List<float> gaps;
                        if (!perFacility.TryGetValue(facility, out gaps)) perFacility[facility] = gaps = new List<float>();
                        for (int i = 0; i < PointsPerMesh; i++)
                        {
                            int t = random.Next(triangles.Length / 3) * 3;
                            float u = (float)random.NextDouble(), v = (float)random.NextDouble();
                            if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                            Vector3 local = vertices[triangles[t]] + u * (vertices[triangles[t + 1]] - vertices[triangles[t]])
                                            + v * (vertices[triangles[t + 2]] - vertices[triangles[t]]);
                            Vector3d world = filter.transform.TransformPoint(local);
                            // The space centre only: within 2 km of the VAB.
                            if ((world - body.GetWorldSurfacePosition(-0.0968, -74.6187, 0.0)).magnitude > 2000.0) continue;
                            double surfaceAltitude = body.GetAltitude(world);
                            double terrainAltitude = pqs.GetSurfaceHeight(body.GetRelSurfaceNVector(body.GetLatitude(world),
                                                                                                   body.GetLongitude(world)))
                                                     - body.Radius;
                            gaps.Add((float)(surfaceAltitude - terrainAltitude));
                        }
                    }
                }
            var text = new StringBuilder(Log.Tag + " KSC grass gap (grass surface over the terrain, m; min / median / max, points):");
            List<float> all = perFacility.Values.SelectMany(g => g).ToList();
            foreach (KeyValuePair<string, List<float>> entry in perFacility.OrderBy(e => e.Key))
                text.Append("\n    ").Append(entry.Key).Append(": ").Append(Stats(entry.Value));
            text.Append("\n    all: ").Append(Stats(all));
            // KSP's flattening under the space centre and Parallax's grass blades' size.
            foreach (PQSMod_MapDecalTangent decal in pqs.GetComponentsInChildren<PQSMod_MapDecalTangent>(true))
                text.Append("\n    decal ").Append(decal.name).Append(": radius ").Append(decal.radius)
                    .Append(", absolute ").Append(decal.absolute).Append(", absoluteOffset ").Append(decal.absoluteOffset)
                    .Append(", heightMapDeformity ").Append(decal.heightMapDeformity).Append(", smoothHeight ")
                    .Append(decal.smoothHeight).Append(", cullBlack ").Append(decal.cullBlack);
            GameObject grass = GameDatabase.Instance.GetModelPrefab("Parallax_StockScatterTextures/Models/Kerbin/bettergrassUV");
            MeshFilter blade = grass != null ? grass.GetComponentInChildren<MeshFilter>(true) : null;
            if (blade != null && blade.sharedMesh != null)
                text.Append("\n    Parallax grass model bounds ").Append(blade.sharedMesh.bounds.size.ToString("F3"))
                    .Append(" times its scale (0.07 to 0.12, Kerbin.cfg)");
            Debug.Log(text.ToString());
        }

        // Minimum, median and maximum of the values, and their count, for the log; sorts the list.
        private static string Stats(List<float> values)
        {
            if (values.Count == 0) return "none";
            values.Sort();
            return values[0].ToString("0.00", CultureInfo.InvariantCulture) + " / "
                   + values[values.Count / 2].ToString("0.00", CultureInfo.InvariantCulture) + " / "
                   + values[values.Count - 1].ToString("0.00", CultureInfo.InvariantCulture) + " (" + values.Count + ")";
        }
    }
}
