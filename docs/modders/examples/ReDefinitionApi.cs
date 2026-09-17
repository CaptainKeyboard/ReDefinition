// ReDefinition's interface for other mods (ReDefinition.Api), reached without a reference to
// ReDefinition.dll.
//
// Copy this file into a mod and put it in the mod's own namespace: the mod then runs with or
// without ReDefinition. Without it, or with an older interface than this file was written
// for, every member returns what the game has without ReDefinition -- no upscaler, no frame
// generation, the screen's size, no jitter, no reset, no profile, no Direct3D 12 -- and
// every call does nothing.
//
// Each member of the interface is bound once, as a typed delegate, when ReDefinition is
// found: a call then costs what a direct call costs, without boxing or argument arrays.
//
// The interface: docs/modders/shared-foundation.md in ReDefinition's repository.
// Licence of this file: MIT.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace YourMod
{
    internal static class ReDefinitionApi
    {
        // The interface version this file was written for (ReDefinition.Api.ApiInfo.Version).
        internal const int WrittenFor = 1;

        internal delegate int PassStateFn(int pass, out string status);

        // Whether ReDefinition is installed with an interface at least this new.
        internal static bool Installed
        {
            get
            {
                Resolve();
                return version >= WrittenFor;
            }
        }

        internal static int Version
        {
            get
            {
                Resolve();
                return version;
            }
        }

        // The members this file looks for that the installed ReDefinition lacks.
        internal static IList<string> MissingMembers()
        {
            Resolve();
            return missing.AsReadOnly();
        }

        // Frame: the frame's state, set before its first scene camera culls.

        internal static bool UpscalerActive { get { Resolve(); return upscalerActive != null && upscalerActive(); } }
        internal static bool FrameGenerationActive { get { Resolve(); return frameGenerationActive != null && frameGenerationActive(); } }
        internal static string Technique { get { Resolve(); return technique != null ? technique() : null; } }
        internal static Vector2Int RenderSize { get { Resolve(); return renderSize != null ? renderSize() : ScreenSize(); } }
        internal static Vector2Int DisplaySize { get { Resolve(); return displaySize != null ? displaySize() : ScreenSize(); } }
        internal static Vector2 Jitter { get { Resolve(); return jitter != null ? jitter() : Vector2.zero; } }
        internal static Vector2 JitterNdc { get { Resolve(); return jitterNdc != null ? jitterNdc() : Vector2.zero; } }
        internal static bool HistoryReset { get { Resolve(); return historyReset != null && historyReset(); } }
        internal static string HistoryResetReason { get { Resolve(); return historyResetReason != null ? historyResetReason() : null; } }
        internal static Vector3d OriginShift { get { Resolve(); return originShift != null ? originShift() : Vector3d.zero; } }
        internal static Vector3d BodyShift { get { Resolve(); return bodyShift != null ? bodyShift() : Vector3d.zero; } }
        internal static bool OriginShifted { get { Resolve(); return originShifted != null && originShifted(); } }

        internal static void RequestHistoryReset(string reason) { Resolve(); if (requestHistoryReset != null) requestHistoryReset(reason); }
        internal static void RegisterHistoryReset(Action<string> handler) { Resolve(); if (registerHistoryReset != null) registerHistoryReset(handler); }
        internal static void UnregisterHistoryReset(Action<string> handler) { Resolve(); if (unregisterHistoryReset != null) unregisterHistoryReset(handler); }

        // Hooks: places in the frame, while ReDefinition's upscaler or frame generation runs.

        internal static void RegisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            Resolve();
            if (registerMotionVectors != null) registerMotionVectors(handler);
        }

        internal static void UnregisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            Resolve();
            if (unregisterMotionVectors != null) unregisterMotionVectors(handler);
        }

        internal static void RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
        {
            Resolve();
            if (registerAfterUpscaling != null) registerAfterUpscaling(handler);
        }

        internal static void UnregisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
        {
            Resolve();
            if (unregisterAfterUpscaling != null) unregisterAfterUpscaling(handler);
        }

        internal static void RegisterOverlay(Action<CommandBuffer, Camera> handler) { Resolve(); if (registerOverlay != null) registerOverlay(handler); }
        internal static void UnregisterOverlay(Action<CommandBuffer, Camera> handler) { Resolve(); if (unregisterOverlay != null) unregisterOverlay(handler); }

        // Profiles: the graphics profile chosen in ReDefinition's window.

        internal static string Profile { get { Resolve(); return profile != null ? profile() : null; } }
        internal static void RegisterProfileChanged(Action<string> handler) { Resolve(); if (registerProfileChanged != null) registerProfileChanged(handler); }
        internal static void UnregisterProfileChanged(Action<string> handler) { Resolve(); if (unregisterProfileChanged != null) unregisterProfileChanged(handler); }

        // D3D12: compute passes on the dxgi.dll proxy's Direct3D 12 device.

        internal static bool D3D12Available { get { Resolve(); return d3d12Available != null && d3d12Available(); } }
        internal static string D3D12Problem { get { Resolve(); return d3d12Problem != null ? d3d12Problem() : "ReDefinition is not installed"; } }
        internal static string D3D12LastRefusal { get { Resolve(); return lastRefusal != null ? lastRefusal() : "ReDefinition is not installed"; } }
        internal static string D3D12LastCompilerMessages { get { Resolve(); return lastCompilerMessages != null ? lastCompilerMessages() : null; } }
        internal static int D3D12FeatureLevel { get { Resolve(); return featureLevel != null ? featureLevel() : 0; } }
        internal static int D3D12ShaderModel { get { Resolve(); return shaderModel != null ? shaderModel() : 0; } }
        internal static int D3D12RaytracingTier { get { Resolve(); return raytracingTier != null ? raytracingTier() : 0; } }
        internal static int D3D12MeshShaderTier { get { Resolve(); return meshShaderTier != null ? meshShaderTier() : 0; } }
        internal static int D3D12VariableShadingRateTier { get { Resolve(); return variableShadingRateTier != null ? variableShadingRateTier() : 0; } }

        internal static int CreateComputePass(string name, byte[] bytecode)
        {
            Resolve();
            return createComputePass != null ? createComputePass(name, bytecode) : 0;
        }

        internal static int CreateComputePassFromSource(string name, string hlsl, string entryPoint)
        {
            Resolve();
            return createFromSource != null ? createFromSource(name, hlsl, entryPoint) : 0;
        }

        internal static int CreateComputePassFromFile(string name, string path, string entryPoint)
        {
            Resolve();
            return createFromFile != null ? createFromFile(name, path, entryPoint) : 0;
        }

        internal static int ComputePassState(int pass, out string status)
        {
            Resolve();
            if (computePassState != null) return computePassState(pass, out status);
            status = "ReDefinition is not installed";
            return -1;
        }

        internal static bool Dispatch(int pass, RenderTexture[] read, RenderTexture[] write, byte[] constants,
                                      int groupsX, int groupsY, int groupsZ, bool nextFrame)
        {
            Resolve();
            return dispatch != null && dispatch(pass, read, write, constants, groupsX, groupsY, groupsZ, nextFrame);
        }

        internal static bool DispatchInto(CommandBuffer buffer, int pass, RenderTexture[] read, RenderTexture[] write,
                                          byte[] constants, int groupsX, int groupsY, int groupsZ, bool nextFrame)
        {
            Resolve();
            return dispatchInto != null
                   && dispatchInto(buffer, pass, read, write, constants, groupsX, groupsY, groupsZ, nextFrame);
        }

        internal static void DestroyComputePass(int pass) { Resolve(); if (destroyComputePass != null) destroyComputePass(pass); }
        internal static void ReleaseTexture(RenderTexture texture) { Resolve(); if (releaseTexture != null) releaseTexture(texture); }

        private static Func<bool> upscalerActive, frameGenerationActive, historyReset, originShifted, d3d12Available;
        private static Func<string> technique, historyResetReason, profile, d3d12Problem, lastRefusal, lastCompilerMessages;
        private static Func<Vector2Int> renderSize, displaySize;
        private static Func<Vector2> jitter, jitterNdc;
        private static Func<Vector3d> originShift, bodyShift;
        private static Func<int> featureLevel, shaderModel, raytracingTier, meshShaderTier, variableShadingRateTier;
        private static Action<string> requestHistoryReset;
        private static Action<Action<string>> registerHistoryReset, unregisterHistoryReset, registerProfileChanged,
                                              unregisterProfileChanged;
        private static Action<Action<CommandBuffer, RenderTexture, RenderTexture, Camera>> registerMotionVectors,
                                                                                         unregisterMotionVectors;
        private static Action<Action<CommandBuffer, RenderTexture, Camera>> registerAfterUpscaling, unregisterAfterUpscaling;
        private static Action<Action<CommandBuffer, Camera>> registerOverlay, unregisterOverlay;
        private static Func<string, byte[], int> createComputePass;
        private static Func<string, string, string, int> createFromSource, createFromFile;
        private static PassStateFn computePassState;
        private static Func<int, RenderTexture[], RenderTexture[], byte[], int, int, int, bool, bool> dispatch;
        private static Func<CommandBuffer, int, RenderTexture[], RenderTexture[], byte[], int, int, int, bool, bool> dispatchInto;
        private static Action<int> destroyComputePass;
        private static Action<RenderTexture> releaseTexture;

        // A call of its own, not inlined: Unity's Screen is only reached without ReDefinition.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector2Int ScreenSize()
        {
            return new Vector2Int(Screen.width, Screen.height);
        }

        private static readonly List<string> missing = new List<string>();
        private static int version;
        private static bool found;
        private static bool looked;
        private static int lastLook;

        // Looked up until ReDefinition's assembly is found, at most once a second: KSP loads
        // mods' assemblies one after another. TickCount wraps after 24.9 days, and the
        // difference of two readings is right across the wrap.
        private static void Resolve()
        {
            if (found) return;
            int now = Environment.TickCount;
            if (looked && unchecked(now - lastLook) < 1000) return;
            looked = true;
            lastLook = now;

            Assembly assembly = null;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                if (loaded.GetName().Name == "ReDefinition" && loaded.GetType("ReDefinition.Api.ApiInfo") != null)
                    assembly = loaded;
            if (assembly == null) return;
            found = true;

            FieldInfo versionField = assembly.GetType("ReDefinition.Api.ApiInfo").GetField("Version");
            version = versionField != null ? (int)versionField.GetRawConstantValue() : 0;
            // An interface older than this file: nothing is bound, everything returns its fallback.
            if (version < WrittenFor) return;

            upscalerActive = Bind<Func<bool>>(assembly, "Frame", "get_UpscalerActive");
            frameGenerationActive = Bind<Func<bool>>(assembly, "Frame", "get_FrameGenerationActive");
            technique = Bind<Func<string>>(assembly, "Frame", "get_Technique");
            renderSize = Bind<Func<Vector2Int>>(assembly, "Frame", "get_RenderSize");
            displaySize = Bind<Func<Vector2Int>>(assembly, "Frame", "get_DisplaySize");
            jitter = Bind<Func<Vector2>>(assembly, "Frame", "get_Jitter");
            jitterNdc = Bind<Func<Vector2>>(assembly, "Frame", "get_JitterNdc");
            historyReset = Bind<Func<bool>>(assembly, "Frame", "get_HistoryReset");
            historyResetReason = Bind<Func<string>>(assembly, "Frame", "get_HistoryResetReason");
            originShift = Bind<Func<Vector3d>>(assembly, "Frame", "get_OriginShift");
            bodyShift = Bind<Func<Vector3d>>(assembly, "Frame", "get_BodyShift");
            originShifted = Bind<Func<bool>>(assembly, "Frame", "get_OriginShifted");
            requestHistoryReset = Bind<Action<string>>(assembly, "Frame", "RequestHistoryReset");
            registerHistoryReset = Bind<Action<Action<string>>>(assembly, "Frame", "RegisterHistoryReset");
            unregisterHistoryReset = Bind<Action<Action<string>>>(assembly, "Frame", "UnregisterHistoryReset");

            registerMotionVectors = Bind<Action<Action<CommandBuffer, RenderTexture, RenderTexture, Camera>>>(
                assembly, "Hooks", "RegisterMotionVectors");
            unregisterMotionVectors = Bind<Action<Action<CommandBuffer, RenderTexture, RenderTexture, Camera>>>(
                assembly, "Hooks", "UnregisterMotionVectors");
            registerAfterUpscaling = Bind<Action<Action<CommandBuffer, RenderTexture, Camera>>>(
                assembly, "Hooks", "RegisterAfterUpscaling");
            unregisterAfterUpscaling = Bind<Action<Action<CommandBuffer, RenderTexture, Camera>>>(
                assembly, "Hooks", "UnregisterAfterUpscaling");
            registerOverlay = Bind<Action<Action<CommandBuffer, Camera>>>(assembly, "Hooks", "RegisterOverlay");
            unregisterOverlay = Bind<Action<Action<CommandBuffer, Camera>>>(assembly, "Hooks", "UnregisterOverlay");

            profile = Bind<Func<string>>(assembly, "Profiles", "get_Current");
            registerProfileChanged = Bind<Action<Action<string>>>(assembly, "Profiles", "RegisterChanged");
            unregisterProfileChanged = Bind<Action<Action<string>>>(assembly, "Profiles", "UnregisterChanged");

            d3d12Available = Bind<Func<bool>>(assembly, "D3D12", "get_Available");
            d3d12Problem = Bind<Func<string>>(assembly, "D3D12", "get_Problem");
            lastRefusal = Bind<Func<string>>(assembly, "D3D12", "get_LastRefusal");
            lastCompilerMessages = Bind<Func<string>>(assembly, "D3D12", "get_LastCompilerMessages");
            featureLevel = Bind<Func<int>>(assembly, "D3D12", "get_FeatureLevel");
            shaderModel = Bind<Func<int>>(assembly, "D3D12", "get_ShaderModel");
            raytracingTier = Bind<Func<int>>(assembly, "D3D12", "get_RaytracingTier");
            meshShaderTier = Bind<Func<int>>(assembly, "D3D12", "get_MeshShaderTier");
            variableShadingRateTier = Bind<Func<int>>(assembly, "D3D12", "get_VariableShadingRateTier");
            createComputePass = Bind<Func<string, byte[], int>>(assembly, "D3D12", "CreateComputePass");
            createFromSource = Bind<Func<string, string, string, int>>(assembly, "D3D12", "CreateComputePassFromSource");
            createFromFile = Bind<Func<string, string, string, int>>(assembly, "D3D12", "CreateComputePassFromFile");
            computePassState = Bind<PassStateFn>(assembly, "D3D12", "ComputePassState");
            dispatch = Bind<Func<int, RenderTexture[], RenderTexture[], byte[], int, int, int, bool, bool>>(
                assembly, "D3D12", "Dispatch");
            dispatchInto = Bind<Func<CommandBuffer, int, RenderTexture[], RenderTexture[], byte[], int, int, int, bool, bool>>(
                assembly, "D3D12", "DispatchInto");
            destroyComputePass = Bind<Action<int>>(assembly, "D3D12", "DestroyComputePass");
            releaseTexture = Bind<Action<RenderTexture>>(assembly, "D3D12", "ReleaseTexture");
        }

        // The public static method of ReDefinition.Api.<type> with the delegate's parameter types,
        // as that delegate; null, and named in MissingMembers, where there is none.
        private static T Bind<T>(Assembly assembly, string type, string method) where T : class
        {
            Type api = assembly.GetType("ReDefinition.Api." + type);
            ParameterInfo[] parameters = typeof(T).GetMethod("Invoke").GetParameters();
            Type[] types = new Type[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) types[i] = parameters[i].ParameterType;
            MethodInfo target = api != null
                ? api.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, types, null)
                : null;
            Delegate bound = target != null ? Delegate.CreateDelegate(typeof(T), target, false) : null;
            if (bound == null) missing.Add(type + "." + method);
            return bound as T;
        }
    }
}
