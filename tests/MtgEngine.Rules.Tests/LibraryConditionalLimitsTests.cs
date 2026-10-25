using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryConditionalLimitsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conditional_top_visibility_is_revoked_and_private_access_never_reaches_an_opponent(bool publicReveal)
    {
        var layers = new LayerSystem();
        var table = new InterpreterTable(layers);
        ContinuousEffect permission = publicReveal
            ? new PlayWithTopCardRevealed(new You()) : new MayLookAtTopCard(new You());
        var source = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(permission, While: HasArtifact())));
        table.Card(table.A, Zone.Library);
        var projector = new ViewProjector(layers, _ => true);
        Assert.Null(projector.Project(table.State, table.A).Players[0].LookableTopCard);
        Assert.Null(projector.Project(table.State, table.A).Players[0].RevealedTopCard);
        var artifact = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Artifact));
        var owner = projector.Project(table.State, table.A).Players[0];
        Assert.NotNull(publicReveal ? owner.RevealedTopCard : owner.LookableTopCard);
        var opponent = projector.Project(table.State, table.B).Players[0];
        Assert.Null(opponent.LookableTopCard);
        Assert.Equal(publicReveal, opponent.RevealedTopCard is not null);
        foreach (var changed in new[]
        {
            table.State with { Objects = table.State.Objects.SetItem(artifact, table.State.Objects[artifact] with { InitialController = table.B }) },
            table.State with { Objects = table.State.Objects.SetItem(source, table.State.Objects[source] with { FaceDown = true }) },
            table.State with { Objects = table.State.Objects.SetItem(source, table.State.Objects[source] with { PhasedOut = true }) },
        })
        {
            foreach (var viewer in new[] { table.A, table.B })
            {
                var hidden = projector.Project(changed, viewer).Players[0];
                Assert.Null(hidden.LookableTopCard);
                Assert.Null(hidden.RevealedTopCard);
            }
        }
    }

    [Fact]
    public void Conditional_search_prohibition_blocks_only_the_affected_players_library_access()
    {
        var layers = new LayerSystem();
        var table = new InterpreterTable(layers);
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new CantSearchLibraries(new You()), While: HasArtifact())));
        var planner = new LibrarySearchPlanner(new MtgEngine.Rules.Query.Query(layers), layers);
        var search = new Search(ObjectFilter.Any, Zone.Hand, Zones: [Zone.Library, Zone.Graveyard]);
        Assert.Contains(Zone.Library, planner.AccessibleZones(table.State, search, table.A));
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Artifact));
        Assert.Equal([Zone.Graveyard], planner.AccessibleZones(table.State, search, table.A));
        Assert.Contains(Zone.Library, planner.AccessibleZones(table.State, search, table.B));
    }

    private static Condition HasArtifact() => new Controls(new You(), new ObjectFilter(Types: CardType.Artifact),
        new Comparison(ComparisonOp.Ge, new Fixed(1)));

    [Fact]
    public void Conditional_draw_limit_tracks_the_sources_controller_and_current_board()
    {
        var layers = new LayerSystem();
        var table = new InterpreterTable(layers);
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new CantDrawMoreThan(new You(), new Fixed(0)),
                While: new Controls(new You(), new ObjectFilter(Types: CardType.Artifact),
                    new Comparison(ComparisonOp.Ge, new Fixed(1))))));
        Assert.Null(layers.PlayerRestrictions(table.State, table.A).MaximumDrawsPerTurn);
        table.Card(table.A, Zone.Library);
        var artifact = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Artifact));
        Assert.Equal(0, layers.PlayerRestrictions(table.State, table.A).MaximumDrawsPerTurn);
        Assert.Null(layers.PlayerRestrictions(table.State, table.B).MaximumDrawsPerTurn);
        var blocked = Subsystems.Wave2().Zones.Draw(table.State, table.A, 1);
        Assert.Empty(blocked.State.In(ZoneKey.Of(Zone.Hand, table.A)));
        Assert.Single(blocked.State.In(ZoneKey.Of(Zone.Library, table.A)));
        var changed = table.State with
        {
            Objects = table.State.Objects.SetItem(artifact, table.State.Objects[artifact] with { InitialController = table.B }),
        };
        Assert.Null(layers.PlayerRestrictions(changed, table.A).MaximumDrawsPerTurn);
        var allowed = Subsystems.Wave2().Zones.Draw(changed, table.A, 1);
        Assert.Single(allowed.State.In(ZoneKey.Of(Zone.Hand, table.A)));
        Assert.Empty(allowed.State.In(ZoneKey.Of(Zone.Library, table.A)));
    }
}
