using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

public sealed partial class CastingTests
{
    [Theory]
    [InlineData(true, false, 2, 20)]
    [InlineData(false, true, 2, 14)]
    [InlineData(false, false, 7, 20)]
    public void Commander_cost_permissions_still_pay_tax_and_are_spent_only_when_used(bool free, bool life, int generic, int remainingLife)
    {
        var table = new CastingTable();
        var id = table.Add(TestFaces.Creature("{5}{G}"), CastingTable.A, CardZone.Command);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, free || life ? 0 : 1, generic));
        var effect = new ActiveEffect(71,
            new MayPlayFromZone(new You(), CardZone.Command, WithoutPayingManaCost: free,
                PayLifeEqualToManaValueInstead: life, OncePerTurn: true),
            null, CastingTable.A, [], [CastingTable.A], new UntilEndOfTurn(), table.State.NextTimestamp, FromStaticAbility: true);
        table.Apply([new ContinuousEffectCreated(effect)]);
        var commander = table.State.Objects[id] with { IsCommander = true };
        var key = (new CommanderIdentity(commander.Owner, commander.Definition.OracleId), CastingTable.A);
        var state = table.State with
        {
            Objects = table.State.Objects.SetItem(id, commander),
            CommanderCasts = table.State.CommanderCasts.SetItem(key, 1),
        };
        var result = casting.Cast(state, new CastSpell(CastingTable.A, id));
        Assert.Equal(ManaPool.Empty, result.State.Player(CastingTable.A).Mana);
        Assert.Equal(remainingLife, result.State.Player(CastingTable.A).Life);
        Assert.Equal(free || life ? (int?)71 : null, Assert.Single(result.State.History.SpellsCast).Permission);
        Assert.Equal(2, result.State.CommandZoneCasts(commander, CastingTable.A));
        Assert.Equal(1, state.CommandZoneCasts(commander, CastingTable.A));
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 3, true)]
    [InlineData(2, 4, false)]
    [InlineData(2, 5, true)]
    public void Command_casts_pay_printed_cost_plus_two_per_previous_cast(int previous, int generic, bool payable)
    {
        var table = new CastingTable();
        var id = table.Add(TestFaces.Creature("{1}{G}"), CastingTable.A, CardZone.Command);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, generic));
        var commander = table.State.Objects[id] with { IsCommander = true };
        var key = (new CommanderIdentity(commander.Owner, commander.Definition.OracleId), CastingTable.A);
        var state = table.State with
        {
            Objects = table.State.Objects.SetItem(id, commander),
            CommanderCasts = table.State.CommanderCasts.SetItem(key, previous),
        };
        var command = new CastSpell(CastingTable.A, id);
        if (!payable)
        {
            Assert.Equal("601.2h", casting.Check(state, command)?.Rule);
            Assert.Equal(previous, state.CommandZoneCasts(commander, CastingTable.A));
            return;
        }

        var result = casting.Cast(state, command);
        Assert.Equal(ManaPool.Empty, result.State.Player(CastingTable.A).Mana);
        Assert.Equal(previous + 1, result.State.CommandZoneCasts(commander, CastingTable.A));
        Assert.Null(Assert.Single(result.State.History.SpellsCast).Permission);
    }

    [Fact]
    public void Command_permission_does_not_allow_noncommanders_or_override_creature_timing()
    {
        var table = new CastingTable();
        var id = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Command);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));
        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, id))?.Rule);
        table.MoveToStep(Step.Upkeep);
        var state = table.State with
        {
            Objects = table.State.Objects.SetItem(id, table.State.Objects[id] with { IsCommander = true }),
        };
        Assert.NotNull(casting.Check(state, new CastSpell(CastingTable.A, id)));
        Assert.NotNull(casting.Check(state, new CastSpell(CastingTable.B, id)));
        Assert.Empty(state.CommanderCasts);
    }
}
