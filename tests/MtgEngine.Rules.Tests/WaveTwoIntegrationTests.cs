using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;
using MtgEngine.Rules.Views;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;
using SpellCastEvent = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 2's exit criterion: the subsystems, composed by <see cref="Subsystems.Wave2"/>, play a
/// two-player game of made-up definitions that have abilities, through <see cref="Game.Apply"/>.
/// The wave-1 exit test proved lands and vanilla creatures; this one proves the parts wave 2
/// added — a replacement, a trigger, a targeted spell, a question the game stops for, and the
/// legend rule — and, at each stage, that the view a player is given is not the state, that the
/// actions offered are the actions allowed, and that the log is still the game.
/// </summary>
public sealed class WaveTwoIntegrationTests
{
    private static PlayerId P0 => WaveTwoIntegrationTable.P0;

    private static PlayerId P1 => WaveTwoIntegrationTable.P1;

    [Fact]
    [RuleRef("104.2a")]
    public void A_game_of_cards_with_abilities_is_played_through_commands_to_a_win()
    {
        var table = new WaveTwoIntegrationTable(AggressorDeck(), DefenderDeck());

        // --- turn 1: what a player may do is what the rules would allow, CR 117.1a ---
        table.PassTo(1, Step.PrecombatMain);
        AssertOffersMatchTheRules(table, P0);
        AssertHandsAreHidden(table);
        Assert.Empty(table.Engine.Legal.For(table.State, P0).Castable);
        table.PlayALand(P0);

        // The opponent has no priority, so nothing at all is offered to them, CR 117.1a.
        var idle = table.Engine.Legal.For(table.State, P1);
        Assert.Empty(idle.Castable);
        Assert.Empty(idle.PlayableLands);
        Assert.False(idle.MayPass);

        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);

        // --- turn 3: two lands, and a legend that draws a card as it enters, CR 603.2 ---
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        AssertOffersMatchTheRules(table, P0);
        var firstOx = CastTheOx(table, P0);

        // --- turn 4: the opponent does the same, so there is something to burn ---
        table.PassTo(4, Step.PrecombatMain);
        table.PlayALand(P1);
        table.TapForMana(P1, 2);
        var theirOx = CastTheOx(table, P1);
        AssertHandsAreHidden(table);

        // --- turn 5: a land that enters tapped, and a second legend, CR 704.5j ---
        table.PassTo(5, Step.PrecombatMain);
        AssertEnteringTappedIsTheRule(table);
        var bog = table.PlayALand(P0, WaveTwoIntegrationTable.BogName);
        Assert.Equal(Zone.Battlefield, table.State.Objects[bog].Zone.Zone);

        table.PassTo(6, Step.PrecombatMain);
        table.PlayALand(P1);

        // --- turn 7: a second copy of the legend, and the rule that asks about it ---
        table.PassTo(7, Step.PrecombatMain);
        table.TapForMana(P0, 2);
        var secondOx = CastTheLegendAndKeepIt(table, firstOx);

        // CR 400.7: the one that was not kept is a new object in the graveyard, so the id it had
        // on the battlefield names nothing now.
        Assert.DoesNotContain(firstOx, table.State.Objects.Keys);
        Assert.Equal(secondOx, Assert.Single(table.Battlefield(P0, WaveTwoIntegrationTable.OxName)));
        Assert.Single(
            table.State.Objects.Values,
            o => o.Zone.Zone == Zone.Graveyard && o.Owner == P0 && o.Definition.Name == WaveTwoIntegrationTable.OxName);

        table.PassTo(8, Step.PrecombatMain);
        table.PlayALand(P1);

        // --- turn 9: three damage to a 2/2 kills it, CR 601.2c then 704.5g ---
        table.PassTo(9, Step.PrecombatMain);
        table.TapForMana(P0, 1);
        var killed = Burn(table, new CastSpellCommand(P0, table.InHand(P0, WaveTwoIntegrationTable.BoltName), Targets: [theirOx]));

        var toCreature = Assert.Single(killed.OfType<DamageDealt>());
        Assert.Equal(theirOx, toCreature.ToObject);
        Assert.Equal(3, toCreature.Amount);
        Assert.False(toCreature.Combat);
        Assert.Contains(killed, e => e is ObjectLeftBy { Verb: "destroyed" });
        Assert.Empty(table.Battlefield(P1, WaveTwoIntegrationTable.OxName));
        Assert.Null(table.State.Outcome);

