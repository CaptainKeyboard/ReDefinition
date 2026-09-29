using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Engineer;
using ReDefinition.Flight.Panel;
using ReDefinition.Flight.Readouts;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // What the round gauge shows (docs/player/navball.md). Auto takes the one for
    // the situation the flight panel works out (FlightProfiles.Situation).
    internal enum GaugeMode
    {
        Auto,
        Stage,
        Burn,
        Impact,
        Closing,
        Attack,
    }

    // KSP's vertical speed gauge made a digital gauge: the vertical speed stands
    // at the navball (NavballSpeeds). The gauge (GaugeVerticalSpeed under the top
    // frame's altimeter, 83 by 85: its picture with the dial's face, scale and
    // bezel, a needle, a knob, the label "VERT SPD" and the sink rate light;
    // sharedassets7) keeps its bezel and its light. Its picture is read as it
    // stands, the theme's where one replaces it, and its face painted over in the
    // face's own colour up to the bezel; needle, knob and label are switched off.
    // On the face: a lit arc of three quarters of a circle, open at the bottom,
    // filled from its left end as far as the value goes, with a dot at its tip,
    // over a dim track; the mode's name, the value, and "AUTO" while the gauge
    // follows the situation. The arc is white, amber or red as the value is
    // fine, close or past its limit, and moves to a new value within a fraction of
    // a second. A click on the gauge goes to the next mode, a right-click to the
    // one before; the mode is kept (PluginData/gauge.cfg).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RoundGauge : MonoBehaviour
    {
        private const float Interval = 0.1f;
        // How far out the face reaches, as a share of the bezel's outer radius:
        // ZTheme's bezel starts just beyond, KSP's a little further out (read
        // from both pictures).
        internal const float Face = 0.84f;
        // The arc: three quarters of a circle, 3 wide, 1.5 inside the face's edge.
        private const float Sweep = 0.75f;
        private const float ArcWidth = 3f;
        private const float ArcInset = 1.5f;

        private static readonly Color Fine = new Color(0.95f, 0.95f, 0.95f);
        private static readonly Color Close = new Color(1f, 0.72f, 0.2f);
        private static readonly Color Past = new Color(1f, 0.32f, 0.28f);
        private static readonly Color Track = new Color(1f, 1f, 1f, 0.12f);

        private TextMeshProUGUI title;
        private TextMeshProUGUI value;
        private TextMeshProUGUI auto;
        private Image arc;
        private RectTransform tip;
        private Image tipImage;
        private float radius;
        private Vector2 middle;
        private Color dim = Color.grey;
        private float next;
        private float shownFill, wantedFill;
        private Color wantedColour = Fine;

        private static GaugeMode mode = Load();

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            VerticalSpeedGauge gauge = null;
            while (Time.unscaledTime < until && (gauge = FindObjectOfType<VerticalSpeedGauge>()) == null) yield return null;
            // A frame more: a theme swaps the gauge's picture as the scene's UI is
            // ready.
            yield return null;
            TextMeshProUGUI label = null;
            if (gauge != null)
                foreach (TextMeshProUGUI text in gauge.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (text.name == "Label_Vert") label = text;
            Image picture = gauge != null ? gauge.GetComponent<Image>() : null;
            Vector2 centre;
            float reach;
            if (label == null || picture == null || !Blank(picture, out centre, out reach))
            {
                Debug.Log(Log.Tag + " The round gauge keeps KSP's vertical speed: its label or its picture was not found.");
                yield break;
            }
            foreach (string part in new[] { "GaugeVerticalSpeedPointerParent", "GaugeVerticalSpeedKnob" })
            {
                Transform child = gauge.transform.Find(part);
                if (child != null) child.gameObject.SetActive(false);
            }
            // The dial's middle and the face's radius on the gauge: neither picture
            // has the dial in its own middle.
            Rect rect = picture.rectTransform.rect;
            middle = new Vector2(centre.x * rect.width, centre.y * rect.height);
            radius = reach * rect.width - ArcInset;

            Image track = Ring(picture.transform, "ReDefinitionGaugeTrack", middle);
            track.fillAmount = Sweep;
            track.color = Track;
            arc = Ring(picture.transform, "ReDefinitionGaugeArc", middle);
            arc.fillAmount = 0f;
            tipImage = Dot(picture.transform);
            tip = tipImage.rectTransform;

            dim = label.color;
            title = Copy(label, "ReDefinitionGaugeTitle", middle + new Vector2(0f, 11f), 52f, label.fontSize * 1.15f, label.color);
            value = Copy(label, "ReDefinitionGaugeValue", middle + new Vector2(0f, -2f), 52f, label.fontSize * 2.1f, Fine);
            auto = Copy(label, "ReDefinitionGaugeAuto", middle + new Vector2(0f, -19f), 32f, label.fontSize * 0.8f, label.color);
            auto.text = "AUTO";
            label.gameObject.SetActive(false);

            // A click anywhere on the face.
            GameObject click = new GameObject("ReDefinitionGaugeClick", typeof(RectTransform), typeof(Image), typeof(GaugeClick));
            click.transform.SetParent(picture.transform, false);
            RectTransform clickRect = (RectTransform)click.transform;
            clickRect.anchorMin = clickRect.anchorMax = clickRect.pivot = new Vector2(0.5f, 0.5f);
            clickRect.sizeDelta = new Vector2(2f * radius, 2f * radius);
            clickRect.anchoredPosition = middle;
            click.GetComponent<Image>().color = Color.clear;
            click.GetComponent<GaugeClick>().Pressed = Step;
            Sprite read = picture.sprite;
            Debug.Log(Log.Tag + " The round gauge is digital, showing " + mode + ": the dial's middle at " + middle.ToString("0.0")
                      + " of the gauge's " + rect.size.ToString("0.0") + " (pivot " + picture.rectTransform.pivot.ToString("0.00")
                      + "), the arc " + radius.ToString("0.0") + " out; the picture " + (read != null ? read.rect.size.ToString("0") : "?")
                      + ", the label under " + label.transform.parent.name + ".");
        }

        // The gauge's picture with its face painted over, where the dial's middle
        // lies off the picture's, as a share of its size, and how far the face
        // reaches, as a share of its width; false where the picture cannot be
        // read. The dial is the picture's opaque part: its middle is that part's.
        private static bool Blank(Image image, out Vector2 centre, out float reach)
        {
            centre = Vector2.zero;
            reach = 0f;
            Sprite sprite = image.sprite;
            int width, height;
            Color32[] pixels = TimeWarpBar.Pixels(sprite, out width, out height);
            if (pixels == null) return false;
            int left = width, right = -1, bottom = height, top = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * width + x].a < 128) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < bottom) bottom = y;
                if (y > top) top = y;
            }
            if (right < left || top < bottom) return false;
            // The face's colour a little east of the middle, clear of KSP's knob
            // and of the scale's numbers.
            Color32 face = pixels[(height / 2) * width + Mathf.Clamp(Mathf.RoundToInt(width * 0.675f), 0, width - 1)];
            float cx = (left + right) / 2f, cy = (bottom + top) / 2f;
            float paint = Face * Mathf.Min(right - left + 1, top - bottom + 1) / 2f;
            centre = new Vector2((cx - (width - 1) / 2f) / width, (cy - (height - 1) / 2f) / height);
            reach = paint / width;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > paint * paint) continue;
                Color32 was = pixels[y * width + x];
                pixels[y * width + x] = new Color32(face.r, face.g, face.b, was.a);
            }
            Texture2D texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            image.sprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
            return true;
        }

        // A ring on the face, filled clockwise from its lower left (Image's radial
        // fill starts at the bottom; the ring is turned by an eighth).
        private Image Ring(Transform parent, string name, Vector2 middle)
        {
            GameObject ring = new GameObject(name, typeof(RectTransform), typeof(Image));
            ring.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)ring.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(2f * radius, 2f * radius);
            rect.anchoredPosition = middle;
            rect.localRotation = Quaternion.Euler(0f, 0f, -45f);
            Image image = ring.GetComponent<Image>();
            image.sprite = Annulus(1f - ArcWidth / radius);
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillOrigin = (int)Image.Origin360.Bottom;
            image.fillClockwise = true;
            image.raycastTarget = false;
            return image;
        }

        private Image Dot(Transform parent)
        {
            GameObject dot = new GameObject("ReDefinitionGaugeTip", typeof(RectTransform), typeof(Image));
            dot.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)dot.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(ArcWidth + 2f, ArcWidth + 2f);
            Image image = dot.GetComponent<Image>();
            image.sprite = Annulus(0f);
            image.raycastTarget = false;
            return image;
        }

        // A white ring, its inner edge at `inner` of its radius (0 for a disc),
        // its edges smoothed over a texel.
        private static Sprite Annulus(float inner)
        {
            const int size = 128;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            float c = (size - 1) / 2f, outer = size / 2f - 0.5f, hole = inner * outer;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float alpha = Mathf.Clamp01(outer - d + 0.5f) * (inner > 0f ? Mathf.Clamp01(d - hole + 0.5f) : 1f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        // A copy of the gauge's label, `width` wide, centred on `at` from the
        // gauge's middle.
        private static TextMeshProUGUI Copy(TextMeshProUGUI label, string name, Vector2 at, float width, float size, Color colour)
        {
            GameObject copy = Instantiate(label.gameObject, label.transform.parent, false);
            copy.name = name;
            copy.SetActive(true);
            RectTransform rect = (RectTransform)copy.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, size * 1.4f);
            rect.anchoredPosition = at;
            TextMeshProUGUI text = copy.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.enableAutoSizing = true;
            text.fontSizeMax = size;
            text.fontSizeMin = size * 0.5f;
            text.color = colour;
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            return text;
        }

        // ------------------------------------------------------------ modes

        private void Step(int by)
        {
            int count = Enum.GetValues(typeof(GaugeMode)).Length;
            mode = (GaugeMode)((((int)mode + by) % count + count) % count);
            Save();
            next = 0f;
        }

        private void Update()
        {
            if (value == null) return;
            // The arc and its tip move to the value each frame.
            shownFill = Mathf.MoveTowards(shownFill, wantedFill, Time.unscaledDeltaTime * 2f);
            arc.fillAmount = shownFill * Sweep;
            arc.color = wantedColour;
            float angle = (-135f + 270f * shownFill) * Mathf.Deg2Rad;
            tip.anchoredPosition = middle + new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * (radius - ArcWidth / 2f);
            tipImage.color = shownFill > 0.001f ? Color.white : Color.clear;

            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Interval;
            Vessel vessel = FlightGlobals.ActiveVessel;
            ReadoutFrame frame = vessel != null ? new ReadoutFrame(vessel) : null;
            if (frame == null || !frame.Ready) return;
            GaugeMode shown = mode == GaugeMode.Auto ? ForSituation(frame) : mode;
            Reading reading;
            try
            {
                reading = Read(shown, frame);
            }
            catch (Exception)
            {
                reading = new Reading();
            }
            title.text = Title(shown);
            auto.gameObject.SetActive(mode == GaugeMode.Auto);
            bool known = reading.Text != null;
            value.text = known ? reading.Text : "-";
            value.color = known ? Fine : dim;
            wantedFill = known ? Mathf.Clamp01((float)reading.Fill) : 0f;
            wantedColour = reading.Level >= 2 ? Past : reading.Level == 1 ? Close : Fine;
        }

        private static string Title(GaugeMode mode)
        {
            switch (mode)
            {
                case GaugeMode.Stage: return "STAGE";
                case GaugeMode.Burn: return "BURN";
                case GaugeMode.Impact: return "IMPACT";
                case GaugeMode.Closing: return "CLOSING";
                case GaugeMode.Attack: return "AOA";
                default: return "";
            }
        }

        // The mode for the situation: a burn while a node is planned in orbit or
        // on the way to a target, else the stage in orbit, the closing speed on
        // the way to a target.
        private static GaugeMode ForSituation(ReadoutFrame frame)
        {
            switch (FlightProfiles.Situation(frame.Vessel))
            {
                case PanelSituation.Landing: return GaugeMode.Impact;
                case PanelSituation.Docking: return GaugeMode.Closing;
                case PanelSituation.Air: return GaugeMode.Attack;
                case PanelSituation.Rendezvous: return Node(frame.Vessel) != null ? GaugeMode.Burn : GaugeMode.Closing;
                case PanelSituation.Orbit: return Node(frame.Vessel) != null ? GaugeMode.Burn : GaugeMode.Stage;
                default: return GaugeMode.Stage;
            }
        }

        // A value as the gauge shows it: its text (null for none), how far the
        // arc fills, and 0 fine, 1 close to its limit, 2 past it.
        internal struct Reading
        {
            internal string Text;
            internal double Fill;
            internal int Level;
        }

        private static Reading Read(GaugeMode mode, ReadoutFrame frame)
        {
            switch (mode)
            {
                case GaugeMode.Stage: return StageReading(frame);
                case GaugeMode.Burn: return BurnReading(frame);
                case GaugeMode.Impact: return ImpactReading(frame);
                case GaugeMode.Closing: return ClosingReading(frame);
                case GaugeMode.Attack: return AttackReading(frame);
                default: return new Reading();
            }
        }

        // The stage's burn time left at full thrust (KSP's delta-v simulation,
        // DeltaVStageInfo.stageBurnTime); the arc its fuel left against the most
        // it had while it was the stage.
        private static int fuelStage = -1;
        private static Vessel fuelVessel;
        private static double fuelMost;

        private static Reading StageReading(ReadoutFrame frame)
        {
            Vessel vessel = frame.Vessel;
            DeltaVStageInfo stage = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.GetStage(vessel.currentStage) : null;
            if (stage == null || stage.stageBurnTime <= 0.0 || stage.fuelMass <= 0f) return new Reading();
            if (vessel != fuelVessel || vessel.currentStage != fuelStage)
            {
                fuelVessel = vessel;
                fuelStage = vessel.currentStage;
                fuelMost = 0.0;
            }
            fuelMost = Math.Max(fuelMost, stage.fuelMass);
            double left = stage.fuelMass / fuelMost;
            return new Reading
            {
                Text = EngineerFormat.Time(stage.stageBurnTime),
                Fill = left,
                Level = left < 0.1 ? 2 : left < 0.25 ? 1 : 0,
            };
        }

        private static ManeuverNode Node(Vessel vessel)
        {
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            return solver != null && solver.maneuverNodes != null && solver.maneuverNodes.Count > 0 ? solver.maneuverNodes[0] : null;
        }

        // The next node: before its burn, the time until the burn starts, half
        // its length before the node (the length by the rocket equation, at the
        // stage's thrust and Isp in vacuum); the arc fills over the last minute,
        // amber in the last ten seconds. Once it is due, the delta-v left; the
        // arc shows how much of it is done, amber in the last tenth.
        private static ManeuverNode mostFor;
        private static double most;

        private static Reading BurnReading(ReadoutFrame frame)
        {
            ManeuverNode node = Node(frame.Vessel);
            if (node == null) return new Reading();
            double left = node.GetBurnVector(frame.Orbit).magnitude;
            if (node != mostFor)
            {
                mostFor = node;
                most = 0.0;
            }
            most = Math.Max(most, left);
            double length = BurnLength(frame.Vessel, left);
            double until = double.IsNaN(length) ? node.UT - frame.Now : node.UT - frame.Now - length / 2.0;
            if (until > 0.0)
                return new Reading
                {
                    Text = "T-" + EngineerFormat.Time(until),
                    Fill = until < 60.0 ? 1.0 - until / 60.0 : 0.0,
                    Level = until < 10.0 ? 1 : 0,
                };
            double done = most > 0.0 ? 1.0 - left / most : 0.0;
            return new Reading { Text = ReadoutMath.Speed(left), Fill = done, Level = done > 0.9 ? 1 : 0 };
        }

        private static double BurnLength(Vessel vessel, double deltaV)
        {
            DeltaVStageInfo stage = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.GetStage(vessel.currentStage) : null;
            if (stage == null || stage.thrustVac <= 0f || stage.ispVac <= 0.0) return double.NaN;
            double exhaust = stage.ispVac * PhysicsGlobals.GravitationalAcceleration;
            double flow = stage.thrustVac / exhaust;
            return vessel.totalMass * (1.0 - Math.Exp(-deltaV / exhaust)) / flow;
        }

        // The time until the vessel's path meets the ground (Impact.Seconds); the
        // arc how much of it the last moment to brake has used up (ReadoutFrame.
        // SuicideBurn): full, and red, when it is time to light.
        private static Reading ImpactReading(ReadoutFrame frame)
        {
            double seconds = frame.ImpactSeconds;
            if (double.IsNaN(seconds)) return new Reading();
            double altitude, countdown, deltaV;
            frame.SuicideBurn(out altitude, out countdown, out deltaV);
            double used = double.IsNaN(countdown) || seconds <= 0.0 ? 0.0 : countdown <= 0.0 ? 1.0 : 1.0 - countdown / seconds;
            return new Reading
            {
                Text = EngineerFormat.Time(seconds),
                Fill = used,
                Level = used >= 0.9 ? 2 : used >= 0.7 ? 1 : 0,
            };
        }

        // How fast the vessel closes on its target, its docking port where it is
        // one, along the line between them; the arc that speed against a safe one
        // for the distance, a hundredth of it per second and at least 0.2 m/s,
        // which fills half of it.
        private static Reading ClosingReading(ReadoutFrame frame)
        {
            if (frame.Target == null) return new Reading();
            ModuleDockingNode port = frame.TargetPort;
            Vector3d at = port != null && port.nodeTransform != null ? (Vector3d)port.nodeTransform.position : frame.TargetPosition;
            Vector3d line = at - (Vector3d)frame.Vessel.ReferenceTransform.position;
            double distance = line.magnitude;
            if (distance <= 0.0) return new Reading();
            double closing = Vector3d.Dot(frame.RelativeVelocity, line / distance);
            double safe = Math.Max(0.2, distance / 100.0);
            return new Reading
            {
                Text = ReadoutMath.Speed(closing),
                Fill = closing / (2.0 * safe),
                Level = closing > 1.5 * safe ? 2 : closing > safe ? 1 : 0,
            };
        }

        // The angle of attack; the arc it against the angle where the wings give
        // the most lift.
        private static Reading AttackReading(ReadoutFrame frame)
        {
            double attack, sideslip;
            frame.AeroAngles(out attack, out sideslip);
            if (double.IsNaN(attack)) return new Reading();
            double share = Math.Abs(attack) / Stall;
            return new Reading { Text = ReadoutMath.Angle(attack), Fill = share, Level = share > 0.85 ? 2 : share > 0.6 ? 1 : 0 };
        }

        // Where KSP's lift curve for wings peaks, in degrees (PhysicsGlobals'
        // "Default" lifting surface: lift over the sine of the angle of attack;
        // 30 in KSP's own Physics.cfg).
        private static double stall = double.NaN;

        internal static double Stall
        {
            get
            {
                if (!double.IsNaN(stall)) return stall;
                stall = 30.0;
                PhysicsGlobals.LiftingSurfaceCurve curve = PhysicsGlobals.GetLiftingSurfaceCurve("Default");
                if (curve != null && curve.liftCurve != null)
                {
                    float best = float.MinValue, at = 0f;
                    for (int i = 1; i <= 100; i++)
                    {
                        float lift = curve.liftCurve.Evaluate(i / 100f);
                        if (lift <= best) continue;
                        best = lift;
                        at = i / 100f;
                    }
                    if (at > 0f) stall = Math.Asin(at) * 180.0 / Math.PI;
                }
                return stall;
            }
        }

        // ------------------------------------------------------------ kept

        private static string Path
        {
            get { return PluginData.Path("gauge.cfg"); }
        }

        private static GaugeMode Load()
        {
            try
            {
                ConfigNode node = File.Exists(Path) ? ConfigNode.Load(Path) : null;
                string text = node != null ? node.GetValue("mode") : null;
                if (text != null) return (GaugeMode)Enum.Parse(typeof(GaugeMode), text, true);
            }
            catch (Exception)
            {
            }
            return GaugeMode.Auto;
        }

        private static void Save()
        {
            string directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            ConfigNode node = new ConfigNode();
            node.AddValue("mode", mode.ToString());
            node.Save(Path);
        }
    }

    // Clicks on the round gauge: the left button one mode on, the right one back.
    public class GaugeClick : MonoBehaviour, IPointerClickHandler
    {
        internal Action<int> Pressed;

        public void OnPointerClick(PointerEventData data)
        {
            if (Pressed == null) return;
            if (data.button == PointerEventData.InputButton.Left) Pressed(1);
            else if (data.button == PointerEventData.InputButton.Right) Pressed(-1);
        }
    }
}
