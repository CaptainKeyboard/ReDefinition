namespace ReDefinition.Core
{
    // The player links the shared GameStubs.cs of the raytracing player (manifest B11), which defines a WeatherView of its own since T18, so that src/Common/Weather/WeatherView.cs can no longer
    // be linked beside it; the precipitation frame (C22) still needs this struct of that file, copied here.
    // The planner's assumption type, which the shared stubs name (src/Common/ForeignAssumption.cs needs the foreign-code checker, which the player does not link).
    internal sealed class ForeignAssumption
    {
    }

    public struct HydrometeorLayer
    {
        public float Width;
        public float Share;
        public float SizeMin;
        public float SizeMax;
        public float Opacity;
    }
}
