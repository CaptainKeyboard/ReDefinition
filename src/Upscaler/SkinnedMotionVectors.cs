using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Motion vectors for skinned geometry: kerbals, and planted flags, whose part
    // plays its deploy animation (FlagSite) on a skinned mesh, as the editor's
    // background flag has one (EditorBackgroundFlag; both decompiled).
    //
    // None of KSP's shaders has a MotionVectors pass (docs/development/upscaler.md),
    // so a renderer on Object mode draws its motion with Unity's own
    // Hidden/Internal-MotionVectors. That shader is not only transform based: its
    // vertex function takes the previous position from TEXCOORD4 where Unity has
    // one -- "_HasLastPositionData ? float4(v.oldPos, 1) : v.vertex"
    // (builtin_shaders-2019.4.18f1, DefaultResourcesExtra/Internal-MotionVectors.shader)
    // -- and a skinned renderer keeps it with SkinnedMeshRenderer.skinnedMotionVectors,
    // which "Specifies whether skinned motion vectors should be used for this
    // renderer" (Unity ScriptReference 2019.4). A renderer on Camera mode draws no
    // motion of its own: "Use only camera movement to track motion".
    //
    // So while the rig runs with the switch on, every skinned renderer gets
    // skinnedMotionVectors on, and Object where it was on Camera; what it had is
    // put back when the rig goes. ForceNoMotion is left as it is: another mod set
    // it, and Scatterer's is HostStack's to undo. Not reached this way
    // is geometry a material's vertex function moves: the internal shader draws
    // the mesh without it.
    //
    // Kerbals and flags come with vessels, EVAs, the IVA view, the editor's ship
    // and scene loads: those start a sweep -- never closer than a quarter of a
    // second to the last, a second for the editor's, which come with every part
    // attached -- a second later one more for what they finished building,
    // and every ten seconds one catches the rest.
    internal sealed class SkinnedMotionVectors
    {
        private sealed class Original
        {
            public SkinnedMeshRenderer Renderer;
            public bool SkinnedMotionVectors;
            public MotionVectorGenerationMode Mode;
        }

        private const float SweepSeconds = 10f;
        private const float FollowUpSeconds = 1f;
        private const float MinEventSeconds = 0.25f;
        private const int NamesListed = 8;

        private readonly List<Original> changed = new List<Original>();
        private readonly Dictionary<int, Original> byId = new Dictionary<int, Original>();
        private float nextSweep;
        private float lastSweep = -1000f;
        private bool dirty = true;
        private bool editorDirty;
        private bool subscribed;

        // From the rig's Update: while wanted, a sweep when something may have
        // brought skinned renderers and every ten seconds; everything put back as
        // soon as it is not wanted.
        internal void Refresh(bool wanted)
        {
            if (!wanted)
            {
                Restore();
                return;
            }
            if (!subscribed) Subscribe();

            // An event brings the sweep forward, but not closer than a quarter of a
            // second to the last one -- a second for the editor, which reports
            // every part attached.
            float now = Time.unscaledTime;
            bool due = now >= nextSweep
                       || (dirty && now - lastSweep >= MinEventSeconds)
                       || (editorDirty && now - lastSweep >= FollowUpSeconds);
            if (!due) return;

            nextSweep = now + (dirty || editorDirty ? FollowUpSeconds : SweepSeconds);
            lastSweep = now;
            dirty = false;
            editorDirty = false;
            Sweep();
        }

        // For what brings skinned renderers without an event: a scene load that
        // keeps the rig (UpscalerRig).
        internal void MarkDirty()
        {
            dirty = true;
        }

        private void Subscribe()
        {
            GameEvents.onVesselLoaded.Add(OnVessel);
            GameEvents.onVesselCreate.Add(OnVessel);
            GameEvents.onVesselChange.Add(OnVessel);
            GameEvents.onFlagPlant.Add(OnVessel);
            GameEvents.onCrewOnEva.Add(OnEva);
            GameEvents.OnCameraChange.Add(OnCamera);
            GameEvents.onEditorShipModified.Add(OnShip);
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            GameEvents.onVesselLoaded.Remove(OnVessel);
            GameEvents.onVesselCreate.Remove(OnVessel);
            GameEvents.onVesselChange.Remove(OnVessel);
            GameEvents.onFlagPlant.Remove(OnVessel);
            GameEvents.onCrewOnEva.Remove(OnEva);
            GameEvents.OnCameraChange.Remove(OnCamera);
            GameEvents.onEditorShipModified.Remove(OnShip);
            subscribed = false;
        }

        private void OnVessel(Vessel vessel)
        {
            dirty = true;
        }

        private void OnEva(GameEvents.FromToAction<Part, Part> action)
        {
            dirty = true;
        }

        private void OnCamera(CameraManager.CameraMode mode)
        {
            dirty = true;
        }

        private void OnShip(ShipConstruct ship)
        {
            editorDirty = true;
        }

        private void Sweep()
        {
            // Renderers destroyed with their vessel.
            if (changed.RemoveAll(original => original.Renderer == null) > 0)
            {
                byId.Clear();
                foreach (Original original in changed) byId[original.Renderer.GetInstanceID()] = original;
            }

            foreach (SkinnedMeshRenderer renderer in Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (renderer == null) continue;
                bool vectorsOff = !renderer.skinnedMotionVectors;
                bool cameraOnly = renderer.motionVectorGenerationMode == MotionVectorGenerationMode.Camera;
                if (!vectorsOff && !cameraOnly) continue;

                // The first state seen is the one to put back, however often
                // something sets it again in between.
                int id = renderer.GetInstanceID();
                if (!byId.ContainsKey(id))
                {
                    Original original = new Original
                    {
                        Renderer = renderer,
                        SkinnedMotionVectors = renderer.skinnedMotionVectors,
                        Mode = renderer.motionVectorGenerationMode,
                    };
                    changed.Add(original);
                    byId[id] = original;
                }

                renderer.skinnedMotionVectors = true;
                if (cameraOnly) renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            }
        }

        // A flag or mode changed since stays.
        internal void Restore()
        {
            Unsubscribe();
            foreach (Original original in changed)
            {
                if (original.Renderer == null) continue;
                if (original.Renderer.skinnedMotionVectors)
                    original.Renderer.skinnedMotionVectors = original.SkinnedMotionVectors;
                if (original.Renderer.motionVectorGenerationMode == MotionVectorGenerationMode.Object)
                    original.Renderer.motionVectorGenerationMode = original.Mode;
            }
            changed.Clear();
            byId.Clear();
            nextSweep = 0f;
            dirty = true;
            editorDirty = false;
        }

        // For the diagnostics: the skinned renderers drawn this frame on the given
        // layers, by what decides their motion vectors, with the first few names.
        // isVisible means drawn by any camera, shadows included.
        internal string Describe(int layerMask)
        {
            int drawn = 0, vectorsOn = 0, objectMode = 0, cameraMode = 0, noMotion = 0, transparent = 0, cloth = 0;
            List<string> names = new List<string>();

            foreach (SkinnedMeshRenderer renderer in Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!renderer.isVisible || (layerMask & (1 << renderer.gameObject.layer)) == 0) continue;

                drawn++;
                if (renderer.skinnedMotionVectors) vectorsOn++;
                switch (renderer.motionVectorGenerationMode)
                {
                    case MotionVectorGenerationMode.Object: objectMode++; break;
                    case MotionVectorGenerationMode.Camera: cameraMode++; break;
                    default: noMotion++; break;
                }
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || material.renderQueue <= 2500) continue;
                    transparent++;
                    break;
                }
                // By name: Cloth lives in a Unity module the mod does not reference.
                if (renderer.GetComponent("Cloth") != null) cloth++;
                if (names.Count < NamesListed && !names.Contains(renderer.name)) names.Add(renderer.name);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(drawn).Append(" drawn: skinned motion vectors on ").Append(vectorsOn)
              .Append(", mode Object ").Append(objectMode).Append(" / Camera ").Append(cameraMode)
              .Append(" / ForceNoMotion ").Append(noMotion)
              .Append(", with a material above queue 2500 ").Append(transparent)
              .Append(", with Cloth ").Append(cloth)
              .Append("; set by ReDefinition ").Append(changed.Count);
            if (names.Count > 0) sb.Append(" ('").Append(string.Join("', '", names.ToArray())).Append("')");
            return sb.ToString();
        }
    }
}
