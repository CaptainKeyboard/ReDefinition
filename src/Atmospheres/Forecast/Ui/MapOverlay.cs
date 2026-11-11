using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Atmospheres.Forecast
{
    // The lines of the overlay as a canvas graphic: quads in screen pixels, drawn by the UI cameras after the scene has been upscaled
    // and (with frame generation) taken as the HUD-less frame, so the overlay is not part of the picture the upscaler sees and is not
    // interpolated. No LineRenderer, no scene geometry: nothing here has motion vectors or depth.
    internal sealed class OverlayGraphic : MaskableGraphic
    {
        private const int VertexLimit = 60000;
        private OverlayScene scene;
        private float pixelScale = 1f;
        private static readonly Color[] BandColors = MakeColors();

        private static Color[] MakeColors()
        {
            var colors = new Color[6];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = FromHex(ForecastFormat.Hex((PrecipBand)i));
                colors[i].a = 0.95f;
            }
            return colors;
        }

        internal static Color FromHex(string hex)
        {
            int rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
        }

        internal void SetScene(OverlayScene value, float scale)
        {
            scene = value;
            pixelScale = scale <= 0f ? 1f : scale;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (scene == null) return;
            int vertices = 0;
            for (int i = 0; i < scene.Lines.Count && vertices < VertexLimit; i++)
            {
                OverlayLine line = scene.Lines[i];
                Color c = BandColors[Mathf.Clamp((int)line.Band, 0, BandColors.Length - 1)];
                if (line.Kind == OverlayKind.Dot)
                {
                    if (line.Points.Count > 0) Quad(vh, line.Points[0], new P2(line.Points[0].X + 3f, line.Points[0].Y), 3f, c);
                    vertices += 4;
                    continue;
                }
                float width = line.Kind == OverlayKind.Ring ? 2f : line.Kind == OverlayKind.Tick ? 2f : 1.5f;
                Color cc = line.Kind == OverlayKind.Track ? new Color(c.r, c.g, c.b, 0.7f) : c;
                for (int p = 1; p < line.Points.Count; p++)
                {
                    Quad(vh, line.Points[p - 1], line.Points[p], width, cc);
                    vertices += 4;
                }
            }
        }

        // A segment as a quad of that width; the points are screen pixels of the whole screen, the canvas covers it.
        private void Quad(VertexHelper vh, P2 a, P2 b, float width, Color c)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            float nx, ny;
            if (len < 1e-3f)
            {
                nx = 0f;
                ny = width * 0.5f;
                dx = width * 0.5f;
                dy = 0f;
            }
            else
            {
                nx = -dy / len * width * 0.5f;
                ny = dx / len * width * 0.5f;
                dx = 0f;
                dy = 0f;
            }
            Vector2 p0 = To(a.X - nx - dx, a.Y - ny - dy), p1 = To(a.X + nx - dx, a.Y + ny - dy);
            Vector2 p2 = To(b.X + nx + dx, b.Y + ny + dy), p3 = To(b.X - nx + dx, b.Y - ny + dy);
            int at = vh.currentVertCount;
            vh.AddVert(p0, c, Vector2.zero);
            vh.AddVert(p1, c, Vector2.zero);
            vh.AddVert(p2, c, Vector2.zero);
            vh.AddVert(p3, c, Vector2.zero);
            vh.AddTriangle(at, at + 1, at + 2);
            vh.AddTriangle(at, at + 2, at + 3);
        }

        // A screen pixel in the canvas' local space (the canvas is the screen, scaled by its factor, origin at its lower left).
        private Vector2 To(float x, float y)
        {
            return new Vector2(x / pixelScale, y / pixelScale);
        }
    }

    // Draws the overlay of a body in the tracking station and in the map view: rings, paths and ticks of the weather systems, labelled on
    // hover. It exists once, made when first asked, and is switched off in every other view.
    internal sealed class MapOverlay : MonoBehaviour
    {
        private const float TracksSeconds = 0.5f;
        private const float MapSeconds = 2f;
        private GameObject view;
        private OverlayGraphic graphic;
        private Canvas canvas;
        private Text legend;
        private Text hover;
        private readonly OverlayScene scene = new OverlayScene();
        private readonly OverlayRules rules = new OverlayRules();
        private readonly KspMapProjector projector = new KspMapProjector();
        private readonly WeatherMap map = new WeatherMap();
        private ForecastTrack[] tracks;
        private int tracksBody = -1;
        private float nextTracks;
        private float nextMap;
        private string legendText = "";

        // The last scene (the probe reads the rings), and the body it is for.
        internal OverlayScene Scene { get { return scene; } }
        internal bool Drawing { get; private set; }

        // The probe's off switch for the on/off comparison of the screen: nothing is drawn while it is set, whatever the settings say.
        internal bool Suppressed;

        // Whether the lines' space is the screen's: origin at the lower left, as large as the screen (the probe checks it, since a vertex
        // is drawn where the pivot of its rectangle puts it).
        internal bool CanvasAligned
        {
            get
            {
                if (graphic == null || canvas == null) return false;
                RectTransform r = graphic.rectTransform;
                float scale = Mathf.Max(canvas.scaleFactor, 0.01f);
                return r.pivot == Vector2.zero && Mathf.Abs(r.rect.width * scale - Screen.width) < 2f && Mathf.Abs(r.rect.height * scale - Screen.height) < 2f;
            }
        }

        private static MapOverlay instance;

        internal static MapOverlay Instance
        {
            get { return instance; }
        }

        internal static MapOverlay Ensure()
        {
            if (instance != null) return instance;
            var go = new GameObject("ReDefinitionForecastOverlay");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MapOverlay>();
            return instance;
        }

        private void Awake()
        {
            // The behaviour lives on this always-active object; the canvas is its child, switched on and off.
            view = new GameObject("View", typeof(RectTransform));
            view.transform.SetParent(transform, false);
            canvas = view.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Under KSP's own interface: the overlay never covers a window.
            canvas.sortingOrder = -50;
            var raycaster = view.AddComponent<GraphicRaycaster>();
            raycaster.enabled = false;
            var scaler = view.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            var holder = new GameObject("Lines", typeof(RectTransform));
            holder.transform.SetParent(view.transform, false);
            RectTransform rect = holder.GetComponent<RectTransform>();
            // The mesh's vertices are in pixels from the lower left of the screen; the pivot is the origin of a graphic's local space.
            rect.pivot = Vector2.zero;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            graphic = holder.AddComponent<OverlayGraphic>();
            graphic.raycastTarget = false;
            legend = MakeText("Legend", TextAnchor.UpperLeft, new Vector2(0f, 1f), new Vector2(12f, -96f));
            hover = MakeText("Hover", TextAnchor.LowerLeft, new Vector2(0f, 0f), Vector2.zero);
            hover.gameObject.SetActive(false);
            view.SetActive(false);
        }

        private Text MakeText(string name, TextAnchor anchor, Vector2 pivot, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(view.transform, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = name == "Legend" ? new Vector2(0f, 1f) : Vector2.zero;
            rect.pivot = pivot;
            rect.sizeDelta = new Vector2(420f, 40f);
            rect.anchoredPosition = position;
            var text = go.AddComponent<Text>();
            text.font = HighLogic.UISkin != null ? HighLogic.UISkin.label.font : null;
            text.fontSize = 14;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            go.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.9f);
            return text;
        }

        // Where the camera looks at: the body of the map camera's target.
        private static CelestialBody FocusBody()
        {
            if (PlanetariumCamera.fetch == null || PlanetariumCamera.fetch.target == null) return null;
            MapObject target = PlanetariumCamera.fetch.target;
            if (target.celestialBody != null) return target.celestialBody;
            return target.vessel != null ? target.vessel.mainBody : null;
        }

        private static bool InMapView()
        {
            return HighLogic.LoadedScene == GameScenes.TRACKSTATION || (HighLogic.LoadedSceneIsFlight && MapView.MapIsEnabled);
        }

        private void LateUpdate()
        {
            Show(Compute());
        }

        // Fills the scene for this frame; true where something is to be drawn.
        private bool Compute()
        {
            if (Suppressed || !ForecastUiSettings.Overlay || !InMapView() || Planetarium.fetch == null) return false;
            CelestialBody body = FocusBody();
            if (body == null || !body.atmosphere) return false;
            double now = Planetarium.GetUniversalTime();
            int index = body.flightGlobalsIndex;
            if (index != tracksBody)
            {
                // Another body in view: the map, the legend and the dots of the one before are not its.
                nextMap = 0f;
                legendText = "";
                map.Resize(1, 1);
                map.Values[0] = 0f;
            }
            if (index != tracksBody || Time.unscaledTime >= nextTracks)
            {
                Func<int, ForecastTrack[]> source = WeatherHooks.BodyTracks;
                tracks = source != null ? source(index) : null;
                tracksBody = index;
                nextTracks = Time.unscaledTime + TracksSeconds;
            }
            double horizon = Math.Min(AtmospheresSettings.ForecastHorizonHours, 96.0);
            if (tracks != null)
            {
                OverlayBuilder.Build(scene, tracks, projector, index, body.Radius, now, horizon, rules);
                legendText = scene.Hidden > 0 ? scene.Hidden.ToString(CultureInfo.InvariantCulture) + " small systems hidden" : "";
                return true;
            }
            // A body without tracks: the field's own precipitation map, with its label.
            if (Time.unscaledTime >= nextMap)
            {
                nextMap = Time.unscaledTime + MapSeconds;
                IWeatherField field = WeatherHub.Field;
                if (field != null && field.GetMap(index, WeatherMapKind.Precipitation, now, map))
                    legendText = map.Approximate || map.Readiness != FieldReadiness.Ready ? ForecastStrings.MapApproximate : ForecastStrings.MapPrecipitation;
                else
                    legendText = "";
            }
            if (legendText.Length == 0 || map.Width < 1) return false;
            OverlayBuilder.AddMapDots(scene, map, projector, index, 48, 24, 0.2f);
            return true;
        }

        private void Show(bool on)
        {
            if (!on)
            {
                if (Drawing)
                {
                    scene.Clear();
                    graphic.SetScene(null, 1f);
                    legend.text = "";
                    hover.gameObject.SetActive(false);
                    view.SetActive(false);
                    Drawing = false;
                }
                return;
            }
            if (!Drawing)
            {
                view.SetActive(true);
                Drawing = true;
            }
            graphic.SetScene(scene, canvas.scaleFactor);
            legend.text = legendText;
            UpdateHover();
        }

        private void UpdateHover()
        {
            Vector3 mouse = Input.mousePosition;
            OverlayRing ring = scene.Pick(mouse.x, mouse.y);
            if (ring == null)
            {
                hover.gameObject.SetActive(false);
                return;
            }
            ForecastSite site;
            string text = HoverText(ring, out site);
            hover.text = text;
            hover.gameObject.SetActive(true);
            hover.rectTransform.anchoredPosition = new Vector2(mouse.x + 14f, mouse.y + 8f) / Mathf.Max(canvas.scaleFactor, 0.01f);
        }

        // The ring's label against the site the window shows (the pad when none).
        private string HoverText(OverlayRing ring, out ForecastSite site)
        {
            if (!ForecastSites.TryLocate(ForecastWindow.CurrentChoice, out site) && !ForecastSites.TryLocate(SiteChoice.Ksc, out site))
                return ForecastFormat.LevelName(ring.Band);
            List<CelestialBody> bodies = GameGlobals.Bodies;
            double radius = bodies != null && scene.BodyIndex >= 0 && scene.BodyIndex < bodies.Count ? bodies[scene.BodyIndex].Radius : 0.0;
            if (site.BodyIndex != scene.BodyIndex) return ForecastFormat.LevelName(ring.Band);
            return OverlayBuilder.HoverLabel(ring, site.LatDeg, site.LonDeg, site.Name, scene.Ut, radius);
        }
    }
}
