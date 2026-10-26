using ReDefinition.Settings.Behaviours;
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
        // Whether the LOD bias is ReDefinition's now, the bias it replaced, and the factor it multiplies that by.
        // KSP never writes the LOD bias. It comes with the quality level, and whether Unity's level still carries
        // ReDefinition's is not known here. Reassert takes it over only where it differs from ReDefinition's: a bias
        // taken over while it is ReDefinition's would be multiplied again at every Apply, and restored to.
        private bool lodApplied;
        private float previousLodBias;
        private float lodRatio = 1f;

        // Whether MSAA is held off now, the sample count it replaced, and the count written (0).
        private bool msaaApplied;
        private int previousAntiAliasing;
        private int appliedAntiAliasing;

        // Whether anisotropic filtering is forced now, the setting it replaced, and the setting written.
        // KSP never writes it either. It comes with the quality level, and Reassert takes it over only where it
        // differs from ReDefinition's.
        private bool anisoApplied;
        private AnisotropicFiltering previousAnisotropic;
        private AnisotropicFiltering appliedAnisotropic;

        // The LOD bias written into QualitySettings; 0 while the bias is not ReDefinition's.
        // Unity picks LOD levels from how many pixels an object covers on screen.
        // At a lower render resolution the same object covers fewer pixels, so
        // Unity switches to coarser meshes earlier, as with the mipmap levels.
        public float AppliedLodBias { get; private set; }

        // Multiplies the LOD bias by Max(1, ratio) while wanted, and restores it otherwise.
        // At NativeAA the ratio is 1, so Max(1, ratio) is 1 and the bias is
        // multiplied by one: the switch changes nothing at AA only.
        // The factor is display height over render height. The mipmap bias is a
        // different quantity: FSR's own log2(render / display) - 1.
        // Affects everything with a LODGroup, so Parallax' scatter objects and
        // part of the part models. Not KSP's terrain, which subdivides its quads
        // by distance rather than by screen size. Parallax's tessellation, which
        // subdivides by pixels of the render target, goes with it
        // (ParallaxTessellation).
        public void SetLodBias(bool wanted, float ratio)
        {
            ParallaxTessellation.Scale(wanted, ratio);
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
        // stack has no MSAA; QualitySettings.antiAliasing is set off as well. KSP sets the quality level and then
        // MSAA, every time it applies its settings (GameSettings.ApplySettings: SetQualityLevel, then
        // antiAliasing = ANTI_ALIASING, decompiled).
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

        // After KSP applied its own settings: Apply or Accept in the settings
        // dialog, the main menu's settings screen.
        //
        // MSAA: a value in Unity other than ReDefinition's is what KSP has just
        // written. ReDefinition's value there is the player's choice only where KSP
        // keeps it as theirs too, GameSettings.ANTI_ALIASING: a player who switched
        // MSAA off while ReDefinition had it off too chose off. Otherwise nothing
        // wrote it. The event also comes from
        // ReDefinition's own follow-up of a change that leaves MSAA alone
        // (KspBehaviour.FollowUp), and a rig set up between KSP's write and the
        // event has saved what KSP wrote already.
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

        // Puts all three back (and Parallax's tessellation), as when the upscaler stops.
        public void RestoreAll()
        {
            SetLodBias(false, 1f);
            SetAntiAliasing(false);
            SetAnisotropic(false);
        }

        // A part of the rig's diagnostics line: each setting's value now, and what it replaced where it is ReDefinition's.
        public string Describe()
        {
            return "LOD bias " + (lodApplied
                       ? previousLodBias.ToString("0.##") + " -> " + AppliedLodBias.ToString("0.##")
                       : QualitySettings.lodBias.ToString("0.##") + " untouched")
                   + ", MSAA " + (msaaApplied ? "forced off (was " + previousAntiAliasing + "x)"
                                              : QualitySettings.antiAliasing + "x untouched")
                   + ", anisotropic " + (anisoApplied ? "forced" : QualitySettings.anisotropicFiltering + " untouched")
                   + ", " + ParallaxTessellation.Describe();
        }
    }
}
