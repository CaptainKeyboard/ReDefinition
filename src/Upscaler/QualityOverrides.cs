using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Game-wide quality settings for the time the upscaler runs.
    //
    // Each is remembered before it is changed and put back afterwards, and each is
    // switched separately, so the effect of one can be judged without the others.
    //
    // Put back only where the value is still the one written here: a value the
    // player or another mod set in the meantime stands.
    //
    // Shadow distance and cascades stay Scatterer's; V-Sync, frame rate limit and
    // texture quality stay the player's.
    internal class QualityOverrides
    {
        private bool lodApplied;
        private float previousLodBias;
        private float lodRatio = 1f;

        private bool msaaApplied;
        private int previousAntiAliasing;
        private int appliedAntiAliasing;

        private bool anisoApplied;
        private AnisotropicFiltering previousAnisotropic;
        private AnisotropicFiltering appliedAnisotropic;

        public float AppliedLodBias { get; private set; }

        // Unity picks LOD levels from how many pixels an object covers on screen.
        // At a lower render resolution the same object covers fewer pixels, so
        // Unity switches to coarser meshes earlier -- as with the mipmap levels.
        //
        // At NativeAA the ratio is 1, so Max(1, ratio) is 1 and the bias is
        // multiplied by one: the switch changes nothing at AA only.
        //
        // The factor is the same one the mipmap bias uses: display over render.
        // Affects everything with a LODGroup, so Parallax' scatter objects and
        // part of the part models. Not KSP's terrain, which subdivides its quads
        // by distance rather than by screen size.
        public void SetLodBias(bool wanted, float ratio)
        {
            if (wanted)
            {
                if (!lodApplied)
                {
                    previousLodBias = QualitySettings.lodBias;
                    lodApplied = true;
                }
                lodRatio = Mathf.Max(1f, ratio);
                AppliedLodBias = previousLodBias * lodRatio;
                QualitySettings.lodBias = AppliedLodBias;
                return;
            }

            if (!lodApplied) return;
            if (Mathf.Approximately(QualitySettings.lodBias, AppliedLodBias))
                QualitySettings.lodBias = previousLodBias;
            lodApplied = false;
            AppliedLodBias = 0f;
        }

        // MSAA resolves before the rig's capture and removes the aliasing a
        // temporal upscaler reconstructs from. The shared RenderTexture of the 3D
        // stack has no MSAA; QualitySettings.antiAliasing is set off as well.
        public void SetAntiAliasing(bool disable)
        {
            if (disable)
            {
                if (msaaApplied) return;
                previousAntiAliasing = QualitySettings.antiAliasing;
                QualitySettings.antiAliasing = 0;
                appliedAntiAliasing = 0;
                msaaApplied = true;
                return;
            }

            if (!msaaApplied) return;
            msaaApplied = false;
            // TUFX switches MSAA off itself for an HDR profile with bloom as it
            // applies a profile, which it does at every scene load and camera change
            // (TexturesUnlimitedFXLoader.ApplyProfile, onLevelWasLoaded,
            // OnCameraChange). Where it applied one while ReDefinition's value was
            // set, to the same value, its rule holds again from the next of those.
            if (QualitySettings.antiAliasing == appliedAntiAliasing)
                QualitySettings.antiAliasing = previousAntiAliasing;
        }

        // The negative mipmap bias pulls in finer mip levels. Without anisotropic
        // filtering those shimmer on surfaces seen at a grazing angle -- the
        // runway, terrain towards the horizon -- where the bias applies. It costs
        // almost nothing.
        public void SetAnisotropic(bool force)
        {
            if (force)
            {
                if (anisoApplied) return;
                previousAnisotropic = QualitySettings.anisotropicFiltering;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                appliedAnisotropic = AnisotropicFiltering.ForceEnable;
                anisoApplied = true;
                return;
            }

            if (!anisoApplied) return;
            if (QualitySettings.anisotropicFiltering == appliedAnisotropic)
                QualitySettings.anisotropicFiltering = previousAnisotropic;
            anisoApplied = false;
        }

        // After KSP applied its own settings -- Apply or Accept in the settings
        // dialog, the main menu's settings screen. KSP sets the quality level
        // there and then MSAA, every time (GameSettings.ApplySettings:
        // SetQualityLevel, then antiAliasing = ANTI_ALIASING, decompiled).
        //
        // MSAA: a value in Unity other than ReDefinition's is what KSP has just
        // written. ReDefinition's value there is the player's choice only where KSP
        // keeps it as theirs too, GameSettings.ANTI_ALIASING -- a player who switched
        // MSAA off while ReDefinition had it off too chose off. Otherwise nothing
        // wrote it: the event also comes from
        // ReDefinition's own follow-up of a change that leaves MSAA alone
        // (KspBehaviour.FollowUp), and a rig set up between KSP's write and the
        // event has saved what KSP wrote already. LOD bias and anisotropic filtering KSP never
        // writes; they come with the quality level, and whether Unity's level
        // still carries ReDefinition's is not known here. They are taken over only
        // where they differ from ReDefinition's: a bias taken over while it is
        // ReDefinition's would be multiplied again at every Apply, and restored to.
        public void Reassert()
        {
            if (lodApplied && !Mathf.Approximately(QualitySettings.lodBias, AppliedLodBias))
            {
                previousLodBias = QualitySettings.lodBias;
                AppliedLodBias = previousLodBias * lodRatio;
                QualitySettings.lodBias = AppliedLodBias;
            }

            if (msaaApplied)
            {
                int now = QualitySettings.antiAliasing;
                if (now != appliedAntiAliasing) previousAntiAliasing = now;
                else if (GameSettings.ANTI_ALIASING == appliedAntiAliasing) previousAntiAliasing = appliedAntiAliasing;
                QualitySettings.antiAliasing = appliedAntiAliasing;
            }

            if (anisoApplied && QualitySettings.anisotropicFiltering != appliedAnisotropic)
            {
                previousAnisotropic = QualitySettings.anisotropicFiltering;
                QualitySettings.anisotropicFiltering = appliedAnisotropic;
            }
        }

        public void RestoreAll()
        {
            SetLodBias(false, 1f);
            SetAntiAliasing(false);
            SetAnisotropic(false);
        }

        public string Describe()
        {
            return "LOD bias " + (lodApplied
                       ? previousLodBias.ToString("0.##") + " -> " + AppliedLodBias.ToString("0.##")
                       : QualitySettings.lodBias.ToString("0.##") + " untouched")
                   + ", MSAA " + (msaaApplied ? "forced off (was " + previousAntiAliasing + "x)"
                                              : QualitySettings.antiAliasing + "x untouched")
                   + ", anisotropic " + (anisoApplied ? "forced" : QualitySettings.anisotropicFiltering + " untouched");
        }
    }
}
