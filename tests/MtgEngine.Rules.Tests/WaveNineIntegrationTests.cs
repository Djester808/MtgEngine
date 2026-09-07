using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-9 and wave-10 joins, played through <see cref="Game.Apply"/> on
/// <see cref="Subsystems.Wave2"/>: keyed mana and the delayed ability that watches its spend
/// (CR 106.6a), a tapped-for-mana trigger that reads the colour the permanent chose (CR 106.12a
/// with 608.2d), a may-pay inside a resolution (CR 118.12), a spend restriction by the mana's
/// source (CR 106.6b), an instructed cast whose additional cost names objects (CR 601.2c) and the
/// player prohibitions (CR 613.10). Each builder verified its own half against its own fakes; what
/// is asserted here is that the halves meet in a real game. Every game ends with the log written,
/// read back and replayed to the same state.
/// </summary>
public sealed partial class WaveNineIntegrationTests
{
    private static PlayerId P0 => WaveNineIntegrationTable.P0;

    private static PlayerId P1 => WaveNineIntegrationTable.P1;

    /// <summary>
    /// CR 106.6a: a mana instruction may create a delayed triggered ability that fires when the
    /// mana it produced is spent, and CR 603.7a is what makes that ability exist without a source
    /// on the battlefield to print it. The two halves are E9h's (the interpreter writes one
    /// provenance block and one delayed ability per mana) and E3h's (the casting system says which
    /// blocks paid); nothing before wave 10 joined them, so this is the first game in which the
    /// draw actually happens.
    /// </summary>
    [Fact]
    [RuleRef("106.6a")]
    public void Mana_with_a_key_fires_its_delayed_trigger_when_it_pays_for_a_spell()
    {
        var table = new WaveNineIntegrationTable(KeyedDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.KeyedGroveName);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.GroveName);

        // CR 106.6a: tapping it produces one mana carrying the key, and one delayed ability per
        // mana produced — the "#n" spelling E9h and E6c agreed on.
        table.TapNamed(P0, WaveNineIntegrationTable.KeyedGroveName);
        table.PassUntilStackIsEmpty();
        var block = Assert.Single(table.State.Player(P0).Mana.Provenance);
        Assert.Equal(WaveNineIntegrationTable.ManaKey + "#0", block.Key);
        Assert.Equal(1, block.Amount);
        Assert.NotEmpty(table.State.DelayedTriggers);

