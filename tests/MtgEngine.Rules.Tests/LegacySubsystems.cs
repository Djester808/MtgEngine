using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Tests;

/// <summary>Reduced composition for isolated legacy tests, never used by the application.</summary>
internal static class LegacySubsystems
{
    public static Subsystems Create(Func<string, bool>? enforced = null)
    {
        var layers = new Layers.LayerSystem();
        var query = new Query.Query(layers);
        var zones = new Zones.ZoneSystem { Characteristics = layers };
        var interpreter = new Stubs.Interpreter();
        var casting = new Casting.CastingSystem(zones, query, layers, interpreter);
        // CR 510.1 and 703.4m: combat damage is the turn system's action, so it needs the combat system.
        // CR 508.1h with 118.6: a cost to attack that is not mana is paid through the casting
        // system, which is the system built with the zone system a cost context needs.
        var combat = new Combat.CombatSystem(layers, query) { Casting = casting };
        var turn = new Turn.TurnSystem(zones, casting, combat, layers);
        var sba = new Sba.StateBasedActions(zones, layers, query);
        return new Subsystems(
            query, zones, turn, casting, combat, sba, new Stubs.Triggers(), layers, new Stubs.Replacement(), interpreter, new Stubs.Choices(),
            new MtgEngine.Rules.Views.ViewProjector(layers, enforced ?? (_ => true)),
            new MtgEngine.Rules.Legal.LegalActions(casting, layers, combat, combat));
    }
}
