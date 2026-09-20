using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using YourMod;

namespace ReDefinitionExample
{
    // Each part of ReDefinition's interface once, in flight, through the wrapper
    // (ReDefinitionApi.cs), so it runs with or without ReDefinition:
    //
    //   * an overlay: a line from the active vessel along its surface velocity, drawn after
    //     the scene and kept out of frame generation;
    //   * a compute pass after the upscaler: ExampleEffect.hlsl, compiled by ReDefinition,
    //     darkens the upscaled image towards its edges on the Direct3D 12 device;
    //   * a camera cut on a key the player sets: the binding stands in this mod's
    //     registration (ReDefinitionExample.cfg) and in ReDefinition's Keys tab, and it puts
    //     the flight camera back to its start distance at once, reporting the jump. Without
    //     ReDefinition the mod keeps its own key.
    //
    // Every history reset is logged.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ExampleAddon : MonoBehaviour
    {
        private const string Tag = "[ReDefinitionExample]";

        // The overlay's line: two vertices, updated every frame without allocating.
        private Mesh line;
        private Material lineMaterial;
        private readonly Vector3[] lineVertices = new Vector3[2];
        private readonly Color[] lineColours = { Color.cyan, Color.cyan };
        private readonly int[] lineIndices = { 0, 1 };

        // The compute pass and what it reads and writes.
        private int pass;
        private RenderTexture darkened;
        private readonly RenderTexture[] read = new RenderTexture[1];
        private readonly RenderTexture[] write = new RenderTexture[1];
        // The compute pass's constants: how far the edges are darkened, from this
        // mod's own settings (SettingsBridge).
        private readonly float[] parameters = { 0.35f, 0f, 0f, 0f };
        private readonly byte[] constants = new byte[16];
        private bool dispatchRefusalLogged;
        private bool overlay = true;

        // The one in the scene, for a setting changed while it runs.
        private static ExampleAddon instance;

        private Action<CommandBuffer, Camera> drawOverlay;
        private Action<CommandBuffer, RenderTexture, Camera> darken;
        private Action<string> logReset;

        private void Start()
        {
            instance = this;
            if (!ReDefinitionApi.Installed)
            {
                Debug.Log(Tag + " ReDefinition is not installed; nothing to show.");
                return;
            }

            line = new Mesh { name = "ReDefinitionExample line" };
            line.MarkDynamic();
            // Unity's built-in debug shader: _Color times the vertex colour. The backbuffer the
            // overlay draws onto holds no scene depth, so no depth test.
            lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            lineMaterial.SetInt("_ZWrite", 0);

            // Delegates made once: a method group makes a new one each time.
            ApplySettings();

            drawOverlay = DrawOverlay;
            darken = Darken;
            logReset = LogReset;
            ReDefinitionApi.RegisterOverlay(drawOverlay);
            ReDefinitionApi.RegisterHistoryReset(logReset);

            string effect = Path.Combine(Path.GetDirectoryName(typeof(ExampleAddon).Assembly.Location), "ExampleEffect.hlsl");
            pass = ReDefinitionApi.CreateComputePassFromFile("ReDefinitionExample darken", effect, "main");
            if (pass != 0)
                ReDefinitionApi.RegisterAfterUpscaling(darken);
            else
                Debug.LogWarning(Tag + " No compute pass: " + ReDefinitionApi.D3D12LastRefusal);
        }

        // The binding's key: this mod's id in its registration, a dot, and the KEY
        // block's name.
        private const string CameraCutKey = "redefinitionexample.cameraCut";

        private void Update()
        {
            if (!CutPressed())
                return;
            FlightCamera camera = FlightCamera.fetch;
            if (camera == null)
                return;
            // Update runs before any camera culls: the reset reaches every mod in this frame.
            camera.SetDistanceImmediate(camera.startDistance);
            ReDefinitionApi.RequestHistoryReset("ReDefinitionExample put the flight camera back to its start distance");
        }

        // With ReDefinition the player's binding, which its settings window sets; without it
        // this mod's own key.
        private bool CutPressed()
        {
            if (ReDefinitionApi.Installed)
                return ReDefinitionApi.KeyPressed(CameraCutKey);
            return Input.GetKey(KeyCode.RightControl) && Input.GetKey(KeyCode.RightShift)
                   && Input.GetKeyDown(KeyCode.J);
        }

        // A setting changed, here or in ReDefinition's window.
        internal static void SettingsChanged()
        {
            if (instance != null) instance.ApplySettings();
        }

        private void ApplySettings()
        {
            parameters[0] = SettingsBridge.Number("edgeDarkening", 0.35f);
            overlay = SettingsBridge.Flag("overlay", true);
        }

        private void DrawOverlay(CommandBuffer buffer, Camera scene)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || !overlay)
                return;

            lineVertices[0] = vessel.CurrentCoM;
            lineVertices[1] = vessel.CurrentCoM + (Vector3)vessel.srf_velocity;
            line.vertices = lineVertices;
            line.colors = lineColours;
            line.SetIndices(lineIndices, MeshTopology.Lines, 0);

            buffer.SetViewProjectionMatrices(scene.worldToCameraMatrix, scene.nonJitteredProjectionMatrix);
            buffer.DrawMesh(line, Matrix4x4.identity, lineMaterial);
        }

        // In the buffer ReDefinition executes right after the upscaler: the dispatch reads the
        // upscaled image, and the blit after it brings the result back into that image.
        private void Darken(CommandBuffer buffer, RenderTexture image, Camera scene)
        {
            if (darkened == null || darkened.width != image.width || darkened.height != image.height
                || darkened.format != image.format)
            {
                ReleaseDarkened();
                darkened = new RenderTexture(image.width, image.height, 0, image.format) { name = "ReDefinitionExample darkened" };
                darkened.Create();
            }

            read[0] = image;
            write[0] = darkened;
            Buffer.BlockCopy(parameters, 0, constants, 0, constants.Length);
            if (!ReDefinitionApi.DispatchInto(buffer, pass, read, write, constants, (image.width + 7) / 8,
                                              (image.height + 7) / 8, 1, false))
            {
                if (!dispatchRefusalLogged)
                    Debug.LogWarning(Tag + " Dispatch refused: " + ReDefinitionApi.D3D12LastRefusal);
                dispatchRefusalLogged = true;
                return;
            }
            buffer.Blit(darkened, image);
        }

        private static void LogReset(string reason)
        {
            Debug.Log(Tag + " History reset: " + reason);
        }

        private void ReleaseDarkened()
        {
            if (darkened == null)
                return;
            ReDefinitionApi.ReleaseTexture(darkened);
            darkened.Release();
            Destroy(darkened);
            darkened = null;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (drawOverlay != null) ReDefinitionApi.UnregisterOverlay(drawOverlay);
            if (darken != null) ReDefinitionApi.UnregisterAfterUpscaling(darken);
            if (logReset != null) ReDefinitionApi.UnregisterHistoryReset(logReset);
            if (pass != 0) ReDefinitionApi.DestroyComputePass(pass);
            ReleaseDarkened();
            if (line != null) Destroy(line);
            if (lineMaterial != null) Destroy(lineMaterial);
        }
    }
}
