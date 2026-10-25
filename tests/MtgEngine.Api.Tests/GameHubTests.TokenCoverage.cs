using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Fact]
    public async Task Resumed_generated_tokens_use_the_same_coverage_for_projection_and_manual_action_guard()
    {
        var pool = Deck(ImplementationStatus.Verified);
        var table = await TableAsync(allowHonourSystem: false, status: ImplementationStatus.Verified, pool: pool);
        var tokenId = new ObjectId(table.Session.State.NextObjectId);
        var created = new ZoneSystem().CreateToken(table.Session.State,
            new TokenSpec("Saproling", Color.Green, CardType.Creature, Power: 1, Toughness: 1),
            new PlayerId(0), false, false);
        table.Store.Rows[table.Session.Id].LogJson = EventLogSerializer.Write(table.Session.Log.AddRange(created.Events));
        var sessions = new GameSessionService(table.Store, pool, new DeckGate(pool));
        var resumed = Assert.IsType<GameSession>(await sessions.FindAsync(table.Session.Id));

        Assert.True(GameSessionService.IsEnforced(resumed, tokenId));
        var (view, _) = resumed.ProjectFor(new PlayerId(0));
        Assert.NotNull(view);
        Assert.True(Assert.Single(view.Battlefield).Enforced);
    }
}
