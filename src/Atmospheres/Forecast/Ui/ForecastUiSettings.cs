namespace ReDefinition.Atmospheres.Forecast
{
    // The settings the forecast interface reads. The existing keys are read from the table; three switches of this package
    // (alertScreenMessage, alertDashboard, forecastOverlay) are keys the integration adds (request in the package report), and read
    // their defaults until the table has them.
    internal static class ForecastUiSettings
    {
        internal const string KeyScreenMessage = "alertScreenMessage";
        internal const string KeyOverlay = "forecastOverlay";
        internal const string KeyDashboard = "alertDashboard";

        private static bool Flag(string key, bool fallback)
        {
            return AtmospheresSettings.Definition(key) != null ? AtmospheresSettings.Number(key) != 0.0 : fallback;
        }

        // forecastEnabled with weatherEffects: the window, the HUD line and the overlay.
        internal static bool ForecastOn
        {
            get { return AtmospheresSettings.WeatherEffects && AtmospheresSettings.ForecastEnabled; }
        }

        internal static bool AlertsOn
        {
            get { return ForecastOn && AtmospheresSettings.AlertsEnabled; }
        }

        internal static bool Overlay
        {
            get { return ForecastOn && Flag(KeyOverlay, true); }
        }

        internal static AlertDeliverySettings Delivery()
        {
            return new AlertDeliverySettings
            {
                Enabled = AlertsOn,
                ScreenMessage = Flag(KeyScreenMessage, true),
                MessageApp = AtmospheresSettings.AlertMessageApp,
                WarpStop = AtmospheresSettings.AlertWarpStop
            };
        }

        internal static bool Kac
        {
            get { return AlertsOn && AtmospheresSettings.AlertKac; }
        }

        internal static KacAction KacActionSetting
        {
            get { return AtmospheresSettings.AlertWarpStop ? KacAction.KillWarp : KacAction.MessageOnly; }
        }
    }
}
