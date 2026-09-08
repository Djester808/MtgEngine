namespace MtgEngine.Cards;

/// <summary>"Whenever you tap a Forest for mana, add an additional {G}."</summary>
public sealed record ManaProducesAdditional(ObjectFilter Sources, string Mana) : ContinuousEffect;

/// <summary>
/// "Creatures you control gain flying", "creatures you control have 'Ward—Pay 2 life'", CR 613.1f.
/// <paramref name="Cost"/> is the keyword's cost written as printed mana ("Ward {2}", "Equip {3}");
/// <paramref name="Costs"/> is that same rider when it is not mana — life, a sacrifice, a discard.
/// <para>
/// The two say one thing, so writing both is a keyword with two costs and is refused (L049): a
/// granted "Ward—Pay 2 life" is <paramref name="Costs"/> with a PayLife and no
/// <paramref name="Cost"/> (CR 702.21a). <paramref name="Costs"/> is a list because a printed
/// keyword cost can be several things paid together, and it is refused on a keyword that takes no
/// cost at all. It is not a <c>GrantAbility</c> wrapping a hand-written trigger: the keyword is the
/// spelling, and a written-out ward would not be counted by anything that reads ward.
/// </para>
/// </summary>
[RuleRef("613.1f")]
public sealed record GrantKeyword(KeywordKind Keyword, string? Cost = null, Amount? N = null, ObjectFilter? Of = null, IReadOnlyList<Cost>? Costs = null) : ContinuousEffect;

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

/// <summary>
/// "Your triggered abilities trigger an additional time", CR 603.2d — and, with
/// <paramref name="Cause"/>, "if a land entering causes a triggered ability of a permanent you
/// control to trigger, that ability triggers an additional time".
/// <para>
/// <paramref name="Who"/> is the controller of the abilities and <paramref name="OfSources"/>
/// narrows the permanents they are on. <paramref name="Cause"/> is the <b>event that caused the
/// trigger</b>, not a second trigger of this card: the extra triggering happens only when the
/// ability triggered because of an event this trigger admits, so "a land entering" is an
/// EntersBattlefield whose filter is the land. Leave it null when every event counts. A reading
/// that puts the cause in <paramref name="OfSources"/> says something else entirely — that filter
/// describes the permanent whose ability triggers, not what made it trigger — and one that wraps
/// the card in a TriggeredAbility of its own gives the card a trigger it does not print.
/// </para>
/// <para>
/// CR 603.2d fixes two things a reading can get wrong: the count is determined once, so this effect
/// does not invoke itself repeatedly, and "a triggered ability of an object" means an ability that
/// object has and never a delayed or reflexive ability created by one (CR 603.7, 603.12).
/// </para>
/// </summary>
[RuleRef("603.2d")]
public sealed record AbilitiesTriggerTwice(PlayerRef Who, ObjectFilter? OfSources = null, Trigger? Cause = null) : ContinuousEffect;

/// <summary>
/// "Players gain hexproof until end of turn", CR 702.11c: the player half of the hexproof keyword,
/// which reads "You can't be the target of spells or abilities your opponents control".
/// <para>
/// It is a named keyword and not <c>PlayerCant(PlayerRestriction.BeTargeted)</c>, even though the
/// two forbid the same thing: hexproof is what other cards look for and take away (CR 702.11e), and
/// an anonymous restriction is invisible to them. It is not the KeywordAbility Hexproof, which is on
/// an object and cannot be given to a player; the same reason <see cref="PlayerProtectionFromEverything"/>
/// exists beside the Protection keyword. Apply it with a StaticAbility whose Players is set, or with
/// ApplyToPlayer for a duration. "Hexproof from [quality]" (CR 702.11d) is defined for permanents
/// only, so this record carries no quality.
/// </para>
/// </summary>
[RuleRef("702.11c")]
public sealed record PlayerHexproof : ContinuousEffect;

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

/// <summary>
/// "Creature cards in graveyards and libraries can't enter the battlefield", "lands can't enter the
/// battlefield": a continuous effect that modifies the rules of the game rather than the
/// characteristics of any object, applied after every other continuous effect (CR 613.11).
/// <para>
/// <paramref name="What"/> is what may not enter. It is read wherever an object would be put onto
/// the battlefield, not only where a spell resolves: a permanent spell whose controller can't put it
/// onto the battlefield goes to its owner's graveyard instead (CR 608.3e), a token with a forbidden
/// characteristic is never created at all (CR 111.5), and a card returned or searched into play
/// simply stays where it is. Nothing is destroyed, countered or replaced — the move does not happen,
/// so this is not a <see cref="ReplacementAbility"/> and never a <c>Counter</c>.
/// </para>
/// <para>
/// The filter is where the printed zones live, so the record itself takes none: "creature cards in
/// graveyards and libraries" is a filter with <c>Types: Creature</c> and an
/// <c>InAnyOf: [Graveyard, Library]</c>, the two zones in one filter. A card that names no zone
/// ("lands can't enter the battlefield") leaves both zone fields off and forbids the entry from
/// anywhere, which is not the same restriction and must not be written as a list of every zone.
/// </para>
/// </summary>
[RuleRef("613.11")]
public sealed record CantEnterBattlefield(ObjectFilter What) : ContinuousEffect;

/// <summary>
/// "Your opponents can't cast spells", "players can't cast spells from graveyards or libraries": a
/// rule-modifying continuous effect over casting itself (CR 601.3 with 613.11) — the cast is simply
/// not a legal action, so nothing goes on the stack and there is nothing to counter.
/// <para>
/// <paramref name="CastFrom"/> is the printed list of zones the restriction covers, and it is a
/// <b>list</b> because the sentence names more than one: "from graveyards or libraries" is one
/// restriction over two zones (CR 400.1 — every zone is its own place), never two static abilities
/// and never two records, whose durations and dependencies would then be independent. Leave it out
/// when the card names no zone; a restriction with no zone covers every zone, so writing out the
/// whole list is a second spelling of the same thing.
/// </para>
/// <para>
/// It is the record's own field and not the filter's, because <see cref="ObjectFilter.CastFrom"/>
/// and <see cref="ObjectFilter.NotCastFrom"/> describe one spell that was cast from somewhere and
/// take one zone each. Set the field or those, never both (L047): two zone riders on one
/// restriction can disagree, and only one of them would be read.
/// </para>
/// </summary>
[RuleRef("601.3")]
public sealed record CantCastSpells(PlayerRef Who, ObjectFilter? Matching = null, IReadOnlyList<Zone>? CastFrom = null) : ContinuousEffect;
