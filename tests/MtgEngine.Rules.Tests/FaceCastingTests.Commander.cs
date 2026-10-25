using MtgEngine.Rules.Commands;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

public sealed partial class FaceCastingTests
{
    [Theory]
    [InlineData(1, 0, 6)]
    [InlineData(1, 2, 4)]
    [InlineData(1, 5, 1)]
    [InlineData(1, 7, 0)]
    [InlineData(2, 5, 3)]
    public void Affinity_reduces_the_combined_printed_cost_and_commander_tax(int priorCasts, int artifacts, int expectedCost)
    {
        var table = new CastingTable();
        var id = table.Add(FaceCastingFaces.Affinity("{4}"), CastingTable.A, CardZone.Command);
        for (var i = 0; i < artifacts; i++)
            table.Add(FaceCastingFaces.Trinket(), CastingTable.A, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, expectedCost));
        var commander = table.State.Objects[id] with { IsCommander = true };
        var key = (new CommanderIdentity(commander.Owner, commander.Definition.OracleId), CastingTable.A);
        var state = table.State with
        {
            Objects = table.State.Objects.SetItem(id, commander),
            CommanderCasts = table.State.CommanderCasts.SetItem(key, priorCasts),
        };

        if (expectedCost > 0)
        {
            var shortPool = state.Player(CastingTable.A) with { Mana = new ManaPool(0, 0, 0, 0, 0, expectedCost - 1) };
            Assert.Equal("601.2h", casting.Check(state with { Players = state.Players.SetItem(0, shortPool) }, new CastSpell(CastingTable.A, id))?.Rule);
        }

        var result = casting.Cast(state, new CastSpell(CastingTable.A, id));
        Assert.Equal(ManaPool.Empty, result.State.Player(CastingTable.A).Mana);
        Assert.Equal(priorCasts + 1, result.State.CommandZoneCasts(commander, CastingTable.A));
    }
}
