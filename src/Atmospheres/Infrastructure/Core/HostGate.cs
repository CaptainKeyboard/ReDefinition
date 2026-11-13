using System;
using System.Reflection;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The hard dependency on the main mod: ReDefinition Atmospheres runs only beside ReDefinition, with an Api new enough and complete
    // enough for every member the atmospheres call. Where it is not, the start-up adds no part, writes one error line with the reason,
    // and the visual features (frost, droplets, lamp, precipitation) never run in a half-working Scene mode.
    internal static class HostGate
    {
        internal const string ApiInfoTypeName = "ReDefinition.Api.ApiInfo";

        // The lowest ReDefinition.Api.ApiInfo.Version these atmospheres work with. Raise it with the change that needs a newer member.
        internal const int RequiredApiVersion = 3;

        // Why the atmospheres cannot start with this host, or null when they can. Every argument is a type of the host (null where it is
        // absent): a test passes types of its own.
        internal static string Problem(Type apiInfo, Type hooks, Type frame, Type keys)
        {
            if (apiInfo == null)
                return "the main mod ReDefinition is not installed (ReDefinition.Api.ApiInfo is not found). Install ReDefinition " +
                       "next to ReDefinition Atmospheres; the atmospheres do not start without it.";
            int version = VersionOf(apiInfo);
            if (version < 0)
                return "the main mod's Api has no readable version (ApiInfo.Version); it is not a release these atmospheres work with. " +
                       "Update ReDefinition and ReDefinition Atmospheres together.";
            if (version < RequiredApiVersion)
                return "the main mod's Api is version " + version + ", these atmospheres need version " + RequiredApiVersion + " or newer. " +
                       "Update ReDefinition.";
            var missing = new ApiBridge(hooks, frame, line => { }).MissingMembers();
            if (!new ForecastKey(keys).Available) missing.Add("Keys.Pressed");
            if (missing.Count > 0)
                return "the main mod's Api (version " + version + ") lacks " + string.Join(", ", missing) +
                       ". Update ReDefinition and ReDefinition Atmospheres together.";
            return null;
        }

        // The game's check: the host's types are found among the loaded assemblies.
        internal static string Problem()
        {
            return Problem(TypeLookup.Find(ApiInfoTypeName), TypeLookup.Find(ApiBridge.HooksTypeName), TypeLookup.Find(ApiBridge.FrameTypeName),
                TypeLookup.Find(ForecastKey.KeysTypeName));
        }

        private static bool? open;

        // Whether the game's host passes the check (decided once; the addons that start beside AtmospheresStartup ask it).
        internal static bool Open
        {
            get
            {
                if (open == null) open = Problem() == null;
                return open.Value;
            }
        }

        // True where the atmospheres may start; false after one error line (through error) naming the reason.
        internal static bool Allows(string problem, Action<string> error)
        {
            if (problem == null) return true;
            error(AtmospheresLog.Prefix + "NOT STARTED: " + problem);
            return false;
        }

        private static int VersionOf(Type apiInfo)
        {
            try
            {
                FieldInfo field = apiInfo.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                if (field == null) return -1;
                object value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
                return value is int ? (int)value : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
