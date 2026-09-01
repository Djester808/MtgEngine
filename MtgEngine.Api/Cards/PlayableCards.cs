using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Api.Cards;

/// <summary>
/// The abilities the running game plays a card by: its hand-written script if it has one, and
/// otherwise its compiled rules text.
/// </summary>
/// <remarks>
/// This class exists because the compiler and the playable game were not connected by any wire.
/// <c>Program.cs</c> registered <see cref="CardPool"/> — five basic lands and a small curated set —
/// as the engine's only <see cref="IAbilitySource"/>, while <see cref="CompiledPool"/>, which reads
/// a card's printed text, was constructed in the test projects and nowhere else. The measured
/// consequence: the deck gate admitted **917** cards while the compiler read every line of 15,262.
/// Every reader written for this engine was exercised by tests and by nothing that plays a game.
/// <para>
/// The written script wins where there is one. Those cards were implemented deliberately, they are
/// what the starter decks are built from, and several express things the compiler still cannot —
/// so falling back to compiled text for them would be a downgrade, not a widening.
/// </para>
/// <para>
/// Every member delegates; none is left to the interface's default. That is not tidiness. A
/// default here returns "no abilities", so a member forgotten in this class would make a card
/// quietly lose one of the things it does while still looking playable — the same failure this
/// project has now found five times and cannot detect from the outside.
/// <see cref="MtgEngine.Api.Tests"/> asserts by reflection that every interface member is
/// overridden here, so adding one to the interface fails the build rather than silently defaulting.
/// </para>
/// </remarks>
public sealed class PlayableCards : IAbilitySource
{
    private readonly CardPool _scripted;
    private readonly CompiledPool _compiled;

    public PlayableCards(CardPool scripted, CompiledPool compiled)
    {
        _scripted = scripted;
        _compiled = compiled;
    }

    /// <summary>
    /// Whether this card cannot be played correctly, and so may not be brought to a table.
    /// </summary>
    /// <remarks>
    /// A written script is by definition complete. Everything else is playable only when the
    /// compiler read <em>every</em> line of it: a card with one ability compiled and three lines
    /// unread is precisely the quietly-wrong game the gate exists to prevent.
    /// </remarks>
    public bool Refuses(CardDefinition card) =>
        !_scripted.Knows(card) && _compiled.Refuses(card);

    /// <summary>Which source answers for this card.</summary>
    private IAbilitySource For(CardDefinition card) =>
        _scripted.Knows(card) ? _scripted : _compiled;

    public SpellDefinition? SpellOf(CardDefinition card) => For(card).SpellOf(card);

    public IReadOnlyList<ActivatedAbilityDefinition> ActivatedOf(CardDefinition card) =>
        For(card).ActivatedOf(card);

