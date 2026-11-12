using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // Every string of the forecast interface in one table, so that a translation touches one file. Titles and tooltips follow the repo
    // rules: a window title and labels in Title Case, tooltips of the buttons a name or a few words in Title Case.
    internal static class ForecastStrings
    {
        internal const string WindowTitle = "Weather Forecast";
        internal const string LauncherTooltip = "Weather Forecast";
        internal const string Close = "Close";
        internal const string Refresh = "Refresh";
        internal const string PinHere = "Pin";
        internal const string PrevPage = "<";
        internal const string NextPage = ">";

        internal const string StateOff = "Forecast is switched off";
        internal const string StateComputing = "Forecast: computing";
        internal const string StateApproximate = "Forecast: approximate, the weather field is still catching up";
        internal const string StateBroken = "Forecast unavailable, the weather field stopped";
        internal const string StateNoBody = "No forecast for this body";
        internal const string StateVacuum = "No weather in vacuum";

        internal const string SiteKsc = "KSC";
        internal const string SiteLaunchPad = "Launch Pad";
        internal const string SiteRunway = "Runway";
        internal const string SiteVessel = "Vessel";
        internal const string SitePinned = "Pinned";

        internal const string SiteTooltipKsc = "Space Centre";
        internal const string SiteTooltipLaunchPad = "Launch Pad";
        internal const string SiteTooltipRunway = "Runway";
        internal const string SiteTooltipVessel = "Active Vessel";
        internal const string SiteTooltipPinned = "Pinned Latitude And Longitude";

        internal const string ColWhen = "When";
        internal const string ColLevel = "Level";
        internal const string ColType = "Type";
        internal const string ColVisibility = "Visibility";
        internal const string ColWind = "Wind From";
        internal const string ColGust = "Gust";
        internal const string ColTemperature = "Field Temp";

        internal const string TooltipStrip = "Precipitation Level Per Hour";
        internal const string TooltipFieldTemperature = "Field Temperature";
        internal const string TooltipBody = "Choose Body";
        internal const string TooltipPin = "Pin Latitude And Longitude";
        internal const string TooltipPage = "Strip Page";

        internal const string LaunchGo = "Launch: Go";
        internal const string LaunchMarginal = "Launch: Marginal";
        internal const string LaunchNoGo = "Launch: No-Go";
        internal const string LaunchUnavailable = "Launch: not assessed";
        internal const string LaunchTooltip = "Launch Conditions";
        internal const string LaunchNote = "Information only, a launch is never blocked";

        internal const string WindOurs = "Wind: ours";
        internal const string WindNowOnlySuffix = ", now only";

        internal const string MapPrecipitation = "Precipitation map";
        internal const string MapApproximate = "Precipitation map, approximate";

        internal const string MessageTitle = "Weather Alert";
        internal const string WarpSummarySuffix = " (passed during time warp)";

        internal const string KacAlarmPrefix = "Storm approaching: ";

        // The labels and lines of the window and of the view that were written in place before.
        internal const string LabelLatitude = "Latitude";
        internal const string LabelLongitude = "Longitude";
        internal const string LabelBody = "Body";
        internal const string LabelSite = "Site";
        internal const string HoursDay = "Hours 0 to 24";
        internal const string IssuedPrefix = "Issued ";
        internal const string StormNowPrefix = "Storm now: ";
        internal const string NextStormPrefix = "Next storm ";
        internal const string StormArrived = "Arrived";
        internal const string ArrivesPrefix = "Arrives ";
        internal const string ClearsPrefix = "Clears ";
        internal const string ClearsAfterHorizon = "Clears after the forecast horizon";
        internal const string NoLaunchWindow = "No launch window in the next 24 h";

        // "Hours 24 to 48".
        internal static string HoursRange(int from, int to)
        {
            return "Hours " + from.ToString(CultureInfo.InvariantCulture) + " to " + to.ToString(CultureInfo.InvariantCulture);
        }

        // "No storm within 24 h".
        internal static string NoStormWithin(string hours)
        {
            return "No storm within " + hours + " h";
        }

        // "Best window in the next 24 h: <from> to <to> (3.5 h)".
        internal static string BestWindow(string from, string to, string hours)
        {
            return "Best window in the next 24 h: " + from + " to " + to + " (" + hours + " h)";
        }

        // The hourly cell: the letter of the band and its number ("H4"), so that the level is readable without the colour.
        internal static string Cell(PrecipBand band)
        {
            return ForecastFormat.Letter(band) + ((int)band).ToString(CultureInfo.InvariantCulture);
        }
    }
}
