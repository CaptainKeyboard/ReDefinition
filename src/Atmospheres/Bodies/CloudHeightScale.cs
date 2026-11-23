using System;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // The height scale of the clouds (owner 2026-11-23 "Ja, so umsetzen", .local/plans/wolken-hoehenkonzept.md): every cloud height is a share of the body's MEASURED
    // troposphere (BodyFacts.MeasuredTropopauseM) times a height factor, and the cloud bodies' horizontal lengths follow with it. The troposphere replaces the generator's
    // assumed depth (a share of the atmosphere's depth) as the unit of the depth keys (innerDepths, outerDepths, ...); a layer set authored in metres names the troposphere
    // it was written for (CLOUDS key heightReferenceM, for example 11000 m for a set authored from the standard atmosphere), and its metre lengths are multiplied by the measured troposphere over that reference.
    // The factor is the body's (CLOUDS key heightFactor) times the viewer's slider. On by default at the factor 1 on the heights only (owner 2026-11-23 16:02, from pictures
    // with 1, 0.75 and 0.6), and only for a set that names heightReferenceM (owner 17:14; today the home planet's authored set); the template's bodies draw as before. A change of a key
    // re-reads the body configuration within a second (OwnCloudModule.EnsureStack).
    internal static class CloudHeightScale
    {
        internal const string TuneOn = "cloud.heightScale", TuneFactor = "cloud.heightFactor";
        // On by default with the factor 1 (owner 2026-11-23 16:02, "Faktor 1 (8,8 km)", on the pictures of PRUEFPROTOKOLL 139).
        internal const float OnDefault = 1f, FactorDefault = 1f, FactorMin = 0.3f, FactorMax = 1.5f;
        // 1: the horizontal lengths (noise, cells, type patches, the meso scale, the wavelengths of the height variation) follow the troposphere too (the concept's
        // section 4); 0 (default, owner 2026-11-23 16:02 "Nur Höhen"): only the heights scale, the sky's sizes and patterns stay as written. With 1 the rain deck looked
        // closed and grey from the ground at the factors 0.75 and 0.6 (PRUEFPROTOKOLL 139).
        internal const string TuneHorizontal = "cloud.heightScaleHorizontal";
        internal const float HorizontalDefault = 0f;

        internal static bool On { get { return Tuning.Get(TuneOn, OnDefault) > 0.5f; } }

        internal static float Factor { get { return Clamp(Tuning.Get(TuneFactor, FactorDefault)); } }

        internal static bool Horizontal { get { return Tuning.Get(TuneHorizontal, HorizontalDefault) > 0.5f; } }

        // What a change must re-read the configuration for.
        internal static string Signature
        {
            get { return (On ? "on " : "off ") + Factor.ToString("0.###", CultureInfo.InvariantCulture) + (Horizontal ? "" : " heights only"); }
        }

        internal static float Clamp(float factor)
        {
            return Math.Max(FactorMin, Math.Min(FactorMax, factor));
        }

        // The troposphere the cloud heights are shares of: the measured one times the factor; NaN where it was not measured.
        internal static double TroposphereM(double measuredM, double factor)
        {
            return measuredM > 0 && factor > 0 ? measuredM * factor : double.NaN;
        }

        // The factor on a metre length of a set written for referenceM: the troposphere over the reference; 1 where either is unknown (the metres stay as written).
        internal static double MetreScale(double troposphereM, double referenceM)
        {
            return referenceM > 0 && troposphereM > 0 ? troposphereM / referenceM : 1.0;
        }
    }
}
