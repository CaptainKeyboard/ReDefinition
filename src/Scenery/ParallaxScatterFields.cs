using System;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scenery
{
    // Parallax Continued's scatters as ReDefinition reads them, for the space centre's
    // grass (KscGrass) and the rays (Raytracing.RtKspMods.ParallaxScatter):
    // ScatterManager.Instance.activeScatterRenderers, and each ScatterRenderer's
    // planetName, scatter (its scatterName and distributionParams.range), meshLOD0-2,
    // instancedMaterialLOD0-2 and outputLOD0-2 (Parallax Continued 1.0.0, Scatter
    // System/ScatterManager.cs, ScatterRenderer.cs, Scatter.cs; public). Read through
    // compiled getters: a FieldInfo's GetValue costs a reflection call per field, nine
    // per renderer a frame. Only where they are as checked
    // (ForeignAssumptions.ParallaxScatters): otherwise Available is false, and both
    // leave Parallax's scatters out.
    internal static class ParallaxScatterFields
    {
        // One clump in an output buffer: Parallax's TransformData, its objectToWorld
        // matrix (TransformData.Size, 64 bytes), as the lift (ReDefinitionKscGrass.compute)
        // and the rays read it.
        internal const int ClumpStride = 64;

        private const BindingFlags Members = BindingFlags.Public | BindingFlags.Instance;

        private static bool looked;
        private static Func<object, object> manager;
        private static Func<object, IList> active;
        private static Func<object, string> planetName;
        private static Func<object, object> scatterOf;
        private static Func<object, string> scatterName;
        private static Func<object, float> range;
        private static readonly Func<object, Mesh>[] meshes = new Func<object, Mesh>[3];
        private static readonly Func<object, Material>[] materials = new Func<object, Material>[3];
        private static readonly Func<object, ComputeBuffer>[] outputs = new Func<object, ComputeBuffer>[3];
        private static bool strideWarned;

        // Whether Parallax is installed and its scatters can be read.
        internal static bool Available
        {
            get
            {
                Look();
                return manager != null;
            }
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            if (!ForeignAssumptions.ParallaxScatters.Holds) return;
            try
            {
                Type managerType = TypeLookup.Find("Parallax.ScatterManager");
                Type rendererType = TypeLookup.Find("Parallax.ScatterRenderer");
                Type scatterType = TypeLookup.Find("Parallax.Scatter");
                Func<object, IList> renderers = CompiledGetter.Of<IList>(managerType.GetField("activeScatterRenderers", Members));
                Func<object, string> planet = CompiledGetter.Of<string>(rendererType.GetField("planetName", Members));
                FieldInfo scatter = rendererType.GetField("scatter", Members);
                Func<object, object> scatterGetter = CompiledGetter.Of<object>(scatter);
                Func<object, string> name = CompiledGetter.Of<string>(scatterType.GetField("scatterName", Members));
                // distributionParams is a struct: its range read in place, without boxing it.
                FieldInfo distribution = scatterType.GetField("distributionParams", Members);
                FieldInfo distance = distribution.FieldType.GetField("range", Members);
                ParameterExpression target = Expression.Parameter(typeof(object), "target");
                Func<object, float> rangeGetter = Expression.Lambda<Func<object, float>>(
                    Expression.Field(Expression.Field(Expression.Convert(target, scatterType), distribution), distance), target).Compile();
                for (int lod = 0; lod < 3; lod++)
                {
                    meshes[lod] = CompiledGetter.Of<Mesh>(rendererType.GetField("meshLOD" + lod, Members));
                    materials[lod] = CompiledGetter.Of<Material>(rendererType.GetField("instancedMaterialLOD" + lod, Members));
                    outputs[lod] = CompiledGetter.Of<ComputeBuffer>(rendererType.GetField("outputLOD" + lod, Members));
                    if (meshes[lod] == null || materials[lod] == null || outputs[lod] == null)
                        throw new MissingFieldException("Parallax.ScatterRenderer", "LOD" + lod);
                }
                if (renderers == null || planet == null || scatterGetter == null || name == null)
                    throw new MissingFieldException("Parallax.ScatterRenderer", "planetName, scatter or scatterName");
                active = renderers;
                planetName = planet;
                scatterOf = scatterGetter;
                scatterName = name;
                range = rangeGetter;
                // Last: Available asks it.
                manager = CompiledGetter.Of<object>(managerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static));
            }
            catch (Exception e)
            {
                manager = null;
                CompatibilityLog.Warn("parallax-scatters", "Parallax's scatters are left out of the rays and the space centre's"
                                                           + " grass (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The active renderers this frame; null where there are none or Parallax cannot be read.
        internal static IList Renderers()
        {
            if (!Available) return null;
            object instance = manager(null);
            return instance != null ? active(instance) : null;
        }

        internal static string PlanetName(object renderer)
        {
            return Available && renderer != null ? planetName(renderer) : null;
        }

        // The renderer's scatter's name, "<body>-<name>"; null where it has none.
        internal static string ScatterName(object renderer)
        {
            object scatter = Available && renderer != null ? scatterOf(renderer) : null;
            return scatter != null ? scatterName(scatter) : null;
        }

        // How far the renderer's scatter is drawn, in metres; 0 where it has none.
        internal static float Range(object renderer)
        {
            object scatter = Available && renderer != null ? scatterOf(renderer) : null;
            return scatter != null ? range(scatter) : 0f;
        }

        internal static Mesh Mesh(object renderer, int lod)
        {
            return Available && renderer != null ? meshes[lod](renderer) : null;
        }

        internal static Material Material(object renderer, int lod)
        {
            return Available && renderer != null ? materials[lod](renderer) : null;
        }

        // The append buffer Parallax's evaluation filled for the renderer's level of detail
        // this frame; null where there is none, it is released, or its clumps are not the
        // 64 bytes read here (one warning).
        internal static ComputeBuffer Output(object renderer, int lod)
        {
            ComputeBuffer buffer = Available && renderer != null ? outputs[lod](renderer) : null;
            if (buffer == null || !buffer.IsValid()) return null;
            if (buffer.stride == ClumpStride) return buffer;
            if (!strideWarned)
            {
                strideWarned = true;
                CompatibilityLog.Warn("parallax-scatter-stride", "Parallax's scatter buffers hold " + buffer.stride
                                      + " bytes a clump, not the " + ClumpStride + " read here: its scatters are left out of"
                                      + " the rays and the space centre's grass.");
            }
            return null;
        }
    }
}