        table.PassTo(10, Step.PrecombatMain);
        table.PlayALand(P1);

        // --- turn 11: the same spell at the player, CR 704.5a then 104.2a ---
        table.PassTo(11, Step.PrecombatMain);
        table.TapForMana(P0, 1);
        var life = table.State.Player(P1).Life;
        var lethal = Burn(table, new CastSpellCommand(P0, table.InHand(P0, WaveTwoIntegrationTable.BoltName), TargetPlayers: [P1]));

        var toPlayer = Assert.Single(lethal.OfType<DamageDealt>());
        Assert.Equal(P1, toPlayer.ToPlayer);
        Assert.Equal(3, toPlayer.Amount);
        Assert.Equal(3, life - table.State.Player(P1).Life);
        Assert.Equal(P1, Assert.Single(table.Log.OfType<PlayerLost>()).Player);
        var ended = Assert.Single(table.Log.OfType<GameEnded>());
        Assert.Equal(P0, Assert.Single(ended.Outcome.Winners));
        Assert.False(ended.Outcome.Draw);

        // CR 104.1: the game is over, and nothing more is accepted or offered.
        Assert.Equal("104.1", table.Refuse(new PassPriority(P0)).Rule);
        Assert.False(table.Engine.Legal.For(table.State, P0).MayPass);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 101.4: a card's "you may" stops the game and asks. The question is raised here, and it
    /// is answered nowhere: resuming a question a card asked needs the interpreter's
    /// <see cref="IResumable"/>, which is wave 3 (E9b) and is not wired into
    /// <see cref="Subsystems.Wave2"/> — so this asserts that the game stops, that the question
    /// belongs to the right player, and that nothing else is accepted while it stands, and stops
    /// short of answering it. See seam-requests/EINT2.md.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_may_trigger_stops_the_game_and_asks_its_controller()
    {
        var table = new WaveTwoIntegrationTable(SageDeck(), DefenderDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);

        // The opponent plays a land every turn too, so nobody is ever asked to discard, CR 514.1.
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);
        table.PassTo(3, Step.PrecombatMain);
        table.TapForMana(P0, 1);

        var card = table.InHand(P0, WaveTwoIntegrationTable.SageName);
        var cast = table.Do(new CastSpellCommand(P0, card));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;

