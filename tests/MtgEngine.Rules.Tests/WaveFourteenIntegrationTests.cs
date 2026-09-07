using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;
using SpellCastEvent = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-13 and wave-14 joins, played through <see cref="Game.Apply"/> on
/// <see cref="Subsystems.Wave2"/>: a keyed target player remembered from the announcement (CR 601.2c
/// with 608.2h), a departed target's controller read as last-known information (CR 400.7), an Aura's
/// object target judged at announcement (CR 601.2c with 702.5b), a creature type chosen as a land
/// enters (CR 614.1c with 614.12) and an any-colour activation whose mana carries a delayed trigger
/// (CR 106.6a with 603.7). Each builder verified its own half against its own fakes; what is asserted
/// here is that the halves meet in a real game. Every game ends with the log written, read back and
/// replayed to the same state.
/// </summary>
public sealed partial class WaveFourteenIntegrationTests
{
    private static PlayerId P0 => WaveFourteenIntegrationTable.P0;

    private static PlayerId P1 => WaveFourteenIntegrationTable.P1;

    /// <summary>
    /// CR 601.2c with 608.2d: "target player loses 2 life and you gain 2 life" cast at the opponent.
    /// The target is chosen as the spell is announced, the resolution reads that player back under
    /// the key the selector declared (CR 608.2h, the wave-13 E0g join), and both totals move
    /// (CR 119.3). The second spell says "that player" through the same key in a later step.
    /// </summary>
    [Fact]
    [RuleRef("601.2c")]
    public void A_keyed_target_player_is_remembered_from_the_announcement_and_both_totals_move()
    {
        var table = new WaveFourteenIntegrationTable(DrainDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);

        // CR 601.2c: the player is chosen at announcement and rides on the stack object.
        var cast = table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveFourteenIntegrationTable.DrainName), TargetPlayers: [P1]));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;
        Assert.Equal([P1], table.State.Objects[spell].Stack!.TargetPlayers);

        // CR 608.2d with 119.3: the resolution reads the announced player, who loses 2, and the
        // caster gains 2 — two life events, one per player.
        var resolved = table.PassUntilStackIsEmpty();
        Assert.Contains(resolved.OfType<LifeChanged>(), e => e.Player == P1 && e.Delta == -2);
        Assert.Contains(resolved.OfType<LifeChanged>(), e => e.Player == P0 && e.Delta == 2);
        Assert.Equal(18, table.State.Player(P1).Life);
        Assert.Equal(22, table.State.Player(P0).Life);
        Assert.Single(table.Graveyard(P0, WaveFourteenIntegrationTable.DrainName));

        // CR 608.2h: "that player" in a later step is the player the first step targeted, read
        // back from the memory the announcement wrote, never the caster (CR 109.5's fallback).
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveFourteenIntegrationTable.EchoName), TargetPlayers: [P1]));
        table.PassUntilStackIsEmpty();
        Assert.Equal(16, table.State.Player(P1).Life);
        Assert.Equal(22, table.State.Player(P0).Life);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 400.7 with 608.2h: "exile target creature, then its controller draws a card". The exile
    /// makes the creature a new object, so "its controller" is the last-known information of the
    /// object that was targeted (the wave-13 E9l join), and the opponent draws — not the caster.
    /// </summary>
    [Fact]
    [RuleRef("400.7")]
    public void A_departed_targets_controller_is_last_known_information_and_that_player_draws()
    {
        var table = new WaveFourteenIntegrationTable(BanishDeck(), OxDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);

        // The opponent puts a creature on the battlefield of their own, CR 601.2.
        table.PassTo(4, Step.PrecombatMain);
        table.PlayALand(P1);
        table.TapForMana(P1, 1);
        table.Do(new CastSpellCommand(P1, table.InHand(P1, WaveFourteenIntegrationTable.OxName)));
        table.PassUntilStackIsEmpty();
        var ox = Assert.Single(table.Battlefield(P1, WaveFourteenIntegrationTable.OxName));

        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);
        var opponentLibrary = table.LibraryCount(P1);
        var opponentHand = table.HandCount(P1);
        var casterLibrary = table.LibraryCount(P0);

        // CR 601.2c: the creature is the announced target.
        var cast = table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveFourteenIntegrationTable.BanishName), Targets: [ox]));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;
        Assert.Equal([ox], table.State.Objects[spell].Stack!.Targets);

        var resolved = table.PassUntilStackIsEmpty();

        // CR 400.7: the exiled card is a new object; the one that was targeted is gone.
        var exiled = WaveFourteenIntegrationTable.Became(resolved, ox);
        Assert.NotEqual(ox, exiled);
        Assert.False(table.State.Objects.ContainsKey(ox));
        Assert.Equal(CardZone.Exile, table.State.Objects[exiled].Zone.Zone);
        Assert.Equal(exiled, Assert.Single(table.Exiled(P1, WaveFourteenIntegrationTable.OxName)));

        // CR 400.7 with 608.2h: "its controller" is who controlled it when it left, so the
        // opponent draws once and the caster draws nothing.
        Assert.Equal(opponentLibrary - 1, table.LibraryCount(P1));
        Assert.Equal(opponentHand + 1, table.HandCount(P1));
        Assert.Equal(casterLibrary, table.LibraryCount(P0));
        Assert.Single(table.Graveyard(P0, WaveFourteenIntegrationTable.BanishName));

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 601.2c with 702.5b: an Aura's enchant ability is a legality of the cast, so "enchant
    /// creature" announced at a land is refused at announcement (the wave-13 E5g join) rather than
    /// resolving and being buried by CR 704.5m; announced at a creature it enters attached to it,
    /// CR 303.4f.
    /// </summary>
    [Fact]
    [RuleRef("601.2c")]
    public void An_aura_is_refused_at_a_land_its_enchant_ability_does_not_admit_and_attaches_to_a_creature()
    {
        var table = new WaveFourteenIntegrationTable(ClingDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        var land = table.PlayALand(P0);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveFourteenIntegrationTable.OxName)));
        table.PassUntilStackIsEmpty();
        var ox = Assert.Single(table.Battlefield(P0, WaveFourteenIntegrationTable.OxName));

        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);

        // CR 601.2c with 702.5b: the land is on the battlefield but is not what the enchant
        // ability admits, so the announcement is refused and nothing was put on the stack or paid.
        var aura = table.InHand(P0, WaveFourteenIntegrationTable.ClingName);
        Assert.Equal("601.2c", table.Refuse(new CastSpellCommand(P0, aura, Targets: [land])).Rule);
        Assert.Equal(aura, table.InHand(P0, WaveFourteenIntegrationTable.ClingName));
        Assert.True(table.State.In(ZoneKey.Shared(CardZone.Stack)).IsEmpty);
        Assert.Equal(1, table.State.Player(P0).Mana.Total);
        Assert.Empty(table.Graveyard(P0, WaveFourteenIntegrationTable.ClingName));

        // CR 702.5b: the creature is admitted, so the same spell is legal at it.
        var cast = table.Do(new CastSpellCommand(P0, aura, Targets: [ox]));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;
        Assert.Equal([ox], table.State.Objects[spell].Stack!.Targets);

        var resolved = table.PassUntilStackIsEmpty();
        var permanent = WaveFourteenIntegrationTable.Became(resolved, spell);

        // CR 303.4f: it enters attached to the creature it was cast at, and to no player.
        Assert.Equal(CardZone.Battlefield, table.State.Objects[permanent].Zone.Zone);
        Assert.Equal(ox, table.State.Objects[permanent].AttachedTo);
        Assert.Null(table.State.Objects[permanent].AttachedToPlayer);

        // CR 704.5m runs on every priority pass and it is still attached a whole turn later.
        table.PassTo(6, Step.PrecombatMain);
        Assert.Equal(permanent, Assert.Single(table.Battlefield(P0, WaveFourteenIntegrationTable.ClingName)));
        Assert.Equal(ox, table.State.Objects[permanent].AttachedTo);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 614.1c with 614.12: "as this land enters, choose a creature type" is part of the entering,
    /// so the land is on the battlefield while its controller is asked (the wave-14 E8d join); the
    /// answer is kept on the permanent (CR 608.2d) and a second ability of the same land reads it —
    /// a creature of the chosen type entering fires it, one of another type does not.
    /// </summary>
    [Fact]
    [RuleRef("614.1c")]
    public void A_land_that_chooses_a_creature_type_as_it_enters_keeps_it_for_its_other_ability()
    {
        var table = new WaveFourteenIntegrationTable(HallDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.Do(new Commands.PlayLand(P0, table.InHand(P0, WaveFourteenIntegrationTable.HallName)));

        // CR 614.12 with 101.4: the question is the land's controller's, the land has already
        // entered while it stands, and the question carries the replacement ability's own index.
        var question = table.State.Pending;
        Assert.NotNull(question);
        Assert.Equal(ChoiceKinds.Value, question.Kind);
        Assert.Equal(P0, question.Player);
        var hall = Assert.Single(table.Battlefield(P0, WaveFourteenIntegrationTable.HallName));
        Assert.Equal(hall, question.Context.Source);
        Assert.Equal(WaveFourteenIntegrationTable.HallReplacementIndex, question.Context.AbilityIndex);
        Assert.Contains(question.Options, o => o.Id == "Goblin");

        // CR 608.2d: the answer is written on the permanent under the card's own key.
        var answered = table.Do(new AnswerChoice(P0, question.Id, ["Goblin"]));
        var chosen = Assert.Single(answered.OfType<ValueChosen>());
        Assert.Equal(hall, chosen.Object);
        Assert.Equal("Goblin", table.State.Objects[hall].Chosen[WaveFourteenIntegrationTable.TypeKey]);
        Assert.Null(table.State.Pending);
        Assert.Equal(P0, table.State.Priority.Holder);

        // CR 120.1: the creatures arrive as the shuffled library deals them, so the walk goes on
        // to the first main phase that holds the Goblin; the hall alone pays for a {1} creature.
        var turn = 1;
        do
        {
            turn += 2;
            table.PassTo(turn, Step.PrecombatMain);
        }
        while (table.Hand(P0, WaveFourteenIntegrationTable.RaiderName).IsEmpty);

        // CR 608.2d with 603.2: the trigger of the same land reads the chosen type, so the Goblin
        // entering fires it and its controller gains 1 life.
        table.TapNamed(P0, WaveFourteenIntegrationTable.HallName);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveFourteenIntegrationTable.RaiderName)));
        table.PassUntilStackIsEmpty();
        Assert.Single(table.Battlefield(P0, WaveFourteenIntegrationTable.RaiderName));
        Assert.Equal(21, table.State.Player(P0).Life);

        // The other half of the filter: an Ox is not the chosen type, so nothing fires. The hall
        // untaps with the next turn (CR 502.2) and pays again.
        do
        {
            turn += 2;
            table.PassTo(turn, Step.PrecombatMain);
        }
        while (table.Hand(P0, WaveFourteenIntegrationTable.OxName).IsEmpty);

        table.TapNamed(P0, WaveFourteenIntegrationTable.HallName);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveFourteenIntegrationTable.OxName)));
        table.PassUntilStackIsEmpty();
        Assert.Single(table.Battlefield(P0, WaveFourteenIntegrationTable.OxName));
        Assert.Equal(21, table.State.Player(P0).Life);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 106.6a with 608.2d: "add one mana of any color; when that mana is spent to cast a spell,
    /// draw a card" activated with a colour announced. Whether the ability used the stack is not
    /// asserted (E3j is changing that this wave); once every resolution has run the pool holds the
    /// announced colour under the ability's key, the delayed ability exists (CR 603.7a), and spending
    /// that mana on a spell fires it.
    /// </summary>
    [Fact]
    [RuleRef("106.6a")]
    public void An_any_colour_activation_with_a_delayed_trigger_adds_the_announced_colour_and_fires_when_it_is_spent()
    {
        var table = new WaveFourteenIntegrationTable(SpringDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        var spring = table.PlayALand(P0, WaveFourteenIntegrationTable.SpringName);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0, WaveFourteenIntegrationTable.GroveName);

        // CR 608.2d with 602.2: blue is announced with the activation on a table of green lands.
        table.Do(new ActivateAbility(P0, spring, 0, ManaColors: "U"));
        table.PassUntilStackIsEmpty();

        // CR 106.6a: the pool holds one blue carrying the key, and one delayed ability per mana
        // produced watches it (CR 603.7a).
        Assert.True(table.State.Objects[spring].Tapped);
        Assert.Equal(1, table.State.Player(P0).Mana.Total);
        var block = Assert.Single(table.State.Player(P0).Mana.Provenance);
        Assert.Equal(Color.Blue, block.Color);
        Assert.Equal(WaveFourteenIntegrationTable.ManaKey + "#0", block.Key);
        Assert.NotEmpty(table.State.DelayedTriggers);

        // CR 106.6a with 603.7: spending that mana on a spell is the event the delayed ability
        // watches; its body resolves and the card is drawn.
        var libraryBefore = table.LibraryCount(P0);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveFourteenIntegrationTable.DrakeName)));
        table.PassUntilStackIsEmpty();

        Assert.Single(table.Battlefield(P0, WaveFourteenIntegrationTable.DrakeName));
        Assert.Equal(0, table.State.Player(P0).Mana.Total);
        Assert.Equal(libraryBefore - 1, table.LibraryCount(P0));
        Assert.Empty(table.State.DelayedTriggers);

        // The other half of the rule: green from the plain land carries no key, so nothing fires.
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0, WaveFourteenIntegrationTable.GroveName);
        table.TapNamed(P0, WaveFourteenIntegrationTable.GroveName);
        Assert.DoesNotContain(table.State.Player(P0).Mana.Provenance, p => p.Key is not null);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// The log is the game: written, read back and folded from nothing, it is the state the engine
    /// is holding. A log that will not round-trip cannot be stored, and a replay that disagrees
    /// means some subsystem changed state without an event — which is exactly what a new join is
    /// most likely to do.
    /// </summary>
    private static void AssertTheLogIsTheGame(WaveFourteenIntegrationTable table)
    {
        var json = EventLogSerializer.Write(table.Log);
        var read = EventLogSerializer.Read(json);

        Assert.Equal(table.Log.Count, read.Count);
        Assert.Equal(json, EventLogSerializer.Write(read));
        Assert.Equal(Describe(table.State), Describe(GameReducer.Replay(table.Log)));
        Assert.Equal(Describe(table.State), Describe(GameReducer.Replay(read)));
    }

    /// <summary>
    /// Everything about a state a player could see or a rule could ask, as text, so a mismatch
    /// reads as a diff: life, the mana pool's provenance, the values an object has chosen, what an
    /// Aura is attached to, the delayed abilities and every zone's contents.
    /// </summary>
    private static string Describe(GameState state) => string.Join(
        "\n",
        [
            $"turn {state.Turn.Number} {state.Turn.Step} active {state.Turn.ActivePlayer} order {state.Turn.TurnOrderIndex}",
            $"priority {state.Priority.Holder} passed [{string.Join(",", state.Priority.PassedInSuccession)}]",
            $"pending {state.Pending?.Kind ?? "none"} {state.Pending?.Player.ToString() ?? string.Empty}",
            $"outcome {(state.Outcome is null ? "none" : $"{string.Join(",", state.Outcome.Winners)} draw {state.Outcome.Draw}")}",
            $"next object {state.NextObjectId} timestamp {state.NextTimestamp} choice {state.NextChoiceId}",
            $"delayed [{string.Join(",", state.DelayedTriggers.Select(d => d.GetType().Name))}]",
            .. state.Players.Select(p =>
                $"player {p.Id} life {p.Life} lost {p.HasLost} mana {p.Mana.Total} lands {p.LandsPlayedThisTurn}"
                + $" provenance [{string.Join(",", p.Mana.Provenance.Select(v => $"{v.Key}:{v.Color}:{v.Amount}:{v.Source}"))}]"),
            .. state.Objects.Values.OrderBy(o => o.Id.Value).Select(o =>
                $"object {o.Id} {o.Definition.Name} in {o.Zone.Zone}/{o.Zone.Player} owner {o.Owner} tapped {o.Tapped} sick {o.SummoningSick} damage {o.Damage}"
                + $" attached {o.AttachedTo}/{o.AttachedToPlayer}"
                + $" chosen [{string.Join(",", o.Chosen.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))}]"),
            .. state.ActiveEffects.OrderBy(e => e.Id).Select(e =>
                $"effect {e.Id} {e.Effect.GetType().Name} by {e.Controller} objects [{string.Join(",", e.AppliesTo)}] players [{string.Join(",", e.AppliesToPlayers)}]"),
            .. state.Zones.OrderBy(z => z.Key.Zone).ThenBy(z => z.Key.Player?.Value ?? -1).Select(z =>
                $"zone {z.Key.Zone}/{z.Key.Player} [{string.Join(",", z.Value)}]"),
        ]);

    /// <summary>Lands and the two keyed-player sorceries.</summary>
    private static ImmutableList<string> DrainDeck() =>
    [
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.GroveId, 10),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.DrainId, 7),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.EchoId, 7),
    ];

    /// <summary>Lands and the instant that exiles a creature and lets its controller draw.</summary>
    private static ImmutableList<string> BanishDeck() =>
    [
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.BanishId, 10),
    ];

    /// <summary>Lands and a creature for the opponent to put on the battlefield.</summary>
    private static ImmutableList<string> OxDeck() =>
    [
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.OxId, 10),
    ];

    /// <summary>Lands, a creature to enchant and the Aura that enchants only creatures.</summary>
    private static ImmutableList<string> ClingDeck() =>
    [
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.GroveId, 10),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.OxId, 7),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.ClingId, 7),
    ];

    /// <summary>Green lands, the hall that chooses a type, and a creature of each type to enter after it.</summary>
    private static ImmutableList<string> HallDeck() =>
    [
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.GroveId, 8),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.HallId, 6),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.RaiderId, 5),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.OxId, 5),
    ];

    /// <summary>Green lands, the spring that adds any colour with a key, and a blue creature only it can pay for.</summary>
    private static ImmutableList<string> SpringDeck() =>
    [
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.GroveId, 8),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.SpringId, 8),
        .. Enumerable.Repeat(WaveFourteenIntegrationTable.DrakeId, 8),
    ];

    /// <summary>Only lands: the opponent plays one every turn, so nothing else is ever decided, CR 514.1.</summary>
    private static ImmutableList<string> LandDeck() =>
        [.. Enumerable.Repeat(WaveFourteenIntegrationTable.GroveId, 24)];
}
