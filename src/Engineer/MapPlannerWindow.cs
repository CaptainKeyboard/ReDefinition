using System.Collections;
using System.Collections.Generic;
using KSP.UI;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The planner in the map view: a panel at the right edge of the screen that slides out over the map, with a tab on its left edge that opens
    // and closes it. It holds the planner's rows as the editor's panel does (PlannerView), over the vessel in flight (FlightPlannerHost). It is
    // there while the map view is, and the plan it shows is the vessel's, kept under the vessel's name.
    //
    // It is made once the flight scene has a vessel, out of KSP's own elements: the rows are dialog rows (DialogGUIBase) created into a scroll
    // list in a frame of its own, on the main canvas.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapPlannerWindow : MonoBehaviour
    {
        // The panel's width, the tab's, and the room left above, below and at its right for KSP's own bars and the toolbar column, in canvas units.
        private const float PanelWidth = 460f, TabWidth = 64f, TabHeight = 30f, TopMargin = 96f, BottomMargin = 70f, RightMargin = 56f;

        // Seconds the panel takes to slide.
        private const float SlideTime = 0.2f;

        private FlightPlannerHost host;
        private PlannerView view;
        private GameObject root;
        private RectTransform frame;
        private DialogGUIBase list;
        private DialogGUIBase tab;
        private bool open;
        private float slide;
        private bool failed;
        // What the last press of a button came to, shown under the buttons.
        private string status = "";

        // Whether the panel is up: the map view is on and a vessel is flown.
        private static bool Wanted
        {
            get { return MapView.MapIsEnabled && FlightGlobals.ActiveVessel != null && HighLogic.LoadedSceneIsFlight; }
        }

        // Asked for by the probes: the panel open and out, without a click; the window itself, and what its last button came to.
        internal static bool OpenAtOnce;
        internal static MapPlannerWindow Instance;

        internal string Status
        {
            get { return status; }
        }

        // As a press of "Fly route", and whether a flight is under way.
        internal void FlyNow()
        {
            if (view != null && !Flying) FlyOrStop();
        }

        internal string FlightStatus
        {
            get { return RouteDriver.Instance != null && RouteDriver.Instance.Flying ? RouteDriver.Instance.Status : status; }
        }

        internal bool Flying
        {
            get { return RouteDriver.Instance != null && RouteDriver.Instance.Flying; }
        }

        // As a press of "Plan nodes".
        internal void PlanNow()
        {
            if (view != null) PlanNodes();
        }

        // The whole route worked out from the vessel as it is, step after step, with the orbits that follow; null before it has been.
        internal RoutePlan WholePlan { get; private set; }

        // Plans the whole route at once, to its last stop or as far as the plan reaches, and says how far that is in the status.
        internal void PlanAllNow()
        {
            List<Destination> route = Route();
            if (route == null || FlightGlobals.ActiveVessel == null) return;
            WholePlan = RouteSimulation.Run(FlightGlobals.ActiveVessel, route);
            status = WholePlan.Steps.Count + " step(s), " + WholePlan.DeltaV.ToString("0") + " m/s" + (WholePlan.Ended != null ? "; " + WholePlan.Ended : "; the last stop is reached") + ".";
        }

        // For the probes: the route's first stop set to an orbit round a body, where the vessel starts.
        internal bool SetStart(string bodyName, double altitude)
        {
            IList<RouteStop> stops = view != null ? view.RouteStops : null;
            PlannerBodies bodies = view != null ? view.BodyList : null;
            if (stops == null || bodies == null || stops.Count < 2) return false;
            int index = bodies.Bodies.FindIndex(b => b.Id == bodyName);
            if (index < 0) return false;
            stops[0].Body = index;
            stops[0].Kind = StopKind.Orbit;
            stops[0].Altitude = altitude;
            view.SaveNow();
            return true;
        }

        // For the probes: the route's second stop set to an orbit round a body, as the player sets it with the rows.
        internal bool SetDestination(string bodyName, double altitude, bool useAtmosphere = false)
        {
            IList<RouteStop> stops = view != null ? view.RouteStops : null;
            PlannerBodies bodies = view != null ? view.BodyList : null;
            if (stops == null || bodies == null || stops.Count < 2) return false;
            int index = bodies.Bodies.FindIndex(b => b.Id == bodyName);
            if (index < 0) return false;
            stops[1].Body = index;
            stops[1].Kind = StopKind.Orbit;
            stops[1].Altitude = altitude;
            stops[1].UseAtmosphere = useAtmosphere;
            view.SaveNow();
            return true;
        }

        // For the probes: the route's second stop set to the surface of a body.
        internal bool SetSurfaceDestination(string bodyName, bool useAtmosphere = false, bool orbitFirst = false)
        {
            IList<RouteStop> stops = view != null ? view.RouteStops : null;
            PlannerBodies bodies = view != null ? view.BodyList : null;
            if (stops == null || bodies == null || stops.Count < 2) return false;
            int index = bodies.Bodies.FindIndex(b => b.Id == bodyName);
            if (index < 0) return false;
            stops[1].Body = index;
            stops[1].Kind = StopKind.Surface;
            stops[1].Altitude = 0.0;
            stops[1].UseAtmosphere = useAtmosphere;
            stops[1].OrbitFirst = orbitFirst;
            view.SaveNow();
            return true;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (failed) return;
            bool wanted = Wanted;
            if (root == null)
            {
                if (!wanted) return;
                try
                {
                    Build();
                }
                catch (System.Exception e)
                {
                    failed = true;
                    Debug.LogWarning(Log.Tag + " The planner in the map view could not be made (" + e.Message + ").");
                    return;
                }
                if (OpenAtOnce) open = true;
            }
            if (root.activeSelf != wanted) root.SetActive(wanted);
            if (!wanted) return;

            host.Tick();
            slide = Mathf.MoveTowards(slide, open ? 1f : 0f, Time.unscaledDeltaTime / SlideTime);
            float hidden = PanelWidth + RightMargin;
            frame.anchoredPosition = new Vector2(Mathf.Lerp(hidden, -RightMargin, Mathf.SmoothStep(0f, 1f, slide)), (BottomMargin - TopMargin) / 2f);
            if (tab != null) tab.Update();
            if (slide > 0f)
            {
                view.Keep(root);
                view.Tick();
                list.Update();
            }
        }

        // The panel made under the main canvas: a frame at the right edge, the tab on its left, and the planner's rows in a scroll list.
        private void Build()
        {
            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            host = new FlightPlannerHost();
            root = new GameObject("ReDefinitionMapPlanner", typeof(RectTransform));
            root.layer = LayerMask.NameToLayer("UI");
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.SetParent(MainCanvasUtil.MainCanvas.transform, false);
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;

            frame = NewRect("Frame", rootRect);
            frame.anchorMin = new Vector2(1f, 0f);
            frame.anchorMax = new Vector2(1f, 1f);
            frame.pivot = new Vector2(1f, 0.5f);
            frame.sizeDelta = new Vector2(PanelWidth, -(TopMargin + BottomMargin));

            Image back = frame.gameObject.AddComponent<Image>();
            back.color = new Color(0.05f, 0.07f, 0.1f, 0.88f);
            back.raycastTarget = true;

            float listWidth = PanelWidth - 6f;
            float rowWidth = PlannerWidgets.RowRoom(listWidth);
            view = new PlannerView(host, rowWidth);
            var rows = new List<DialogGUIBase>();
            rows.Add(Header(rowWidth));
            rows.AddRange(view.Rows());
            TooltipText.WrapAll(rows);
            UiHygiene.DeferInteractable(rows);
            var layout = new DialogGUIVerticalLayout(rowWidth, -1f, 0f, new RectOffset(0, 0, 0, 12), TextAnchor.UpperLeft, rows.ToArray());
            float height = Screen.height / GameSettings.UI_SCALE - TopMargin - BottomMargin - 8f;
            var scroll = new DialogGUIScrollList(new Vector2(listWidth, Mathf.Max(200f, height)), false, true, layout);
            list = scroll;
            RectTransform content = NewRect("Content", frame);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.offsetMin = new Vector2(3f, 3f);
            content.offsetMax = new Vector2(-3f, -3f);
            var layouts = new Stack<Transform>();
            layouts.Push(content);
            list.Create(ref layouts, skin);
            DialogTooltips.Attach(new[] { list });
            RectTransform listRect = list.uiItem != null ? list.uiItem.transform as RectTransform : null;
            if (listRect != null)
            {
                listRect.anchorMin = Vector2.zero;
                listRect.anchorMax = Vector2.one;
                listRect.offsetMin = listRect.offsetMax = Vector2.zero;
            }
            PlannerWidgets.Settle(scroll);
            view.Finish();
            ScrollThrough.Apply(list.uiItem);
            UiHygiene.MouseOnly(list.uiItem);

            // The tab: a button on the panel's left edge, near the top, which goes out with the panel.
            RectTransform tabHolder = NewRect("Tab", frame);
            tabHolder.anchorMin = tabHolder.anchorMax = new Vector2(0f, 1f);
            tabHolder.pivot = new Vector2(1f, 1f);
            tabHolder.sizeDelta = new Vector2(TabWidth, TabHeight);
            tabHolder.anchoredPosition = new Vector2(0f, -16f);
            var button = new DialogGUIButton("Plan", () => open = !open, TabWidth, TabHeight, false);
            tab = button;
            var tabLayouts = new Stack<Transform>();
            tabLayouts.Push(tabHolder);
            tab.Create(ref tabLayouts, skin);
            RectTransform buttonRect = tab.uiItem != null ? tab.uiItem.transform as RectTransform : null;
            if (buttonRect != null)
            {
                buttonRect.anchorMin = Vector2.zero;
                buttonRect.anchorMax = Vector2.one;
                buttonRect.offsetMin = buttonRect.offsetMax = Vector2.zero;
            }
            UiHygiene.MouseOnly(tab.uiItem);
            Debug.Log(Log.Tag + " The planner in the map view made: " + PanelWidth.ToString("0") + " wide, rows " + rowWidth.ToString("0") + " wide.");
        }

        // The two switches, the navigation and the autopilot, and the line that says what the route is doing.
        private DialogGUIBase Header(float width)
        {
            float button = (width - 8f - 6f) / 2f;
            var navigation = new DialogGUIButton(() => NavigationOn() ? "<color=#8fdc6a>Navigation</color>" : "Navigation", ToggleNavigation, button, 28f, false);
            navigation.OptionInteractableCondition = () => !Flying;
            navigation.tooltipText = "Plan the route and keep the plan up to date while you fly it yourself"
                                     + (ParsekBridge.Unavailable != null ? ". " + ParsekBridge.Unavailable : "");
            var pilot = new DialogGUIButton(() => Flying ? "<color=#8fdc6a>Autopilot</color>" : "Autopilot", FlyOrStop, button, 28f, false);
            pilot.OptionInteractableCondition = () => Flying || MechJebDriver.Unavailable == null;
            pilot.tooltipText = MechJebDriver.Unavailable != null ? "Autopilot: " + MechJebDriver.Unavailable : "Fly the whole route with MechJeb";
            var buttons = new DialogGUIHorizontalLayout(width, 32f, 6f, new RectOffset(4, 4, 4, 2), TextAnchor.MiddleLeft, navigation, pilot);
            var text = new DialogGUILabel(() => RouteDriver.Instance != null && RouteDriver.Instance.Status.Length > 0 ? RouteDriver.Instance.Status : status, width - 12f, 36f);
            // What Parsek does with the flight, where it is there: a line that is empty otherwise.
            var parsek = new DialogGUILabel(() => ParsekBridge.RecordingState ?? "", width - 12f, 18f);
            return new DialogGUIVerticalLayout(width, -1f, 2f, new RectOffset(0, 0, 0, 0), TextAnchor.UpperLeft, buttons, text, parsek);
        }

        // Starts the route's flight, or stops the one that is under way.
        private void FlyOrStop()
        {
            if (RouteDriver.Instance == null) return;
            if (Flying)
            {
                RouteDriver.Instance.StopFlight(FlightGlobals.ActiveVessel);
                return;
            }
            List<Destination> route = Route();
            if (route != null) RouteDriver.Instance.StartFlight(FlightGlobals.ActiveVessel, route);
        }

        // Whether the route is planned for the player: by the navigation, and by the autopilot, which includes it.
        private static bool NavigationOn()
        {
            RouteState state = RouteState.Of(FlightGlobals.ActiveVessel);
            return state != null && (state.Navigation || state.Autopilot);
        }

        private void ToggleNavigation()
        {
            if (RouteDriver.Instance != null) RouteDriver.Instance.SetNavigation(FlightGlobals.ActiveVessel, !NavigationOn());
        }

        // The route of the plan as destinations, from the first stop the vessel has reached: the leg to the next one planned as nodes.
        private void PlanNodes()
        {
            List<Destination> route = Route();
            if (route == null) return;
            NodePlanner.Result result = LegPlanner.PlanNext(new Frame(FlightGlobals.ActiveVessel), route);
            status = result.Ok
                ? result.Why + " The burn is in " + KSPUtil.PrintTimeCompact(result.UT - Planetarium.GetUniversalTime(), false) + ", " + result.DeltaV.Length.ToString("0") + " m/s."
                : result.Why;
        }

        // The planner's route as destinations; null, with the reason in the status, where there is none.
        private List<Destination> Route()
        {
            List<Destination> route = RouteSource.FromStops(view.RouteStops, view.BodyList, view.ReleasesAt);
            if (route == null) status = "There is no route yet.";
            return route;
        }

        private static RectTransform NewRect(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (view != null) view.Dispose();
            if (root != null) Destroy(root);
        }
    }
}
