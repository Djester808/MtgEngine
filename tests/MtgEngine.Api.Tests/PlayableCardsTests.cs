using System.Reflection;
using MtgEngine.Api.Cards;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The wire between the card compiler and the game that plays cards.
/// </summary>
/// <remarks>
/// For most of this engine's life there was no such wire. <c>Program.cs</c> registered
/// <see cref="CardPool"/> — five basic lands and a small curated set — as the only
/// <see cref="IAbilitySource"/>, while <see cref="CompiledPool"/> was constructed in the test
/// projects and nowhere else. Measured at the time: the deck gate admitted 917 cards while the
/// compiler read every line of 15,262. Every reader written for this engine was exercised by tests
/// and by nothing that plays a game.
/// </remarks>
public sealed class PlayableCardsTests
{
    private static PlayableCards Source() => new(new CardPool(), new CompiledPool());

    private static CardDefinition Card(string name, string text, CardType types) => new()
    {
        OracleId = "oracle-playable-" + name.ToLowerInvariant().Replace(' ', '-'),
        Name = name,
        OracleText = text,
        CardTypes = types,
    };

    /// <summary>
    /// Every member of the interface is delegated, not left to its default.
    /// </summary>
    /// <remarks>
    /// This is the test that matters most here, and it is not tidiness. Every default on
    /// <see cref="IAbilitySource"/> returns "nothing" — no spell, no triggers, no statics. A member
    /// forgotten in <see cref="PlayableCards"/> would therefore make every compiled card quietly
    /// lose one of the things it does, while still looking perfectly playable from the outside.
    /// That is the exact failure this project has found five times by hand and can detect no other
    /// way, so adding a member to the interface should fail this test rather than ship silently.
    /// </remarks>
    [Fact]
    public void Every_ability_the_interface_can_answer_is_delegated()
    {
        var declared = typeof(PlayableCards)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = typeof(IAbilitySource)
            .GetInterfaces()
            .Append(typeof(IAbilitySource))
            .SelectMany(i => i.GetMethods())
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "PlayableCards does not delegate these, so they fall back to the interface default "
                + "and every compiled card silently loses them:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>A card the compiler reads completely is playable.</summary>
    [Fact]
    public void A_card_whose_every_line_reads_is_admitted()
    {
        var bolt = Card("Playable Bolt Test", "~ deals 3 damage to any target.", CardType.Instant);

        Assert.False(Source().Refuses(bolt));
        Assert.NotNull(Source().SpellOf(bolt));
    }

    /// <summary>
    /// A card with one ability read and the rest of its text unread is refused.
    /// </summary>
    /// <remarks>
    /// The gate used to ask whether <em>any</em> of five ability collections was non-empty, which
    /// let 6,815 half-read cards into games. One compiled ability is not an implemented card.
    /// </remarks>
    [Fact]
    public void A_card_with_an_unread_line_is_refused_even_though_something_compiled()
    {
        var half = Card(
            "Playable Half Test",
            "~ deals 3 damage to any target.\nAbsolutely nothing about this line is readable.",
            CardType.Instant);

        var compiled = CardCompiler.Compile(half);
        Assert.False(compiled.IsComplete);

        // Something did compile — which is exactly why the old question gave the wrong answer.
        Assert.NotNull(compiled.Spell);
        Assert.True(Source().Refuses(half));
    }

    /// <summary>A hand-written script wins over the compiler for the same card.</summary>
    /// <remarks>
    /// The curated cards were implemented deliberately and several express things the compiler
    /// still cannot, so falling back to their compiled text would be a downgrade. Asserted on a
    /// real member of the pool rather than a fixture, so it keeps meaning something if the pool
    /// changes.
    /// </remarks>
    [Fact]
    public void A_written_script_answers_for_a_card_that_has_one()
    {
        var pool = new CardPool();
        var scripted = StarterCards.All.First();

        var card = new CardDefinition
        {
            OracleId = "oracle-scripted-probe",
            Name = scripted.Name,
            OracleText = "this text is deliberately unreadable by the compiler",
            CardTypes = CardType.Creature,
        };

        Assert.True(pool.Knows(card));
        Assert.False(Source().Refuses(card));
    }
}
