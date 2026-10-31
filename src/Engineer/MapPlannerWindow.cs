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
        private const float PanelWidth = 460f, TabWidth = 84f, TabHeight = 30f, TopMargin = 96f, BottomMargin = 70f, RightMargin = 56f;

        // Seconds the panel takes to slide.
        private const float SlideTime = 0.2f;

        private FlightPlannerHost host;
        private PlannerView view;
        private GameObject root;
        private RectTransform frame;
        // The panel's background and rows, which are off while the panel is shut so that only its tab shows.
        private Image frameBack;
        private RectTransform frameContent;
        private DialogGUIBase list;
        private DialogGUIBase tab;
        private bool open;
        private float slide;
        // How many rows the panel is made with (PlannerPool); the panel is made again, larger, where the route needs more.
        private int level;
        private bool failed;
        // Whether the panel was out in the last frame, for the frame it goes in.
        private bool wasShown;
        // What the last press of a button came to, shown under the buttons.
        private string status = "";

        // Whether the panel is up: the map view is on and a vessel is flown.
        private static bool Wanted
        {
            get { return MapView.MapIsEnabled && FlightGlobals.ActiveVessel != null && HighLogic.LoadedSceneIsFlight; }
        }

        // Whether the panel may be made before the map is opened: some seconds into the flight, with the vessel resting or in orbit and its engines
        // off. Making it takes seconds (about fifteen thousand dialog elements), which is better spent where nothing is flown than when the map is opened.
        private const float PrebuildAfter = 4f;

        private static bool QuietFlight
        {
            get
            {
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null || !HighLogic.LoadedSceneIsFlight || Time.timeSinceLevelLoad < PrebuildAfter) return false;
                bool resting = vessel.situation == Vessel.Situations.PRELAUNCH || vessel.situation == Vessel.Situations.LANDED
                               || vessel.situation == Vessel.Situations.SPLASHED || vessel.situation == Vessel.Situations.ORBITING
                               || vessel.situation == Vessel.Situations.ESCAPING;
                return resting && vessel.ctrlState.mainThrottle < 0.01f;
            }
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
            // A plan that could not be made again leaves no old plan standing.
            WholePlan = null;
            if (route == null || FlightGlobals.ActiveVessel == null) return;
            view.SaveNow();
            WholePlan = RouteSimulation.Run(FlightGlobals.ActiveVessel, route);
            if (RoutePreview.Instance != null) RoutePreview.Instance.Show(WholePlan);
            status = WholePlan.Steps.Count + " step(s), " + WholePlan.DeltaV.ToString("0") + " m/s" + (WholePlan.Ended != null ? "; " + WholePlan.Ended.TrimEnd('.') : "; the last stop is reached") + ".";
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

        // For the probes: the route's second stop an orbit round a body and its third the surface of it, where a satellite stays in the orbit.
        internal bool SetOrbitThenSurface(string bodyName, double altitude, bool useAtmosphere = false)
        {
            IList<RouteStop> stops = view != null ? view.RouteStops : null;
            PlannerBodies bodies = view != null ? view.BodyList : null;
            if (stops == null || bodies == null || stops.Count < 2) return false;
            int index = bodies.Bodies.FindIndex(b => b.Id == bodyName);
            if (index < 0) return false;
            stops[1].Body = index;
            stops[1].Kind = StopKind.Orbit;
            stops[1].Altitude = altitude;
            if (stops.Count < 3) view.AddStopNow();
            if (stops.Count < 3) return false;
            stops[2].Body = index;
            stops[2].Kind = StopKind.Surface;
            stops[2].Altitude = 0.0;
            stops[2].UseAtmosphere = useAtmosphere;
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
                if (!wanted && !QuietFlight) return;
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
            if (view != null && view.RequiredLevel > level)
            {
                Rebuild(view.RequiredLevel);
                return;
            }
            if (root.activeSelf != wanted) root.SetActive(wanted);
            // Out of the panel's way: its lists, marks and typing lock go where it shuts or the map closes.
            bool shownNow = wanted && (open || slide > 0f);
            if (wasShown && !shownNow && view != null) view.Flush();
            wasShown = shownNow;
            if (!wanted) return;

            long update = PlannerTiming.Start();
            // The vessel is read only for a panel that is open or opening.
            if (open || slide > 0f) host.Tick();
            slide = Mathf.MoveTowards(slide, open ? 1f : 0f, Time.unscaledDeltaTime / SlideTime);
            // Shut, the panel's left edge is where the margin ends and its tab, on that edge, stands in the margin beside the stock toolbar.
            float hidden = PanelWidth - RightMargin;
            bool bodyShown = slide > 0f;
            if (frameBack.enabled != bodyShown) frameBack.enabled = bodyShown;
            if (frameContent.gameObject.activeSelf != bodyShown) frameContent.gameObject.SetActive(bodyShown);
            frame.anchoredPosition = new Vector2(Mathf.Lerp(hidden, -RightMargin, Mathf.SmoothStep(0f, 1f, slide)), (BottomMargin - TopMargin) / 2f);
            if (tab != null) tab.Update();
            if (slide > 0f)
            {
                view.Keep(root);
                view.Tick();
                long listing = PlannerTiming.Start();
                list.Update();
                PlannerTiming.Stop("list.Update", listing);
            }
            PlannerTiming.Stop("window.Update", update);
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
            frameBack = back;
            // The game's own window sprite where the skin has one, as the editor's panel has through its copy of the crew panel, so that a theme
            // that replaces the sprite's texture reaches it; a flat colour where it has none.
            Sprite windowSprite = skin.window != null && skin.window.normal != null ? skin.window.normal.background : null;
            if (windowSprite != null)
            {
                back.sprite = windowSprite;
                back.type = Image.Type.Sliced;
                back.color = new Color(1f, 1f, 1f, 0.95f);
            }
            else back.color = new Color(0.05f, 0.07f, 0.1f, 0.88f);
            back.raycastTarget = true;
            EscapeGuard.Own(frame.gameObject);
            UnityMouseEvents.ShieldFrame(frame, () => slide > 0f);

            long build = PlannerTiming.Start();
            float listWidth = PanelWidth - 6f;
            float rowWidth = PlannerWidgets.RowRoom(listWidth);
            view = new PlannerView(host, rowWidth, level);
            var rows = new List<DialogGUIBase>();
            rows.Add(Header(rowWidth));
            rows.AddRange(view.Rows());
            TooltipText.WrapAll(rows);
            UiHygiene.DeferInteractable(rows);
            PlannerTiming.Stop("Build.rows", build);
            build = PlannerTiming.Start();
            var layout = new DialogGUIVerticalLayout(rowWidth, -1f, 0f, new RectOffset(0, 0, 0, 12), TextAnchor.UpperLeft, rows.ToArray());
            float height = Screen.height / GameSettings.UI_SCALE - TopMargin - BottomMargin - 8f;
            var scroll = new DialogGUIScrollList(new Vector2(listWidth, Mathf.Max(200f, height)), false, true, layout);
            list = scroll;
            RectTransform content = NewRect("Content", frame);
            frameContent = content;
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.offsetMin = new Vector2(3f, 3f);
            content.offsetMax = new Vector2(-3f, -3f);
            var layouts = new Stack<Transform>();
            layouts.Push(content);
            list.Create(ref layouts, skin);
            PlannerTiming.Stop("Build.create", build);
            build = PlannerTiming.Start();
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
            PlannerTiming.Stop("Build.settle", build);

            // The tab: a button on the panel's left edge, near the top, which goes out with the panel.
            RectTransform tabHolder = NewRect("Tab", frame);
            tabHolder.anchorMin = tabHolder.anchorMax = new Vector2(0f, 1f);
            tabHolder.pivot = new Vector2(1f, 1f);
            tabHolder.sizeDelta = new Vector2(TabWidth, TabHeight);
            tabHolder.anchoredPosition = new Vector2(0f, -16f);
            var button = new DialogGUIButton("Planner", () => open = !open, TabWidth, TabHeight, false);
            button.tooltipText = "Route Planner";
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
            DialogTooltips.Attach(new[] { tab });
            // Off until the panel slides out; the tab, which is not under it, stays.
            frameContent.gameObject.SetActive(false);
            // One pass over the rows before the panel is first shown, which switches the rows that are not shown off here, once, and not in the first
            // frame the panel is out. KSP's elements find their text and image components only while they are active, so the panel is made switched on.
            build = PlannerTiming.Start();
            list.Update();
            PlannerTiming.Stop("Build.prime", build);
            Debug.Log(Log.Tag + " The planner in the map view made: " + PanelWidth.ToString("0") + " wide, rows " + rowWidth.ToString("0") + " wide.");
        }

        // The two switches, the navigation and the autopilot, the button that plans the whole route, and the line that says what the route is doing.
        private DialogGUIBase Header(float width)
        {
            float button = (width - 8f - 12f) / 3f;
            var navigation = new DialogGUIButton(() => NavigationOn() ? "<color=#8fdc6a>Navigation</color>" : "Navigation", ToggleNavigation, button, 28f, false);
            navigation.OptionInteractableCondition = () => !Flying;
            navigation.tooltipText = "Plan the route and keep the plan up to date while you fly it yourself"
                                     + (ParsekBridge.Unavailable != null ? ". " + ParsekBridge.Unavailable : "");
            var pilot = new DialogGUIButton(() => Flying ? "<color=#8fdc6a>Autopilot</color>" : "Autopilot", FlyOrStop, button, 28f, false);
            pilot.OptionInteractableCondition = () => Flying || MechJebDriver.Unavailable == null;
            pilot.tooltipText = MechJebDriver.Unavailable != null ? "Autopilot: " + MechJebDriver.Unavailable : "Fly the whole route with MechJeb";
            var whole = new DialogGUIButton("Plan route", PlanAllNow, button, 28f, false);
            whole.OptionInteractableCondition = () => !Flying;
            whole.tooltipText = "Plan the whole route from here and draw it on the map";
            var buttons = new DialogGUIHorizontalLayout(width, 32f, 6f, new RectOffset(4, 4, 4, 2), TextAnchor.MiddleLeft, navigation, pilot, whole);
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

        // For the probes: `count` stops added to the route, as the plus does.
        internal void AddStops(int count)
        {
            for (int i = 0; i < count && view != null; i++) view.AddStopNow();
        }

        // For the probes: the panel out, whether it was made before they asked or not.
        internal void OpenNow()
        {
            open = true;
        }

        // The route drawn on the map and the plan behind it are those of a vessel that is no longer flown.
        internal void ForgetPlan()
        {
            WholePlan = null;
            if (RoutePreview.Instance != null) RoutePreview.Instance.Hide();
        }

        // The route outgrew the rows: the panel goes, with its plan saved, and is made again in the next frame for the larger route. It stays open
        // as it was, since the window's own state is not the panel's.
        private void Rebuild(int needed)
        {
            Debug.Log(Log.Tag + " The planner in the map view is made again for a longer route (rows for level " + needed + " of " + PlannerPool.Full + ").");
            level = needed;
            if (view != null) view.Dispose();
            if (root != null) Destroy(root);
            view = null;
            root = null;
            list = null;
            tab = null;
            frame = null;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (view != null) view.Dispose();
            if (root != null) Destroy(root);
        }
    }
}
