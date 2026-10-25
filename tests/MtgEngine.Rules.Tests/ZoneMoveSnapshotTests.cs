using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class ZoneMoveSnapshotTests
{
    [Fact]
    public void Snapshot_preserves_compound_keys_and_uses_live_identity_counters()
    {
        var table = new InterpreterTable();
        var commander = new CommanderIdentity(table.A, "fixture", new CardInstanceId(7));
        var source = new ObjectId(11);
        var state = table.State with
        {
            CommanderCasts = table.State.CommanderCasts.Add((commander, table.B), 2),
            Players = table.State.Players.SetItem(0, table.State.Player(table.A) with
            {
                CommanderDamage = table.State.Player(table.A).CommanderDamage.Add(commander, 14),
            }),
            History = table.State.History with
            {
                DamageDealtToPlayer = table.State.History.DamageDealtToPlayer.Add((source, table.B), 3),
                AbilitiesResolvedThisTurn = table.State.History.AbilitiesResolvedThisTurn.Add((source, 2), 1),
            },
        };
        var current = table.State with { NextObjectId = 99, NextTimestamp = 100, NextChoiceId = 101 };
        var restored = ZoneMoveSnapshot.Evaluate(ZoneMoveSnapshot.Capture(state), current);
        Assert.Equal(2, restored.CommanderCasts[(commander, table.B)]);
        Assert.Equal(14, restored.Player(table.A).CommanderDamage[commander]);
        Assert.Equal(3, restored.History.DamageDealtToPlayer[(source, table.B)]);
        Assert.Equal(1, restored.History.AbilitiesResolvedThisTurn[(source, 2)]);
        Assert.Equal(99, restored.NextObjectId);
        Assert.Equal(100, restored.NextTimestamp);
        Assert.Equal(101, restored.NextChoiceId);
        Assert.Null(restored.Pending);
    }
}
