using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ReDefinition.Framework
{
    // Makes the profiles visible before anything applies them. Once, at the
    // main menu -- loading and ModuleManager's patches are done by then -- one
    // entry in the log lists every profile the GameDatabase holds, every problem
    // the reader found with them, and every PROFILE block of a registration for a
    // profile no REDEFINITION_PROFILE defines. A visual pack's author who writes
    // or patches a profile sees here whether it arrived as meant.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class ProfileReport : MonoBehaviour
    {
        private void Start()
        {
            try
            {
                List<string> problems = new List<string>();
                List<GraphicsProfile> profiles = ProfileLibrary.LoadAll(problems);

                StringBuilder sb = new StringBuilder();
                sb.Append(UpscalerProbe.Tag).Append(" Graphics profiles: ").Append(profiles.Count).Append(" found.");
                foreach (GraphicsProfile profile in profiles)
                {
                    sb.AppendLine().Append("  '").Append(profile.Name).Append("' (").Append(profile.Title).Append("): ")
                      .Append(profile.Modules.Count).Append(" module(s)");
                }
                List<string> names = new List<string>();
                foreach (GraphicsProfile profile in profiles) names.Add(profile.Name);
                // Built here where nothing has built them yet: with the bundling off,
                // nothing else needs them this early.
                IList<IBundledMod> mods = BundledSettings.Mods;
                if (BundledSettings.Built) problems.AddRange(ModProfiles.Unknown(mods, names));
                foreach (string problem in problems)
                    sb.AppendLine().Append("  Problem: ").Append(problem);
                string chosen = BundledSettings.ProfileName;
                sb.AppendLine().Append("  Chosen: ").Append(string.IsNullOrEmpty(chosen) ? "none yet" : "'" + chosen + "'")
                  .Append(BundledSettings.Enabled ? "." : ", but the bundling is off.");
                Debug.Log(sb.ToString());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(UpscalerProbe.Tag + " Graphics profiles could not be listed: " + e.Message);
            }

            Destroy(gameObject);
        }
    }
}
