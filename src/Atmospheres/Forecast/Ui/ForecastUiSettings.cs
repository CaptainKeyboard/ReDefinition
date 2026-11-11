namespace ReDefinition.Atmospheres.Forecast
{
    // The settings the forecast interface reads, all from the table. The dashboard's push is the dashboard's own listener and has its own
    // switch there (DashboardSettings.WeatherPush).
    internal static class ForecastUiSettings
    {
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
            get { return ForecastOn && AtmospheresSettings.ForecastOverlay; }
        }

        internal static AlertDeliverySettings Delivery()
        {
            return new AlertDeliverySettings
            {
                Enabled = AlertsOn,
                ScreenMessage = AtmospheresSettings.AlertScreenMessage,
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
