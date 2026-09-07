using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;
using SpellCastEvent = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-10 joins, played through <see cref="Game.Apply"/> on <see cref="Subsystems.Wave2"/>: a
/// tapped-for-mana trigger that adds a mana of the colour the firing produced (CR 106.12a with
/// 608.2d), an Aura whose enchant ability names a player rather than an object (CR 601.2c with
/// 702.5b), and an activation that chooses the colour of "add one mana of any color" (CR 608.2d).
/// Each builder verified its own half against its own fakes; what is asserted here is that the
/// halves meet in a real game. Every game ends with the log written, read back and replayed to the
/// same state.
/// </summary>
public sealed partial class WaveTenIntegrationTests
{
    private static PlayerId P0 => WaveTenIntegrationTable.P0;

    private static PlayerId P1 => WaveTenIntegrationTable.P1;

    /// <summary>
    /// CR 106.12a with 608.2d: "whenever a land you control is tapped for green mana, add an
    /// additional one mana of that color". E6d's half is the firing carrying the colour it produced
    /// under <c>TriggerSystem.TappedForManaColor</c>; E9i's is the interpreter reading a chosen
    /// colour back out of the run's context. The green land makes two green; the white land is not
    /// what the trigger asks for, so it makes one white and nothing more.
    /// </summary>
    [Fact]
    [RuleRef("106.12a")]
    public void A_tapped_for_mana_trigger_adds_a_mana_of_the_colour_the_firing_produced()
    {
        var table = new WaveTenIntegrationTable(FacetDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0, WaveTenIntegrationTable.GroveName);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0, WaveTenIntegrationTable.ValeName);
        table.PassTo(5, Step.PrecombatMain);
        var paying = table.PlayALand(P0, WaveTenIntegrationTable.GroveName);

