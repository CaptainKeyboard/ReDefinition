namespace ReDefinition.Autopilot.Planning
{
    // How far along its route a vessel has come: the last stop it has reached, for the route it was counted for. A stop the route comes back to,
    // as a round trip ends where it began, is reached only after the stop before it, so that a vessel on the pad is at the start and not at the end.
    internal sealed class RouteProgress
    {
        // The last stop reached, by its index in the route; -1 before the first.
        public int Reached = -1;
        // The route it was counted for (RoutePlan.SignatureOf): another route starts the count again.
        public string Key = "";
    }
}
