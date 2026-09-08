namespace MtgEngine.Cards;

/// <summary>"Whenever you tap a Forest for mana, add an additional {G}."</summary>
public sealed record ManaProducesAdditional(ObjectFilter Sources, string Mana) : ContinuousEffect;

/// <summary>
/// "Creatures can't attack you", "creatures can't attack you unless their controller pays {W/P} for
/// each creature they control that's attacking you". <paramref name="Player"/> is the player being
/// protected and <paramref name="Unless"/> the board exception ("unless it's a Zombie"); neither of
/// them can carry a cost, which is why the tax is its own field. A card prints one exception or the
/// other, so setting both <paramref name="Unless"/> and <paramref name="UnlessPays"/> is a
/// contradiction the linter refuses (L040) — two exceptions on one restriction would let an attack
/// through by satisfying either, and no printed card says that.
/// <para>
/// <paramref name="UnlessPays"/> is the attack tax, CR 508.1h: a cost the attacking creature's
/// controller must pay while declaring attackers, determined and locked in as the declaration is
/// made, and the attack is simply illegal without it. It is not a <see cref="CostModifier"/>, which
/// raises the cost of casting a spell, and it is not <see cref="MustAttack"/> read backwards.
/// </para>
/// <para>
/// <paramref name="PerAttackingCreature"/> is whether that price is paid once for the declaration or
/// once for every attacking creature. A tax whose scale is <b>generic</b> mana needs no flag — "{2}
/// for each creature they control that's attacking you" is a PayManaCost whose generic is
/// Times(AttackersAgainst(You), 2), and that spelling stays the one for a generic tax. A coloured or
/// Phyrexian symbol cannot be scaled that way, and that is what this field is for.
/// </para>
/// <para>
/// The cost repeats and is not multiplied at translation time: <c>{W/P}</c> for each attacker is
/// three separate <c>{W/P}</c> payments, each of which its payer may pay with two life instead, so a
/// reading that writes the total as one mana cost of three loses the per-symbol choice and prices
/// the tax wrongly the moment a symbol is coloured or Phyrexian. CR 508.1h locks the total in once
/// the attackers are declared, so the count is the attackers that filter admits at that moment and
/// never a running count as blockers or removal change it. The flag says nothing with no price to
/// repeat (L045).
/// </para>
/// </summary>
[RuleRef("508.1h")]
public sealed record CantAttack(PlayerRef? Player = null, ObjectFilter? Unless = null, Cost? UnlessPays = null, bool PerAttackingCreature = false) : ContinuousEffect;

public sealed record TokensEnterWithCounters(PlayerRef Who, string Counter, Amount Count) : ContinuousEffect;

public sealed record CountersDoubled(PlayerRef Who, string? Counter = null) : ContinuousEffect;

public sealed record LifeGainDoubled(PlayerRef Who) : ContinuousEffect;

public sealed record AbilitiesTriggerTwice(PlayerRef Who, ObjectFilter? OfSources = null) : ContinuousEffect;

[RuleRef("701.19")]
public sealed record RegenerationShield : ContinuousEffect;

[RuleRef("122.1a")]
public sealed record TreatAsHavingKeywordCounter(string Counter) : ContinuousEffect;

/// <summary>
/// "Combat damage can't be prevented", CR 615.12: damage the shields still see and still apply
/// themselves to, and still fail to reduce. It is not <see cref="PreventDamage"/> with anything
/// turned around — that record makes prevention happen; this one makes it not happen — and it is
/// not <c>DealDamage.CantBePrevented</c>, which is the rider on one instruction of one card's own
/// damage. This is the board-wide continuous effect, so damage from every source it admits is
/// unpreventable however it is dealt.
/// <para>
/// <paramref name="Combat"/> is the printed "combat damage": true for combat damage alone, false
/// for noncombat damage alone, null for all damage. <paramref name="From"/> narrows the sources —
/// "damage from red sources can't be prevented" — and null admits any source. CR 615.12 keeps the
/// rest: an applicable prevention effect is still applied once, prevents nothing, and any other
/// thing it does still happens, and an existing shield is not spent.
/// </para>
/// </summary>
[RuleRef("615.12")]
public sealed record DamageCantBePrevented(ObjectFilter? From = null, bool? Combat = null) : ContinuousEffect;

/// <summary>
/// "Play with the top card of your library revealed", CR 401.5 with CR 701.18c ("play" here means
/// to play the game, not to play a card). The top card is revealed to <b>every</b> player for as
/// long as the effect applies, and CR 401.6 makes it a new object each time it stops being revealed
/// and is revealed again.
/// <para>
/// It is not <see cref="MayLookAtTopCard"/>, which is the private half of the same rule — "you may
/// look at the top card of your library" shows it to one player and to nobody else. The two are
/// printed side by side on many cards and read as the same sentence by a reader that only notices
/// the top card is known; an opponent's information is the whole difference, so a card that says
/// "revealed" is this record and never that one. It grants no permission to play the card either:
/// "you may play the top card of your library" is <see cref="MayPlayFromZone"/>, a second sentence
/// and a second record.
/// </para>
/// </summary>
[RuleRef("401.5")]
public sealed record PlayWithTopCardRevealed(PlayerRef Who) : ContinuousEffect;
