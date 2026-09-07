using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCastEvent = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The cast a resolving effect instructs, CR 608.2g, and the two things a command may not grant
/// itself: the zone it casts from (CR 601.3) and the mana cost it does not pay (CR 118.9). Also
/// CR 601.2c's half of an Aura's legality, which belongs to the announcement and not to the
/// resolution, and CR 106.6a's record of what a payment was actually spent on.
/// </summary>
public sealed class CastingInstructedTests
{
    private readonly CastingSystem casting = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    [RuleRef("608.2g")]
    public void An_instructed_cast_is_made_where_the_plain_cast_has_no_priority()
    {
        // CR 117.1a: a player casts a spell only as they hold priority, and nobody holds it while
        // a spell resolves. CR 608.2g: a cast an effect instructs happens inside that resolution,
        // so the same announcement is legal — otherwise "you may cast that card" could never cast.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));
        table.Priority(CastingTable.B);

        Assert.Equal("117.1a", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);

        var cast = casting.CastInstructed(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Single(cast.Events.OfType<SpellCastEvent>());
        Assert.Equal(CardZone.Stack, cast.State.Objects[StackZone.Top(cast.State)!.Value].Zone.Zone);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
    }

    [Fact]
    [RuleRef("608.2g")]
    public void An_instructed_cast_reaches_the_cost_question_the_timing_check_hid()
    {
        // CR 601.2c: which permanent pays an additional sacrifice is chosen as the spell is cast.
        // While the plain cast is refused for timing that refusal is all anyone ever sees, so the
        // instructed road is what makes the cost's own refusal — and its answer — reachable.
        var table = new CastingTable();
        var spell = table.Add(CastingInstructedCards.SacrificeInstant(), CastingTable.A, CardZone.Hand);
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Priority(CastingTable.B);

        var timing = Assert.Throws<RefusedException>(() => casting.Cast(table.State, new CastSpell(CastingTable.A, spell)));
        Assert.Equal("117.1a", timing.Rule);

        var cost = Assert.Throws<RefusedException>(() => casting.CastInstructed(table.State, new CastSpell(CastingTable.A, spell)));
        Assert.Equal("601.2c", cost.Rule);

        var cast = casting.CastInstructed(table.State, new CastSpell(CastingTable.A, spell, CostObjects: [land]));
        Assert.Single(cast.Events.OfType<SpellCastEvent>());
        Assert.Equal(land, Assert.Single(cast.Events.OfType<ObjectLeftBy>(), e => e.Verb == "sacrificed").Object);
    }

    [Fact]
    [RuleRef("118.9")]
    public void A_free_cast_claimed_on_a_command_alone_is_refused()
    {
        // CR 118.9: "without paying its mana cost" is an announcement an effect makes. A player
        // saying it on the command grants nothing, so the mana cost still has to be paid and the
        // refusal names the rule that was claimed rather than an empty pool.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Hand);

        var refusal = casting.Check(table.State, new CastSpell(CastingTable.A, spell, WithoutPayingManaCost: true));

        Assert.Equal("118.9", refusal?.Rule);
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_free_cast_claimed_from_a_graveyard_is_refused_for_the_zone_first()
    {
        // CR 601.3: a card is cast from its owner's hand unless an effect says otherwise, and the
        // mana half of the command is not such an effect. The zone check is answered first because
        // a card that may not be cast at all is not a question about what it costs.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Graveyard);

        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, spell, WithoutPayingManaCost: true))?.Rule);
    }

    [Fact]
    [RuleRef("118.9")]
    public void An_instructed_free_cast_pays_no_mana_and_records_that_it_did_not()
    {
        // CR 608.2g with 118.9: the caller of the instructed road is the resolving effect, so the
        // flag beside the command is that effect's announcement and is honoured — from the zone
        // the effect found the card in, with nothing in the pool.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Graveyard);

        var cast = casting.CastInstructed(table.State, new CastSpell(CastingTable.A, spell, WithoutPayingManaCost: true));

        var top = StackZone.Top(cast.State)!.Value;
        Assert.Contains("free", cast.State.Objects[top].Stack!.OptionalCostsPaid!);
        Assert.DoesNotContain(cast.Events, e => e is ManaChanged);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void An_aura_that_enchants_only_objects_is_refused_at_a_player()
    {
        // CR 702.5b: "enchant creature" names what this Aura may be attached to, and a player is
        // not one of them. CR 601.2c: that is a legality of the announcement, so the cast is
        // refused — not accepted and then buried by CR 704.5m on the next check.
        var table = new CastingTable();
        var aura = table.Add(CastingInstructedCards.ObjectAura(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));

        var refusal = casting.Check(table.State, new CastSpell(CastingTable.A, aura, TargetPlayers: [CastingTable.B]));

        Assert.Equal("601.2c", refusal?.Rule);
        Assert.Equal(CardZone.Hand, table.State.Objects[aura].Zone.Zone);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void An_aura_that_enchants_players_is_cast_at_one()
    {
        // CR 702.5b and 303.4f: "enchant player" admits a player, so the same announcement stands
        // and the spell goes on the stack carrying the player it was cast at.
        var table = new CastingTable();
        var aura = table.Add(CastingInstructedCards.PlayerAura(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, aura, TargetPlayers: [CastingTable.B]));

        var top = StackZone.Top(cast.State)!.Value;
        Assert.Equal(CastingTable.B, Assert.Single(cast.State.Objects[top].Stack!.TargetPlayers));
    }

    [Fact]
    [RuleRef("106.6a")]
    public void A_cast_records_the_spell_its_mana_was_spent_on_and_the_blocks_that_paid()
    {
        // CR 106.6a: "when that mana is spent to cast a spell" needs two things the payment alone
        // does not say — which object it was spent on, and which mana it took. The spell is the
        // object on the stack (CR 400.7), so the event learns its id, and the entry keeps the
        // blocks positive because what was spent on a spell is a quantity, not a subtraction.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var beast = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Hand);
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, land, 0)).Events);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, beast));

        var spell = Assert.Single(cast.Events.OfType<SpellCastEvent>()).Spell;
        var spend = Assert.Single(cast.Events.OfType<ManaChanged>(), m => m.Reason == "cast");
        Assert.Equal(spell, spend.SpentOn);

        var block = Assert.Single(cast.State.Objects[spell].Stack!.ManaProvenanceSpent!);
        Assert.Equal(land, block.Source);
        Assert.Equal(1, block.Amount);
        Assert.Equal(Color.Green, block.Color);
    }

    [Fact]
    [RuleRef("106.6a")]
    public void Mana_a_mana_ability_adds_was_spent_on_nothing()
    {
        // CR 106.6a: production is not a spend, so the mana entering the pool names no object to
        // have been spent on — only the payment does.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, land, 0));

        Assert.Null(Assert.Single(activated.Events.OfType<ManaChanged>()).SpentOn);
    }

    [Fact]
    [RuleRef("608.2d")]
    public void An_any_colour_activation_adds_the_colour_the_command_names()
    {
        // CR 608.2d: the choice an "add one mana of any color" offers is made as the ability is
        // applied, and the activation is where the command can carry it — one letter per mana.
        var table = new CastingTable();
        var prism = table.Add(CastingInstructedCards.AnyColorPrism(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, prism, 0, ManaColors: "W"));

        var pool = activated.State.Player(CastingTable.A).Mana;
        Assert.Equal(1, pool.White);
        Assert.Equal(1, pool.Total);
    }

    [Fact]
    [RuleRef("608.2d")]
    public void An_any_colour_activation_with_no_colour_or_a_wrong_letter_is_refused()
    {
        // CR 608.2d: a choice that was not made cannot be applied, and a letter that names no
        // colour is not a choice either — both are refused naming the rule rather than guessed at.
        var table = new CastingTable();
        var prism = table.Add(CastingInstructedCards.AnyColorPrism(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        Assert.Equal("608.2d", casting.Check(table.State, new ActivateAbility(CastingTable.A, prism, 0))?.Rule);
        Assert.Equal("608.2d", casting.Check(table.State, new ActivateAbility(CastingTable.A, prism, 0, ManaColors: "Q"))?.Rule);
    }

    [Fact]
    [RuleRef("115.6")]
    public void A_player_an_opponents_spells_cannot_target_is_refused_at_announcement()
    {
        // CR 115.6 with 601.2c: the choice of targets is part of the announcement, so a player who
        // cannot be targeted is refused there. The flag is the card-text form — "by spells or
        // abilities your opponents control" — so it is read from the caster's point of view: the
        // protected player may still name themselves, and the unprotected one may be named at all.
        var (system, table, _) = Limited(CastingTable.B, new PlayerLimits(PlayerRestriction.BeTargeted));
        var mine = table.Add(CastingInstructedCards.TargetedInstant(), CastingTable.A, CardZone.Hand);
        var theirs = table.Add(CastingInstructedCards.TargetedInstant(), CastingTable.B, CardZone.Hand);

        Assert.Equal("115.6", system.Check(table.State, new CastSpell(CastingTable.A, mine, TargetPlayers: [CastingTable.B]))?.Rule);
        Assert.Null(system.Check(table.State, new CastSpell(CastingTable.A, mine, TargetPlayers: [CastingTable.A])));

        table.Priority(CastingTable.B);
        Assert.Null(system.Check(table.State, new CastSpell(CastingTable.B, theirs, TargetPlayers: [CastingTable.B])));
    }

    [Fact]
    [RuleRef("702.16j")]
    public void Protection_from_everything_is_refused_to_its_own_controller_too()
    {
        // CR 702.16j carries no "your opponents control": a player with protection from everything
        // can't be targeted by spells or abilities at all, so the reading that lets the flag be
        // self-named must not let this one be. Same seam, two prohibitions, two readings.
        var (system, table, _) = Limited(CastingTable.B, new PlayerLimits(ProtectionFromEverything: true));
        var theirs = table.Add(CastingInstructedCards.TargetedInstant(), CastingTable.B, CardZone.Hand);
        table.Priority(CastingTable.B);

        Assert.Equal("115.6", system.Check(table.State, new CastSpell(CastingTable.B, theirs, TargetPlayers: [CastingTable.B]))?.Rule);
    }

    [Fact]
    [RuleRef("702.16c")]
    public void An_aura_is_refused_at_a_player_who_cannot_be_enchanted()
    {
        // CR 702.16c with 702.16j: protection from everything states every quality, so no Aura may
        // be attached to that player. The announcement is refused for the enchanting and not for
        // the targeting, because "cannot be enchanted" is the nearer reason this spell is illegal.
        var (system, table, _) = Limited(CastingTable.B, new PlayerLimits(ProtectionFromEverything: true));
        var aura = table.Add(CastingInstructedCards.PlayerAura(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));

        Assert.Equal("702.16c", system.Check(table.State, new CastSpell(CastingTable.A, aura, TargetPlayers: [CastingTable.B]))?.Rule);
        Assert.Equal(CardZone.Hand, table.State.Objects[aura].Zone.Zone);
    }

    [Fact]
    [RuleRef("608.2b")]
    public void An_aura_whose_player_became_protected_does_not_resolve()
    {
        // CR 608.2b: the protection arrived after the Aura was cast, so the only target is illegal
        // as it resolves and the spell does not resolve — it goes to its owner's graveyard without
        // ever entering the battlefield, which is not the same graveyard CR 704.5m would give it.
        var (system, table, limits) = Limited(CastingTable.B, PlayerLimits.None);
        var aura = table.Add(CastingInstructedCards.PlayerAura(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));
        var cast = system.Cast(table.State, new CastSpell(CastingTable.A, aura, TargetPlayers: [CastingTable.B]));

        var attached = system.ResolveTop(cast.State);
        Assert.Contains(attached.State.Objects.Values, o => o.Zone.Zone == CardZone.Battlefield && o.AttachedToPlayer == CastingTable.B);

        limits[CastingTable.B] = new PlayerLimits(ProtectionFromEverything: true);
        var countered = system.ResolveTop(cast.State);

        Assert.DoesNotContain(countered.State.Objects.Values, o => o.Zone.Zone == CardZone.Battlefield);
        Assert.Contains(countered.State.Objects.Values, o => o.Zone.Zone == CardZone.Graveyard);
    }

    [Fact]
    [RuleRef("601.3a")]
    public void An_instructed_cast_from_a_graveyard_is_made_where_the_plain_one_is_refused()
    {
        // CR 601.3a: an effect that says a player may cast a card is itself the permission CR 601.3
        // asks for, so the instruction casts from the zone the effect found the card in. The same
        // command a player announced still has no permission and is refused for the zone.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));

        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
        Assert.Null(casting.CheckInstructed(table.State, new CastSpell(CastingTable.A, spell)));

        var cast = casting.CastInstructed(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Single(cast.Events.OfType<SpellCastEvent>());
        Assert.Equal(CardZone.Stack, cast.State.Objects[StackZone.Top(cast.State)!.Value].Zone.Zone);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void The_cost_of_an_instructed_cast_from_a_graveyard_is_asked_for()
    {
        // CR 601.2b and 601.2c: the additional cost of a card a may-cast names in the graveyard has
        // to be askable before the answer casts it, so CostChoices reads the plan on the instructed
        // road. The refusal that stands in its place is the cost's own, not the zone's.
        var table = new CastingTable();
        var spell = table.Add(CastingInstructedCards.SacrificeInstant(), CastingTable.A, CardZone.Graveyard);
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var other = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var asked = Assert.Single(casting.CostChoices(table.State, new CastSpell(CastingTable.A, spell)));
        Assert.Equal([land, other], asked.Legal);
        Assert.Equal(1, asked.Count);
        Assert.Equal("601.2c", casting.CheckInstructed(table.State, new CastSpell(CastingTable.A, spell))?.Rule);

        var cast = casting.CastInstructed(table.State, new CastSpell(CastingTable.A, spell, CostObjects: [land]));

        Assert.Single(cast.Events.OfType<SpellCastEvent>());
        Assert.Equal(land, Assert.Single(cast.Events.OfType<ObjectLeftBy>(), e => e.Verb == "sacrificed").Object);
    }

    /// <summary>
    /// A casting system whose characteristics seam reports one player's limits, CR 613.10. The
    /// table of limits comes back with it so a test can put one in force after a spell is cast,
    /// which is what CR 608.2b's re-check is about.
    /// </summary>
    private static (CastingSystem System, CastingTable Table, Dictionary<PlayerId, PlayerLimits> Limits) Limited(PlayerId player, PlayerLimits limits)
    {
        var chars = new PrintedCharacteristics();
        chars.Limits[player] = limits;
        var system = new CastingSystem(new CastingFakeZones(), new CastingBacklogQuery(), chars, new RecordingInterpreter());
        return (system, new CastingTable(), chars.Limits);
    }
}

/// <summary>
/// Cards as data for <see cref="CastingInstructedTests"/>, built here rather than read from
/// <c>cards/</c>. The names are deliberately not any printed card's, as CARD_ENGINE_PLAN.md
/// § "A card is data or it does not exist" requires of the engine and of its tests.
/// </summary>
internal static class CastingInstructedCards
{
    /// <summary>An instant whose additional cost sacrifices a land, CR 601.2b: a cost that names an object.</summary>
    public static CardDefinition SacrificeInstant() => new(
        "CASTING INSTRUCTED TITHE",
        "Casting Instructed Tithe",
        Layout.Normal,
        [
            new Face(
                "Casting Instructed Tithe",
                FaceRole.Main,
                new TypeLine(CardType.Instant),
                null,
                AdditionalCosts: [new SacrificeCost(new ObjectFilter(Types: CardType.Land), new Fixed(1))]),
        ]);

    /// <summary>An instant with no cost whose command names a player, CR 115.4: what the announcement's target legality is asked of.</summary>
    public static CardDefinition TargetedInstant() => new(
        "CASTING INSTRUCTED SPARK",
        "Casting Instructed Spark",
        Layout.Normal,
        [new Face("Casting Instructed Spark", FaceRole.Main, new TypeLine(CardType.Instant), null)]);

    /// <summary>"Enchant creature", CR 702.5b: an Aura that admits objects and no player.</summary>
    public static CardDefinition ObjectAura() => Aura(
        "CASTING INSTRUCTED CLING",
        "Casting Instructed Cling",
        new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature)));

    /// <summary>"Enchant player", CR 702.5b: an Aura that admits a player.</summary>
    public static CardDefinition PlayerAura() => Aura(
        "CASTING INSTRUCTED SHROUD",
        "Casting Instructed Shroud",
        new KeywordAbility(KeywordKind.Enchant, OfPlayer: new PlayerFilter()));

    /// <summary>"{T}: Add one mana of any color", CR 608.2d: the colour travels with the activation.</summary>
    public static CardDefinition AnyColorPrism() => new(
        "CASTING INSTRUCTED PRISM",
        "Casting Instructed Prism",
        Layout.Normal,
        [
            new Face(
                "Casting Instructed Prism",
                FaceRole.Main,
                new TypeLine(CardType.Artifact),
                "{1}",
                Abilities: [new ActivatedAbility([new TapSelf()], new Cards.AddMana(new ManaProduction(AnyColor: true)))]),
        ]);

    private static CardDefinition Aura(string oracleId, string name, KeywordAbility enchant) => new(
        oracleId,
        name,
        Layout.Normal,
        [
            new Face(
                name,
                FaceRole.Main,
                new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]),
                "{G}",
                Abilities: [enchant]),
        ]);
}
