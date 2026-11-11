using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The shaders of the Atmospheres' own bundle (atmospheres.shaders, built by tools/build_atmospheres_bundle.ps1 from
    // unity/Assets/ReDefinitionAtmospheres) found by shader name. The bundle is opened once; every lookup is cached with its error, so
    // a missing bundle, a bundle from another Unity version or a shader that does not compile on this device costs one attempt and
    // one message, and the caller falls back. The bundle must come from Unity 2019.4.18f1, KSP's version: the player rejects any other
    // without a word.
    internal static class AtmosphereShaders
    {
        internal const string BundleName = "atmospheres.shaders";

        private static AssetBundle bundle;
        private static bool bundleTried;
        private static string bundleError;
        private static string pathOverride;
        private static readonly Dictionary<string, Shader> shaders = new Dictionary<string, Shader>();
        private static readonly Dictionary<string, string> errors = new Dictionary<string, string>();

        // The bundle's file in the game; a test names another.
        internal static string BundlePath
        {
            get
            {
                if (pathOverride != null) return pathOverride;
                return Path.Combine(KSPUtil.ApplicationRootPath, Path.Combine("GameData", Path.Combine("ReDefinition",
                    Path.Combine("Atmospheres", Path.Combine("Shaders", BundleName)))));
            }
        }

        // The shader of that name from the bundle, or null with the reason in error. A second call for the same name answers from the
        // cache, including the failure.
        internal static Shader Get(string name, out string error)
        {
            if (string.IsNullOrEmpty(name))
            {
                error = "no shader name given";
                return null;
            }
            Shader cached;
            if (shaders.TryGetValue(name, out cached))
            {
                error = null;
                return cached;
            }
            if (errors.TryGetValue(name, out error)) return null;

            Shader found = Look(name, out error);
            if (found != null)
            {
                shaders[name] = found;
                error = null;
                return found;
            }
            errors[name] = error;
            return null;
        }

        private static Shader Look(string name, out string error)
        {
            Open();
            if (!Opened)
            {
                error = bundleError;
                return null;
            }
            return FindInBundle(name, out error);
        }

        // The engine calls are in methods of their own that are never inlined: the unit tests (no engine) must be able to run the code
        // around them, and a method that contains an engine call cannot even be compiled there.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Shader FindInBundle(string name, out string error)
        {
            foreach (Shader loaded in bundle.LoadAllAssets<Shader>())
            {
                if (loaded == null || loaded.name != name) continue;
                if (loaded.isSupported)
                {
                    error = null;
                    return loaded;
                }
                error = name + " is in the bundle but does not compile or run on this graphics device";
                return null;
            }
            error = "the bundle has no " + name;
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static AssetBundle LoadBundle(string path)
        {
            return AssetBundle.LoadFromFile(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UnloadBundle()
        {
            bundle.Unload(true);
        }

        // Opens the bundle once.
        private static void Open()
        {
            if (bundleTried) return;
            bundleTried = true;
            string path = BundlePath;
            if (!File.Exists(path))
            {
                bundleError = "the shader bundle is missing: " + path + " (tools/build_atmospheres_bundle.ps1 builds it)";
                return;
            }
            bundle = LoadBundle(path);
            if (!Opened) bundleError = "the shader bundle could not be loaded: " + path + " (it has to come from Unity 2019.4.18f1)";
        }

        // Whether the bundle is open. A reference test, not Unity's == on the object: that operator starts the engine's type
        // initialiser, which the unit tests (no engine) must not reach.
        private static bool Opened
        {
            get { return !ReferenceEquals(bundle, null); }
        }

        // Forgets everything, so that the next lookup opens the bundle again; with a path, the bundle at that path (tests).
        internal static void Reset(string path = null)
        {
            if (Opened) UnloadBundle();
            bundle = null;
            bundleTried = false;
            bundleError = null;
            pathOverride = path;
            shaders.Clear();
            errors.Clear();
        }
    }
}
