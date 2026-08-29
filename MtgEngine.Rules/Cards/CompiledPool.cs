using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.Cards;

/// <summary>
/// An ability source that compiles each card's rules text the first time it is asked about.
/// </summary>
/// <remarks>
/// This is what makes the compiler more than an analysis tool: the engine asks what a card does,
/// and the answer is read from the card's own printed text rather than from a table someone
/// maintained by hand. A card the compiler cannot fully read still answers with the parts it
/// could — which is why <see cref="Refuses"/> exists, so a deck check can turn those away rather
/// than letting a half-read card into a game.
/// </remarks>
public sealed class CompiledPool : IAbilitySource
{
    private readonly Dictionary<string, CompiledCard> _compiled = [];
    private readonly Dictionary<string, string> _textOf = [];

    /// <summary>Compiles on first use and remembers, because a game asks repeatedly.</summary>
    /// <remarks>
    /// Two different cards under one key would silently serve the first one's behaviour to the
    /// second, and the result is a card that plays as something else entirely. It has happened
    /// twice in test cards that shared a name, and both times it looked like an engine bug — the
    /// creature came out the wrong size, and nothing pointed at the cache. It is cheap to notice
    /// and impossible to debug, so it is checked.
    /// </remarks>
    public CompiledCard For(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var key = card.OracleId.Length > 0 ? card.OracleId : card.Name;
        if (_compiled.TryGetValue(key, out var already))
        {
            if (!string.Equals(_textOf[key], card.OracleText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Two different cards share the id '{key}' ({card.Name}). The compiled pool "
                        + "would serve one card's behaviour for the other.");
            }

            return already;
        }

        var fresh = CardCompiler.Compile(card);
        _compiled[key] = fresh;
        _textOf[key] = card.OracleText;
        return fresh;
    }

    /// <summary>
    /// Whether this card has rules text the compiler could not read.
    /// </summary>
    /// <remarks>
    /// A deck containing one is not playable. The alternative — letting it through with the
    /// lines that did compile — produces a game that looks right and is quietly playing a
    /// different card, which is the failure mode the whole design exists to avoid.
    /// </remarks>
    public bool Refuses(CardDefinition card) => !For(card).IsComplete;

    public SpellDefinition? SpellOf(CardDefinition card) => For(card).Spell;

    public SpellDefinition? AdventureOf(CardDefinition card) => For(card).Adventure;

    public string? AdventureCostOf(CardDefinition card) => For(card).AdventureCostRaw;

    public SpellDefinition? PreparedSpellOf(CardDefinition card) => For(card).PreparedSpell;

    public string? PreparedCostOf(CardDefinition card) => For(card).PreparedCostRaw;

    public bool PreparedIsInstantOf(CardDefinition card) => For(card).PreparedIsInstant;

    public int DevourCountOf(CardDefinition card) => For(card).DevourCount;

    public Mana.ManaCostSpec? MiracleCostOf(CardDefinition card) => For(card).Spell?.MiracleCost;

    public IReadOnlyList<CardHalf> HalvesOf(CardDefinition card) => For(card).Halves;

    public bool HasFuse(CardDefinition card) => For(card).HasFuse;

    public IReadOnlyList<ActivatedAbilityDefinition> ActivatedOf(CardDefinition card) =>
        For(card).Activated;

    public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) =>
        For(card).Triggers;

    public IReadOnlyList<ContinuousEffectDefinition> StaticsOf(CardDefinition card) =>
        For(card).Statics;

    public string? AttacksOnlyIfDefenderControls(CardDefinition card) =>
        For(card).AttacksOnlyIfDefenderControls;

    /// <summary>
    /// What this card does to somebody's spell or ability costs (CR 601.2f, 602.2b).
    /// </summary>
    /// <remarks>
    /// The seam the engine half was built against and nothing implemented, so every
    /// <c>CostModifier</c> the engine could apply was one no card could ever say. Answering it
    /// here is what connects the two.
    /// <para>
    /// <c>CostReducersOf</c> is deliberately left to its default: the compiler now emits
    /// modifiers for every cell it reads, the old reducer's single cell included, and a card
    /// answering both would be discounted twice by <c>Game.ModifiersOn</c>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CostModifier> CostModifiersOf(CardDefinition card) =>
        For(card).CostModifiers;

    public bool ShowsTopOfLibrary(CardDefinition card) => For(card).ShowsTopOfLibrary;

    public bool RemovesHandLimit(CardDefinition card) => For(card).RemovesHandLimit;

    public Abilities.ChoiceOnEntry ChoosesOnEntry(CardDefinition card) =>
        For(card).ChoosesOnEntry;

    public int ExtraLandDrops(CardDefinition card) => For(card).ExtraLandDrops;

    public bool MayDeclineUntap(CardDefinition card) => For(card).MayDeclineUntap;

    public bool SkipsDrawStep(CardDefinition card) => For(card).SkipsDrawStep;

    public bool RevealsTopOfLibrary(CardDefinition card) => For(card).RevealsTopOfLibrary;

    public IReadOnlyList<ReplacementEffectDefinition> ReplacementsOf(CardDefinition card) =>
        For(card).Replacements;

    public Domain.Enums.KeywordAbility GrantedKeywords(CardDefinition card) =>
        For(card).GrantedKeywords;

    /// <summary>
    /// Continuous effects named rather than registered — see <see cref="GenerativeEffects"/>.
    /// </summary>
    public ContinuousEffectDefinition? FloatingEffect(string definitionId) =>
        GenerativeEffects.Resolve(definitionId);
}
