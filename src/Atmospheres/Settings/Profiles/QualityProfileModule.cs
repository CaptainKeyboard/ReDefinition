using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Writes the tier's budget row when the tier changes: at the first start (no marker yet), when the player names another tier, when the
    // host's profile changes under Auto, and when the memory guard moves the tier. The tier in force is kept in PluginData/profile.cfg, so
    // that a restart with the same tier leaves the player's own budget values alone. The check runs once a second.
    [AtmospheresModule(5)]
    internal sealed class QualityProfileModule : IAtmospheresModule
    {
        internal const string MarkerFile = "profile.cfg";
        internal const string MarkerKey = "appliedTier";
        // The revision of the tier table the marker was written with (TierRows.Revision): a changed table is written once more, also at the same tier.
        internal const string RevisionKey = "rowRevision";
        private const float CheckSeconds = 1f;

        private float next;
        private QualityTier applied = QualityTier.Auto;

        public void Init()
        {
            CloudFastPath.Source = FastPathOfTier;
            QualityTier? marked = ReadMarker(AtmospheresSettings.Mod);
            QualityTier effective = QualityProfile.Effective();
            if (QualityProfile.NeedsApply(effective, marked) || ReadRevision(AtmospheresSettings.Mod) != TierRows.Revision) Apply(effective);
            else applied = effective;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            next -= dt;
            if (next > 0f) return;
            next = CheckSeconds;
            QualityTier effective = QualityProfile.Effective();
            if (effective != applied) Apply(effective);
        }

        public void Shutdown()
        {
        }

        private void Apply(QualityTier effective)
        {
            QualityProfile.Apply(effective);
            applied = effective;
            AtmospheresSettings.Save();
            WriteMarker(AtmospheresSettings.Mod, effective);
            AtmospheresLog.Say("quality tier " + effective + ": volume steps " + AtmospheresSettings.VolumeSteps + ", volume resolution "
                               + AtmospheresSettings.VolumeResolution.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", lamps "
                               + AtmospheresSettings.LightCount + ", simulation grid " + AtmospheresSettings.SimGrid + ", orbit map "
                               + AtmospheresSettings.OrbitMapWidth);
        }

        // The tier the marker names; null where there is no marker or it is damaged.
        // The fast cloud path of the tier in force (CloudFastPath.Source in the game).
        internal static bool FastPathOfTier()
        {
            return TierRows.For(QualityProfile.Effective()).CloudFastPath;
        }

        internal static QualityTier? ReadMarker(string mod)
        {
            return ModSettingsFile.Read<QualityTier?>(mod, MarkerFile, ParseMarker, null);
        }

        // The table revision the marker names; 0 where there is none (a marker from before the revision was written).
        internal static int ReadRevision(string mod)
        {
            return ModSettingsFile.Read(mod, MarkerFile, ParseRevision, 0);
        }

        internal static int ParseRevision(ConfigNode root)
        {
            string text;
            int revision;
            if (root == null || !ModSettingsFile.TryString(root, RevisionKey, out text)) return 0;
            return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out revision) ? revision : 0;
        }

        internal static QualityTier? ParseMarker(ConfigNode root)
        {
            string text;
            if (root == null || !ModSettingsFile.TryString(root, MarkerKey, out text)) return null;
            QualityTier tier;
            return Enum.TryParse(text, true, out tier) && tier != QualityTier.Auto ? tier : (QualityTier?)null;
        }

        internal static void WriteMarker(string mod, QualityTier tier)
        {
            ModSettingsFile.Write(mod, MarkerFile, root =>
            {
                root.AddValue(MarkerKey, tier.ToString());
                root.AddValue(RevisionKey, TierRows.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
        }
    }
}