        // CR 608.1 and 603.3: it resolves, enters, and the trigger goes on the stack.
        var resolved = table.PassRound();
        Assert.Contains(resolved, e => e is StackObjectResolved);
        Assert.Single(table.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.NotEqual(spell, table.State.In(ZoneKey.Shared(Zone.Stack))[0]);

        // CR 608.2 and 101.4: resolving the trigger asks, and the game stops there.
        var handBefore = table.State.In(ZoneKey.Of(Zone.Hand, P0)).Count;
        table.PassRound();
        var pending = Assert.IsType<PendingChoice>(table.State.Pending);
        Assert.Equal(ChoiceKinds.May, pending.Kind);
        Assert.Equal(P0, pending.Player);
        Assert.Equal(2, pending.Options.Count);
        Assert.Equal(handBefore, table.State.In(ZoneKey.Of(Zone.Hand, P0)).Count);

        // CR 101.4: while it stands, the only thing anyone may do is answer it.
        Assert.Equal("101.4", table.Refuse(new PassPriority(P0)).Rule);
        var offered = table.Engine.Legal.For(table.State, P0);
        Assert.True(offered.MustAnswerChoice);
        Assert.False(offered.MayPass);
        Assert.Empty(offered.Castable);
        Assert.False(table.Engine.Legal.For(table.State, P1).MustAnswerChoice);

        // And answering it is refused rather than thrown: resuming a question a card asked needs
        // the interpreter's IResumable, so until E9b lands the table is told, the answer is not
        // taken, and the question still stands.
        var unanswerable = table.Refuse(new AnswerChoice(P0, pending.Id, [pending.Options[0].Id]));
        Assert.Equal("101.4", unanswerable.Rule);
        Assert.Equal(pending.Id, table.State.Pending!.Id);

        // The other player is told the game is waiting and on whom, and nothing that was offered.
        var theirs = table.Engine.Views.Project(table.State, P1).Pending;
        Assert.NotNull(theirs);
        Assert.Equal(P0, theirs!.Player);
        Assert.Empty(theirs.Options);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// Cast the ox with the mana in the pool, let it resolve, and let its enters trigger resolve
    /// too: CR 603.2 for the trigger, 603.3 for it going on the stack under its controller, and
    /// 608.2 for the card it draws.
    /// </summary>
    [RuleRef("603.3")]
    private static ObjectId CastTheOx(WaveTwoIntegrationTable table, PlayerId player)
    {
        var card = table.InHand(player, WaveTwoIntegrationTable.OxName);
        var cast = table.Do(new CastSpellCommand(player, card));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;

        var resolved = table.PassRound();
        var permanent = WaveTwoIntegrationTable.Became(resolved, spell);
        var fired = Assert.Single(resolved.OfType<TriggerFired>());
        Assert.Equal(permanent, Assert.Single(fired.Subjects));
        Assert.Equal(player, fired.Controller);

        // CR 603.3: it waits on the stack; the draw is not part of entering.
        var handBefore = table.State.In(ZoneKey.Of(Zone.Hand, player)).Count;
        Assert.Equal(fired.StackObject, Assert.Single(table.State.In(ZoneKey.Shared(Zone.Stack))));

        table.PassRound();
        Assert.Empty(table.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(handBefore + 1, table.State.In(ZoneKey.Of(Zone.Hand, player)).Count);
        return permanent;
    }

    /// <summary>
    /// CR 704.5j: a second legend of the same name under one controller is a state-based action
    /// that asks rather than acts. The answer names the one kept; the rest go to their owners'
    /// graveyards.
    /// </summary>
    [RuleRef("704.5j")]
    private static ObjectId CastTheLegendAndKeepIt(WaveTwoIntegrationTable table, ObjectId already)
    {
        var card = table.InHand(table.State.Turn.ActivePlayer, WaveTwoIntegrationTable.OxName);
        var cast = table.Do(new CastSpellCommand(P0, card));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;

        var resolved = table.PassRound();
        var entered = WaveTwoIntegrationTable.Became(resolved, spell);

        var pending = Assert.IsType<PendingChoice>(table.State.Pending);
        Assert.Equal(ChoiceKinds.LegendRule, pending.Kind);
        Assert.Equal(P0, pending.Player);
        Assert.Equal(1, pending.Min);
        Assert.Equal(1, pending.Max);
        Assert.Equal([already, entered], [.. pending.Options.Select(o => o.Object).OfType<ObjectId>().OrderBy(o => o.Value)]);

        // CR 101.4: nothing else happens until it is answered.
        Assert.Equal("101.4", table.Refuse(new PassPriority(P0)).Rule);

        var keep = pending.Options.Single(o => o.Object == entered);
        var answered = table.Do(new AnswerChoice(P0, pending.Id, [keep.Id]));
        Assert.Contains(answered, e => e is ChoiceAnswered);
        Assert.Null(table.State.Pending);

        // The enters trigger of the one that stayed is still waiting, and still draws.
        var handBefore = table.State.In(ZoneKey.Of(Zone.Hand, P0)).Count;
        table.PassRound();
        Assert.Equal(handBefore + 1, table.State.In(ZoneKey.Of(Zone.Hand, P0)).Count);
        return entered;
    }

    /// <summary>Cast the burn spell with the target it was given and let it resolve, CR 601.2c and 608.2.</summary>
    [RuleRef("601.2c")]
    private static ImmutableList<GameEvent> Burn(WaveTwoIntegrationTable table, CastSpellCommand command)
    {
        var cast = table.Do(command);
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;
        var entry = table.State.Objects[spell].Stack!;
        Assert.Equal(command.Targets ?? [], entry.Targets);
        Assert.Equal(command.TargetPlayers ?? [], entry.TargetPlayers);
        return table.PassRound();
    }

    /// <summary>
    /// CR 601.2 and 305.1: every card the legal actions offer is one the casting system accepts,
    /// and every card in hand it does not offer is one the casting system refuses. The two are
    /// asked separately here — one through <see cref="ILegalActions"/>, one through
    /// <see cref="ICastingSystem.Check"/> — so an offer that is not an action would show.
    /// </summary>
    [RuleRef("117.1a")]
    private static void AssertOffersMatchTheRules(WaveTwoIntegrationTable table, PlayerId player)
    {
        var state = table.State;
        var offered = table.Engine.Legal.For(state, player);
        var hand = state.In(ZoneKey.Of(Zone.Hand, player));

        Assert.Equal(
            [.. hand.Where(id => table.Engine.Casting.Check(state, new CastSpellCommand(player, id)) is null)],
            offered.Castable);
        Assert.Equal(
            [.. hand.Where(id => table.Engine.Casting.Check(state, new Commands.PlayLand(player, id)) is null)],
            offered.PlayableLands);

        // CR 305.9: a land is played, never cast, so no land is ever castable.
        Assert.DoesNotContain(offered.Castable, id => state.Objects[id].Face.Types.Types.HasFlag(CardType.Land));
        Assert.All(offered.PlayableLands, id => Assert.True(state.Objects[id].Face.Types.Types.HasFlag(CardType.Land)));
        Assert.Equal(state.Priority.Holder == player, offered.MayPass);
    }

    /// <summary>
    /// CR 400.2: a hand is a hidden zone. A viewer is given their own hand and the size of every
    /// other one, so no object another player is holding appears anywhere in their view — not by
    /// name, not by oracle id, not as an id they could ask about.
    /// </summary>
    [RuleRef("400.2")]
    private static void AssertHandsAreHidden(WaveTwoIntegrationTable table)
    {
        foreach (var viewer in new[] { P0, P1 })
        {
            var other = viewer == P0 ? P1 : P0;
            var view = table.Engine.Views.Project(table.State, viewer);
            var theirs = table.State.In(ZoneKey.Of(Zone.Hand, other));

            // Everything this viewer is entitled to: their own hand and every public zone. A name
            // they hold themselves is theirs to see, so what must not appear is a name only the
            // other player is holding.
            var maySee = Visible(table, viewer);
            var told = maySee.Select(id => table.State.Objects[id].Definition.Name).ToHashSet(StringComparer.Ordinal);
            var onlyTheirs = theirs
                .Select(id => table.State.Objects[id].Definition.Name)
                .Where(name => !told.Contains(name))
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(table.State.In(ZoneKey.Of(Zone.Hand, viewer)), [.. view.Hand.Select(o => o.Id)]);
            foreach (var seen in Everything(view))
            {
                Assert.DoesNotContain(seen.Id, theirs);
                Assert.Contains(seen.Id, maySee);
                Assert.DoesNotContain(seen.Name, onlyTheirs);
            }

            // CR 400.2 again: the size of a hidden zone is public, and only the size.
            Assert.Equal(theirs.Count, view.Players.Single(p => p.Id == other).HandCount);
            Assert.Equal(table.State.In(ZoneKey.Of(Zone.Library, other)).Count, view.Players.Single(p => p.Id == other).LibraryCount);
        }
    }

    /// <summary>
    /// CR 614.1c: "this land enters tapped" is a replacement effect, and the replacement system
    /// applies it — asked with the move that would put the land onto the battlefield, it hands
    /// back the same move with the permanent tapped. What no subsystem does is offer that move
    /// to it: a land played, a permanent spell resolving and an effect that moves a card all
    /// call <see cref="IZoneSystem.Move"/> directly, so the effect never runs in a real game.
    /// The rule is asserted here where it is implemented; the join is in seam-requests/EINT2.md.
    /// </summary>
    [RuleRef("614.1c")]
    private static void AssertEnteringTappedIsTheRule(WaveTwoIntegrationTable table)
    {
        var state = table.State;
        var card = table.InHand(P0, WaveTwoIntegrationTable.BogName);
        var entering = state.Objects[card] with
        {
            Id = new ObjectId(state.NextObjectId),
            Zone = ZoneKey.Of(Zone.Battlefield, P0),
            Timestamp = state.NextTimestamp,
        };

        var replaced = table.Engine.Replacement.Replace(state, new ObjectMoved(card, entering, -1), P0);
        Assert.True(Assert.Single(replaced.Events.OfType<ObjectMoved>()).To.Tapped);
    }

    /// <summary>
    /// The log is the game: written, read back, and folded from nothing, it is the state the
    /// engine is holding. Both halves matter — a log that will not round-trip cannot be stored,
    /// and a replay that disagrees means some subsystem changed state without an event. Wave 2's
    /// events are in it: a trigger fired, a question asked, a question answered.
    /// </summary>
    private static void AssertTheLogIsTheGame(WaveTwoIntegrationTable table)
    {
        Assert.Contains(table.Log, e => e is TriggerFired);
        Assert.Contains(table.Log, e => e is ChoiceRequested);

        var json = EventLogSerializer.Write(table.Log);
        var read = EventLogSerializer.Read(json);

        Assert.Equal(table.Log.Count, read.Count);
        Assert.Equal(json, EventLogSerializer.Write(read));
        Assert.Equal(Describe(table.State), Describe(GameReducer.Replay(table.Log)));
        Assert.Equal(Describe(table.State), Describe(GameReducer.Replay(read)));
    }

    /// <summary>Every object this viewer is entitled to see: the public zones, CR 400.2, and their own hand.</summary>
    [RuleRef("400.2")]
    private static ImmutableHashSet<ObjectId> Visible(WaveTwoIntegrationTable table, PlayerId viewer)
    {
        var state = table.State;
        var open = new[] { Zone.Battlefield, Zone.Stack, Zone.Graveyard, Zone.Exile, Zone.Command };
        return
        [
            .. state.Objects.Values
                .Where(o => open.Contains(o.Zone.Zone) || (o.Zone.Zone == Zone.Hand && o.Owner == viewer))
                .Select(o => o.Id),
        ];
    }

    private static IEnumerable<ObjectView> Everything(GameView view) =>
        [.. view.Battlefield, .. view.Stack, .. view.Hand, .. view.Exile, .. view.Players.SelectMany(p => p.Graveyard.AddRange(p.Command))];

    /// <summary>
    /// Everything about a state that a player could see or a rule could ask, as text, so a
    /// mismatch reads as a diff rather than as "objects are not equal".
    /// </summary>
    private static string Describe(GameState state) => string.Join(
        "\n",
        [
            $"turn {state.Turn.Number} {state.Turn.Step} active {state.Turn.ActivePlayer} order {state.Turn.TurnOrderIndex}",
            $"priority {state.Priority.Holder} passed [{string.Join(",", state.Priority.PassedInSuccession)}]",
            $"pending {state.Pending?.Kind ?? "none"} {state.Pending?.Player.ToString() ?? string.Empty}",
            $"outcome {(state.Outcome is null ? "none" : $"{string.Join(",", state.Outcome.Winners)} draw {state.Outcome.Draw}")}",
            $"next object {state.NextObjectId} timestamp {state.NextTimestamp} choice {state.NextChoiceId}",
            .. state.Players.Select(p => $"player {p.Id} life {p.Life} lost {p.HasLost} mana {p.Mana.Total} lands {p.LandsPlayedThisTurn} drawn {p.ObjectsDrawnThisTurn}"),
            .. state.Objects.Values.OrderBy(o => o.Id.Value).Select(o =>
                $"object {o.Id} {o.Definition.Name} in {o.Zone.Zone}/{o.Zone.Player} owner {o.Owner} tapped {o.Tapped} sick {o.SummoningSick} damage {o.Damage}"),
            .. state.Zones.OrderBy(z => z.Key.Zone).ThenBy(z => z.Key.Player?.Value ?? -1).Select(z =>
                $"zone {z.Key.Zone}/{z.Key.Player} [{string.Join(",", z.Value)}]"),
        ]);

    /// <summary>Lands to cast with, two legends, and the burn. Weighted so the opening hand holds a start and the draws bring the rest.</summary>
    private static ImmutableList<string> AggressorDeck() =>
    [
        .. Enumerable.Repeat(WaveTwoIntegrationTable.GroveId, 8),
        .. Enumerable.Repeat(WaveTwoIntegrationTable.BogId, 3),
        .. Enumerable.Repeat(WaveTwoIntegrationTable.OxId, 7),
        .. Enumerable.Repeat(WaveTwoIntegrationTable.BoltId, 5),
    ];

    /// <summary>Lands and a creature to be burned: the defender plays a land every turn, so nobody is ever asked to discard, CR 514.1.</summary>
    private static ImmutableList<string> DefenderDeck() =>
    [
        .. Enumerable.Repeat(WaveTwoIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveTwoIntegrationTable.OxId, 4),
    ];

    private static ImmutableList<string> SageDeck() =>
    [
        .. Enumerable.Repeat(WaveTwoIntegrationTable.GroveId, 10),
        .. Enumerable.Repeat(WaveTwoIntegrationTable.SageId, 6),
    ];
}
