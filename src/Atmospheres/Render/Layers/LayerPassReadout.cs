using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // E40 PP (the weather player reproduces what the game shows): the numbers the layer pass ran with, read back from its material and written as JSON, so that a replay
    // in the weather player can be compared with the game input by input (tools/weather_metrics/e40pp.py writes the player's own readout of the same pass and lists every
    // uniform that differs). The names are the uniforms the layer shader and its includes declare; LayerPassReadoutTests keeps the lists equal to the declarations.
    internal static class LayerPassReadout
    {
        internal static readonly string[] Floats = { "_PlanetR", "_LayerCount" };

        internal static readonly string[] Matrices = { "_WorldToMap" };

        internal static readonly string[] Vectors =
        {
            "_PlanetCenter", "_CamPos", "_CamForward", "_CamRight", "_CamUp", "_MapDim", "_Sun", "_SunColor", "_AmbientTop", "_AmbientBottom", "_MarchP", "_StepP", "_LimitP",
            "_PhaseP", "_OutP", "_OutW", "_SkipP", "_SpacingP", "_CostP", "_DitherP", "_ReconP", "_ReconD", "_MarchTex_TexelSize",
            "_NoiseL", "_NoiseP", "_NoiseQ", "_NoiseR", "_NoiseS", "_NoiseT", "_BillowP", "_HeapP", "_FirstHitP",
            "_LightP", "_LightQ", "_LightR", "_LightS", "_LightT", "_SunAir", "_LightU", "_LightV", "_LightW", "_LightX", "_CellA", "_CellB", "_CacheA", "_CacheB",
            "_FarA", "_FarB", "_FarC", "_FarE", "_FarD", "_CullA", "_CullB", "_CullC", "_CullD", "_CullT", "_EdgeP"
        };

        internal static readonly string[] Arrays =
        {
            "_LayerA", "_LayerB", "_LayerC", "_LayerD", "_LayerE", "_DitherM", "_NoiseOffset", "_NoiseLayer", "_NoiseStretch", "_NoiseAxis", "_LightM", "_LightL",
            "_MoonDir", "_MoonCol", "_BoltPos", "_BoltCol", "_LayerH", "_LayerH2", "_CullL"
        };

        internal static readonly string[] Textures =
        {
            "_TypeProfile", "_TypeInfo", "_CovMaps", "_TypeMaps", "_CovReach", "_SceneDepth", "_MarchTex", "_NoiseShape", "_NoiseDetail", "_NoiseCurl", "_NoiseFlow",
            "_NoiseColour", "_NoiseLattice", "_LightCache", "_HeightMaps", "_CullTiles", "_CullMax0", "_CullMax1", "_CullMin0", "_CullMin1", "_EdgeMask"
        };

        // Where the numbers come from: a material in the game and in the player, a stand-in in the tests.
        internal interface ISource
        {
            float Float(string name);
            Vector4 Vector(string name);
            Vector4[] Array(string name);
            Matrix4x4 Matrix(string name);
            // "<width>x<height>[x<depth>]" of the texture bound, or null for none
            string Texture(string name);
        }

        private sealed class MaterialSource : ISource
        {
            private readonly Material m;

            internal MaterialSource(Material material)
            {
                m = material;
            }

            public float Float(string name) { return m.GetFloat(name); }
            public Vector4 Vector(string name) { return m.GetVector(name); }
            public Vector4[] Array(string name) { return m.GetVectorArray(name); }
            public Matrix4x4 Matrix(string name) { return m.GetMatrix(name); }

            public string Texture(string name)
            {
                Texture t = m.GetTexture(name);
                if (t == null) return null;
                var t3 = t as Texture3D;
                var ta = t as Texture2DArray;
                return t.width + "x" + t.height + (t3 != null ? "x" + t3.depth : ta != null ? "x" + ta.depth : "");
            }
        }

        internal static string Uniforms(Material material)
        {
            return material == null ? "{}" : Uniforms(new MaterialSource(material));
        }

        // {"floats": {...}, "vectors": {...}, "arrays": {...}, "matrices": {...}, "textures": {...}}; a vector array the material does not hold is null.
        internal static string Uniforms(ISource s)
        {
            var b = new StringBuilder(16384);
            b.Append("{\"floats\": {");
            for (int i = 0; i < Floats.Length; i++)
            {
                if (i > 0) b.Append(", ");
                b.Append('"').Append(Floats[i]).Append("\": ").Append(Num(s.Float(Floats[i])));
            }
            b.Append("},\n \"vectors\": {");
            for (int i = 0; i < Vectors.Length; i++)
            {
                if (i > 0) b.Append(",\n  ");
                b.Append('"').Append(Vectors[i]).Append("\": ");
                Vec(b, s.Vector(Vectors[i]));
            }
            b.Append("},\n \"arrays\": {");
            for (int i = 0; i < Arrays.Length; i++)
            {
                if (i > 0) b.Append(",\n  ");
                b.Append('"').Append(Arrays[i]).Append("\": ");
                Vector4[] a = s.Array(Arrays[i]);
                if (a == null) { b.Append("null"); continue; }
                b.Append('[');
                for (int k = 0; k < a.Length; k++)
                {
                    if (k > 0) b.Append(", ");
                    Vec(b, a[k]);
                }
                b.Append(']');
            }
            b.Append("},\n \"matrices\": {");
            for (int i = 0; i < Matrices.Length; i++)
            {
                if (i > 0) b.Append(", ");
                Matrix4x4 m = s.Matrix(Matrices[i]);
                b.Append('"').Append(Matrices[i]).Append("\": [");
                for (int r = 0; r < 4; r++)
                {
                    if (r > 0) b.Append(", ");
                    Vec(b, m.GetRow(r));
                }
                b.Append(']');
            }
            b.Append("},\n \"textures\": {");
            for (int i = 0; i < Textures.Length; i++)
            {
                if (i > 0) b.Append(", ");
                string t = s.Texture(Textures[i]);
                b.Append('"').Append(Textures[i]).Append("\": ").Append(t == null ? "null" : "\"" + t + "\"");
            }
            b.Append("}}");
            return b.ToString();
        }

        internal static void Vec(StringBuilder b, Vector4 v)
        {
            b.Append('[').Append(Num(v.x)).Append(", ").Append(Num(v.y)).Append(", ").Append(Num(v.z)).Append(", ").Append(Num(v.w)).Append(']');
        }

        // A float as JSON: round-trip digits; NaN and the infinities (not JSON) as null.
        internal static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static string Num(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static string Str(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder(s.Length + 2);
            b.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
    }
}