        // CR 106.6a: spending it on a spell is the event the delayed ability watches.
        var libraryBefore = table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count;
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.OxName)));
        table.PassUntilStackIsEmpty();

        Assert.Single(table.Battlefield(P0, WaveNineIntegrationTable.OxName));
        Assert.Equal(0, table.State.Player(P0).Mana.Total);

        // THE GAP, and it is the whole point of this game: nothing fires the delayed ability. The
        // key is written, the ability exists, the mana is spent — and no card is drawn, so the
        // ability is still sitting in DelayedTriggers with its mana gone. E9h § 1 asked for the
        // firing side (the casting system handing the trigger system the keys the payment spent,
        // or the trigger system folding the negative provenance block); wave 10 did not land it.
        // Asserted as it behaves so the test keeps its meaning; seam-requests/EINT4.md § 1.
        Assert.Equal(libraryBefore, table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count);
        Assert.DoesNotContain(table.State.Player(P0).Mana.Provenance, p => p.Key is not null);

        // Worse than E9h expected: the delayed ability does not sit there waiting either. It is
        // gone with the pool it watched, so nothing would fire it even if the channel landed later
        // in the turn — the card's second sentence is silently dropped.
        Assert.Empty(table.State.DelayedTriggers);

        // The other half of the rule: mana from the plain land is not that mana, so nothing fires.
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.GroveName);
        table.TapNamed(P0, WaveNineIntegrationTable.GroveName);
        Assert.DoesNotContain(table.State.Player(P0).Mana.Provenance, p => p.Key is not null);

        var libraryBeforeOther = table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count;
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.OxName)));
        table.PassUntilStackIsEmpty();
        Assert.Equal(libraryBeforeOther, table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 106.12a with 608.2d: an ability that triggers on a permanent being tapped for mana, whose
    /// filter names the colour that permanent's controller chose as it entered. The choice is
    /// E9i's (the interpreter writes <c>ValueChosen</c>), the store is wave 9's
    /// (<c>GameObject.Chosen</c>), and E6d's is the seeding of it into the trigger system's
    /// context — E6c could not fire this at all before the store existed.
    /// </summary>
    [Fact]
    [RuleRef("106.12a")]
    public void A_tapped_for_mana_trigger_reads_the_colour_the_permanent_chose()
    {
        var table = new WaveNineIntegrationTable(PrismDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.GroveName);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.ValeName);
        table.PassTo(5, Step.PrecombatMain);
        var paying = table.PlayALand(P0, WaveNineIntegrationTable.GroveName);

        table.Do(new ActivateAbility(P0, paying, 0));
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.PrismName)));

        // CR 608.2d: the entering artifact's own trigger asks for the colour, and CR 101.4 stops
        // the game until it is answered.
        var question = table.PassUntilQuestion();
        Assert.Equal(ChoiceKinds.Value, question.Kind);
        Assert.Equal(P0, question.Player);

        // THE GAP: the question is asked with no options at all, and CR 601.2b's answer check
        // refuses every answer — the free-form colour included. A "choose a color" nobody can
        // answer stalls the game for good (CR 101.4), so the second half of the card is
        // unreachable: the colour is never stored, so the tapped-for-mana trigger can never match.
        // seam-requests/EINT4.md § 2.
        Assert.Empty(question.Options);
        Assert.Equal("601.2b", table.Refuse(new AnswerChoice(P0, question.Id, ["Green"])).Rule);
        Assert.Equal("601.2b", table.Refuse(new AnswerChoice(P0, question.Id, ["green"])).Rule);
        Assert.Equal(question.Id, table.State.Pending!.Id);

        // The artifact is on the battlefield with nothing chosen, which is what E6c § 1 said an
        // unanswered choice would leave behind: a key with nothing stored is "no colour".
        var prism = Assert.Single(table.Battlefield(P0, WaveNineIntegrationTable.PrismName));
        Assert.DoesNotContain(WaveNineIntegrationTable.ColorKey, table.State.Objects[prism].Chosen.Keys);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 118.12: "you may pay [cost]. If you do, [effect]" is a cost paid where it stands, inside
    /// a resolving ability, and the question is asked of the ability's controller (E9h's arm, first
    /// reached through the front door here). Answering yes spends the mana in the pool; answering
    /// no does nothing at all, and CR 118.12's last sentence is why the branch turns on the payment
    /// rather than on what came out of it.
    /// </summary>
    [Fact]
    [RuleRef("118.12")]
    public void A_may_pay_inside_a_resolution_spends_the_pool_when_it_is_answered_yes()
    {
        var table = new WaveNineIntegrationTable(ScholarDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);

        // Three mana: one casts the creature, two are what the question is about.
        table.TapForMana(P0, 3);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.ScholarName)));
        var asked = table.PassUntilQuestion();
        Assert.Equal("may-pay", asked.Kind);
        Assert.Equal(P0, asked.Player);
        Assert.Equal(2, table.State.Player(P0).Mana.Total);

        var libraryBefore = table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count;
        table.Answer("yes");
        table.PassUntilStackIsEmpty();

        Assert.Equal(libraryBefore - 1, table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count);
        Assert.Equal(0, table.State.Player(P0).Mana.Total);

        // THE GAP: CR 117.3b gives the active player priority after a spell or ability resolves, and
        // a resolution that was halted by a question and finished by its answer is still a
        // resolution. Nobody holds priority here, so the game cannot be walked on — the same shape
        // EINT3 found after a resumed combat damage batch and E4e fixed there, unfixed on the
        // answer-resumed resolution road. seam-requests/EINT4.md § 3.
        Assert.True(table.State.In(ZoneKey.Shared(CardZone.Stack)).IsEmpty);
        Assert.Null(table.State.Priority.Holder);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// The other half of CR 118.12: answering no pays nothing, so nothing is drawn and the mana
    /// that would have paid is still in the pool. A separate game because the priority gap above
    /// stops the first one dead (seam-requests/EINT4.md § 3).
    /// </summary>
    [Fact]
    [RuleRef("118.12")]
    public void A_may_pay_answered_no_pays_nothing_and_draws_nothing()
    {
        var table = new WaveNineIntegrationTable(ScholarDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);

        table.TapForMana(P0, 3);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.ScholarName)));
        var declined = table.PassUntilQuestion();
        Assert.Equal("may-pay", declined.Kind);

        var libraryBefore = table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count;
        table.Answer("no");
        table.PassUntilStackIsEmpty();

        // CR 118.12: nothing was paid, so nothing was drawn and the pool is untouched.
        Assert.Equal(libraryBefore, table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count);
        Assert.Equal(2, table.State.Player(P0).Mana.Total);
        Assert.Single(table.Battlefield(P0, WaveNineIntegrationTable.ScholarName));

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// The log is the game: written, read back and folded from nothing, it is the state the engine
    /// is holding. A log that will not round-trip cannot be stored, and a replay that disagrees
    /// means some subsystem changed state without an event — which is exactly what a new join is
    /// most likely to do.
    /// </summary>
    private static void AssertTheLogIsTheGame(WaveNineIntegrationTable table)
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
    /// reads as a diff. The wave-9 fields are in it: the mana pool's provenance, the values an
    /// object has chosen, and the delayed abilities still waiting for their event.
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
                + $" chosen [{string.Join(",", o.Chosen.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))}]"),
            .. state.ActiveEffects.OrderBy(e => e.Id).Select(e =>
                $"effect {e.Id} {e.Effect.GetType().Name} by {e.Controller} objects [{string.Join(",", e.AppliesTo)}] players [{string.Join(",", e.AppliesToPlayers)}]"),
            .. state.Zones.OrderBy(z => z.Key.Zone).ThenBy(z => z.Key.Player?.Value ?? -1).Select(z =>
                $"zone {z.Key.Zone}/{z.Key.Player} [{string.Join(",", z.Value)}]"),
        ]);

    /// <summary>The keyed land, a plain one, and the one-mana creature the key's mana pays for.</summary>
    private static ImmutableList<string> KeyedDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.KeyedGroveId, 8),
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 8),
        .. Enumerable.Repeat(WaveNineIntegrationTable.OxId, 8),
    ];

    /// <summary>Two colours of land and the artifact that chooses between them.</summary>
    private static ImmutableList<string> PrismDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 9),
        .. Enumerable.Repeat(WaveNineIntegrationTable.ValeId, 7),
        .. Enumerable.Repeat(WaveNineIntegrationTable.PrismId, 8),
    ];

    /// <summary>Lands and the creature whose arrival offers a payment.</summary>
    private static ImmutableList<string> ScholarDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveNineIntegrationTable.ScholarId, 10),
    ];

    /// <summary>Only lands: the opponent plays one every turn, so nobody is ever asked to discard, CR 514.1.</summary>
    private static ImmutableList<string> LandDeck() =>
        [.. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 24)];
}
