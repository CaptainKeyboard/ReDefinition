using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B13
{
    // The season mode through the season state: the climates of a tilted and of an untilted body, every mode, both hemispheres.
    [TestClass]
    public class SeasonModeTests
    {
        private static BodyClimate Climate(double tilt)
        {
            BodyFacts f = BodyTestSupport.Planet("Body", BodyTestSupport.HomeYear);
            f.AxisFromReferenceDeg = tilt > 0 ? tilt : 0.0;
            f.ObliquityDeg = tilt;
            BodyClimate c = BodyTestSupport.Run(f).Climate;
            Assert.AreEqual(tilt, c.Seasons.TiltDegrees, "the climate carries the tilt");
            return c;
        }

        [TestMethod]
        public void TheStateGivesTheModesPhaseAtTheLatitude()
        {
            BodyClimate tilted = Climate(25);
            BodyClimate flat = Climate(0);
            for (int i = 0; i < 100; i++)
            {
                double ut = i * BodyTestSupport.HomeYear / 25.0;
                foreach (double lat in new[] { 10.0, 50.0 })
                {
                    Assert.AreEqual(0.5, SeasonState.Phase(tilted, ut, lat, SeasonMode.GlobalSummer).Value, 1e-12);
                    Assert.AreEqual(0.5, SeasonState.Phase(tilted, ut, -lat, SeasonMode.GlobalSummer).Value, 1e-12);
                    Assert.AreEqual(0.0, SeasonState.Phase(tilted, ut, lat, SeasonMode.GlobalWinter).Value, 1e-12);
                    Assert.AreEqual(0.0, SeasonState.Phase(tilted, ut, -lat, SeasonMode.GlobalWinter).Value, 1e-12);
                    Assert.AreEqual(SeasonState.Phase(tilted, ut, lat, SeasonMode.GlobalSeasons).Value, SeasonState.Phase(tilted, ut, -lat, SeasonMode.GlobalSeasons).Value, 1e-12);
                    Assert.AreEqual(0.5, SeasonClock.PhaseDistance(SeasonState.Phase(tilted, ut, lat, SeasonMode.RealSeasons).Value, SeasonState.Phase(tilted, ut, -lat, SeasonMode.RealSeasons).Value), 1e-9);
                    Assert.AreEqual(SeasonState.Phase(flat, ut, lat, SeasonMode.GlobalSeasons).Value, SeasonState.Phase(flat, ut, -lat, SeasonMode.GlobalSeasons).Value, 1e-12);
                }
            }
        }

        [TestMethod]
        public void TheTimeIsRequiredAndTheForcedPhaseWinsOverTheMode()
        {
            BodyClimate tilted = Climate(25);
            Assert.IsNull(SeasonState.Phase(tilted, null, 30, SeasonMode.RealSeasons), "no time, no phase");
            float forced = AtmospheresSettings.ForcePhase;
            try
            {
                AtmospheresSettings.ForcePhase = 0.3f;
                foreach (SeasonMode mode in Enum.GetValues(typeof(SeasonMode)))
                    Assert.AreEqual(0.3, SeasonState.Phase(tilted, 1e6, -30, mode).Value, 1e-6, mode + ": forcePhase is the debug override");
            }
            finally
            {
                AtmospheresSettings.ForcePhase = forced;
            }
        }
    }
}
