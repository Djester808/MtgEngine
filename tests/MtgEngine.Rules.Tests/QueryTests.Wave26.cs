using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The four readings wave 26 owed the query layer: a modified permanent (CR 700.9), "an opponent"
/// as the existential it is and nothing else (CR 102.2), the life one firing event gained or lost
/// (CR 603.10), and the one public reading of CR 605.1a every subsystem shares. The world is the
/// one <see cref="QueryTests"/> declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("700.9")]
    public void A_permanent_is_modified_by_a_counter_an_equipment_or_an_aura_its_controller_controls()
    {
        // CR 700.9 with 122: a counter of *any* kind modifies, not only +1/+1 — the reading that
        // asks for one kind is the one two translators wrote.
        var counter = World.New().Creature(1, Alice, 2, 2).Counters(1, "shield", 1);
        Assert.True(Wave26Modified(counter, 1));

        // CR 301.5: equipped. The Equipment records its host, so the question is asked from the
        // other end of the link — a permanent keeps no list of what is attached to it.
        var equipped = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 0, 0, Cards.Zone.Battlefield, CardType.Artifact, Color.Colorless, ["Equipment"])
            .Attach(2, 1);
        Assert.True(Wave26Modified(equipped, 1));

        // CR 303.4: enchanted by an Aura that permanent's own controller controls.
        var enchanted = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 0, 0, Cards.Zone.Battlefield, CardType.Enchantment, Color.White, ["Aura"])
            .Attach(2, 1);
        Assert.True(Wave26Modified(enchanted, 1));

        // None of the three: a creature with an unattached Equipment beside it is not modified.
        var plain = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 0, 0, Cards.Zone.Battlefield, CardType.Artifact, Color.Colorless, ["Equipment"]);
        Assert.False(Wave26Modified(plain, 1));
    }

    [Fact]
    [RuleRef("700.9")]
    public void An_opponents_aura_does_not_modify_your_creature_and_nothing_off_the_battlefield_does()
    {
        // CR 303.4 through CR 700.9: the Aura clause names *that permanent's controller*, so an
        // opponent's Aura on your creature leaves it unmodified. This is the half a reading loses,
        // and it is the whole difference between the two spellings. An Equipment carries no such
        // clause (CR 301.5), so whoever controls it, the creature it is attached to is equipped.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 0, 0, Cards.Zone.Battlefield, CardType.Enchantment, Color.White, ["Aura"])
            .Attach(2, 1)
            .ControlledBy(2, Bob);
        Assert.False(Wave26Modified(world, 1));

        var theirs = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 0, 0, Cards.Zone.Battlefield, CardType.Artifact, Color.Colorless, ["Equipment"])
            .Attach(2, 1)
            .ControlledBy(2, Bob);
        Assert.True(Wave26Modified(theirs, 1));

        // CR 700.9 names a permanent, and a card in a graveyard is no permanent (CR 110.1) — a
        // creature card there may well carry counters (CR 122.1a) and is still not modified.
        var dead = World.New()
            .Creature(1, Alice, 2, 2, Cards.Zone.Graveyard)
            .Counters(1, "+1/+1", 2);
        Assert.False(Wave26Modified(dead, 1, Cards.Zone.Graveyard));
    }

    [Fact]
    [RuleRef("102.2")]
    public void An_opponent_is_some_opponent_and_never_the_opponents_added_together()
    {
        // CR 102.2: "if an opponent controls three or more creatures" asks whether one of them
        // does. Two opponents controlling two each is four creatures and no such opponent, which
        // is the reading EachOpponent gets wrong — and gets wrong only at three players, so a
        // two-player test cannot tell the two spellings apart.
        var split = World.New(3)
            .Creature(1, Bob, 1, 1).Creature(2, Bob, 1, 1)
            .Creature(3, new PlayerId(2), 1, 1).Creature(4, new PlayerId(2), 1, 1);
        var three = new Controls(new AnOpponent(), ObjectFilter.Creature, new Comparison(ComparisonOp.Ge, new Fixed(3)));

        Assert.False(split.Holds(three));
        Assert.True(split.Holds(new Controls(new EachOpponent(), ObjectFilter.Creature, new Comparison(ComparisonOp.Ge, new Fixed(3)))));

        // One opponent reaching the number is enough, whichever of them it is.
        Assert.True(split.Creature(5, Bob, 1, 1).Holds(three));
        Assert.True(split.Creature(5, new PlayerId(2), 1, 1).Holds(three));

        // Nobody's own board answers it: the controller's creatures are not an opponent's.
        Assert.False(World.New(3).Creature(1, Alice, 1, 1).Creature(2, Alice, 1, 1).Creature(3, Alice, 1, 1).Holds(three));
    }

    [Fact]
    [RuleRef("102.2")]
    public void The_negation_of_an_existential_is_no_opponent_and_not_some_opponent_who_does_not()
    {
        // CR 102.2: "if no opponent controls a creature" is the printed line, and it is written as
        // the negation of this condition. The split is made under the negation and not over it —
        // over it, one empty-boarded opponent beside one with a creature would answer true, which
        // is a different card.
        var one = new Controls(new AnOpponent(), ObjectFilter.Creature, new Comparison(ComparisonOp.Ge, new Fixed(1)));
        var mixed = World.New(3).Creature(1, Bob, 1, 1);

        Assert.True(mixed.Holds(one));
        Assert.False(mixed.Holds(new NotCondition(one)));
        Assert.True(World.New(3).Holds(new NotCondition(one)));
    }

    [Fact]
    [RuleRef("102.2")]
    public void An_opponent_names_nobody_to_act_on_and_is_refused_outside_a_condition()
    {
        // It names some opponent, so there is no player it could hand an effect. Answering with the
        // first opponent would aim a spell at a seat the card never chose, so the reference is
        // refused by name — the linter refuses the shape (L041) and this is the engine's half.
        var world = World.New(3).Creature(1, Bob, 1, 1);

        var players = Assert.Throws<RefusedException>(() => world.Players(new AnOpponent()));
        Assert.Equal("102.2", players.Rule);
        Assert.Contains(nameof(AnOpponent), players.Reason, StringComparison.Ordinal);

        // A selector reaching it is the same refusal: a filter is not a condition, and counting
        // "creatures an opponent controls" outside one has no opponent to count for.
        var counted = Assert.Throws<RefusedException>(
            () => world.Count(new ObjectFilter(Types: CardType.Creature, ControlledBy: new AnOpponent())));
        Assert.Equal("102.2", counted.Rule);

        // Inside a condition the same filter is answered, because the condition binds the opponent.
        Assert.True(world.Holds(new Controls(new AnOpponent(), ObjectFilter.Creature, new Comparison(ComparisonOp.Ge, new Fixed(1)))));
    }

    [Fact]
    [RuleRef("603.10")]
    public void The_life_the_firing_event_gained_or_lost_is_read_from_its_own_key()
    {
        // CR 603.10: the amount is fixed when the ability triggered, so the trigger system carries
        // it into the resolution and the reader parses it back. Three events, three keys.
        var world = World.New()
            .Choose(MtgEngine.Rules.Query.Query.TriggeringLifeGainedAmount, "4")
            .Choose(MtgEngine.Rules.Query.Query.TriggeringLifeLostAmount, "7");

        Assert.Equal(4, world.Value(new TriggeringLifeGained()));
        Assert.Equal(7, world.Value(new TriggeringLifeLost()));

        // Neither falls back to the other, and neither is damage: life lost is a payment, an edict
        // or a burn spell and only the last of those deals any (CR 118.3 with 119.3).
        var damage = World.New().Choose(MtgEngine.Rules.Triggers.TriggerSystem.TriggeringDamageAmount, "5");
        Assert.Equal(5, damage.Value(new TriggeringDamage()));
        Assert.Equal(0, damage.Value(new TriggeringLifeGained()));
        Assert.Equal(0, damage.Value(new TriggeringLifeLost()));
        Assert.Equal(0, world.Value(new TriggeringDamage()));

        // An ability read outside any firing carries no key: no life was gained or lost, which is
        // what zero stands for, and is an answer rather than a refusal.
        Assert.Equal(0, World.New().Value(new TriggeringLifeGained()));
        Assert.Equal(0, World.New().Value(new TriggeringLifeLost()));

        // Text no trigger wrote is not a number and is passed over rather than parsed as one.
        var noisy = World.New().Choose(MtgEngine.Rules.Query.Query.TriggeringLifeGainedAmount, "four");
        Assert.Equal(0, noisy.Value(new TriggeringLifeGained()));
    }

    [Fact]
    [RuleRef("605.1a")]
    public void A_mana_ability_meets_all_four_criteria_and_the_fourth_is_the_library()
    {
        // CR 605.1a: no target, could add mana, not a loyalty ability, and its cost and effect move
        // no card to or from a library. The fourth criterion is the one three private copies of
        // this predicate all missed (seam-requests/R2.md § 4).
        var add = new AddMana(new ManaProduction(Fixed: "{G}"));
        Assert.True(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new TapSelf()], add)));

        // "Add {G}. Search your library for a basic land card and put it into your hand" adds mana
        // and is not a mana ability: it moves a card from a library.
        var searching = new ActivatedAbility(
            [new TapSelf()],
            new Sequence([add, new Search(new ObjectFilter(Types: CardType.Land), Cards.Zone.Hand)]));
        Assert.False(MtgEngine.Rules.Query.ManaAbilities.Is(searching));

        // Drawing and milling cross the same border with no zone written on them.
        Assert.False(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new TapSelf()], new Sequence([add, new Draw(new Fixed(1))]))));
        Assert.False(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new MillCost(new Fixed(1))], add)));

        // The other three criteria, each on its own: a target (CR 115.6) and a loyalty cost
        // (CR 606.1) disqualify, and an ability that adds nothing was never one.
        Assert.False(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}"), Who: new TargetPlayer(PlayerFilter.Any)))));
        Assert.False(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new TapSelf()], new Sequence([add, new Destroy(new Target(ObjectFilter.Creature))]))));
        Assert.False(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new LoyaltyCost(1)], add)));
        Assert.False(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new TapSelf()], new Draw(new Fixed(1)))));

        // CR 605.2: a mana ability stays one when the game will not let it produce mana, so a
        // conditional or optional branch that could add mana is enough.
        Assert.True(MtgEngine.Rules.Query.ManaAbilities.Is(new ActivatedAbility([new TapSelf()], new May(add))));
    }

    [Fact]
    [RuleRef("605.1a")]
    public void The_query_seam_asks_the_same_reading_the_runner_and_the_casting_system_do()
    {
        // HasAbilityOfKind is the query layer's half of CR 605.1a, and it must not be a second
        // opinion: the searching ability is a non-mana activated ability to this seam as well.
        var add = new AddMana(new ManaProduction(Fixed: "{G}"));
        var plain = Wave26WithAbility(World.New(), 1, Alice, new ActivatedAbility([new TapSelf()], add));
        var searching = Wave26WithAbility(
            World.New(),
            1,
            Alice,
            new ActivatedAbility([new TapSelf()], new Sequence([add, new Search(new ObjectFilter(Types: CardType.Land), Cards.Zone.Hand)])));

        Assert.Equal(1, plain.Count(new ObjectFilter(Also: new HasAbilityOfKind(AbilityKind.Mana))));
        Assert.Equal(0, plain.Count(new ObjectFilter(Also: new HasAbilityOfKind(AbilityKind.NonMana))));
        Assert.Equal(0, searching.Count(new ObjectFilter(Also: new HasAbilityOfKind(AbilityKind.Mana))));
        Assert.Equal(1, searching.Count(new ObjectFilter(Also: new HasAbilityOfKind(AbilityKind.NonMana))));

        // The colour question is the same reading of the same effect tree (CR 106.1).
        Assert.Equal(1, plain.Count(new ObjectFilter(Also: new ProducesMana(Color.Green))));
        Assert.Equal(0, plain.Count(new ObjectFilter(Also: new ProducesMana(Color.Blue))));
    }

    /// <summary>Whether one object answers <see cref="IsModified"/>, asked through the filter that carries it.</summary>
    private static bool Wave26Modified(World world, int id, Cards.Zone zone = Cards.Zone.Battlefield) =>
        world.Query.Matches(world.State, new ObjectFilter(In: zone, Also: new IsModified()), new ObjectId(id), world.Context);

    /// <summary>A permanent whose characteristics carry one activated ability, CR 613.1a.</summary>
    private static World Wave26WithAbility(World world, int id, PlayerId owner, ActivatedAbility ability)
    {
        var placed = world.Land(id, owner);
        placed.Layers.Set(new ObjectId(id), placed.Layers.Of(placed.State, new ObjectId(id)) with { Abilities = [ability] });
        return placed;
    }
}
