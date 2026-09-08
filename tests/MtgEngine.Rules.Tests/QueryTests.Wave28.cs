using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 28's readings: the two keys a firing carries about a prevention (CR 615.13) and about the
/// payment for the spell it watched (CR 400.7d), and V16's four amounts and its negated keyword —
/// the greatest damage one player took (CR 102.2), the creatures that died (CR 700.4), the running
/// prevention's own amount (CR 615), the largest group sharing a name (CR 201.2) and
/// <c>ObjectFilter.NotHas</c> (CR 702.1). The world is the one <see cref="QueryTests"/> declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("615.13")]
    public void The_prevented_amount_is_read_from_its_own_key_and_never_from_the_damage_dealt()
    {
        // CR 615.13 with 603.10: a shield that stops 2 of 5 leaves 3 dealt. Two numbers on one
        // event, two keys, and "that much" is the prevented one.
        var both = World.New()
            .Choose(TriggerSystem.TriggeringPreventedDamageAmount, "2")
            .Choose(TriggerSystem.TriggeringDamageAmount, "3");
        Assert.Equal(2, both.Value(new TriggeringPreventedDamage()));
        Assert.Equal(3, both.Value(new TriggeringDamage()));

        // The fallback this reader must not have. A firing on a damage event writes only the dealt
        // key; reading it for "that much damage prevented" would deal 3 for a prevention that never
        // happened — and for damage prevented in full there is no dealt key at all, so the wrong
        // reading is silent in both directions.
        var dealtOnly = World.New().Choose(TriggerSystem.TriggeringDamageAmount, "3");
        Assert.Equal(0, dealtOnly.Value(new TriggeringPreventedDamage()));
    }

    [Fact]
    [RuleRef("400.7d")]
    public void An_absent_payment_key_is_a_firing_that_watched_no_cast_and_not_a_payment_of_zero()
    {
        // CR 400.7d: the ability references what was spent to cast the spell that triggered it. No
        // total key at all is a firing that watched no cast — refused, because "if no mana was spent
        // to cast it" is a card, and answering 0 would play that card off every other trigger.
        Assert.Throws<RefusedException>(() => World.New().Value(new ManaSpentOnTriggering()));
        Assert.Throws<RefusedException>(() => World.New().Value(new ManaSpentOnTriggering(Color.Red)));

        // The total present and zero is the other sentence, and it answers 0.
        var free = World.New().Choose(TriggerSystem.ManaSpentOnTriggeringAmount, "0");
        Assert.Equal(0, free.Value(new ManaSpentOnTriggering()));

        // A colour nobody paid has no key of its own; the total's presence is what proves the cast
        // was watched, so the colour reads the honest zero rather than refusing.
        var paid = World.New()
            .Choose(TriggerSystem.ManaSpentOnTriggeringAmount, "3")
            .Choose(TriggerSystem.ManaSpentOnTriggeringColorPrefix + Color.Red, "2");
        Assert.Equal(3, paid.Value(new ManaSpentOnTriggering()));
        Assert.Equal(2, paid.Value(new ManaSpentOnTriggering(Color.Red)));
        Assert.Equal(0, paid.Value(new ManaSpentOnTriggering(Color.Green)));
    }

    [Fact]
    [RuleRef("400.7d")]
    public void The_payment_read_is_the_triggering_spells_and_never_this_spells_own()
    {
        // CR 400.7 with 601.2h: ManaSpentOnThis reads the entry of the object the ability is on, and
        // the triggering spell has usually resolved and ceased to exist by then. The two amounts
        // answer about different spells, so neither reader falls back to the other's source.
        var world = World.New()
            .Spell(1, Alice, copy: false)
            .As(1)
            .Choose(TriggerSystem.ManaSpentOnTriggeringAmount, "5");
        Assert.Equal(5, world.Value(new ManaSpentOnTriggering()));
        Assert.Equal(0, world.Value(new ManaSpentOnThis()));
    }

    [Fact]
    [RuleRef("615")]
    public void A_running_preventions_own_amount_is_refused_until_the_replacement_road_carries_it()
    {
        // CR 615: "for each 1 damage prevented this way" is a number the prevention knows as it
        // applies. Nothing writes it yet (seam-requests/E5l.md), and 0 is a legal answer — a shield
        // that met no damage prevented none — so an absent key must not look like that answer.
        Assert.Throws<RefusedException>(() => World.New().Value(new PreventedDamage()));

        // It is not the trigger's key either: TriggeringPreventedDamage is somebody else's
        // prevention read afterwards, and reading it inside the replacement would read an event that
        // has not happened.
        var trigger = World.New().Choose(TriggerSystem.TriggeringPreventedDamageAmount, "4");
        Assert.Throws<RefusedException>(() => trigger.Value(new PreventedDamage()));

        var carried = World.New().Choose(MtgEngine.Rules.Query.Query.PreventedDamageAmount, "2");
        Assert.Equal(2, carried.Value(new PreventedDamage()));
        Assert.Equal(0, World.New().Choose(MtgEngine.Rules.Query.Query.PreventedDamageAmount, "0").Value(new PreventedDamage()));
    }

    [Fact]
    [RuleRef("102.2")]
    public void The_greatest_damage_dealt_to_a_player_this_turn_is_a_maximum_and_never_a_total()
    {
        // CR 102.2 with 120.3: "if an opponent was dealt 7 or more damage this turn" asks whether
        // one of them was. Four and three across two opponents is seven damage and no such opponent
        // — and in a duel the sum and the max agree, so only a three-player world tells them apart.
        var split = E5lDamaged(E5lDamaged(World.New(3), Bob, 4), new PlayerId(2), 3);
        var opponents = new GreatestDamageDealtThisTurn(PlayerFilter.Opponents);
        Assert.Equal(4, split.Value(opponents));

        // One opponent reaching the number is the whole sentence, whichever of them it is.
        Assert.Equal(7, E5lDamaged(split, new PlayerId(2), 7).Value(opponents));

        // Your own damage is not an opponent's, and a group nobody is in was dealt nothing.
        Assert.Equal(0, E5lDamaged(World.New(3), Alice, 9).Value(opponents));
        Assert.Equal(9, E5lDamaged(World.New(3), Alice, 9).Value(new GreatestDamageDealtThisTurn(new PlayerFilter(IsYou: true))));
    }

    [Fact]
    [RuleRef("120.3")]
    public void Damage_a_named_source_dealt_to_a_player_is_read_from_the_pair_and_not_the_total()
    {
        // CR 120.3: "the greatest damage dealt to an opponent by a creature you control this turn"
        // is one question about two halves, and neither running total can answer it alone.
        var world = E5lDealt(E5lDealt(E5lDamaged(World.New(3), Bob, 6), 1, Bob, 4), 2, new PlayerId(2), 5)
            .Creature(1, Alice, 2, 2)
            .Creature(2, Bob, 2, 2);

        var mine = new GreatestDamageDealtThisTurn(
            PlayerFilter.Opponents,
            new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())));
        Assert.Equal(4, world.Value(mine));

        // Without the source named, the receiver's own total answers — six, which is what the pair
        // map deliberately does not say.
        Assert.Equal(6, world.Value(new GreatestDamageDealtThisTurn(PlayerFilter.Opponents)));
    }

    [Fact]
    [RuleRef("700.4")]
    public void A_creature_that_died_is_counted_by_the_move_and_not_by_the_graveyard()
    {
        // CR 700.4: dies means put into a graveyard from the battlefield. A creature card that was
        // milled or discarded is in a graveyard and never died, so a graveyard count gets this
        // wrong in one direction and a creature that died and was exiled gets it wrong in the other.
        var world = E5lDied(World.New().Creature(1, Alice, 2, 2).Creature(2, Bob, 1, 1), 1);
        Assert.Equal(1, world.Value(new CreaturesDiedThisTurn()));

        var milled = world.Creature(3, Alice, 1, 1, Cards.Zone.Graveyard);
        Assert.Equal(1, milled.Value(new CreaturesDiedThisTurn()));

        // A land that was sacrificed is not a creature that died.
        Assert.Equal(1, E5lDied(milled.Land(4, Alice), 4).Value(new CreaturesDiedThisTurn()));

        // The filter narrows within the deaths, read against the departed object's own face.
        var goblins = E5lDied(world, 2);
        Assert.Equal(2, goblins.Value(new CreaturesDiedThisTurn(new ObjectFilter(Types: CardType.Creature))));
        Assert.Equal(1, goblins.Value(new CreaturesDiedThisTurn(new ObjectFilter(ControlledBy: new You()))));
    }

    [Fact]
    [RuleRef("700.4")]
    public void A_death_filter_that_names_a_zone_or_asks_the_board_is_refused()
    {
        var world = E5lDied(World.New().Creature(1, Alice, 2, 2), 1);

        // Where the card is now is not what "died" counts (CR 700.4).
        Assert.Throws<RefusedException>(() =>
            world.Value(new CreaturesDiedThisTurn(new ObjectFilter(In: Cards.Zone.Graveyard))));

        // A predicate about an object the state no longer holds has no answer, only a guess.
        Assert.Throws<RefusedException>(() =>
            world.Value(new CreaturesDiedThisTurn(new ObjectFilter(Also: new IsAttached()))));
    }

    [Fact]
    [RuleRef("201.2")]
    public void The_largest_group_sharing_a_name_is_not_a_count_of_the_permanents()
    {
        // CR 201.2: "if you control three or more lands with the same name". Three differently named
        // lands satisfy a plain count and do not satisfy the card.
        var lands = new ObjectFilter(Types: CardType.Land, ControlledBy: new You());
        var group = new GreatestSameNameCount(lands);

        // Each world gets its own layers: E5lNamed rewrites what the layer system reports.
        static World Three() => World.New().Land(1, Alice).Land(2, Alice).Land(3, Alice);

        var different = Three();
        Assert.Equal(3, different.Count(lands));
        Assert.Equal(1, different.Value(group));

        var same = E5lNamed(E5lNamed(Three(), 2, "Object 1"), 3, "Object 1");
        Assert.Equal(3, same.Value(group));

        // Two of one name and one of another is a group of two, not of three.
        Assert.Equal(2, E5lNamed(Three(), 2, "Object 1").Value(group));

        // CR 201.2a: a nameless object shares its name with nothing, including another nameless one.
        var nameless = E5lNamed(E5lNamed(Three(), 1, string.Empty), 2, string.Empty);
        Assert.Equal(1, nameless.Value(group));

        // Nothing admitted is nothing shared.
        Assert.Equal(0, World.New().Value(group));
    }

    [Fact]
    [RuleRef("201.2")]
    public void A_group_filter_that_already_names_the_name_is_refused()
    {
        // CR 201.2: the card says "the same name", not a name. A named filter is a CountOf, and
        // answering it here would make the two spellings read alike.
        var world = World.New().Land(1, Alice).Land(2, Alice);
        Assert.Throws<RefusedException>(() =>
            world.Value(new GreatestSameNameCount(new ObjectFilter(Types: CardType.Land, Named: "Object 1"))));
    }

    [Fact]
    [RuleRef("702.1")]
    public void A_filter_without_a_keyword_admits_exactly_what_does_not_have_it()
    {
        // CR 702.1: "creatures without flying" negates the same lookup Has does, so a granted
        // keyword counts as had — the field is not a subtype list.
        var world = E5lKeyword(World.New().Creature(1, Alice, 2, 2).Creature(2, Alice, 1, 1), 1, KeywordKind.Flying);

        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, Has: KeywordKind.Flying)));
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, NotHas: KeywordKind.Flying)));

        // The two halves are about one keyword each: a creature without flying may well have another.
        var both = E5lKeyword(world, 2, KeywordKind.Trample);
        Assert.Equal(1, both.Count(new ObjectFilter(Types: CardType.Creature, NotHas: KeywordKind.Flying)));
        Assert.Equal(2, both.Count(new ObjectFilter(Types: CardType.Creature, NotHas: KeywordKind.Menace)));

        // Demanding and forbidding the same keyword admits nothing (the linter's L045 in the engine).
        Assert.Equal(0, both.Count(new ObjectFilter(Types: CardType.Creature, Has: KeywordKind.Flying, NotHas: KeywordKind.Flying)));
    }

    /// <summary>Damage a player has been dealt this turn, as the turn's history folds it (CR 120.3).</summary>
    private static World E5lDamaged(World world, PlayerId player, int amount) =>
        world with
        {
            State = world.State with
            {
                History = world.State.History with
                {
                    DamageReceivedByPlayer = world.State.History.DamageReceivedByPlayer.SetItem(player, amount),
                },
            },
        };

    /// <summary>The pair half of the same history: what one source dealt to one player (CR 120.3).</summary>
    private static World E5lDealt(World world, int source, PlayerId player, int amount) =>
        world with
        {
            State = world.State with
            {
                History = world.State.History with
                {
                    DamageDealtToPlayer = world.State.History.DamageDealtToPlayer.SetItem((new ObjectId(source), player), amount),
                },
            },
        };

    /// <summary>The turn's record of a death, the departed object itself as CR 603.10 keeps it.</summary>
    private static World E5lDied(World world, int id) =>
        world with
        {
            State = world.State with
            {
                History = world.State.History with
                {
                    Died = world.State.History.Died.Add(world.State.Objects[new ObjectId(id)]),
                },
            },
        };

    /// <summary>The name the layers report for an object, CR 201.2 — the only characteristic this group reads.</summary>
    private static World E5lNamed(World world, int id, string name)
    {
        world.Layers.Set(new ObjectId(id), world.Layers.Of(world.State, new ObjectId(id)) with { Name = name });
        return world;
    }

    /// <summary>A keyword the layers report on an object, granted or printed alike (CR 702.1).</summary>
    private static World E5lKeyword(World world, int id, KeywordKind keyword)
    {
        var known = world.Layers.Of(world.State, new ObjectId(id));
        world.Layers.Set(new ObjectId(id), known with { Abilities = known.Abilities.Add(new KeywordAbility(keyword)) });
        return world;
    }
}
