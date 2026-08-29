using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Xunit.Abstractions;

namespace MtgEngine.Rules.Tests;

// Scratch diagnostics for the r10-scopes round. Deleted before commit.
public sealed class WardScratchR10Tests(ITestOutputHelper output)
{
    private static readonly CompiledPool Pool = new();

    [Fact]
    public void Ward_grant_diagnostic()
    {
        var herald = new CardDefinition
        {
            OracleId = "oracle-scratch-ward-herald",
            Name = "Scratch Ward Herald",
            OracleText = "Other creatures you control have ward {2}.",
            CardTypes = CardType.Creature,
            Power = 3,
            Toughness = 3,
        };

        var compiled = CardCompiler.Compile(herald);
        output.WriteLine("complete: " + compiled.IsComplete);
        foreach (var s in compiled.Statics)
            output.WriteLine("static: " + s.Id + " layer " + s.Layer);

        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, TestCards.Deck(40, "Alice")),
                new PlayerSetup(bob, "Bob", 20, TestCards.Deck(40, "Bob")),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: Pool);

        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);

        game.Create(alice, herald, Zone.Battlefield);
        var shielded = game.Create(
            alice, TestCards.Creature("Scratch Shielded Bear", 2, 2), Zone.Battlefield);

        var granted = Characteristics.Of(game.State, Pool, game.State.GetObject(shielded));
        output.WriteLine("granted triggers on bear: "
            + string.Join(", ", granted.GrantedTriggers.Select(t => t.Id)));

        TestCards.PassUntil(
            game,
            () => game.State.ActivePlayerId == bob
                && game.State.CurrentStep == TurnStep.PrecombatMain);

        var bolt = new CardDefinition
        {
            OracleId = "oracle-scratch-ward-bolt",
            Name = "Scratch Ward Bolt",
            OracleText = "~ deals 3 damage to any target.",
            CardTypes = CardType.Instant,
        };

        var mark = game.Log.Count;
        var cast = game.CastSpell(
            bob, TestCards.PutInHand(game, bob, bolt), [Target.ToPermanent(shielded)]);

        output.WriteLine("--- immediately after cast ---");
        output.WriteLine("cast id: " + cast);
        output.WriteLine("stack: " + game.State.Stack.Count);
        output.WriteLine("choice: " + (game.State.Choice?.Kind.ToString() ?? "none"));
        foreach (var e in game.Log.Skip(mark))
            output.WriteLine("log: " + e.GetType().Name + " :: " + e);

        for (var i = 0; i < 12 && game.State.Choice is null; i++)
        {
            if (game.State.Priority.Holder is not { } holder)
                break;

            mark = game.Log.Count;
            game.PassPriority(holder);

            output.WriteLine("--- pass " + i + " by "
                + (holder == alice ? "alice" : "bob") + " ---");

            foreach (var e in game.Log.Skip(mark))
                output.WriteLine("log: " + e.GetType().Name + " :: " + e);
        }

        output.WriteLine("choice: " + (game.State.Choice?.Kind.ToString() ?? "none"));
        output.WriteLine("stack: " + game.State.Stack.Count);
        output.WriteLine("bear on battlefield: " + game.State.Battlefield.Contains(shielded));
    }
}
