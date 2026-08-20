using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Cards;

/// <summary>
/// What the engine knows how to play.
/// </summary>
/// <remarks>
/// Cards are declared as data over the primitives in <c>MtgEngine.Rules.Abilities</c> rather than
/// written as a class each. The primitives were the slow part; a card is now a few lines, and the
/// ones that share a shape share it rather than each having its own copy of "deal N damage to any
/// target".
/// <para>
/// Written against card names, because a name is checkable by eye and a GUID is not — a pool of
/// plausible-looking wrong oracle ids would bind real cards to the wrong behaviour and look
/// entirely correct doing it. The ids are then resolved from the card database at startup, so a
/// lookup at play time is by oracle id, which is what a deck actually stores and what survives
/// a card being renamed.
/// </para>
/// <para>
/// The name remains a fallback for anything the resolve could not find, so the pool still works
/// with a card database that is missing or still downloading rather than silently playing
/// nothing.
/// </para>
/// <para>
/// A card not in here is not playable, and <c>GameTableService</c> refuses the deck rather than
/// letting it play wrong — see the note there on why that is the least bad of the three options.
/// </para>
/// <para>
/// Basic lands are the exception that proves the pattern: every one of them is "{T}: Add {C}" for
/// one colour, so they are generated from their subtype rather than written out five times.
/// </para>
/// </remarks>
public sealed class CardPool : IAbilitySource
{
    private readonly Dictionary<string, CardScript> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CardScript> _byOracleId = new(StringComparer.Ordinal);

    public CardPool()
    {
        foreach (var script in BasicLands().Concat(StarterCards.All))
            _byName[script.Name] = script;
    }

    /// <summary>How many cards the engine implements.</summary>
    public int Count => _byName.Count;

    /// <summary>The names of every card the engine implements.</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>Whether a specific card can be played, by name.</summary>
    public bool Knows(string name) => _byName.ContainsKey(name);

    /// <summary>How many of them have been tied to an oracle id.</summary>
    public int ResolvedCount => _byOracleId.Count;

    /// <summary>
    /// Ties each script to the oracle id of the card it names.
    /// </summary>
    /// <remarks>
    /// Called once at startup with a name-to-oracle-id lookup from the card database. Until it
    /// runs the pool still works by name, so a missing or half-downloaded card database costs
    /// accuracy across renames rather than the ability to play at all.
    /// </remarks>
    public void ResolveOracleIds(IReadOnlyDictionary<string, string> oracleIdsByName)
    {
        ArgumentNullException.ThrowIfNull(oracleIdsByName);

        foreach (var (name, script) in _byName)
        {
            if (oracleIdsByName.TryGetValue(name, out var oracleId)
                && !string.IsNullOrEmpty(oracleId))
            {
                _byOracleId[oracleId] = script;
            }
        }
    }

    public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) =>
        Find(card)?.Triggers ?? [];

    public IReadOnlyList<ContinuousEffectDefinition> StaticsOf(CardDefinition card) =>
        Find(card)?.Statics ?? [];

    public IReadOnlyList<ReplacementEffectDefinition> ReplacementsOf(CardDefinition card) =>
        Find(card)?.Replacements ?? [];

    public IReadOnlyList<ActivatedAbilityDefinition> ActivatedOf(CardDefinition card) =>
        Find(card)?.Activated ?? [];

    public SpellDefinition? SpellOf(CardDefinition card) => Find(card)?.Spell;

    public ContinuousEffectDefinition? FloatingEffect(string definitionId) =>
        StarterCards.FloatingEffects.GetValueOrDefault(definitionId);

    private CardScript? Find(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        // Oracle id first: it is what a deck stores, and it survives a card being renamed. The
        // name is the fallback for anything that was never resolved.
        return _byOracleId.GetValueOrDefault(card.OracleId ?? string.Empty)
            ?? _byName.GetValueOrDefault(card.Name);
    }

    /// <summary>The five basic lands, generated rather than written out (CR 305.6).</summary>
    private static IEnumerable<CardScript> BasicLands()
    {
        // Named as the cards are named, because that is the key the pool uses. A Forest from any
        // set is the same card and plays the same way (CR 305.6).
        var basics = new (string Subtype, ManaColor Color)[]
        {
            ("Plains", ManaColor.White),
            ("Island", ManaColor.Blue),
            ("Swamp", ManaColor.Black),
            ("Mountain", ManaColor.Red),
            ("Forest", ManaColor.Green),
        };

        foreach (var (subtype, color) in basics)
        {
            yield return new CardScript
            {
                Name = subtype,
                Activated =
                [
                    new ActivatedAbilityDefinition
                    {
                        Id = "mana",
                        Text = $"{{T}}: Add {{{ManaLetter(color)}}}.",
                        RequiresTap = true,
                        Produces = [new ManaProduction(color)],
                    },
                ],
            };
        }
    }

    private static char ManaLetter(ManaColor color) => color switch
    {
        ManaColor.White => 'W',
        ManaColor.Blue => 'U',
        ManaColor.Black => 'B',
        ManaColor.Red => 'R',
        _ => 'G',
    };
}

/// <summary>Everything one card can contribute to a game.</summary>
public sealed record CardScript
{
    /// <summary>The card's exact name, which is how the pool finds it. See StarterCards.</summary>
    public required string Name { get; init; }

    public SpellDefinition? Spell { get; init; }

    public IReadOnlyList<TriggeredAbilityDefinition> Triggers { get; init; } = [];

    public IReadOnlyList<ContinuousEffectDefinition> Statics { get; init; } = [];

    public IReadOnlyList<ReplacementEffectDefinition> Replacements { get; init; } = [];

    public IReadOnlyList<ActivatedAbilityDefinition> Activated { get; init; } = [];
}

/// <summary>The target specs cards keep reaching for (CR 115.1).</summary>
public static class Targets
{
    /// <summary>"Any target": a creature or a player (CR 115.4).</summary>
    public static readonly TargetSpec AnyTarget = new()
    {
        Kind = TargetKind.Any,
        Description = "any target",
        ObjectFilter = (state, abilities, obj, controller) =>
            Characteristics.Of(state, abilities, obj).IsCreature,
    };

    public static readonly TargetSpec AnyPlayer = new()
    {
        Kind = TargetKind.Player,
        Description = "target player",
    };

    public static readonly TargetSpec TargetCreature = new()
    {
        Kind = TargetKind.Permanent,
        Description = "target creature",
        ObjectFilter = (state, abilities, obj, controller) =>
            Characteristics.Of(state, abilities, obj).IsCreature,
    };

    public static readonly TargetSpec TargetSpell = new()
    {
        Kind = TargetKind.SpellOnStack,
        Description = "target spell",
    };

    /// <summary>A creature an opponent controls (CR 109.5: "you" is the controller).</summary>
    public static readonly TargetSpec TargetCreatureAnOpponentControls = new()
    {
        Kind = TargetKind.Permanent,
        Description = "target creature an opponent controls",
        ObjectFilter = (state, abilities, obj, controller) =>
            obj.ControllerId != controller && Characteristics.Of(state, abilities, obj).IsCreature,
    };
}
