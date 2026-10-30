using System;
using System.Collections.Generic;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Scaling;
using UnityEngine;
using Vectrosity;

namespace ReDefinition.Engineer
{
    // Draws the legs of a planned route in the map view as lines, the way Parsek draws its trajectories. Each leg is one Vectrosity line on the
    // orbit-line layer. Its points are sampled once from the leg's Kepler orbit between its start and end. Every frame they are put in
    // scaled space again from the body's scaled centre. The lines are drawn in the map camera's pre-cull, after the camera has moved.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class RoutePreview : MonoBehaviour
    {
        // KSP puts every orbit line on layer 31 (OrbitRendererBase), in 3D and in 2D.
        private const int OrbitLayer = 31;
        // The width of KSP's orbit lines in pixels (OrbitRendererBase.MakeLine); the scale is applied as in ScalingHooks.
        private const float OrbitWidth = 5f;
        // The points of a leg: 360 degrees at the stock sampleResolution of 2, as the stock orbit lines have.
        private const int Samples = 180;
        private const double Turn = 2.0 * Math.PI;

        internal static RoutePreview Instance;

        private sealed class Leg
        {
            public string Name;
            public CelestialBody Body;
            public Color Color;
            // The sampled points relative to the body's centre, in the world's axes.
            public Vector3d[] Relative;
            public VectorLine Line;
            // 1 where the line was made for the 3D mode, 2 for the 2D mode.
            public int Mode;
        }

        private readonly List<Leg> legs = new List<Leg>();
        private int drawnFrame = -1;

        private void Awake()
        {
            Instance = this;
            Camera.onPreCull += OnMapCameraPreCull;
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnMapCameraPreCull;
            if (Instance == this) Instance = null;
            Clear();
        }

        // Replaces the lines with the legs of `plan`; a plan without legs only takes them away.
        internal void Show(RoutePlan plan)
        {
            Clear();
            if (plan == null) return;
            try
            {
                for (int i = 0; i < plan.Legs.Count; i++)
                {
                    Leg leg = Sample(plan.Legs[i], i);
                    if (leg != null) legs.Add(leg);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Route preview: the legs could not be sampled: " + e);
                Clear();
            }
        }

        internal void Hide()
        {
            Clear();
        }

        private void Clear()
        {
            foreach (Leg leg in legs) DropLine(leg);
            legs.Clear();
        }

        private static void DropLine(Leg leg)
        {
            if (leg.Line == null) return;
            VectorLine line = leg.Line;
            VectorLine.Destroy(ref line);
            leg.Line = null;
        }

        // A leg's points along its orbit from the start to the end: no more than one revolution of an ellipse, and the open arc of a hyperbola.
        private static Leg Sample(PlannedLeg planned, int index)
        {
            Orbit orbit = planned.Orbit;
            if (orbit == null || orbit.referenceBody == null || planned.EndUT <= planned.StartUT) return null;
            bool hyperbolic = orbit.eccentricity > 1.0;
            if (!hyperbolic && orbit.eccentricity >= 1.0) return null;
            double from = orbit.EccentricAnomalyAtUT(planned.StartUT);
            double to = orbit.EccentricAnomalyAtUT(planned.EndUT);
            if (double.IsNaN(from) || double.IsNaN(to)) return null;
            if (!hyperbolic)
            {
                // The anomaly is cyclic: the arc runs on from the start, and a leg of a whole period or more is the whole ellipse.
                double span = orbit.period > 0.0 && planned.EndUT - planned.StartUT < orbit.period ? to - from : Turn;
                if (span < 0.0 || span > Turn) span -= Turn * Math.Floor(span / Turn);
                to = from + span;
            }
            double semiMinorAxis = orbit.semiMinorAxis;
            double step = (to - from) / (Samples - 1);
            Vector3d centre = orbit.referenceBody.position;
            var relative = new Vector3d[Samples];
            for (int i = 0; i < Samples; i++)
            {
                relative[i] = orbit.getPositionFromEccAnomalyWithSemiMinorAxis(from + step * i, semiMinorAxis) - centre;
                if (!IsFinite(relative[i].x) || !IsFinite(relative[i].y) || !IsFinite(relative[i].z)) return null;
            }
            CelestialBody body = orbit.referenceBody;
            return new Leg
            {
                Name = "ReDefinitionRouteLeg-" + index,
                Body = body,
                Color = LegColor(body),
                Relative = relative,
            };
        }

        // A leg on another body gets that body's orbit colour in the map; the Sun, which has none, the stock orbit line's grey.
        private static Color LegColor(CelestialBody body)
        {
            if (body.orbitDriver != null) return body.orbitDriver.orbitColor;
            Color node = new Color(0.71f, 0.71f, 0.71f, 1f);
            Color grey = node * 0.5f;
            grey.a = node.a;
            return grey;
        }

        // Whether the map lines are in the 3D mode: KSP turns it off when the camera is far out or many orbits are shown (MapView.UpdateMap).
        private static bool Lines3D()
        {
            return MapView.fetch == null || MapView.Draw3DLines;
        }

        // A solid line in the style of KSP's orbit lines (OrbitRendererBase.MakeLine), in the colour of the leg.
        private static VectorLine MakeLine(string name, Color color)
        {
            var points = new List<Vector3>(Samples);
            for (int i = 0; i < Samples; i++) points.Add(Vector3.zero);
            var line = new VectorLine(name, points, OrbitWidth * UiScaling.World, LineType.Continuous);
            Material material = MapView.OrbitLinesMaterial;
            if (material != null)
            {
                line.texture = material.mainTexture;
                line.material = material;
                material.SetFloat("_FadeStrength", GameSettings.ORBIT_FADE_STRENGTH);
                material.SetFloat("_FadeSign", GameSettings.ORBIT_FADE_DIRECTION_INV ? -1f : 1f);
            }
            line.continuousTexture = true;
            line.UpdateImmediate = true;
            line.SetColor(color);
            return line;
        }

        private void Update()
        {
            if (MapView.MapIsEnabled) return;
            foreach (Leg leg in legs)
                if (leg.Line != null && leg.Line.active) leg.Line.active = false;
        }

        private void OnMapCameraPreCull(Camera camera)
        {
            if (legs.Count == 0 || PlanetariumCamera.fetch == null || camera != PlanetariumCamera.Camera) return;
            if (!HighLogic.LoadedSceneIsFlight || !MapView.MapIsEnabled) return;
            // The event can come more than once in a frame.
            if (drawnFrame == Time.frameCount) return;
            drawnFrame = Time.frameCount;
            foreach (Leg leg in legs)
            {
                try
                {
                    Draw(leg);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " Route preview: " + leg.Name + " could not be drawn: " + e.Message);
                    DropLine(leg);
                }
            }
        }

        private static void Draw(Leg leg)
        {
            Transform scaledBody = leg.Body.scaledBody != null ? leg.Body.scaledBody.transform : null;
            if (scaledBody == null)
            {
                if (leg.Line != null) leg.Line.active = false;
                return;
            }
            // KSP makes its orbit lines again when the mode changes; Vectrosity's own change from 3D to 2D leaves the line not drawn.
            int mode = Lines3D() ? 1 : 2;
            if (leg.Line != null && leg.Mode != mode) DropLine(leg);
            if (leg.Line == null) leg.Line = MakeLine(leg.Name, leg.Color);
            leg.Mode = mode;
            VectorLine line = leg.Line;
            float width = OrbitWidth * UiScaling.World;
            if (Mathf.Abs(line.lineWidth - width) > 0.01f) line.lineWidth = width;
            // The layer in both modes; the transform is cleared in 3D only, where the mesh is made of absolute scaled-space points. In 2D
            // the line belongs to Vectrosity's canvas, which is left alone.
            if (line.rectTransform != null)
            {
                line.rectTransform.gameObject.layer = OrbitLayer;
                if (mode == 1)
                {
                    line.rectTransform.position = Vector3.zero;
                    line.rectTransform.rotation = Quaternion.identity;
                    line.rectTransform.localScale = Vector3.one;
                }
            }
            // The body's scaled centre plus the offset to the point: ScaledSpace's own offset, which jumps with the floating origin, is not used.
            Vector3 centre = scaledBody.position;
            double inverse = ScaledSpace.InverseScaleFactor;
            List<Vector3> points = line.points3;
            for (int i = 0; i < leg.Relative.Length && i < points.Count; i++)
                points[i] = centre + (Vector3)(leg.Relative[i] * inverse);
            line.drawStart = 0;
            line.drawEnd = points.Count - 1;
            if (!line.active) line.active = true;
            if (mode == 1) line.Draw3D();
            else line.Draw();
        }

        // Whether the number is an ordinary one: not NaN, and not infinite, as an anomaly far outside a hyperbola gives.
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
