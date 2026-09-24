using System.Collections.Generic;
using System.IO;
using System.Text;
using FidelityFX.FSR3;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Loads the precompiled compute shaders from the AssetBundle.
    //
    // A Unity player cannot compile shaders at run time: they come from the
    // editor of exactly the version the game was built with, 2019.4.18f1. The
    // bundle is built in unity/ with the menu item "ReDefinition / Build
    // AssetBundle".
    internal static class FsrShaderBundle
    {
        public const string BundleName = "redefinition.shaders";

        // Order does not matter, but the names have to match the file names in
        // unity/Assets/ReDefinition/Shaders -- Unity names the asset after the
        // file.
        private const string PrepareInputs = "ffx_fsr3upscaler_prepare_inputs_pass";
        private const string LumaPyramid = "ffx_fsr3upscaler_luma_pyramid_pass";
        private const string ShadingChangePyramid = "ffx_fsr3upscaler_shading_change_pyramid_pass";
        private const string ShadingChange = "ffx_fsr3upscaler_shading_change_pass";
        private const string PrepareReactivity = "ffx_fsr3upscaler_prepare_reactivity_pass";
        private const string LumaInstability = "ffx_fsr3upscaler_luma_instability_pass";
        private const string Accumulate = "ffx_fsr3upscaler_accumulate_pass";
        private const string AccumulateSharpen = "ffx_fsr3upscaler_accumulate_pass_sharpen";
        private const string Rcas = "ffx_fsr3upscaler_rcas_pass";
        private const string AutoGenReactive = "ffx_fsr3upscaler_autogen_reactive_pass";
        private const string TcrAutoGen = "ffx_fsr3upscaler_tcr_autogen_pass";
        private const string DebugView = "ffx_fsr3upscaler_debug_view_pass";

        private static AssetBundle bundle;
        private static Dictionary<string, ComputeShader> byName;

        public static string LastError { get; private set; }

        public static string BundlePath
        {
            get
            {
                return Path.Combine(KSPUtil.ApplicationRootPath,
                    Path.Combine("GameData", Path.Combine("ReDefinition",
                        Path.Combine("Shaders", BundleName))));
            }
        }

        // Returns null and sets LastError when something is missing. The caller
        // should be able to display that instead of just throwing.
        // Two sets in the bundle: one with HDR_COLOR_INPUT, one without. The camera
        // decides which -- KSP renders without HDR in the editor and with it in
        // flight; a forced floating point buffer changes the clamping during
        // blending and turns the water at the horizon transparent.
        public static Fsr3UpscalerShaders Load(bool hdr, bool sharpening = true)
        {
            if (!File.Exists(BundlePath))
            {
                LastError = "AssetBundle not found: " + BundlePath
                            + "\nOpen the project in unity/ and run 'ReDefinition / Build AssetBundle'.";
                return null;
            }

            if (bundle == null)
            {
                bundle = AssetBundle.LoadFromFile(BundlePath);
                if (bundle == null)
                {
                    LastError = "AssetBundle could not be loaded: " + BundlePath
                                + "\nDoes it come from Unity 2019.4.18f1? The player rejects older or newer versions.";
                    return null;
                }
            }

            // Search by the loaded object's name rather than by asset path: how
            // Unity addresses the entries in the bundle depends on importer
            // details, whereas the object name follows the file.
            if (byName == null)
            {
                byName = new Dictionary<string, ComputeShader>();
                foreach (ComputeShader loaded in bundle.LoadAllAssets<ComputeShader>())
                {
                    if (loaded != null) byName[loaded.name] = loaded;
                }

                Debug.Log(Log.Tag + " AssetBundle loaded, " + byName.Count
                          + " compute shaders in it: " + string.Join(", ", Keys()));
            }

            string suffix = hdr ? "" : "_ldr";

            List<string> missing = new List<string>();
            Fsr3UpscalerShaders shaders = new Fsr3UpscalerShaders
            {
                prepareInputsPass = Get(PrepareInputs + suffix, missing),
                lumaPyramidPass = Get(LumaPyramid + suffix, missing),
                shadingChangePyramidPass = Get(ShadingChangePyramid + suffix, missing),
                shadingChangePass = Get(ShadingChange + suffix, missing),
                prepareReactivityPass = Get(PrepareReactivity + suffix, missing),
                lumaInstabilityPass = Get(LumaInstability + suffix, missing),

                // The keyword is baked into the shader (multi_compile in compute
                // shaders only exists from Unity 2020.1): the sharpen variant writes
                // its result for the RCAS pass, the other straight into the output.
                // The rig dispatches with EnableSharpening to match
                // (UpscalerRig.Sharpening).
                accumulatePass = Get((sharpening ? AccumulateSharpen : Accumulate) + suffix, missing),

                sharpenPass = Get(Rcas + suffix, missing),
                autoGenReactivePass = Get(AutoGenReactive + suffix, missing),
                tcrAutoGenPass = Get(TcrAutoGen + suffix, missing),
                debugViewPass = Get(DebugView + suffix, missing),
            };

            if (missing.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("The AssetBundle is missing ").Append(missing.Count).Append(" shaders: ");
                sb.Append(string.Join(", ", missing.ToArray()));
                sb.Append("\nWas the bundle rebuilt after a change to the shaders?");
                LastError = sb.ToString();
                return null;
            }

            LastError = null;
            return shaders;
        }

        // The shaders command buffers draw with, from the same bundle: the masks'
        // (UpscalerMasks) and the one for EVE's clouds in the motion vectors
        // (CloudMotionVectors).
        public const string MaskShaderName = "Hidden/ReDefinition/Masks";
        public const string CloudMotionShaderName = "Hidden/ReDefinition/CloudMotion";
        // The vessel's motion vectors checked per pixel (VesselMotionAudit).
        public const string MotionAuditShaderName = "Hidden/ReDefinition/MotionAudit";
        private static readonly Dictionary<string, Shader> namedShaders = new Dictionary<string, Shader>();
        private static readonly Dictionary<string, string> namedShaderErrors = new Dictionary<string, string>();

        // Null and the reason where it is not there. Searched once per loaded
        // bundle: a bundle without it stays without it.
        public static Shader LoadShader(string name, out string error)
        {
            Shader found;
            if (namedShaders.TryGetValue(name, out found))
            {
                namedShaderErrors.TryGetValue(name, out error);
                return found;
            }
            if (bundle == null)
            {
                error = "the shader bundle is not loaded";
                return null;
            }
            found = null;
            error = "the shader bundle has no " + name + " -- was it rebuilt after that shader was added?";
            foreach (Shader loaded in bundle.LoadAllAssets<Shader>())
            {
                if (loaded == null || loaded.name != name) continue;
                if (loaded.isSupported)
                {
                    found = loaded;
                    error = null;
                }
                else
                    error = name + " is not supported on this graphics device";
                break;
            }
            namedShaders[name] = found;
            namedShaderErrors[name] = error;
            return found;
        }

        public static void Unload()
        {
            byName = null;
            namedShaders.Clear();
            namedShaderErrors.Clear();
            if (bundle == null) return;
            bundle.Unload(true);
            bundle = null;
        }

        private static ComputeShader Get(string name, List<string> missing)
        {
            ComputeShader shader;
            if (!byName.TryGetValue(name, out shader) || shader == null)
            {
                missing.Add(name);
                return null;
            }
            return shader;
        }

        private static string[] Keys()
        {
            string[] keys = new string[byName.Count];
            byName.Keys.CopyTo(keys, 0);
            System.Array.Sort(keys);
            return keys;
        }
    }
}
