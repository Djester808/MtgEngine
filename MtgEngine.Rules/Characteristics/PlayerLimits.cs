using MtgEngine.Cards;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// What a player currently can't do, or can't have done to them: the union of every player-scoped
/// continuous effect in force on them (CR 613.10). "Can't" beats "does" (CR 101.2), so every
/// reader of this record treats a set flag as a prohibition it may not step over.
/// <para>
/// <see cref="PlayerRestriction"/> is the vocabulary's flag set and carries four of the six
/// prohibitions; "can't win the game" and "can't gain life" have no flag there and are their own
/// records in the vocabulary, so they are their own fields here. One reading serves both
/// spellings: <c>CantLoseGame</c> and <c>PlayerCant(Lose)</c> land on the same flag.
/// </para>
/// </summary>
[RuleRef("613.10")]
public sealed record PlayerLimits(
    PlayerRestriction Cant = PlayerRestriction.None,
    bool CantWin = false,
    bool CantGainLife = false,
    bool ProtectionFromEverything = false)
{
    /// <summary>A player no continuous effect restricts.</summary>
    public static readonly PlayerLimits None = new();

    /// <summary>Is this restriction in force? <see cref="PlayerRestriction.None"/> never is.</summary>
    public bool Has(PlayerRestriction restriction) =>
        restriction != PlayerRestriction.None && (Cant & restriction) == restriction;

    /// <summary>CR 101.2 with 704.5a-c and 903.10a: a player who can't lose does not lose.</summary>
    [RuleRef("104.3")]
    public bool CantLose => Has(PlayerRestriction.Lose);

    /// <summary>"Your life total can't change", CR 119.3 read as a prohibition: no gain, no loss, no setting.</summary>
    [RuleRef("119.3")]
    public bool LifeTotalCantChange => Has(PlayerRestriction.LifeTotalChanges);

    /// <summary>
    /// CR 120.3 and 702.16j: damage to this player is prevented, either because an effect says so
    /// or because protection from everything prevents all damage that would be dealt to them.
    /// </summary>
    [RuleRef("120.3")]
    public bool CantBeDealtDamage => Has(PlayerRestriction.BeDealtDamage) || ProtectionFromEverything;

    /// <summary>
    /// CR 115.6 and 702.16j: the player can't be targeted. The flag is the card-text form
    /// ("by spells or abilities your opponents control"); protection from everything is the
    /// stronger one, which no source can target through.
    /// </summary>
    [RuleRef("702.16j")]
    public bool CantBeTargeted => Has(PlayerRestriction.BeTargeted) || ProtectionFromEverything;

    /// <summary>A life total that can't change can't go up either, so the two readings are joined here.</summary>
    [RuleRef("119.3")]
    public bool CantGainLifeNow => CantGainLife || LifeTotalCantChange;

    /// <summary>
    /// CR 702.16j: the third of the three things protection from everything does for a player —
    /// they can't be enchanted, so an Aura already attached to them is put into its owner's
    /// graveyard by CR 704.5m and one being cast at them has no legal target (CR 601.2c). No
    /// <see cref="PlayerRestriction"/> flag spells this on its own: a card that says only "can't
    /// be enchanted" has no vocabulary record, so the protection is the only source of it.
    /// </summary>
    [RuleRef("702.16j")]
    public bool CantBeEnchanted => ProtectionFromEverything;
}

