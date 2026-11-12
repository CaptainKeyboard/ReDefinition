using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Atmospheres.Forecast
{
    // The air temperature of the weather field at a site and a time, kept for the hours the table asks again every second. The report's
    // steps do not carry a temperature, so the window asks the field; the cache is keyed by the field's DefinitionId and the site.
    internal sealed class FieldTemperatureCache
    {
        private readonly Dictionary<long, float> values = new Dictionary<long, float>();
        private long definition = long.MinValue;
        private ulong site;
        private const int Limit = 512;

        internal float Get(IWeatherField field, ForecastSite where, double ut)
        {
            if (field == null) return float.NaN;
            ulong key = AlertEvaluator.SiteKey(where);
            if (field.DefinitionId != definition || key != site || values.Count > Limit)
            {
                values.Clear();
                definition = field.DefinitionId;
                site = key;
            }
            long at = (long)Math.Round(ut);
            float k;
            if (values.TryGetValue(at, out k)) return k;
            WeatherSample sample;
            k = field.Sample(where.BodyIndex, where.LatDeg, where.LonDeg, where.AltAgl, ut, out sample) ? sample.TemperatureK : float.NaN;
            values[at] = k;
            return k;
        }
    }

    // The forecast window: KSP's own dialog (stock look, ZTheme restyles it where it is installed) with the site selector, the pinned place,
    // the state and the next storm, the launch conditions, the hourly strip and the table. It draws a ForecastView and decides nothing;
    // the view is rebuilt about once a second while the window is open. Escape closes it.
    internal sealed class ForecastWindow
    {
        private const float Width = 560f;
        private const float PageWidth = 530f;
        private const float RowHeight = 22f;
        private const float TableRow = 18f;
        private const float RefreshSeconds = 1f;
        private const int StripCells = 24;
        private const float CellWidth = 20f;
        // Six buttons of 84 pixels and five gaps of 2: 514, within the page of 530.
        private const float SiteButtonWidth = 84f;
        private static readonly float[] ColumnWidths = ForecastView.ColumnWidths;

        // The choice the window shows, shared with the overlay's hover label.
        internal static SiteChoice CurrentChoice = SiteChoice.Ksc;

        private static ForecastWindow instance;

        internal static ForecastWindow Instance
        {
            get
            {
                if (instance == null) instance = new ForecastWindow();
                return instance;
            }
        }

        private PopupDialog shown;
        private ForecastView view = new ForecastView();
        private readonly FieldTemperatureCache temperatures = new FieldTemperatureCache();
        private float nextRefresh;
        private int stripPage;
        private int builtRows;
        private bool closing;
        private string latText = "0";
        private string lonText = "0";
        private string[] richStrip = new string[StripCells];
        private string[] richLead = new string[StripCells];

        // Called after the window is closed, however it was closed, and after it is opened.
        internal Action OnClosed;
        internal Action OnOpened;

        internal bool IsOpen { get { return shown != null; } }
        internal GameObject Root { get { return shown != null ? shown.gameObject : null; } }
        internal ForecastView View { get { return view; } }

        internal void Toggle()
        {
            if (shown != null) Close();
            else Show();
        }

        internal void Show()
        {
            Refresh();
            Build();
            if (OnOpened != null) OnOpened();
        }

        internal void Close()
        {
            if (shown != null)
            {
                closing = true;
                shown.Dismiss();
            }
            shown = null;
            ReleaseBodyChoice();
            if (OnClosed != null) OnClosed();
        }

        // Makes the dialog anew. The rows of the table are as many as the report has steps (at least a day).
        private void Build()
        {
            if (shown != null) shown.Dismiss();
            builtRows = Math.Max(24, view.Rows.Length);
            var rows = new List<DialogGUIBase>();
            rows.Add(Line(RowHeight, SiteButtons()));
            rows.Add(Line(RowHeight, PinRow()));
            rows.Add(Label(() => view.StateText.Length > 0 ? view.StateText : view.SiteLine));
            rows.Add(Label(() => view.StateText.Length > 0 ? view.SiteLine : view.IssuedLine));
            rows.Add(Label(() => view.NextStormLine));
            rows.Add(Label(() => view.ArrivalLine));
            rows.Add(Label(() => view.ClearLine));
            DialogGUILabel launch = Label(() => view.LaunchHeadline.Length > 0 ? view.LaunchHeadline + " (" + ForecastStrings.LaunchNote + ")" : " ");
            launch.tooltipText = ForecastStrings.LaunchTooltip;
            rows.Add(launch);
            for (int i = 0; i < ForecastView.LaunchReasonLines; i++)
            {
                int line = i;
                rows.Add(Label(() => view.LaunchReasons[line]));
            }
            rows.Add(Label(() => view.WindowLine.Length > 0 ? view.WindowLine : " "));
            DialogGUILabel wind = Label(() => view.WindLine.Length > 0 ? view.WindLine : " ");
            rows.Add(wind);
            rows.Add(Packed(RowHeight, StripRow(richStrip, false)));
            rows.Add(Packed(16f, StripRow(richLead, true)));
            rows.Add(Line(RowHeight, StripPager()));
            rows.Add(TableHeader());
            var table = new List<DialogGUIBase>();
            for (int r = 0; r < builtRows; r++) table.Add(TableLine(r));
            TabScrollList list = new TabScrollList(new Vector2(Width - 20f, 190f),
                new DialogGUIVerticalLayout(Width - 40f, -1f, 0f, new RectOffset(2, 2, 2, 2), TextAnchor.UpperLeft, table.ToArray()));
            rows.Add(list);
            DialogGUIHorizontalLayout buttons = new DialogGUIHorizontalLayout(Width - 20f, 30f, 6f, new RectOffset(), TextAnchor.MiddleCenter,
                new DialogGUIButton(ForecastStrings.Refresh, () => { Refresh(); }, 100f, 26f, false),
                new DialogGUIButton(ForecastStrings.Close, Close, 90f, 26f, false));
            rows.Add(buttons);
            var dialog = new MultiOptionDialog("ReDefinitionForecast", "", ForecastStrings.WindowTitle, HighLogic.UISkin, Width, rows.ToArray());
            shown = DialogWindow.Show(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, false));
            ScrollListSettle.Apply(list);
            ScrollBackground.Clear(shown.gameObject);
            ScrollThrough.Apply(shown.gameObject);
            UiHygiene.MouseOnly(shown.gameObject);
            EscapeGuard.Own(shown);
            TooltipText.WrapAll(rows);
            DialogTooltips.Attach(rows);
            shown.gameObject.AddComponent<Driver>().Window = this;
        }

        private static DialogGUILabel Label(Func<string> text)
        {
            return new DialogGUILabel(text, PageWidth);
        }

        private static DialogGUIHorizontalLayout Line(float height, params DialogGUIBase[] children)
        {
            return new DialogGUIHorizontalLayout(PageWidth, height, 2f, new RectOffset(), TextAnchor.MiddleLeft, children);
        }

        // A row without spacing: the 24 cells of the strip are 24 x 20 = 480 pixels and the row holds them with 2 to spare.
        private static DialogGUIHorizontalLayout Packed(float height, params DialogGUIBase[] children)
        {
            return new DialogGUIHorizontalLayout(PageWidth, height, 0f, new RectOffset(), TextAnchor.MiddleLeft, children);
        }

        // The five sites: the chosen one is marked with brackets, since the skin's "on" state of a toggle reads the wrong way round.
        private DialogGUIBase[] SiteButtons()
        {
            var list = new List<DialogGUIBase>();
            // The body selector first: it picks the body of the pinned place, and shows the weather of any body that has it.
            DialogGUIButton body = new DialogGUIButton(() => BodyLabel(), CycleBody, SiteButtonWidth, RowHeight, false);
            body.tooltipText = ForecastStrings.TooltipBody;
            list.Add(body);
            foreach (SiteChoice choice in new[] { SiteChoice.Ksc, SiteChoice.LaunchPad, SiteChoice.Runway, SiteChoice.Vessel })
            {
                SiteChoice c = choice;
                DialogGUIButton b = new DialogGUIButton(() => (CurrentChoice == c ? "[" + ForecastSites.NameOf(c) + "]" : ForecastSites.NameOf(c)), () => Choose(c),
                    SiteButtonWidth, RowHeight, false);
                b.tooltipText = TooltipOf(c);
                b.OptionInteractableCondition = () => Available(c);
                list.Add(b);
            }
            DialogGUIButton pinned = new DialogGUIButton(() => (CurrentChoice == SiteChoice.Pinned ? "[" + ForecastStrings.SitePinned + "]" : ForecastStrings.SitePinned),
                () => Choose(SiteChoice.Pinned), SiteButtonWidth, RowHeight, false);
            pinned.tooltipText = ForecastStrings.SiteTooltipPinned;
            pinned.OptionInteractableCondition = () => ForecastSites.Pinned.Set;
            list.Add(pinned);
            return list.ToArray();
        }

        private static string TooltipOf(SiteChoice c)
        {
            switch (c)
            {
                case SiteChoice.Ksc: return ForecastStrings.SiteTooltipKsc;
                case SiteChoice.LaunchPad: return ForecastStrings.SiteTooltipLaunchPad;
                case SiteChoice.Runway: return ForecastStrings.SiteTooltipRunway;
                case SiteChoice.Vessel: return ForecastStrings.SiteTooltipVessel;
                default: return ForecastStrings.SiteTooltipPinned;
            }
        }

        private static bool Available(SiteChoice c)
        {
            ForecastSite site;
            return ForecastSites.TryLocate(c, out site);
        }

        private DialogGUIBase[] PinRow()
        {
            var lat = new DialogGUITextInput(latText, false, 8, text =>
            {
                latText = text;
                return text;
            }, 80f, RowHeight);
            var lon = new DialogGUITextInput(lonText, false, 8, text =>
            {
                lonText = text;
                return text;
            }, 80f, RowHeight);
            var pin = new DialogGUIButton(ForecastStrings.PinHere, Pin, 60f, RowHeight, false);
            pin.tooltipText = ForecastStrings.TooltipPin;
            return new DialogGUIBase[] { new DialogGUILabel(ForecastStrings.LabelLatitude, 62f), lat, new DialogGUILabel(ForecastStrings.LabelLongitude, 70f), lon, pin };
        }

        // The body of the pinned place, or the one the site is on.
        private string BodyLabel()
        {
            List<CelestialBody> all = GameGlobals.Bodies;
            int index = CurrentChoice == SiteChoice.Pinned ? ForecastSites.Pinned.BodyIndex : CurrentBodyIndex();
            return all != null && index >= 0 && index < all.Count ? all[index].bodyName : ForecastStrings.LabelBody;
        }

        private static int CurrentBodyIndex()
        {
            ForecastSite site;
            return ForecastSites.TryLocate(CurrentChoice, out site) ? site.BodyIndex : ForecastSites.Pinned.BodyIndex;
        }

        // What the body selector replaced: the pinned place and the choice before the first click, put back when the window closes (a body
        // picked to look at is not a place the player pinned, and it is not kept watched).
        private bool pinFromBody;
        private SiteChoice choiceBeforeBody;
        private bool pinWasSet;
        private int pinBodyBefore;
        private double pinLatBefore, pinLonBefore;

        // The next body where weather runs becomes the body of the pinned place (its latitude and longitude as typed), for as long as the
        // window is open; the pin button makes it the player's own pin.
        private void CycleBody()
        {
            List<CelestialBody> bodies = ForecastSites.WeatherBodies();
            if (bodies.Count == 0) return;
            int current = CurrentChoice == SiteChoice.Pinned ? ForecastSites.Pinned.BodyIndex : CurrentBodyIndex();
            int at = bodies.FindIndex(b => b.flightGlobalsIndex == current);
            CelestialBody next = bodies[(at + 1) % bodies.Count];
            if (!pinFromBody)
            {
                pinFromBody = true;
                choiceBeforeBody = CurrentChoice;
                PinnedSite pin = ForecastSites.Pinned;
                pinWasSet = pin.Set;
                pinBodyBefore = pin.BodyIndex;
                pinLatBefore = pin.LatDeg;
                pinLonBefore = pin.LonDeg;
            }
            ForecastSites.Pinned.BodyIndex = next.flightGlobalsIndex;
            ParseInto(ForecastSites.Pinned);
            ForecastSites.Pinned.Set = true;
            CurrentChoice = SiteChoice.Pinned;
            WatchPinned();
            Refresh();
        }

        private void Pin()
        {
            ForecastSite site;
            int body = ForecastSites.TryLocate(CurrentChoice, out site) ? site.BodyIndex : ForecastSites.Pinned.BodyIndex;
            if (!ParseInto(ForecastSites.Pinned)) return;
            pinFromBody = false;
            ForecastSites.Pinned.BodyIndex = body;
            ForecastSites.Pinned.Set = true;
            CurrentChoice = SiteChoice.Pinned;
            WatchPinned();
            Refresh();
        }

        // Puts back the pin and the choice the body selector replaced, and lets the watched place go.
        private void ReleaseBodyChoice()
        {
            if (!pinFromBody) return;
            pinFromBody = false;
            PinnedSite pin = ForecastSites.Pinned;
            pin.Set = pinWasSet;
            pin.BodyIndex = pinBodyBefore;
            pin.LatDeg = pinLatBefore;
            pin.LonDeg = pinLonBefore;
            if (CurrentChoice == SiteChoice.Pinned) CurrentChoice = choiceBeforeBody;
            WatchPinned();
        }

        private ForecastSite watchedPin;
        private bool watching;

        // A pinned place is watched by the service, so that the alerts cover it (the sites of the setting alertSites are the pad and the
        // vessel); the place pinned before is let go.
        private void WatchPinned()
        {
            ForecastService service = ForecastModule.Service;
            if (service == null) return;
            if (watching) service.Unwatch(watchedPin);
            ForecastSite site;
            watching = ForecastSites.TryLocate(SiteChoice.Pinned, out site);
            if (!watching) return;
            watchedPin = site;
            service.Watch(site, false);
        }

        private bool ParseInto(PinnedSite pin)
        {
            double lat, lon;
            if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                || !double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) return false;
            pin.LatDeg = Math.Max(-90.0, Math.Min(90.0, lat));
            pin.LonDeg = ForecastMath.WrapLon(lon);
            return true;
        }

        private void Choose(SiteChoice choice)
        {
            CurrentChoice = choice;
            stripPage = 0;
            Refresh();
        }

        // The hourly strip: one label of CellWidth pixels a cell, the level as its letter and number on the band's colour.
        private DialogGUIBase[] StripRow(string[] texts, bool lead)
        {
            var list = new List<DialogGUIBase>();
            list.Add(new DialogGUISpace(2f));
            for (int i = 0; i < StripCells; i++)
            {
                int cell = i;
                DialogGUILabel label = new DialogGUILabel(() => texts[cell] ?? " ", CellWidth);
                if (!lead) label.tooltipText = ForecastStrings.TooltipStrip;
                list.Add(label);
            }
            return list.ToArray();
        }

        private DialogGUIBase[] StripPager()
        {
            var prev = new DialogGUIButton(ForecastStrings.PrevPage, () => Page(-1), 30f, RowHeight, false);
            var next = new DialogGUIButton(ForecastStrings.NextPage, () => Page(1), 30f, RowHeight, false);
            prev.tooltipText = ForecastStrings.TooltipPage;
            next.tooltipText = ForecastStrings.TooltipPage;
            prev.OptionInteractableCondition = () => view.StripPage > 0;
            next.OptionInteractableCondition = () => view.StripPage < view.StripPages - 1;
            return new DialogGUIBase[]
            {
                prev, next,
                new DialogGUILabel(() => view.StripPages > 1 ? ForecastStrings.HoursRange(view.StripPage * StripCells, (view.StripPage + 1) * StripCells) : ForecastStrings.HoursDay, 200f)
            };
        }

        private void Page(int step)
        {
            stripPage = Math.Max(0, stripPage + step);
            Refresh();
        }

        private DialogGUIBase TableHeader()
        {
            var cells = new List<DialogGUIBase>();
            for (int c = 0; c < ForecastView.Columns.Length; c++)
            {
                DialogGUILabel head = new DialogGUILabel("<b>" + ForecastView.Columns[c] + "</b>", ColumnWidths[c]);
                if (c == ForecastView.Columns.Length - 1) head.tooltipText = ForecastStrings.TooltipFieldTemperature;
                cells.Add(head);
            }
            // No spacing, as in the rows below, so that the columns stand over each other.
            return Packed(TableRow, cells.ToArray());
        }

        private DialogGUIBase TableLine(int row)
        {
            var cells = new List<DialogGUIBase>();
            for (int c = 0; c < ColumnWidths.Length; c++)
            {
                int column = c;
                cells.Add(new DialogGUILabel(() => row < view.Rows.Length ? view.Rows[row][column] : " ", ColumnWidths[c]));
            }
            return new DialogGUIHorizontalLayout(PageWidth - 20f, TableRow, 0f, new RectOffset(), TextAnchor.MiddleLeft, cells.ToArray());
        }

        // The view from the game: the site's report, the engine's numbers, the launch check, the state of the field.
        internal void Refresh()
        {
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            view = ForecastView.Build(InputNow());
            for (int i = 0; i < StripCells; i++)
            {
                if (i < view.Strip.Length)
                {
                    StripCell cell = view.Strip[i];
                    string back = cell.BackHex + Alpha(cell.Alpha);
                    richStrip[i] = "<mark=#" + back + "><color=#" + cell.TextHex + ">" + cell.Text + "</color></mark>";
                    richLead[i] = cell.Lead;
                }
                else
                {
                    richStrip[i] = " ";
                    richLead[i] = " ";
                }
            }
            if (shown != null && view.Rows.Length > builtRows) Build();
        }

        private static string Alpha(float alpha)
        {
            int a = Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
            return a.ToString("X2", CultureInfo.InvariantCulture);
        }

        internal ForecastViewInput InputNow()
        {
            var input = new ForecastViewInput();
            input.Enabled = ForecastUiSettings.ForecastOn;
            input.NowUt = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
            input.DateText = ForecastSites.DateText;
            input.HorizonHours = AtmospheresSettings.ForecastHorizonHours;
            IWeatherField field = WeatherHub.Field;
            input.Readiness = field != null ? field.Readiness : FieldReadiness.Seeding;
            ForecastSite site;
            input.HasBody = ForecastSites.TryLocate(CurrentChoice, out site);
            input.Site = site;
            input.StripFirst = stripPage * StripCells;
            input.StripCells = StripCells;
            if (!input.HasBody) return input;
            List<CelestialBody> bodies = GameGlobals.Bodies;
            CelestialBody body = bodies != null && site.BodyIndex >= 0 && site.BodyIndex < bodies.Count ? bodies[site.BodyIndex] : null;
            input.BodyName = body != null ? body.bodyName : "";
            input.BodyRadiusM = body != null ? body.Radius : 0.0;
            input.LaunchSite = ForecastSites.IsLaunchSite(CurrentChoice);
            input.Vacuum = body == null || !body.atmosphere || VesselAboveAtmosphere(body);
            input.FieldTemperatureK = ut => temperatures.Get(field, site, ut);
            Func<ForecastSite, ForecastReport> source = WeatherHooks.Forecast;
            input.Report = source != null ? source(site) : null;
            ForecastService service = ForecastModule.Service;
            if (service != null)
            {
                ForecastResult detail = service.DetailOf(site);
                if (detail != null)
                    input.Detail = new ForecastDetail { Known = true, ThresholdEtaUt = detail.ThresholdEtaUt, ClearUt = detail.ClearUt, ActiveAtStart = detail.ActiveAtStart };
                if (input.LaunchSite) input.Launch = service.AssessLaunch(site);
            }
            return input;
        }

        // The vessel the site follows is above the air of its body: no weather there.
        private static bool VesselAboveAtmosphere(CelestialBody body)
        {
            if (CurrentChoice != SiteChoice.Vessel) return false;
            Vessel vessel = GameGlobals.ActiveVessel;
            return vessel != null && vessel.altitude > body.atmosphereDepth;
        }

        private void Tick()
        {
            if (Time.unscaledTime < nextRefresh) return;
            Refresh();
            // A skin refresh may colour the list's background again.
            if (shown != null) ScrollBackground.Clear(shown.gameObject);
        }

        // The dialog's object is gone (Escape, a scene change); the window forgets it and tells the launcher.
        private void Gone()
        {
            if (shown != null && shown.gameObject != null) return;
            if (closing)
            {
                closing = false;
                return;
            }
            shown = null;
            ReleaseBodyChoice();
            if (OnClosed != null) OnClosed();
        }

        private sealed class Driver : MonoBehaviour
        {
            internal ForecastWindow Window;

            private void Update()
            {
                if (Window != null) Window.Tick();
            }

            private void OnDestroy()
            {
                if (Window != null) Window.Gone();
            }
        }
    }
}
