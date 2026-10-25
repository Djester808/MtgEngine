using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed partial class InterpreterTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void A_search_privately_shows_nonmatching_cards_only_within_its_permitted_portion(bool bounded, int inspected)
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Card(table.B, Zone.Library);
        table.Put(table.B, ZoneKey.Of(Zone.Library, table.B), InterpreterTable.Definition(CardType.Land));
        var before = table.State;
        var events = table.Resolve(table.OnStack(table.A,
            new Search(ObjectFilter.Creature, Zone.Hand, Top: bounded ? new Fixed(1) : null,
                LibraryOf: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false)));
        var looked = Assert.Single(events.OfType<CardsLookedAt>());
        Assert.Equal(inspected, looked.Cards.Count);
        Assert.Equal(new[] { table.A }, looked.Viewers);
        Assert.Single(table.Pending.Options);
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var projector = new Views.ViewProjector(new LayerSystem(), _ => true);
        Assert.Equal(inspected, projector.Project(restored, table.A).LastLook!.Cards.Count);
        Assert.Equal(inspected, projector.Project(restored, table.A).InspectedLibraryCards.Count);
        Assert.Empty(projector.Project(restored, table.B).InspectedLibraryCards);
        Assert.Null(projector.Project(restored, table.B).LastLook);
        var completed = table.Answer(Option(table, table.In(Zone.Library, table.B)[0]));
        Assert.DoesNotContain(completed, e => e is CardsLookedAt);
        Assert.Empty(table.State.LibraryInspections);
        Assert.Empty(projector.Project(table.State, table.A).InspectedLibraryCards);
        Assert.NotNull(projector.Project(table.State, table.A).LastLook);
    }

    [Fact]
    public void A_search_over_a_zone_union_offers_each_library_card_and_records_the_search_once()
    {
        var table = new InterpreterTable();
        Stocked(table);
        var events = table.Resolve(table.OnStack(table.A,
            new Search(ObjectFilter.Any, Zone.Hand, Zones: [Zone.Library, Zone.Library])));
        Assert.Single(events.OfType<LibrarySearched>());
        Assert.Equal(2, table.Pending.Options.Count);
        Assert.Equal(2, table.Pending.Options.Select(option => option.Id).Distinct().Count());
    }

    [Fact]
    public void A_qualified_opponent_library_search_can_find_nothing_after_reload()
    {
        var table = new InterpreterTable(new LayerSystem());
        var own = table.Card(table.A, Zone.Library);
        var match = table.Put(table.B, ZoneKey.Of(Zone.Library, table.B), InterpreterTable.Definition(CardType.Land));
        var spell = table.OnStack(table.A,
            new Search(new ObjectFilter(Types: CardType.Land), Zone.Exile,
                LibraryOf: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false));
        var before = table.State;
        var events = table.Resolve(spell);
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var pending = Assert.IsType<PendingChoice>(restored.Pending);
        Assert.Equal(table.A, pending.Player);
        Assert.Equal(0, pending.Min);
        Assert.Equal(match, Assert.Single(pending.Options).Object);
        var engine = Subsystems.Wave2();
        Assert.Empty(engine.Views.Project(restored, table.B).ChoiceCards);
        var answer = new Commands.AnswerChoice(table.A, pending.Id, []);
        var result = engine.Choices.Answer(restored, answer);
        var final = GameReducer.Apply(restored, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.Equal(ZoneKey.Of(Zone.Library, table.B), Assert.Single(result.Events.OfType<ZoneReordered>(), e => e.Shuffle).Zone);
        Assert.Equal(own, Assert.Single(final.In(ZoneKey.Of(Zone.Library, table.A))));
        Assert.Equal(match, Assert.Single(final.In(ZoneKey.Of(Zone.Library, table.B))));
        Assert.Empty(final.In(ZoneKey.Shared(Zone.Exile)));
        Assert.Null(final.Pending);
        Assert.Empty(final.LibraryInspections);
        Assert.Empty(engine.Views.Project(final, table.A).InspectedLibraryCards);
    }

    [Fact]
    public void Searching_another_library_keeps_choices_private_and_shuffles_that_library()
    {
        var table = new InterpreterTable(new LayerSystem());
        var own = table.Card(table.A, Zone.Library);
        var selected = table.Card(table.B, Zone.Library);
        table.Card(table.B, Zone.Library);
        var spell = table.OnStack(table.A,
            new Search(ObjectFilter.Any, Zone.Exile, LibraryOf: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false));
        var before = table.State;
        var events = table.Resolve(spell);
        var searched = Assert.Single(events.OfType<LibrarySearched>());
        Assert.Equal(table.B, searched.Player);
        Assert.Equal(table.A, searched.Searcher);
        Assert.Equal(table.A, table.Pending.Player);
        Assert.Equal(2, table.Pending.Options.Count);
        Assert.DoesNotContain(table.Pending.Options, option => option.Object == own);
        var projector = new Views.ViewProjector(new LayerSystem(), _ => true);
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.Equal(2, projector.Project(restored, table.A).ChoiceCards.Count);
        Assert.Empty(projector.Project(restored, table.B).ChoiceCards);
        var answer = table.Answer(table.Pending.Options.Single(option => option.Object == selected).Id);
        Assert.Equal(ZoneKey.Of(Zone.Library, table.B), Assert.Single(answer.OfType<ZoneReordered>(), e => e.Shuffle).Zone);
        Assert.Equal(own, Assert.Single(table.In(Zone.Library, table.A)));
        Assert.Single(table.In(Zone.Library, table.B));
        Assert.Null(table.State.Pending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Another_library_search_checks_the_searchers_prohibition_not_the_owners(bool blockSearcher)
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Card(table.B, Zone.Library);
        table.Put(blockSearcher ? table.A : table.B, ZoneKey.Shared(Zone.Battlefield),
            InterpreterTable.Definition(CardType.Enchantment, new StaticAbility(new CantSearchLibraries(new You()))));
        var events = table.Resolve(table.OnStack(table.A,
            new Search(ObjectFilter.Any, Zone.Exile, LibraryOf: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false)));
        if (blockSearcher)
        {
            Assert.Null(table.State.Pending);
            Assert.DoesNotContain(events, e => e is LibrarySearched);
            Assert.Equal(ZoneKey.Of(Zone.Library, table.B), Assert.Single(events.OfType<ZoneReordered>(), e => e.Shuffle).Zone);
        }
        else
        {
            Assert.Equal(table.A, table.Pending.Player);
            Assert.Single(table.Pending.Options);
            Assert.Single(events.OfType<LibrarySearched>());
        }
    }

    [Theory]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Another_library_search_orders_the_selected_card_in_its_owners_library(Placement placement)
    {
        var table = new InterpreterTable(new LayerSystem());
        var own = table.Card(table.A, Zone.Library);
        var selected = table.Card(table.B, Zone.Library);
        table.Card(table.B, Zone.Library);
        table.Resolve(table.OnStack(table.A,
            new Search(ObjectFilter.Any, Zone.Library, Where: placement, LibraryOf: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false)));
        var events = table.Answer(Option(table, selected));
        var library = table.In(Zone.Library, table.B);
        Assert.Equal(selected, placement == Placement.Top ? library[0] : library[^1]);
        Assert.Equal(2, library.Count);
        Assert.Equal(own, Assert.Single(table.In(Zone.Library, table.A)));
        Assert.DoesNotContain(events, e => e is ObjectMoved);
    }

    [Fact]
    public void A_resolved_library_draw_limit_freezes_its_amount_and_applies_only_to_its_player()
    {
        var layers = new LayerSystem();
        var table = new InterpreterTable(layers);
        var before = table.Life(table.A);
        table.Run(new Apply(new CantDrawMoreThan(new You(), new LifeOf(new You())), new Self(), new UntilEndOfTurn()));
        table.Run(new GainLife(new Fixed(3)));
        Assert.Equal(before, layers.PlayerRestrictions(table.State, table.A).MaximumDrawsPerTurn);
        Assert.Null(layers.PlayerRestrictions(table.State, table.B).MaximumDrawsPerTurn);
        Assert.Null(layers.PlayerRestrictions(table.State with { ActiveEffects = [] }, table.A).MaximumDrawsPerTurn);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 2)]
    public void Bounded_search_exposes_only_available_top_cards_to_its_searcher(int count, int expected)
    {
        var table = new InterpreterTable(new LayerSystem());
        Stocked(table);
        table.Resolve(table.OnStack(table.A, new Search(ObjectFilter.Any, Zone.Hand, Top: new Fixed(count))));
        Assert.Equal(expected, table.Pending.Options.Count);
        var projector = new Views.ViewProjector(new LayerSystem(), _ => true);
        Assert.Equal(expected, projector.Project(table.State, table.A).ChoiceCards.Count);
        Assert.Empty(projector.Project(table.State, table.B).ChoiceCards);
    }

    [Fact]
    public void A_top_card_search_limits_the_library_before_applying_the_card_filter()
    {
        var table = new InterpreterTable(new LayerSystem());
        var (land, creature) = Stocked(table);
        table.Resolve(table.OnStack(table.A, new Search(ObjectFilter.Creature, Zone.Hand, Top: new Fixed(1))));
        Assert.Equal(land, table.In(Zone.Library, table.A)[0]);
        Assert.Empty(table.Pending.Options);
        var events = table.Answer();
        Assert.Contains(creature, table.In(Zone.Library, table.A));
        Assert.Empty(table.In(Zone.Hand, table.A));
        Assert.Single(events.OfType<ZoneReordered>(), e => e.Shuffle);
    }

    [Fact]
    public void Automatic_decline_survives_a_later_prompt_without_blocking_other_optional_actions()
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Run(new Apply(new CantSearchLibraries(new You()), new Self(), new UntilEndOfTurn()));
        table.Resolve(table.OnStack(table.A, new Sequence([
            new May(new Search(ObjectFilter.Any, Zone.Hand), StoreChoiceAs: "searched"),
            new May(new GainLife(new Fixed(1))),
            new If(new ChoiceIs("searched", "no"), new GainLife(new Fixed(2))),
        ])));
        Assert.Equal("may", table.Pending.Kind);
        Assert.Equal("no", table.Pending.Context.Extra["answered:searched"]);
        var events = table.Answer("yes");
        Assert.Null(table.State.Pending);
        Assert.Equal(23, table.Life(table.A));
        Assert.DoesNotContain(events, e => e is LibrarySearched or ZoneReordered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_prohibited_optional_search_does_not_offer_yes_or_shuffle(bool separateShuffle)
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new CantSearchLibraries(new You()))));
        table.Card(table.A, Zone.Library);
        Effect optional = separateShuffle
            ? new Sequence([new Search(ObjectFilter.Any, Zone.Hand, ShuffleAfter: false), new Shuffle()])
            : new Search(ObjectFilter.Any, Zone.Hand);
        var events = table.Resolve(table.OnStack(table.A, new Sequence([
            new May(optional, StoreChoiceAs: "searched"), new GainLife(new Fixed(1)),
        ])));
        Assert.Null(table.State.Pending);
        Assert.DoesNotContain(events, e => e is LibrarySearched or ZoneReordered);
        Assert.Equal(21, table.Life(table.A));
        Assert.Single(table.In(Zone.Library, table.A));
        Assert.Empty(table.In(Zone.Hand, table.A));
    }

    [Fact]
    public void Resolving_a_search_prohibition_scopes_it_to_its_player_and_removing_it_restores_access()
    {
        var layers = new LayerSystem();
        var table = new InterpreterTable(layers);
        table.Run(new Apply(new CantSearchLibraries(new You()), new Self(), new UntilEndOfTurn()));
        Assert.True(layers.PlayerRestrictions(table.State, table.A).CantSearchLibraries);
        Assert.False(layers.PlayerRestrictions(table.State, table.B).CantSearchLibraries);
        Assert.False(layers.PlayerRestrictions(table.State with { ActiveEffects = [] }, table.A).CantSearchLibraries);
    }

    [Fact]
    public void A_mixed_zone_search_offers_only_the_accessible_zone()
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new CantSearchLibraries(new You()))));
        table.Card(table.A, Zone.Library);
        var available = table.Card(table.A, Zone.Graveyard);
        var events = table.Resolve(table.OnStack(table.A,
            new Search(ObjectFilter.Any, Zone.Hand, Zones: [Zone.Library, Zone.Graveyard])));
        Assert.Equal(available, Assert.Single(table.Pending.Options).Object);
        Assert.DoesNotContain(events, e => e is LibrarySearched);
    }

    [Theory]
    [InlineData(Zone.Library)]
    [InlineData(Zone.Graveyard)]
    public void Search_prohibition_blocks_only_library_access_and_preserves_following_instructions(Zone from)
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new CantSearchLibraries(new You()))));
        table.Card(table.A, from);
        var events = table.Resolve(table.OnStack(table.A, new Sequence([
            new Search(ObjectFilter.Any, Zone.Hand, From: from), new GainLife(new Fixed(1)),
        ])));
        if (from == Zone.Library)
        {
            Assert.Null(table.State.Pending);
            Assert.DoesNotContain(events, e => e is LibrarySearched);
            Assert.Single(events.OfType<ZoneReordered>(), e => e.Shuffle);
            Assert.Empty(table.In(Zone.Hand, table.A));
            Assert.Equal(21, table.Life(table.A));
        }
        else
        {
            Assert.Equal("search", table.Pending.Kind);
            Assert.Single(table.Pending.Options);
        }
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    public void Finding_nothing_obeys_the_shuffle_instruction_and_continues(bool shuffleAfter, bool onlyIfFound, int shuffles)
    {
        var table = new InterpreterTable();
        Stocked(table);
        var original = table.In(Zone.Library, table.A);
        table.Resolve(table.OnStack(table.A, new Sequence([
            new Search(new ObjectFilter(Types: CardType.Land), Zone.Hand,
                ShuffleAfter: shuffleAfter, ShuffleOnlyIfFound: onlyIfFound),
            new GainLife(new Fixed(2)),
        ])));
        Assert.Equal(0, table.Pending.Min);
        var events = table.Answer();
        Assert.Equal(shuffles, events.OfType<ZoneReordered>().Count(e => e.Shuffle && e.Zone == ZoneKey.Of(Zone.Library, table.A)));
        Assert.Equal(original.OrderBy(id => id.Value), table.In(Zone.Library, table.A).OrderBy(id => id.Value));
        Assert.DoesNotContain(events, e => e is ObjectMoved);
        Assert.Empty(table.In(Zone.Hand, table.A));
        Assert.Null(table.State.Pending);
        Assert.Equal(22, table.Life(table.A));
        Assert.Single(events.OfType<StackObjectResolved>());
    }

    [Fact]
    public void An_unqualified_library_search_requires_finding_an_available_card()
    {
        var table = new InterpreterTable();
        Stocked(table);
        table.Resolve(table.OnStack(table.A, new Search(ObjectFilter.Any, Zone.Hand)));
        Assert.Equal(1, table.Pending.Min);
        Assert.Equal(1, table.Pending.Max);
    }

    [Fact]
    [RuleRef("701.23d")]
    public void An_unqualified_search_refuses_an_empty_answer_after_replay_and_can_still_complete()
    {
        var table = new InterpreterTable(new LayerSystem());
        var card = table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Search(ObjectFilter.Any, Zone.Hand));
        var before = table.State;
        var events = table.Resolve(spell);
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var pending = Assert.IsType<PendingChoice>(restored.Pending);
        var engine = Subsystems.Wave2();

        Assert.Throws<RefusedException>(() =>
            engine.Choices.Answer(restored, new Commands.AnswerChoice(table.A, pending.Id, [])));
        Assert.Same(pending, restored.Pending);
        Assert.Equal(card, Assert.Single(restored.In(ZoneKey.Of(Zone.Library, table.A))));
        Assert.Empty(restored.In(ZoneKey.Of(Zone.Hand, table.A)));

        var result = engine.Choices.Answer(restored,
            new Commands.AnswerChoice(table.A, pending.Id, [Assert.Single(pending.Options).Id]));
        var final = GameReducer.Apply(restored, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        var moved = Assert.Single(result.Events.OfType<ObjectMoved>(), move => move.From == card);
        Assert.Equal(ZoneKey.Of(Zone.Hand, table.A), moved.To.Zone);
        Assert.Equal(moved.To.Id, Assert.Single(final.In(ZoneKey.Of(Zone.Hand, table.A))));
        Assert.Empty(final.In(ZoneKey.Of(Zone.Library, table.A)));
        Assert.Null(final.Pending);
        Assert.Empty(final.LibraryInspections);
        Assert.Single(result.Events.OfType<ChoiceAnswered>());
    }

    [Fact]
    public void A_qualified_library_search_may_find_nothing_even_when_a_match_exists()
    {
        var table = new InterpreterTable();
        Stocked(table);
        table.Resolve(table.OnStack(table.A, new Search(new ObjectFilter(Types: CardType.Land), Zone.Hand)));
        Assert.NotEmpty(table.Pending.Options);
        Assert.Equal(0, table.Pending.Min);
    }

    [Fact]
    public void Searching_for_up_to_zero_does_not_offer_one_card()
    {
        var table = new InterpreterTable();
        Stocked(table);
        table.Resolve(table.OnStack(table.A, new Search(ObjectFilter.Any, Zone.Hand, UpTo: new Fixed(0))));
        Assert.Equal(0, table.Pending.Max);
    }
}
