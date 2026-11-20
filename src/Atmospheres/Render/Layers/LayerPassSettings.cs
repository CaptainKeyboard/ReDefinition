using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // One number of the pass's settings as pass.json carries it and the weather player's job takes it back (master plan phase 0 item 3, the pinned capture): the path of a
    // field below one of the roots (Frame, Recon, Noise, Soft) and its value as round-trip text ("R"), so that infinities and NaN pass through JSON. The values of a
    // leaf are one string; "null" marks a reference or nullable field that holds nothing.
    [Serializable]
    internal sealed class LayerPassSetting
    {
        public string path;
        public string[] values;
    }

    // The march and temporal settings in effect, read from the objects the pass ran with (LayerFrame with its light, far view and culling, the reconstruction's
    // effective ReconSettings, the noise switches, the soft density's statics) by walking their public instance fields, and set back the same way in the weather
    // player. A field added to any of these classes is carried without a change here, so the player can not fall behind the game by a hand-kept table again
    // (the cause found in .local/plans/wolken-m1-fakten.md section F: e40gn.py's table marched 25 m steps and 3 taps where the game marched 6 m and 8).
    // Leaves are the primitive types and enums; structs (Vector3, Color, Matrix4x4, CloudLight) are walked into; arrays by element; class fields of this namespace are
    // walked into, other references (a LightCache, a texture) are left out and named in Skipped.
    internal static class LayerPassSettings
    {
        internal const string RootFrame = "Frame", RootRecon = "Recon", RootNoise = "Noise", RootSoft = "Soft";
        internal const string Null = "null";

        // What a walk left out (a reference of another kind), for the readout.
        internal sealed class Result
        {
            internal readonly List<LayerPassSetting> Settings = new List<LayerPassSetting>();
            internal readonly List<string> Skipped = new List<string>();
            // The skipped references that hold something (a reference's default is null, so these are not at their default: a light cache that is on): the replay cannot
            // set them, so a replay of such a pass is not the game's and names them as problems.
            internal readonly List<string> NotDefault = new List<string>();
        }

        internal static Result Collect(LayerFrame frame, ReconSettings recon, NoiseSwitches noise)
        {
            var r = new Result();
            if (frame != null) Walk(frame, frame.GetType(), RootFrame, r, 0);
            if (recon != null) Walk(recon, recon.GetType(), RootRecon, r, 0);
            if (noise != null) Walk(noise, noise.GetType(), RootNoise, r, 0);
            r.Settings.Add(Leaf(RootSoft + ".Softness", CloudSoftDensity.Softness));
            r.Settings.Add(Leaf(RootSoft + ".DetailMean", CloudSoftDensity.DetailMean));
            return r;
        }

        private static LayerPassSetting Leaf(string path, object value)
        {
            return new LayerPassSetting { path = path, values = new[] { Text(value) } };
        }

        // The fields of a type in declaration order (metadata token), public instance ones only.
        private static FieldInfo[] Fields(Type t)
        {
            FieldInfo[] f = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(f, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            return f;
        }

        private static bool IsLeaf(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(decimal);
        }

        // Classes of the pass's own namespace are walked into; a LightCache is a GPU resource, not a setting (its own switches are the light settings').
        private static bool Walkable(Type t)
        {
            return t.IsClass && t.Namespace == typeof(LayerFrame).Namespace && t != typeof(LightCache) && !typeof(UnityEngine.Object).IsAssignableFrom(t)
                   && !typeof(Delegate).IsAssignableFrom(t);
        }

        // Depth 8 is far beyond the deepest nesting (Frame.Light.Lights[0].Colour.r is 4); a cycle would stop there instead of overflowing the stack.
        private const int MaxDepth = 8;

        private static void Walk(object obj, Type t, string path, Result r, int depth)
        {
            if (depth > MaxDepth) { r.Skipped.Add(path + " (too deep)"); return; }
            foreach (FieldInfo f in Fields(t))
            {
                string p = path + "." + f.Name;
                WalkValue(f.GetValue(obj), f.FieldType, p, r, depth + 1);
            }
        }

        private static void WalkValue(object value, Type t, string p, Result r, int depth)
        {
            Type under = Nullable.GetUnderlyingType(t);
            if (under != null)
            {
                if (value == null) r.Settings.Add(new LayerPassSetting { path = p, values = new[] { Null } });
                else WalkValue(value, under, p, r, depth);
                return;
            }
            if (IsLeaf(t))
            {
                r.Settings.Add(Leaf(p, value));
                return;
            }
            if (t.IsArray)
            {
                var a = value as Array;
                if (a == null) { r.Skipped.Add(p + " (null array)"); return; }
                Type e = t.GetElementType();
                for (int k = 0; k < a.Length; k++) WalkValue(a.GetValue(k), e, p + "[" + k.ToString(CultureInfo.InvariantCulture) + "]", r, depth + 1);
                return;
            }
            if (t.IsValueType)
            {
                Walk(value, t, p, r, depth);
                return;
            }
            if (Walkable(t))
            {
                if (value == null) r.Settings.Add(new LayerPassSetting { path = p, values = new[] { Null } });
                else Walk(value, value.GetType(), p, r, depth);
                return;
            }
            r.Skipped.Add(p + " (" + t.Name + (value == null ? ", null" : ", set") + ")");
            if (value != null) r.NotDefault.Add(p + " (" + t.Name + ")");
        }

        internal static string Text(object value)
        {
            if (value == null) return Null;
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is bool) return (bool)value ? "1" : "0";
            if (value is Enum) return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static object Parse(Type t, string text)
        {
            if (t == typeof(float)) return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (t == typeof(double)) return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
            if (t.IsEnum) return Enum.ToObject(t, long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
            return Convert.ChangeType(text, t, CultureInfo.InvariantCulture);
        }

        // The settings as a JSON array of {"path", "values"} (the form the player's job reads with JsonUtility).
        internal static string Json(IList<LayerPassSetting> settings)
        {
            var b = new StringBuilder(settings.Count * 48 + 4);
            b.Append('[');
            for (int i = 0; i < settings.Count; i++)
            {
                if (i > 0) b.Append(",\n  ");
                b.Append("{\"path\": ").Append(LayerPassReadout.Str(settings[i].path)).Append(", \"values\": [");
                string[] v = settings[i].values ?? new string[0];
                for (int k = 0; k < v.Length; k++)
                {
                    if (k > 0) b.Append(", ");
                    b.Append(LayerPassReadout.Str(v[k]));
                }
                b.Append("]}");
            }
            return b.Append(']').ToString();
        }

        // Sets every setting onto the objects (the player's frame, its reconstruction settings, its noise switches); the soft density's statics are set too. A path in
        // skip (exact, or a prefix ending in '.') is left as it is. Returns the paths that could not be set (unknown field, wrong root, a value that does not parse):
        // a replay with any of them is not the game's pass.
        internal static List<string> Apply(IEnumerable<LayerPassSetting> settings, LayerFrame frame, ReconSettings recon, NoiseSwitches noise, ICollection<string> skip)
        {
            var problems = new List<string>();
            if (settings == null) return problems;
            foreach (LayerPassSetting s in settings)
            {
                if (s == null || string.IsNullOrEmpty(s.path)) continue;
                if (Skipped(s.path, skip)) continue;
                string[] parts = s.path.Split('.');
                string problem = null;
                try
                {
                    switch (parts[0])
                    {
                        case RootFrame: problem = Set(frame, parts, 1, s.values); break;
                        case RootRecon: problem = Set(recon, parts, 1, s.values); break;
                        case RootNoise: problem = Set(noise, parts, 1, s.values); break;
                        case RootSoft:
                            if (parts.Length == 2 && parts[1] == "Softness") CloudSoftDensity.Softness = (double)Parse(typeof(double), s.values[0]);
                            else if (parts.Length == 2 && parts[1] == "DetailMean") CloudSoftDensity.DetailMean = (double)Parse(typeof(double), s.values[0]);
                            else problem = "unknown";
                            break;
                        default: problem = "unknown root"; break;
                    }
                }
                catch (Exception e)
                {
                    problem = e.GetType().Name + " " + e.Message;
                }
                if (problem != null) problems.Add(s.path + ": " + problem);
            }
            return problems;
        }

        private static bool Skipped(string path, ICollection<string> skip)
        {
            if (skip == null) return false;
            foreach (string k in skip)
                if (path == k || (k.EndsWith(".", StringComparison.Ordinal) && path.StartsWith(k, StringComparison.Ordinal))) return true;
            return false;
        }

        // Sets the value at parts[i..] below obj (a class instance or a boxed struct, which the caller writes back). Null where it was set, else the problem.
        private static string Set(object obj, string[] parts, int i, string[] values)
        {
            if (obj == null) return "no object for the root";
            if (values == null || values.Length == 0) return "no value";
            string part = parts[i];
            int index = -1;
            int open = part.IndexOf('[');
            if (open >= 0)
            {
                index = int.Parse(part.Substring(open + 1, part.Length - open - 2), CultureInfo.InvariantCulture);
                part = part.Substring(0, open);
            }
            FieldInfo f = obj.GetType().GetField(part, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return "no field " + part + " on " + obj.GetType().Name;
            bool last = i == parts.Length - 1;
            if (index >= 0)
            {
                var a = f.GetValue(obj) as Array;
                if (a == null || index >= a.Length) return "no element " + index + " of " + part;
                Type e = f.FieldType.GetElementType();
                if (last)
                {
                    a.SetValue(LeafValue(e, values[0]), index);
                    return null;
                }
                object child = a.GetValue(index);
                string p = Set(child, parts, i + 1, values);
                if (p == null && e.IsValueType) a.SetValue(child, index);
                return p;
            }
            Type ft = f.FieldType;
            Type under = Nullable.GetUnderlyingType(ft);
            if (last)
            {
                if (values[0] == Null)
                {
                    if (ft.IsValueType && under == null) return "null for a value field";
                    f.SetValue(obj, null);
                    return null;
                }
                if (!IsLeaf(under ?? ft)) return "not a leaf";
                f.SetValue(obj, Parse(under ?? ft, values[0]));
                return null;
            }
            object value = f.GetValue(obj);
            if (value == null)
            {
                Type make = under ?? ft;
                if (!make.IsValueType && !Walkable(make)) return "null reference " + part;
                value = Activator.CreateInstance(make, true);
            }
            string problem = Set(value, parts, i + 1, values);
            if (problem == null && (ft.IsValueType || under != null || f.GetValue(obj) == null)) f.SetValue(obj, value);
            return problem;
        }

        private static object LeafValue(Type t, string text)
        {
            if (text == Null) return null;
            return Parse(t, text);
        }

        // --- The named summary of the march and the temporal settings (pass.json "march"; the full list is "settings") ------------------------------

        internal static string MarchJson(LayerFrame f, ReconSettings r)
        {
            var b = new StringBuilder(1024);
            b.Append("{\"minStepM\": ").Append(LayerPassReadout.Num(f.MinStepM))
             .Append(", \"gridPoints\": ").Append(f.MaxSteps)
             .Append(", \"sunSteps\": ").Append(f.Light.SunStepsOverride > 0 ? f.Light.SunStepsOverride : f.SunSteps)
             .Append(", \"frameSunSteps\": ").Append(f.SunSteps)
             .Append(", \"sunMarchM\": ").Append(LayerPassReadout.Num(f.SunMarchM))
             .Append(", \"lightStride\": ").Append(f.LightStride)
             .Append(", \"lightCutoff\": ").Append(LayerPassReadout.Num(f.LightCutoff))
             .Append(", \"earlyOut\": ").Append(LayerPassReadout.Num(f.EarlyOut))
             .Append(", \"stepGrowth\": ").Append(LayerPassReadout.Num(f.StepGrowth))
             .Append(", \"transmittanceGrowth\": ").Append(LayerPassReadout.Num(f.TransmittanceGrowth))
             .Append(", \"cullStepGrowth\": ").Append(f.Cull.StepGrowth ? 1 : 0)
             .Append(", \"cullStepGrowthPerM\": ").Append(LayerPassReadout.Num(f.Cull.StepGrowthPerM))
             .Append(", \"emptyLeap\": ").Append(LayerPassReadout.Num(f.EmptyLeap))
             .Append(", \"leapScales\": ").Append(LayerPassReadout.Num(f.LeapScales))
             .Append(", \"ditherSequence\": ").Append(f.DitherSequence)
             .Append(", \"frameResolution\": ").Append(LayerPassReadout.Num(f.Resolution));
            if (r != null)
            {
                b.Append(", \"reconMode\": ").Append(LayerPassReadout.Str(r.Mode.ToString()))
                 .Append(", \"resolution\": ").Append(LayerPassReadout.Num(r.Resolution))
                 .Append(", \"accumulate\": ").Append(LayerPassReadout.Num(r.Accumulate))
                 .Append(", \"rotation\": ").Append(r.Rotation ? 1 : 0)
                 .Append(", \"blockSide\": ").Append(r.Block)
                 .Append(", \"freshWeight\": ").Append(LayerPassReadout.Num(r.FreshWeight))
                 .Append(", \"staleWeight\": ").Append(LayerPassReadout.Num(r.StaleWeight))
                 .Append(", \"clipSigma\": ").Append(LayerPassReadout.Num(r.ClipSigma))
                 .Append(", \"catmullHistory\": ").Append(r.CatmullHistory ? 1 : 0)
                 .Append(", \"driftSeconds\": ").Append(LayerPassReadout.Num(r.DriftSeconds));
            }
            return b.Append('}').ToString();
        }

        // --- Hashes ----------------------------------------------------------------------------------------------------------------------------------

        // FNV-1a over 64 bits (Fowler, Noll, Vo; the offset basis and the prime of the published 64-bit variant): the same bytes give the same hash in the game and in the player.
        internal static ulong Fnv1a64(byte[] data, ulong hash = 14695981039346656037UL)
        {
            if (data == null) return hash;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }

        internal static string Hex(ulong h)
        {
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    // The files of the pinned capture and of its replay: float32 .npy (NumPy format 1.0, little endian, C order), which tools/weather_metrics/m0parity.py reads. Rows are
    // written as the caller holds them (Unity's readbacks: the bottom row first).
    internal static class PinFiles
    {
        internal static void WriteFloat32(string path, int height, int width, int channels, float[] data)
        {
            if (data.Length != height * width * channels) throw new ArgumentException("the data has " + data.Length + " values, not " + height + " x " + width + " x " + channels);
            string shape = channels > 1 ? "(" + height + ", " + width + ", " + channels + ")" : "(" + height + ", " + width + ")";
            string header = "{'descr': '<f4', 'fortran_order': False, 'shape': " + shape + ", }";
            // The header is padded with spaces so that magic (6) + version (2) + length (2) + header + newline is a multiple of 64 (the format's alignment).
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                writer.Write(bytes);
            }
        }

        // The values of a file WriteFloat32 wrote (format 1.0, '<f4'): the floats after the header, in the file's order.
        internal static float[] ReadFloat32(string path)
        {
            byte[] all = System.IO.File.ReadAllBytes(path);
            if (all.Length < 10 || all[0] != 0x93 || all[1] != (byte)'N' || all[6] != 1) throw new System.IO.InvalidDataException(path + " is not a NumPy 1.0 file");
            int start = 10 + (all[8] | all[9] << 8);
            var data = new float[(all.Length - start) / 4];
            Buffer.BlockCopy(all, start, data, 0, data.Length * 4);
            return data;
        }
    }
}
