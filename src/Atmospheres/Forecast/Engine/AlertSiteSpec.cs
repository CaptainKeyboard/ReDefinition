using System;

namespace ReDefinition.Atmospheres.Forecast
{
    // The sites the user wants alerts for (the setting alertSites): a list of words separated by commas, semicolons or spaces. "KSC" is
    // the pad of the Space Centre (it also gets LaunchWindow alerts), "vessel" is the active vessel while it is on the ground or in the
    // air of an atmosphere. An empty setting means both. Other words are ignored; a site added in the forecast window is watched through
    // ForecastService.Watch. Pure.
    internal struct AlertSiteSpec
    {
        internal bool Ksc;
        internal bool Vessel;

        internal static AlertSiteSpec Parse(string text)
        {
            var spec = new AlertSiteSpec();
            if (string.IsNullOrWhiteSpace(text))
            {
                spec.Ksc = true;
                spec.Vessel = true;
                return spec;
            }
            foreach (string word in text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = word.Trim().ToLowerInvariant();
                if (w == "ksc") spec.Ksc = true;
                else if (w == "vessel") spec.Vessel = true;
            }
            return spec;
        }
    }
}
