using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // The viewer's solo control (E40 SOLO): one layer and one type of the stack drawn alone over a clear sky, at a stated strength, as a uniform field or as one cell ahead of the camera.
    // It writes the overrides CloudSoloPlan lists (the keys the pass reads: cloud.soloLayer, cloud.mapOverride.<layer>, cloud.typeOverride.<layer>, cloud.deckRule.<layer>, cloud.coverCombine,
    // cloud.cellKm / cellLat / cellLon) through SetKey and ResetKey, so that the options' A/B flip and the presets carry it and Close puts it back; the control's own state is read from the
    // keys in force (SoloNow), never kept apart. No precipitation is forced by anything but forceLevel 0: the solo picture is the cloud pass alone.
    internal sealed partial class CloudViewer
    {
        // The distance of a cell ahead of the camera in km, and its range on the control (a cell is about 14 km wide; its anvil reaches 40 km).
        internal const double SoloCellMinKm = 8.0, SoloCellMaxKm = 90.0, SoloCellDefaultKm = 35.0;
        private double soloCellAheadKm = SoloCellDefaultKm;

        // The layers of the stack of the own cloud pass as the pass draws them (empty where it does not).
        internal static IList<CloudLayerDef> StackDefs()
        {
            var list = new List<CloudLayerDef>();
            OwnCloudModule m = OwnCloudModule.Current;
            LayerStackSource stack = m == null ? null : m.Stack as LayerStackSource;
            if (stack == null) return list;
            for (int l = 0; l < stack.LayerCount; l++) list.Add(stack.LayerAt(l));
            return list;
        }

        // The solo selection the keys in force describe (no solo: On is false). The window asks many times a frame: one read a frame.
        private SoloSelection soloCache;
        private int soloCacheFrame = -1;

        internal SoloSelection SoloNow()
        {
            int frame = Time.frameCount;
            if (soloCacheFrame != frame)
            {
                soloCache = CloudSoloPlan.Read(StackDefs(), (key, d) => store.Effective(key, d));
                soloCacheFrame = frame;
            }
            return soloCache;
        }

        internal double SoloCellAheadKm
        {
            get { return soloCellAheadKm; }
        }

        // layer -1: every layer, the field's own weather. Otherwise the type alone at its default strength (or the strength it has now where the layer is the one already solo).
        internal void SetSolo(int layer, int type)
        {
            IList<CloudLayerDef> defs = StackDefs();
            if (layer < 0 || layer >= defs.Count || type < 0 || type >= defs[layer].Types.Count)
            {
                ApplySolo(defs, new SoloSelection { Layer = -1, Type = -1 });
                return;
            }
            // A type whose tower feeds an anvil is a cell by default (a uniform field of towers has no anvil to show); the control's cell toggle changes that either way.
            ApplySolo(defs, new SoloSelection
            {
                Layer = layer, Type = type, Strength = CloudSoloPlan.DefaultStrength(defs[layer], type), Cell = CloudSoloPlan.IsCellType(defs[layer], type), CellKm = CloudSoloPlan.DefaultCellKm
            });
        }

        internal void SetSoloStrength(float strength)
        {
            SoloSelection s = SoloNow();
            if (!s.On) return;
            s.Strength = Math.Max(0f, Math.Min(1f, strength));
            ApplySolo(StackDefs(), s);
        }

        internal void SetSoloCell(bool on)
        {
            SoloSelection s = SoloNow();
            if (!s.On) return;
            s.Cell = on;
            s.CellKm = CloudSoloPlan.DefaultCellKm;
            ApplySolo(StackDefs(), s);
        }

        internal void SetSoloCellAhead(double km)
        {
            soloCellAheadKm = Math.Max(SoloCellMinKm, Math.Min(SoloCellMaxKm, km));
            SoloSelection s = SoloNow();
            if (s.On && s.Cell) ApplySolo(StackDefs(), s);
        }

        // Writes the selection's overrides; a cell's centre is the point ahead of the camera, in the layer's map frame (its drift is taken out). A cell is held still: it moves with the wind.
        private void ApplySolo(IList<CloudLayerDef> defs, SoloSelection s)
        {
            soloCacheFrame = -1;
            if (s.On && s.Cell)
            {
                double lat, lon;
                if (CellAhead(defs[s.Layer], soloCellAheadKm, out lat, out lon))
                {
                    s.CellLatDeg = lat;
                    s.CellLonDeg = lon;
                }
                else s.Cell = false;
                if (s.Cell && !frozenClouds) FreezeClouds(true);
            }
            foreach (KeyValuePair<string, float> pair in CloudSoloPlan.Overrides(defs, s))
            {
                if (float.IsNaN(pair.Value)) ResetKey(pair.Key);
                else SetKey(pair.Key, pair.Value);
            }
            // The solo picture is the cloud pass alone: no precipitation of the weather's (its extinction and rain are not the clouds).
            store.SetSetting(AtmospheresSettings.Keys.ForceLevel, s.On ? 0.0 : settingsAtOpen[AtmospheresSettings.Keys.ForceLevel]);
            store.SetSetting(AtmospheresSettings.Keys.ForceType, s.On ? (double)(int)ForceType.Auto : settingsAtOpen[AtmospheresSettings.Keys.ForceType]);
            CloudBridgeLights.RequestHistoryReset("cloud viewer solo");
            AtmospheresLog.Say("cloud viewer: " + (s.On ? "solo " + CloudSoloPlan.Label(defs[s.Layer], s.Type) + " at " + s.Strength.ToString("0.00", CultureInfo.InvariantCulture)
                               + (s.Cell ? ", one cell " + soloCellAheadKm.ToString("0", CultureInfo.InvariantCulture) + " km ahead" : ", a uniform field") : "solo off"));
        }

        // The point the cell stands at: ahead of the camera along its heading, as the layer's map holds it (the world longitude less the layer's drift).
        private bool CellAhead(CloudLayerDef layer, double km, out double lat, out double lon)
        {
            lat = lon = 0.0;
            try
            {
                Vessel v = GameGlobals.ActiveVessel;
                if (v == null || v.mainBody == null || FlightCamera.fetch == null) return false;
                CelestialBody body = v.mainBody;
                Vector3d at = FlightCamera.fetch.mainCamera.transform.position;
                T09Pose pose = world.ReadPose();
                T09Views.Destination(body.GetLatitude(at), body.GetLongitude(at), pose.AzimuthDeg, km, body.Radius, out lat, out lon);
                lon -= layer.MapShiftDeg;
                while (lon > 180.0) lon -= 360.0;
                while (lon < -180.0) lon += 360.0;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // What the status line says of the solo mode.
        internal string SoloText()
        {
            SoloSelection s = SoloNow();
            IList<CloudLayerDef> defs = StackDefs();
            if (!s.On || s.Layer >= defs.Count) return "Solo: off, every layer is drawn";
            return "Solo: " + CloudSoloPlan.Label(defs[s.Layer], s.Type) + " at " + s.Strength.ToString("0.00", CultureInfo.InvariantCulture) + (s.Cell ? ", one cell" : ", a uniform field");
        }

        // A viewer that closes with its options kept still takes the solo mode back (as it does the forced weather).
        private void RestoreSolo()
        {
            foreach (string key in CloudSoloPlan.GlobalKeys()) Restore(key);
            foreach (KeyValuePair<string, float> pair in CloudSoloPlan.Overrides(StackDefs(), new SoloSelection { Layer = -1, Type = -1 })) Restore(pair.Key);
        }
    }
}
