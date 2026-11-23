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
            "_PhaseP", "_OutP", "_OutW", "_SkipP", "_SpacingP", "_ReuseP", "_LeapP", "_CostP", "_DitherP", "_ReconP", "_ReconD", "_FarRot", "_MarchTex_TexelSize",
            "_NoiseP", "_NoiseQ", "_NoiseR", "_NoiseS", "_NoiseT", "_BillowP", "_HeapP", "_BlendP", "_MapReachCov", "_MapReachType",
            "_LightP", "_LightQ", "_LightR", "_LightS", "_LightT", "_SunAir", "_LightU", "_LightV", "_LightW", "_LightX", "_CellA", "_CellB", "_CacheA", "_CacheX", "_CacheY", "_CacheZ", "_CacheH", "_CacheSlice",
            "_LvA", "_LvU", "_LvE", "_LvN", "_LvZ", "_LvC", "_LvF", "_LvSlice", "_LvP", "_LvD", "_LvR", "_LvS", "_LvBlur", "_LvBlurSigma",
            "_FarA", "_FarB", "_FarC", "_FarE", "_FarD", "_CullA", "_CullB", "_CullD", "_CullT", "_EdgeP", "_FieldP", "_CellK"
        };

        internal static readonly string[] Arrays =
        {
            "_LayerA", "_LayerB", "_LayerC", "_LayerD", "_LayerE", "_DitherM", "_NoiseOffset", "_NoiseLayer", "_NoiseStretch", "_NoiseAxis", "_LightM", "_LightL",
            "_MoonDir", "_MoonCol", "_BoltPos", "_BoltCol", "_LayerH", "_LayerH2", "_CullL", "_LvO", "_FieldC", "_CellE", "_CellS"
        };

        internal static readonly string[] Textures =
        {
            "_TypeProfile", "_TypeInfo", "_CovMaps", "_TypeMaps", "_CovReach", "_SceneDepth", "_MarchTex", "_NoiseShape", "_NoiseDetail", "_NoiseCurl", "_NoiseFlow",
            "_NoiseColour", "_LightCache", "_LightVolume", "_LvBlurSrc", "_HeightMaps", "_CullTiles", "_CullMax0", "_CullMax1", "_EdgeMask", "_CloudField"
        };

        // The local keywords the layer shader declares (multi_compile_local; LightVolume.Keyword, LayerRenderer.LatticeKeyword, CloudDevVariant.Keyword): the
        // readout says for each whether the material has it on.
        internal static readonly string[] Keywords = { "CLOUD_LIGHTVOL", "CLOUD_LATTICE", "CLOUD_DEV" };

        // Where the numbers come from: a material in the game and in the player, a stand-in in the tests.
        internal interface ISource
        {
            float Float(string name);
            Vector4 Vector(string name);
            Vector4[] Array(string name);
            Matrix4x4 Matrix(string name);
            // "<width>x<height>[x<depth>]" of the texture bound, or null for none
            string Texture(string name);
            bool KeywordOn(string name);
            // Every keyword the material has on (null: not known)
            string[] KeywordsOn();
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
            public bool KeywordOn(string name) { return m.IsKeywordEnabled(name); }
            public string[] KeywordsOn() { return m.shaderKeywords; }

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

        // {"floats": {...}, "vectors": {...}, "arrays": {...}, "matrices": {...}, "textures": {...}, "keywords": {...}, "keywordsOn": [...], "readFailures": [...]}; a vector
        // array the material does not hold is null. A read that throws (a vector array never set on the material, where the engine throws instead of returning null) is null
        // too and named in readFailures with the exception's message, so a capture cannot fail on it.
        internal static string Uniforms(ISource s)
        {
            var failures = new System.Collections.Generic.List<string>();
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
                Vector4[] a;
                try
                {
                    a = s.Array(Arrays[i]);
                }
                catch (Exception e)
                {
                    a = null;
                    failures.Add(Arrays[i] + ": " + e.GetType().Name + ": " + e.Message);
                }
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
            b.Append("},\n \"keywords\": {");
            for (int i = 0; i < Keywords.Length; i++)
            {
                if (i > 0) b.Append(", ");
                string on;
                try
                {
                    on = s.KeywordOn(Keywords[i]) ? "true" : "false";
                }
                catch (Exception e)
                {
                    on = "null";
                    failures.Add(Keywords[i] + ": " + e.GetType().Name + ": " + e.Message);
                }
                b.Append('"').Append(Keywords[i]).Append("\": ").Append(on);
            }
            b.Append("},\n \"keywordsOn\": ");
            string[] all = null;
            try
            {
                all = s.KeywordsOn();
            }
            catch (Exception e)
            {
                failures.Add("shaderKeywords: " + e.GetType().Name + ": " + e.Message);
            }
            if (all == null) b.Append("null");
            else
            {
                b.Append('[');
                for (int i = 0; i < all.Length; i++) b.Append(i > 0 ? ", " : "").Append(Str(all[i]));
                b.Append(']');
            }
            b.Append(",\n \"readFailures\": [");
            for (int i = 0; i < failures.Count; i++) b.Append(i > 0 ? ", " : "").Append(Str(failures[i]));
            b.Append("]}");
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
