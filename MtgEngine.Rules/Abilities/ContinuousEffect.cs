using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// The layers continuous effects are applied in (CR 613.1, 613.4).
/// </summary>
/// <remarks>
/// Numbered with gaps so a sublayer can be inserted without renumbering, and ordered so that
/// sorting by the enum value is the rules' order. Layer 7's sublayers are the ones that come up
/// constantly: setting power and toughness (7b) happens before modifying it (7c), which is why
/// a creature that "becomes 0/1" and has a +1/+1 counter is 1/2 and not 0/1.
/// </remarks>
public enum EffectLayer
{
    /// <summary>Copiable values (CR 613.1a).</summary>
    Copy = 100,

    /// <summary>Control-changing effects (CR 613.1b).</summary>
    Control = 200,

    /// <summary>Text-changing effects (CR 613.1c).</summary>
    Text = 300,

    /// <summary>Type-changing effects (CR 613.1d).</summary>
    Type = 400,

    /// <summary>Color-changing effects (CR 613.1e).</summary>
    Color = 500,

    /// <summary>Ability-adding and ability-removing effects (CR 613.1f).</summary>
    Ability = 600,

    /// <summary>Characteristic-defining power/toughness (CR 613.4a).</summary>
    PowerToughnessCda = 710,

    /// <summary>Effects that set power and/or toughness to a value (CR 613.4b).</summary>
    PowerToughnessSet = 720,

    /// <summary>Effects and counters that modify power and/or toughness (CR 613.4c).</summary>
    PowerToughnessModify = 730,

    /// <summary>Effects that switch power and toughness (CR 613.4d).</summary>
    PowerToughnessSwitch = 740,
}

/// <summary>
/// The characteristics of an object while they are being worked out, layer by layer.
/// </summary>
/// <remarks>
/// Mutable on purpose, and only ever during one computation. CR 613.1 describes exactly this:
/// start from the printed values and apply each applicable effect in order. An effect in layer 5
/// changing a creature's colour can bring a layer 7c effect into range that was not applying a
/// moment ago, so the layers have to run over one accumulating object rather than being combined
/// at the end.
/// </remarks>
public sealed class CharacteristicsBuilder
{
    internal CharacteristicsBuilder(GameObject obj)
    {
        Subject = obj;
        Power = obj.Card.Power;
        Toughness = obj.Card.Toughness;
        CardTypes = obj.Card.CardTypes;
        Keywords = obj.Card.Keywords;
        ControllerId = obj.ControllerId;
        Subtypes = [.. obj.Card.Subtypes];
        Colors = [.. obj.Card.ColorIdentity];
    }

    /// <summary>The permanent being computed, with its printed card and its counters.</summary>
    public GameObject Subject { get; }

    public int? Power { get; set; }

    public int? Toughness { get; set; }

    public CardType CardTypes { get; set; }

    public KeywordAbility Keywords { get; set; }

    public Guid ControllerId { get; set; }

    public List<string> Subtypes { get; }

    public List<ManaColor> Colors { get; }

    /// <summary>Adds to power and toughness, the layer 7c operation (CR 613.4c).</summary>
    public void Modify(int power, int toughness)
    {
        if (Power is not null)
            Power += power;

        if (Toughness is not null)
            Toughness += toughness;
    }

    /// <summary>Sets power and toughness, the layer 7b operation (CR 613.4b).</summary>
    public void Set(int power, int toughness)
    {
        Power = power;
        Toughness = toughness;
    }

    /// <summary>Swaps power and toughness, the layer 7d operation (CR 613.4d).</summary>
    public void Switch() => (Power, Toughness) = (Toughness, Power);

    internal ComputedCharacteristics Build() => new()
    {
        Power = Power,
        Toughness = Toughness,
        CardTypes = CardTypes,
        Keywords = Keywords,
        ControllerId = ControllerId,
        Subtypes = [.. Subtypes],
        Colors = [.. Colors],
    };
}

/// <summary>
/// A continuous effect: what it applies to, which layer it applies in, and what it does.
/// </summary>
/// <remarks>
/// Never stored in <see cref="GameState"/>. A static ability's effect exists exactly while its
/// source is on the battlefield (CR 604.2), so it is recomputed from the battlefield every time
/// rather than recorded — which is the whole fix. The previous engine had
/// <c>IStaticAbility.Apply(state) =&gt; state</c> and wrote the buff into the creature; when the
/// lord left, nothing took it back off.
/// </remarks>
public sealed record ContinuousEffectDefinition
{
    public required string Id { get; init; }

    public required EffectLayer Layer { get; init; }

    /// <summary>
    /// Whether this effect applies to the given object. <c>source</c> is the permanent whose
    /// static ability this is, or null for an effect floating free of its source.
    /// </summary>
    public required Func<GameState, GameObject?, GameObject, bool> Applies { get; init; }

    /// <summary>What it does, applied to the characteristics as they stand at its layer.</summary>
    public required Action<CharacteristicsBuilder> Apply { get; init; }
}

/// <summary>
/// A replacement effect: it watches for an event and replaces it with different ones (CR 614.1).
/// </summary>
/// <remarks>
/// Replacement effects do not trigger and do not use the stack. They modify the event before it
/// ever happens, which is why an event that is replaced never triggers anything watching for it
/// (CR 603.2g) — the original event did not occur.
/// </remarks>
public sealed record ReplacementEffectDefinition
{
    public required string Id { get; init; }

    /// <summary>Whether this effect applies to the event that is about to happen.</summary>
    public required Func<GameEvent, GameState, GameObject, bool> Applies { get; init; }

    /// <summary>
    /// What happens instead. An empty list means the event simply does not happen — which is how
    /// prevention works (CR 615.1).
    /// </summary>
    public required Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>> Replace { get; init; }

    /// <summary>Where the source has to be for the effect to apply (CR 614.6).</summary>
    public Zone FunctionsFrom { get; init; } = Zone.Battlefield;
}
