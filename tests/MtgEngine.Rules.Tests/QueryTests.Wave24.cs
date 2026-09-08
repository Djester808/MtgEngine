using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The six readers wave 24 owed the query layer: the damage a firing carried (CR 603.10), the
/// attackers aimed at a player (CR 508.1), the spell that targets something (CR 115.1), the zone a
/// spell was cast from (CR 601.3), an alternative cost told apart from an additional one (CR 118.9)
/// and the two filter fields nobody read (CR 202.1, CR 903.4). The world is the one
/// <see cref="QueryTests"/> declares; the helpers below only reach into it for the three things it
/// has no builder for — a stack entry, a declaration of attackers, and what a card is printed as.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("603.10")]
    public void The_triggering_damage_is_the_number_the_firing_carried()
    {
        // CR 603.10: the trigger system writes the damage of the event that fired the ability into
        // the firing's choices, culture-invariantly, and the resolution reads it from there.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Choose(Rules.Triggers.TriggerSystem.TriggeringDamageAmount, "3");

        Assert.Equal(3, world.Value(new TriggeringDamage()));

        // An ability copied outside a firing carries no such key: no damage was dealt to it, and
        // zero is the number that stands for it rather than a refusal.
        Assert.Equal(0, World.New().Creature(1, Alice, 2, 2).Value(new TriggeringDamage()));

        // Text that is not a number is not a number, whatever the machine's culture says.
        Assert.Equal(0, World.New().Choose(Rules.Triggers.TriggerSystem.TriggeringDamageAmount, "3,5").Value(new TriggeringDamage()));
    }

    [Fact]
    [RuleRef("508.1")]
    public void Attackers_against_a_player_count_the_declaration_and_their_planeswalkers()
    {
        // Bob is attacked by two creatures: one aimed at him and one aimed at a planeswalker he
        // controls. CR 506.1: both are attacking his side of the board, so the flag counts them.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 2, 2)
            .Creature(3, Alice, 2, 2)
            .Creature(4, Bob, 0, 0, types: CardType.Planeswalker)
            .Creature(5, Alice, 0, 0, types: CardType.Planeswalker);
        world = Wave24Attack(world, Alice, (1, new CombatTarget(Bob, null)), (2, new CombatTarget(null, new ObjectId(4))), (3, new CombatTarget(null, new ObjectId(5))));

        var againstBob = new AttackersAgainst(new EachOpponent());
        Assert.Equal(2, world.Value(againstBob));

        // Clearing the flag is the card that names the player alone, and the planeswalker's
        // attacker drops out of it.
        Assert.Equal(1, world.Value(new AttackersAgainst(new EachOpponent(), IncludingPermanentsTheyControl: false)));

        // The third attacker is aimed at Alice's own planeswalker, so "attacking you" is one.
        Assert.Equal(1, world.Value(new AttackersAgainst(new You())));

        // No combat is no attackers, CR 506.1, and the amount says zero rather than refusing.
        Assert.Equal(0, World.New().Value(againstBob));
    }

    [Fact]
    [RuleRef("115.1")]
    public void A_spell_that_targets_a_permanent_is_admitted_by_what_it_targeted()
    {
        // CR 115.1 with 601.2c: the filter describes the spell, and the predicate inside it
        // describes what that spell announced as its target.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Bob, 2, 2)
            .Spell(3, Bob, copy: false)
            .Spell(4, Bob, copy: false)
            .Spell(5, Bob, copy: false);
        world = Wave24Stack(world, 3, Wave24Entry(targets: [new ObjectId(1)]));
        world = Wave24Stack(world, 4, Wave24Entry(targets: [new ObjectId(2)]));
        world = Wave24Stack(world, 5, Wave24Entry(targetPlayers: [Alice]));

        var targetsYours = new ObjectFilter(
            In: Cards.Zone.Stack,
            Spell: true,
            Also: new Targets(new ObjectFilter(ControlledBy: new You())));

        Assert.True(world.Query.Matches(world.State, targetsYours, new ObjectId(3), world.Context));
        Assert.False(world.Query.Matches(world.State, targetsYours, new ObjectId(4), world.Context));

        // A player target is no object, so it admits no ObjectFilter — the spell aimed at Alice
        // does not target a permanent she controls.
        Assert.False(world.Query.Matches(world.State, targetsYours, new ObjectId(5), world.Context));
        Assert.Equal(1, world.Count(targetsYours));

        // A spell with no announced target is admitted by nothing here.
        var noTargets = World.New().Creature(1, Alice, 2, 2).Spell(2, Bob, copy: false);
        Assert.False(noTargets.Query.Matches(noTargets.State, targetsYours, new ObjectId(2), noTargets.Context));
    }

    [Fact]
    [RuleRef("601.3")]
    public void The_zone_a_spell_was_cast_from_is_read_off_its_stack_entry()
    {
        // CR 601.3: "spells cast from anywhere other than a hand" is the same printed line read two
        // ways, so a spell cast from a graveyard is admitted by one field and refused by the other.
        var world = World.New()
            .Spell(1, Alice, copy: false)
            .Spell(2, Alice, copy: false)
            .Spell(3, Alice, copy: false)
            .Creature(4, Alice, 2, 2);
        world = Wave24Stack(world, 1, Wave24Entry(castFrom: Cards.Zone.Hand));
        world = Wave24Stack(world, 2, Wave24Entry(castFrom: Cards.Zone.Graveyard));

        var fromHand = new ObjectFilter(In: Cards.Zone.Stack, Spell: true, CastFrom: Cards.Zone.Hand);
        var notFromHand = new ObjectFilter(In: Cards.Zone.Stack, Spell: true, NotCastFrom: Cards.Zone.Hand);

        Assert.True(world.Query.Matches(world.State, fromHand, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, notFromHand, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, fromHand, new ObjectId(2), world.Context));
        Assert.True(world.Query.Matches(world.State, notFromHand, new ObjectId(2), world.Context));

        // A spell that was never cast — a copy put onto the stack, CR 707.10 — was cast from
        // nowhere, and so is object 3, whose entry records no zone: neither field admits them.
        Assert.False(world.Query.Matches(world.State, fromHand, new ObjectId(3), world.Context));
        Assert.False(world.Query.Matches(world.State, notFromHand, new ObjectId(3), world.Context));

        // And a permanent matches neither, because it was not cast from anywhere the state holds.
        var permanent = new ObjectFilter(CastFrom: Cards.Zone.Hand);
        Assert.False(world.Query.Matches(world.State, permanent, new ObjectId(4), world.Context));
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(NotCastFrom: Cards.Zone.Hand), new ObjectId(4), world.Context));
    }

    [Fact]
    [RuleRef("118.9")]
    public void An_alternative_cost_is_named_rather_than_counted_so_a_kicked_spell_answers_no()
    {
        // seam-requests/E3n.md § 1: the condition asks whether an alternative cost was paid, and
        // the markers on the stack entry name what was paid. Kicker is paid on top of the mana cost
        // (CR 601.2b), so a kicked spell is not a spell cast for an alternative cost.
        var world = World.New().Spell(1, Alice, copy: false).Spell(2, Alice, copy: false).Spell(3, Alice, copy: false);
        world = Wave24Stack(world, 1, Wave24Entry(paid: [nameof(KeywordKind.Kicker), "additional"]));
        world = Wave24Stack(world, 2, Wave24Entry(paid: ["alternative"]));
        world = Wave24Stack(world, 3, Wave24Entry(paid: [nameof(KeywordKind.Flashback)]));

        Assert.False(world.As(1).Holds(new PaidAlternativeCost()));
        Assert.True(world.As(2).Holds(new PaidAlternativeCost()));

        // A keyword alternative cost answers it by its own name, and by the keyword when named.
        Assert.True(world.As(3).Holds(new PaidAlternativeCost()));
        Assert.True(world.As(3).Holds(new PaidAlternativeCost(KeywordKind.Flashback)));
        Assert.False(world.As(3).Holds(new PaidAlternativeCost(KeywordKind.Kicker)));

        // The two markers for a cost a permission replaced: a waived mana cost and life paid
        // instead of one (CR 118.9) are both alternative costs.
        Assert.True(Wave24Stack(world, 1, Wave24Entry(paid: ["free"])).As(1).Holds(new PaidAlternativeCost()));
        Assert.True(Wave24Stack(world, 1, Wave24Entry(paid: ["life"])).As(1).Holds(new PaidAlternativeCost()));

        // Nothing paid at all, and a source that is no spell, are both false.
        Assert.False(Wave24Stack(world, 1, Wave24Entry()).As(1).Holds(new PaidAlternativeCost()));
        Assert.False(World.New().Creature(1, Alice, 2, 2).As(1).Holds(new PaidAlternativeCost()));
    }

    [Fact]
    [RuleRef("202.1")]
    public void A_mana_cost_filter_reads_the_printed_symbols_and_not_their_value()
    {
        // CR 202.1: "{0}" is a printed cost and a land's absent cost is not, which is the whole
        // reason this field exists beside ManaValue — both cost 0 and only one is written.
        var world = World.New()
            .Creature(1, Alice, 1, 1, manaCost: "{0}")
            .Creature(2, Alice, 1, 1, manaCost: "{1}")
            .Creature(3, Alice, 1, 1, manaCost: "{W}")
            .Land(4, Alice);

        var zeroOrOne = new ObjectFilter(ManaCostIs: ["{0}", "{1}"]);
        Assert.Equal(2, world.Count(zeroOrOne));
        Assert.True(world.Query.Matches(world.State, zeroOrOne, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, zeroOrOne, new ObjectId(3), world.Context));

        // A land has no printed cost, so it matches no entry — where ManaValue Eq 0 would take it.
        Assert.False(world.Query.Matches(world.State, zeroOrOne, new ObjectId(4), world.Context));
        Assert.Equal(2, world.Count(new ObjectFilter(ManaValue: new Comparison(ComparisonOp.Eq, new Fixed(0)))));

        // "{W}" is one symbol and not the number one, and an empty list matches nothing at all.
        Assert.Equal(1, world.Count(new ObjectFilter(ManaCostIs: ["{W}"])));
        Assert.Equal(0, world.Count(new ObjectFilter(ManaCostIs: [])));
    }

    [Fact]
    [RuleRef("903.4")]
    public void Colors_outside_the_commander_identity_are_read_off_the_commander_card()
    {
        // CR 903.4: the identity is the commander card's colours and the mana symbols its text
        // prints, so a white-blue commander leaves red outside and takes colourless in.
        var world = World.New()
            .Commander(1, Alice)
            .Creature(2, Alice, 1, 1, colors: Color.White)
            .Creature(3, Alice, 1, 1, colors: Color.Red)
            .Creature(4, Alice, 1, 1, colors: Color.White | Color.Red)
            .Land(5, Alice);
        world = Wave24Printed(world, 1, colors: Color.White, manaCost: "{1}{U}");

        var outside = new ObjectFilter(NotInCommanderIdentityColors: true);
        Assert.False(world.Query.Matches(world.State, outside, new ObjectId(2), world.Context));
        Assert.True(world.Query.Matches(world.State, outside, new ObjectId(3), world.Context));

        // A colour outside is enough, whatever else the object is.
        Assert.True(world.Query.Matches(world.State, outside, new ObjectId(4), world.Context));

        // A colourless object has no colour outside any identity.
        Assert.False(world.Query.Matches(world.State, outside, new ObjectId(5), world.Context));

        // Inside the identity: the commander itself, the white creature and the colourless land.
        Assert.Equal(3, world.Count(new ObjectFilter(NotInCommanderIdentityColors: false)));
        Assert.Equal(2, world.Count(outside));

        // The mana symbols an ability's cost prints are part of the identity, CR 903.4: a commander
        // whose text prints {R} takes red in, and the red creature is no longer outside it.
        var withRed = World.New()
            .Commander(1, Alice)
            .Creature(2, Alice, 1, 1, colors: Color.Red)
            .Creature(3, Alice, 1, 1, colors: Color.Green);
        withRed = Wave24Printed(
            withRed,
            1,
            colors: Color.White,
            manaCost: "{1}{W}",
            abilities: [new KeywordAbility(KeywordKind.Flashback, "{R}")]);

        Assert.False(withRed.Query.Matches(withRed.State, outside, new ObjectId(2), withRed.Context));
        Assert.True(withRed.Query.Matches(withRed.State, outside, new ObjectId(3), withRed.Context));
    }

    [Fact]
    [RuleRef("903.4")]
    public void A_game_with_no_commander_refuses_the_identity_question_by_name()
    {
        // The refusal is the honest answer: answered as "inside", the filter would silently admit
        // every object in a game that designates no commander at all.
        var world = World.New().Creature(1, Alice, 1, 1, colors: Color.Red);

        var refused = Assert.Throws<RefusedException>(
            () => world.Query.Matches(world.State, new ObjectFilter(NotInCommanderIdentityColors: true), new ObjectId(1), world.Context));

        Assert.Equal("903.4", refused.Rule);
        Assert.Contains(nameof(ObjectFilter.NotInCommanderIdentityColors), refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A stack entry the test writes by hand: the fields the announcement fills in, CR 601.2.</summary>
    private static StackEntry Wave24Entry(
        ImmutableList<ObjectId>? targets = null,
        ImmutableList<PlayerId>? targetPlayers = null,
        Cards.Zone? castFrom = null,
        ImmutableList<string>? paid = null) =>
        new(null, null, targets ?? [], targetPlayers ?? [], [], null, false, CastFrom: castFrom, OptionalCostsPaid: paid);

    /// <summary>The stack entry of one object, replaced — <see cref="World"/> builds only an empty one.</summary>
    private static World Wave24Stack(World world, int id, StackEntry entry) =>
        world with
        {
            State = world.State with
            {
                Objects = world.State.Objects.SetItem(
                    new ObjectId(id),
                    world.State.Objects[new ObjectId(id)] with { Stack = entry }),
            },
        };

    /// <summary>A declaration of attackers, CR 508.1: each attacker with what it is attacking.</summary>
    private static World Wave24Attack(World world, PlayerId attacking, params (int Attacker, CombatTarget Target)[] attackers) =>
        world with
        {
            State = world.State with
            {
                Combat = new CombatState(
                    attacking,
                    attackers.ToImmutableDictionary(a => new ObjectId(a.Attacker), a => a.Target),
                    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty,
                    AttackersDeclared: true,
                    BlockersDeclared: false),
            },
        };

    /// <summary>
    /// What a card is printed as, which is what CR 903.4 reads: the fake layer system answers the
    /// printed reading from what the test set, so setting it here sets both.
    /// </summary>
    private static World Wave24Printed(
        World world,
        int id,
        Color colors,
        string? manaCost = null,
        ImmutableList<Ability>? abilities = null)
    {
        var current = world.Layers.Of(world.State, new ObjectId(id));
        world.Layers.Set(new ObjectId(id), current with { Colors = colors, ManaCost = manaCost, Abilities = abilities ?? [] });
        return world;
    }
}
