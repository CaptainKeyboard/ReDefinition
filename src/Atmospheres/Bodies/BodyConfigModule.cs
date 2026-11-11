namespace ReDefinition.Atmospheres
{
    // Puts the REDEFINITION_BODY reader in place of the generic default provider before any module asks for a body's climate. Without
    // it every body reads the generic rules and a ModuleManager patch of a body rule (the AURORA rule of the home planet) never reaches
    // the climate (series K-W3b: D38.body_rule_enabled 0 although the log showed the patch applied). The nodes are read lazily on the
    // first Get, after ModuleManager.
    [AtmospheresModule(1)]
    internal sealed class BodyConfigModule : IAtmospheresModule
    {
        public void Init()
        {
            BodyConfigProvider.Install();
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
        }

        public void Shutdown()
        {
            Bodies.Provider = null;
        }
    }
}
