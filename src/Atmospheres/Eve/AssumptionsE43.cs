using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the layer wind steering (package E43) takes for granted about the TVC build of EVE (Atmosphere.dll 3.2.2.0 as the painter's entry, AssumptionsC23):
    //   - CloudsRaymarchedVolume.upwardsCloudSpeed is a float that EVE reads every frame (UpdateNoiseOffsets), so a change is live
    //     (.local/plans/eve-findings.md section 2);
    //   - CloudsPQS.mainPeriod / offset are the layer's drift (entry of C23).
    // The altitude of a TVC layer is fixed at load; nothing here moves a layer in height.
    [AtmospheresAssumptions]
    internal static class AssumptionsE43
    {
        internal const string Id = "atmo-e43-layer-wind";

        private const string Volume = "Atmosphere.CloudsRaymarchedVolume";

        public static readonly ForeignAssumption LayerWind =
            new ForeignAssumption(Id, "EVE", "the vertical speed of the clouds of a volumetric layer follows the updraft of the weather field")
                .Type(Volume)
                .Field(Volume, "upwardsCloudSpeed", "System.Single")
                .OnChange(WhenChanged.Fallback, "upwardsCloudSpeed stays as EVE's own config has it; the drift of the layers is not affected");
    }
}