        table.Do(new ActivateAbility(P0, paying, 0));
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.FacetName)));
        table.PassUntilStackIsEmpty();
        Assert.Single(table.Battlefield(P0, WaveTenIntegrationTable.FacetName));

        // CR 106.12a: the green land is tapped, the artifact's trigger fires, and its resolution
        // adds one more of the colour that was produced — two green in the pool, CR 106.4.
        table.PassTo(7, Step.PrecombatMain);
        table.PlayALand(P0, WaveTenIntegrationTable.GroveName);
        table.TapNamed(P0, WaveTenIntegrationTable.GroveName);
        table.PassUntilStackIsEmpty();

        Assert.Equal(2, table.State.Player(P0).Mana.Total);
        Assert.All(table.State.Player(P0).Mana.Provenance, block => Assert.Equal(Color.Green, block.Color));

        // The other half of the filter: white is not the colour the trigger asks for, so tapping
        // the white land adds one white and fires nothing (CR 106.12a's "of that color").
        table.TapNamed(P0, WaveTenIntegrationTable.ValeName);
        table.PassUntilStackIsEmpty();

        Assert.Equal(3, table.State.Player(P0).Mana.Total);
        Assert.Single(table.State.Player(P0).Mana.Provenance, block => block.Color == Color.White);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// The same card spelled the way <c>TappedForMana</c>'s own documentation says the two halves
    /// agree — an <c>AddMana</c> whose <c>ManaProduction.ChosenColorKey</c> is the trigger system's
    /// key and nothing else set. THE GAP: <c>EffectInterpreter.PoolFor</c> reads a chosen colour
    /// only when <c>ChosenColor</c> is <em>also</em> true, so the documented spelling reaches no
    /// interpreter arm and the resolution throws <see cref="NotImplementedException"/> out of
    /// <see cref="Game.Apply"/> — a crash, not a refusal. The linter has no rule that would catch
    /// it (L015 only forbids both colour fields on the trigger), so a lint-clean card can do this.
    /// Asserted as it behaves so the test keeps its meaning; seam-requests/EINT5.md § 1.
    /// </summary>
    [Fact]
    [RuleRef("608.2d")]
    public void The_documented_chosen_colour_key_spelling_reaches_no_interpreter_arm()
    {
        var table = new WaveTenIntegrationTable(LensDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0, WaveTenIntegrationTable.GroveName);
        table.PassTo(3, Step.PrecombatMain);
        var paying = table.PlayALand(P0, WaveTenIntegrationTable.GroveName);

        table.Do(new ActivateAbility(P0, paying, 0));
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.LensName)));
        table.PassUntilStackIsEmpty();
        Assert.Single(table.Battlefield(P0, WaveTenIntegrationTable.LensName));

        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0, WaveTenIntegrationTable.GroveName);
        table.TapNamed(P0, WaveTenIntegrationTable.GroveName);

        // The trigger itself is fine — it matched, it is on the stack, and it carries the colour.
        Assert.False(table.State.In(ZoneKey.Shared(CardZone.Stack)).IsEmpty);

        Assert.Throws<NotImplementedException>(table.PassUntilStackIsEmpty);

        // The log up to the crash is still a game: nothing half-applied was written to it.
        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 601.2c with 702.5b: an Aura's enchant ability is a legality of the cast, so an "enchant
    /// creature" Aura announced at a player is refused at announcement rather than resolving and
    /// being buried by CR 704.5m. The "enchant player" twin, whose <c>KeywordAbility.OfPlayer</c>
    /// names a player filter, resolves attached to that player (E3f's event, E5c's rule).
    /// </summary>
    [Fact]
    [RuleRef("601.2c")]
    public void An_object_restricted_aura_is_refused_at_a_player_and_the_player_aura_attaches()
    {
        var table = new WaveTenIntegrationTable(AuraDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);

        // CR 601.2c: the enchant ability names creatures, the announcement names a player, and the
        // casting system rules the target out before anything is put on the stack.
        var restricted = table.InHand(P0, WaveTenIntegrationTable.CreatureAuraName);
        Assert.Equal("601.2c", table.Refuse(new CastSpellCommand(P0, restricted, TargetPlayers: [P1])).Rule);
        Assert.NotEmpty(table.Hand(P0, WaveTenIntegrationTable.CreatureAuraName));
        Assert.Empty(table.Battlefield(P0, WaveTenIntegrationTable.CreatureAuraName));
        Assert.Empty(table.Graveyard(P0, WaveTenIntegrationTable.CreatureAuraName));

        // CR 702.5b: the twin's enchant ability names a player, so the same announcement is legal.
        var card = table.InHand(P0, WaveTenIntegrationTable.PlayerAuraName);
        var cast = table.Do(new CastSpellCommand(P0, card, TargetPlayers: [P1]));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;
        Assert.Equal([P1], table.State.Objects[spell].Stack!.TargetPlayers);

        var resolved = table.PassRound();
        var aura = WaveTenIntegrationTable.Became(resolved, spell);

        // CR 303.4f: attached to the player it was cast at, and to no object.
        Assert.Equal(CardZone.Battlefield, table.State.Objects[aura].Zone.Zone);
        Assert.Equal(P1, table.State.Objects[aura].AttachedToPlayer);
        Assert.Null(table.State.Objects[aura].AttachedTo);

        // CR 704.5m runs on every priority pass and it is still there a whole turn later.
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);
        Assert.Equal(aura, Assert.Single(table.Battlefield(P0, WaveTenIntegrationTable.PlayerAuraName)));
        Assert.Equal(P1, table.State.Objects[aura].AttachedToPlayer);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 608.2d: "add one mana of any color" chooses its colour as the ability is activated, and
    /// P13's <c>ActivateAbility.ManaColors</c> is where the choice is announced. E3h's half is the
    /// casting system reading it; without a colour there is no choice to apply, and the activation
    /// is refused naming the same rule rather than adding some default.
    /// </summary>
    [Fact]
    [RuleRef("608.2d")]
    public void An_any_colour_activation_adds_the_colour_it_announced_and_is_refused_without_one()
    {
        var table = new WaveTenIntegrationTable(FontDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0, WaveTenIntegrationTable.GroveName);
        table.PassTo(3, Step.PrecombatMain);
        var paying = table.PlayALand(P0, WaveTenIntegrationTable.GroveName);

        table.Do(new ActivateAbility(P0, paying, 0));
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.FontName)));
        table.PassUntilStackIsEmpty();
        var font = Assert.Single(table.Battlefield(P0, WaveTenIntegrationTable.FontName));

        table.PassTo(5, Step.PrecombatMain);

        // CR 608.2d: no colour announced, so the effect has no choice to apply and the activation
        // is refused. The permanent is untapped afterwards — a refused activation costs nothing.
        Assert.Equal("608.2d", table.Refuse(new ActivateAbility(P0, font, 0)).Rule);
        Assert.False(table.State.Objects[font].Tapped);
        Assert.Equal(0, table.State.Player(P0).Mana.Total);

        // CR 608.2d with 602.2: the announced colour is the mana that is added.
        table.Do(new ActivateAbility(P0, font, 0, ManaColors: "W"));

        Assert.True(table.State.Objects[font].Tapped);
        Assert.Equal(1, table.State.Player(P0).Mana.Total);
        Assert.Equal(Color.White, Assert.Single(table.State.Player(P0).Mana.Provenance).Color);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// The log is the game: written, read back and folded from nothing, it is the state the engine
    /// is holding. A log that will not round-trip cannot be stored, and a replay that disagrees
    /// means some subsystem changed state without an event — which is exactly what a new join is
    /// most likely to do.
    /// </summary>
    private static void AssertTheLogIsTheGame(WaveTenIntegrationTable table)
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
    /// reads as a diff. The wave-10 fields are in it: the mana pool's provenance and colours, the
    /// values an object has chosen, what an Aura is attached to, and the effects standing on
    /// players.
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

    /// <summary>Two colours of land and the artifact whose trigger adds one more of the colour produced.</summary>
    private static ImmutableList<string> FacetDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 9),
        .. Enumerable.Repeat(WaveTenIntegrationTable.ValeId, 7),
        .. Enumerable.Repeat(WaveTenIntegrationTable.FacetId, 8),
    ];

    /// <summary>The same table with the documented spelling of the same card.</summary>
    private static ImmutableList<string> LensDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveTenIntegrationTable.LensId, 10),
    ];

    /// <summary>Lands and the two Auras: one that enchants a creature, one that enchants a player.</summary>
    private static ImmutableList<string> AuraDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 8),
        .. Enumerable.Repeat(WaveTenIntegrationTable.CreatureAuraId, 8),
        .. Enumerable.Repeat(WaveTenIntegrationTable.PlayerAuraId, 8),
    ];

    /// <summary>Lands and the artifact that adds one mana of any color.</summary>
    private static ImmutableList<string> FontDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveTenIntegrationTable.FontId, 10),
    ];

    /// <summary>Only lands: the opponent plays one every turn, so nothing else is ever decided, CR 514.1.</summary>
    private static ImmutableList<string> LandDeck() =>
        [.. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 24)];
}
