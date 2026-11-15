using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // One type of one layer of the stack that the solo control can draw alone.
    internal struct SoloEntry
    {
        internal int Layer, Type;
        internal string LayerName, TypeName, Group;
        // The type's tower feeds an anvil: it is drawn as one cell.
        internal bool Cell;
    }

    // What the solo probe (SoloProbe, series K-S1) asks of the running game beyond the viewer probe's rig: the solo control, the layer mask the pass gave its shader, the camera's
    // field of view and the share of the frame a picture's cloud fills against the picture of the same pose without it.
    internal interface ISoloRig : IViewerRig
    {
        IList<SoloEntry> SoloEntries { get; }
        // The solo selection the keys in force describe (Layer below 0: none).
        SoloSelection Solo { get; }
        void SetSolo(int layer, int type);
        void SetSoloStrength(float strength);
        void SetSoloCellAhead(double km);
        // The layer mask the cloud pass gave its shader in its last frame (bit i = layer i; 0 = every layer): what the shader draws is read here, not in the keys.
        int PassLayerMask { get; }
        // The camera's vertical field of view in degrees.
        double FieldOfViewDeg { get; }
        // The share of the frame a viewer picture fills with cloud against the baseline picture (both by their file names); false where either cannot be read.
        bool MeasurePictures(string picture, string baseline, out MaskReading reading);
    }

    // The game's solo rig: the viewer's solo control, the pass's renderer, the camera and the picture files.
    internal sealed class GameSoloRig : GameViewerRig, ISoloRig
    {
        public IList<SoloEntry> SoloEntries
        {
            get
            {
                var list = new List<SoloEntry>();
                IList<CloudLayerDef> defs = CloudViewer.StackDefs();
                for (int l = 0; l < defs.Count; l++)
                    for (int t = 0; t < defs[l].Types.Count; t++)
                        list.Add(new SoloEntry { Layer = l, Type = t, LayerName = defs[l].Name, Group = defs[l].Group ?? "", TypeName = defs[l].Types[t].Name ?? "", Cell = CloudSoloPlan.IsCellType(defs[l], t) });
                return list;
            }
        }

        public SoloSelection Solo { get { return viewer.SoloNow(); } }
        public void SetSolo(int layer, int type) { viewer.SetSolo(layer, type); }
        public void SetSoloStrength(float strength) { viewer.SetSoloStrength(strength); }
        public void SetSoloCellAhead(double km) { viewer.SetSoloCellAhead(km); }

        public int PassLayerMask
        {
            get
            {
                OwnCloudModule m = OwnCloudModule.Current;
                return m != null && m.Renderer != null ? m.Renderer.UploadedLayerMask : -1;
            }
        }

        public double FieldOfViewDeg
        {
            get
            {
                FlightCamera fc = FlightCamera.fetch;
                return fc != null && fc.mainCamera != null ? fc.mainCamera.fieldOfView : double.NaN;
            }
        }

        public bool MeasurePictures(string picture, string baseline, out MaskReading reading)
        {
            reading = default(MaskReading);
            Texture2D a = Load(picture), b = Load(baseline);
            try
            {
                if (a == null || b == null || a.width != b.width || a.height != b.height) return false;
                reading = PictureMask.Measure(a.GetPixels32(), b.GetPixels32(), a.width, a.height);
                return reading.Valid;
            }
            finally
            {
                if (a != null) UnityEngine.Object.Destroy(a);
                if (b != null) UnityEngine.Object.Destroy(b);
            }
        }

        private Texture2D Load(string fileName)
        {
            try
            {
                string path = CapturePicturePath(fileName);
                if (!System.IO.File.Exists(path)) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(System.IO.File.ReadAllBytes(path))) return tex;
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
