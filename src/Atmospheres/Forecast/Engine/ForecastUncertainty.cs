using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The optional, display-only error of an "issued" forecast (the setting forecastUncertainty; zero is exact and the default). The
    // weather itself is never touched. The error is a timing shift of what a step shows and a position shift of the tracks, both growing
    // with the lead time and zero at lead 0; its direction and size come from a hash of the subject (the site, or the system key) and
    // the 3-hour bucket the forecast was issued in, so the picture does not flicker between reports of one bucket. The confidence of a
    // step is 1 minus the error scale. The numbers (a quarter of the lead as the most timing shift, 0.83 % of the body's radius of position
    // per lead hour, the full error scale at a 24-hour lead) are taste values of the owner's question Q2 [OWNER].
    internal static class ForecastUncertainty
    {
        internal const double IssueBucketSeconds = 3.0 * 3600.0;
        internal const double TimingFraction = 0.25;
        // The position error at full scale per hour of lead, as a share of the body's radius.
        internal const double PositionRadiusPerLeadHour = 0.0083;
        internal const double FullErrorLeadSeconds = 24.0 * 3600.0;

        // The key of a subject (a site or a system) for the bucket the forecast was issued in.
        internal static ulong IssueKey(ulong subject, double startUt)
        {
            return ForecastMath.Mix(ForecastMath.Mix(0x51A7E5UL, subject), (long)Math.Floor(startUt / IssueBucketSeconds));
        }

        // The key of a site.
        internal static ulong SiteSubject(int bodyIndex, long latTenth, long lonTenth)
        {
            return ForecastMath.Mix(ForecastMath.Mix(ForecastMath.Mix(0x517EUL, (long)bodyIndex), latTenth), lonTenth);
        }

        // By how many seconds the shown step is moved in time at a lead; zero at lead 0.
        internal static double TimingShiftSeconds(ulong issueKey, float amount01, double leadSeconds)
        {
            if (amount01 <= 0f || leadSeconds <= 0.0) return 0.0;
            return ForecastMath.Unit(ForecastMath.Mix(issueKey, 1L)) * amount01 * TimingFraction * leadSeconds;
        }

        // The confidence of a step at a lead: 1 at lead 0, falling with the lead and the error scale.
        internal static float Confidence(ulong issueKey, float amount01, double leadSeconds)
        {
            if (amount01 <= 0f || leadSeconds <= 0.0) return 1f;
            double growth = Math.Min(1.0, leadSeconds / FullErrorLeadSeconds);
            double error = amount01 * Math.Abs(ForecastMath.Unit(ForecastMath.Mix(issueKey, 1L))) * growth;
            return (float)Math.Max(0.0, 1.0 - error);
        }

        // Moves the samples of tracks by the position error of their lead; in place (the tracks are the caller's copies).
        internal static void ShiftTracks(ForecastTrack[] tracks, float amount01, double startUt, double bodyRadiusM)
        {
            if (amount01 <= 0f || tracks == null) return;
            for (int t = 0; t < tracks.Length; t++)
            {
                ForecastTrack tr = tracks[t];
                ulong key = IssueKey(tr.CellKey, startUt);
                double angle = Math.PI * ForecastMath.Unit(ForecastMath.Mix(key, 3L));
                double size = Math.Abs(ForecastMath.Unit(ForecastMath.Mix(key, 2L)));
                for (int i = 0; i < tr.Ut.Length; i++)
                {
                    double lead = tr.Ut[i] - startUt;
                    if (lead <= 0.0) continue;
                    double metres = amount01 * size * (lead / 3600.0) * PositionRadiusPerLeadHour * bodyRadiusM;
                    double lat, lon;
                    ForecastMath.Offset(tr.Lat[i], tr.Lon[i], metres * Math.Cos(angle), metres * Math.Sin(angle), bodyRadiusM, out lat, out lon);
                    tr.Lat[i] = lat;
                    tr.Lon[i] = lon;
                }
            }
        }
    }
}