    public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) =>
        For(card).TriggersOf(card);

    public IReadOnlyList<ContinuousEffectDefinition> StaticsOf(CardDefinition card) =>
        For(card).StaticsOf(card);

    /// <summary>What this card's static abilities give a player (CR 702.11c, 702.18a).</summary>
    public IReadOnlyList<PlayerQualityDefinition> PlayerQualitiesOf(CardDefinition card) =>
        For(card).PlayerQualitiesOf(card);

    /// <inheritdoc />
    public MtgEngine.Rules.State.StaticBans BansOf(CardDefinition card) =>
        For(card).BansOf(card);

    public IReadOnlyList<ReplacementEffectDefinition> ReplacementsOf(CardDefinition card) =>
        For(card).ReplacementsOf(card);

    /// <inheritdoc />
    /// <remarks>
    /// A timing permission is read off the battlefield when the question is asked, so a card
    /// that is not delegated here loses it silently: Vedalken Orrery and Leyline of Anticipation
    /// compile perfectly and then grant nothing at a real table. The fourth member to be added
    /// to <c>IAbilitySource</c> and forgotten here, which is why the reflection test in
    /// <c>PlayableCardsTests</c> exists rather than a code review convention.
    /// </remarks>
    public IReadOnlyList<MtgEngine.Rules.State.FlashPermission> FlashPermissionsOf(
        CardDefinition card) =>
        For(card).FlashPermissionsOf(card);

    public IReadOnlyList<CostReducer> CostReducersOf(CardDefinition card) =>
        For(card).CostReducersOf(card);

    /// <summary>What a card does to somebody's spell or ability costs (CR 601.2f, 602.2b).</summary>
    /// <remarks>
    /// The member this class exists for. <see cref="CompiledPool"/> answers it and
    /// <c>Game</c> reads it through an <c>is ICostModifierSource</c> test — which this class
    /// fails unless it is here, so without this line the compiler would emit a modifier for
    /// every card that prints one and the running game would apply none of them.
    /// </remarks>
    public IReadOnlyList<CostModifier> CostModifiersOf(CardDefinition card) =>
        For(card).CostModifiersOf(card);

    public SpellDefinition? AdventureOf(CardDefinition card) => For(card).AdventureOf(card);

    public string? AdventureCostOf(CardDefinition card) => For(card).AdventureCostOf(card);

    public SpellDefinition? PreparedSpellOf(CardDefinition card) => For(card).PreparedSpellOf(card);

    public string? PreparedCostOf(CardDefinition card) => For(card).PreparedCostOf(card);

    public bool PreparedIsInstantOf(CardDefinition card) => For(card).PreparedIsInstantOf(card);

    public int DevourCountOf(CardDefinition card) => For(card).DevourCountOf(card);

    public int AmplifyCountOf(CardDefinition card) => For(card).AmplifyCountOf(card);

    public SpellDefinition? CleaveSpellOf(CardDefinition card) => For(card).CleaveSpellOf(card);

    public string? CleaveCostOf(CardDefinition card) => For(card).CleaveCostOf(card);

    public SpellDefinition? GiftSpellOf(CardDefinition card) => For(card).GiftSpellOf(card);

    public bool HasGift(CardDefinition card) => For(card).HasGift(card);

    public bool HasReadAhead(CardDefinition card) => For(card).HasReadAhead(card);

    public Rules.Mana.ManaCostSpec? MiracleCostOf(CardDefinition card) =>
        For(card).MiracleCostOf(card);

    public IReadOnlyList<CardHalf> HalvesOf(CardDefinition card) => For(card).HalvesOf(card);

    public bool HasFuse(CardDefinition card) => For(card).HasFuse(card);

    public KeywordAbility GrantedKeywords(CardDefinition card) => For(card).GrantedKeywords(card);

    public bool ShowsTopOfLibrary(CardDefinition card) => For(card).ShowsTopOfLibrary(card);

    public bool RemovesHandLimit(CardDefinition card) => For(card).RemovesHandLimit(card);

    public IReadOnlyList<HandSizeChange> HandSizeChangesOf(CardDefinition card) =>
        For(card).HandSizeChangesOf(card);

    public ChoiceOnEntry ChoosesOnEntry(CardDefinition card) => For(card).ChoosesOnEntry(card);

    public string? ChosenNameFilterOf(CardDefinition card) => For(card).ChosenNameFilterOf(card);

    public int ExtraLandDrops(CardDefinition card) => For(card).ExtraLandDrops(card);

    /// <inheritdoc />
    /// <remarks>
    /// Symmetrical land drops (e.g. Rites of Flourishing) scope to all players, not just the
    /// controller. The interface default scopes to <c>PlayerScope.You</c>, so without this
    /// delegation every extra land drop would go to the controller alone.
    /// </remarks>
    public PlayerScope ExtraLandDropScope(CardDefinition card) =>
        For(card).ExtraLandDropScope(card);

    public bool MayDeclineUntap(CardDefinition card) => For(card).MayDeclineUntap(card);

    public bool SkipsDrawStep(CardDefinition card) => For(card).SkipsDrawStep(card);

    public bool MayBeginOnBattlefield(CardDefinition card) =>
        For(card).MayBeginOnBattlefield(card);

    public IReadOnlyList<CastLimit> CastLimitsOf(CardDefinition card) =>
        For(card).CastLimitsOf(card);

    public bool RevealsTopOfLibrary(CardDefinition card) => For(card).RevealsTopOfLibrary(card);

    public IReadOnlyList<MtgEngine.Rules.State.LibraryTopPermission> LibraryTopPermissionsOf(
        CardDefinition card) =>
        For(card).LibraryTopPermissionsOf(card);

    /// <inheritdoc />
    /// <remarks>
    /// A graveyard-play permission is looked up only by the count of permissions on a permanent,
    /// never by id — so without this delegation the count is always zero and no card ever permits
    /// playing lands from a graveyard.
    /// </remarks>
    public IReadOnlyList<MtgEngine.Rules.State.GraveyardPlayPermission> GraveyardPlayPermissionsOf(
        CardDefinition card) =>
        For(card).GraveyardPlayPermissionsOf(card);

    public string? AttacksOnlyIfDefenderControls(CardDefinition card) =>
        For(card).AttacksOnlyIfDefenderControls(card);

    public string? CantBlockMatching(CardDefinition card) => For(card).CantBlockMatching(card);

    /// <summary>
    /// A generated continuous effect, looked up by the id that names it rather than by a card.
    /// </summary>
    /// <remarks>
    /// The one member here that is not asked about a card, so it cannot be routed by
    /// <see cref="For"/>: an id travels on an event and the card it came from is long gone. Both
    /// sources are asked, written scripts first, for the same reason the rest prefer them.
    /// <para>
    /// This is the member the delegation test caught. Left to the interface default it returns
    /// null, and every temporary pump, grant and animation the compiler generates would have
    /// silently stopped applying the moment this class was wired in — with nothing failing.
    /// </para>
    /// </remarks>
    public ContinuousEffectDefinition? FloatingEffect(string definitionId) =>
        _scripted.FloatingEffect(definitionId) ?? _compiled.FloatingEffect(definitionId);
}
