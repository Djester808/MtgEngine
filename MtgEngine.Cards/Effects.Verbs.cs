namespace MtgEngine.Cards;

/// <summary>
/// "Deals 3 damage": to objects through <paramref name="To"/>, to players through
/// <paramref name="ToPlayer"/>, or to both when the card names both ("deals 2 damage to target
/// creature and 2 damage to that creature's controller").
///
/// The three words "any target" are not both halves written out: they are the AnyTarget selector
/// in <paramref name="To"/>, with <paramref name="ToPlayer"/> left out (CR 115.4). Use the two
/// halves together only when the card really names two things to damage.
///
/// <para>
/// <paramref name="ExcessTo"/> is "excess damage is dealt to that creature's controller instead":
/// the damage over what was lethal to the creature or planeswalker damaged goes to a player
/// (CR 120.3d defines excess damage as the amount beyond what is needed to destroy it, and
/// CR 702.19c is the trample rule that reads it). Only the excess moves; the rest is dealt as
/// usual, and a creature that takes less than lethal leaves no excess at all. Normally a
/// <see cref="ControllerOf"/> over the same selector <paramref name="To"/> names.
/// </para>
/// <para>
/// It rides on the damage the card deals, so it needs a <paramref name="To"/> to have an excess to
/// read (L031): damage to a player is never excess. It is not a second DealDamage — a reading that
/// deals the excess as its own instruction has no way to know what was lethal — and it is not
/// trample, which is a KeywordAbility about assigning combat damage before it is dealt.
/// </para>
/// <para>
/// <paramref name="ExcessIf"/> is the gate the printed line puts on that redirection — "if the
/// creature you control has trample, excess damage is dealt to that creature's controller instead",
/// the sentence CR 702.19c is written under — read as the damage is dealt and only about where the
/// excess goes; the damage itself happens either way. Never an If around two copies of the same
/// damage instruction, which is one printed sentence written twice and the reading the translators
/// disagreed over. Leave it null when the card redirects the excess unconditionally, and never set
/// it without <paramref name="ExcessTo"/> (L031), which would gate nothing.
/// </para>
/// </summary>
[RuleRef("120")]
public sealed record DealDamage(Amount N, Selector? To = null, PlayerRef? ToPlayer = null, Selector? Source = null, bool CantBePrevented = false, PlayerRef? ExcessTo = null, Condition? ExcessIf = null) : Effect;

/// <summary>"deals 4 damage divided as you choose among one, two, three, or four targets".</summary>
[RuleRef("601.2d")]
public sealed record DealDividedDamage(Amount Total, Selector Among, Selector? Source = null) : Effect;

/// <summary>
/// "Distribute three +1/+1 counters among one, two, or three target creatures", CR 601.2d: the
/// division is announced as the spell is cast, every named target gets at least one counter, and no
/// more than <paramref name="MaxTargets"/> targets may be chosen. <paramref name="N"/> is the total
/// distributed, <paramref name="Counter"/> the kind, and <paramref name="Among"/> the target
/// selector, whose own Count is the number of targets and is normally left null so MaxTargets rules
/// it. This is the counter twin of DealDividedDamage; do not spell a division as ForEach over
/// targets, which would give every target the whole amount.
/// </summary>
[RuleRef("601.2d")]
public sealed record DistributeCounters(Amount N, string Counter, Selector Among, int MaxTargets) : Effect;

/// <summary>
/// "Change the target of target spell with a single target", CR 115.7: the spell or ability keeps
/// resolving but points somewhere else. <paramref name="What"/> selects the spell or ability on the
/// stack; <paramref name="To"/> names the new target when the card names it ("change its target to
/// this creature") and is left null when the card lets a player choose. Set
/// <paramref name="AllTargets"/> for "you may choose new targets for it" (CR 114.6), which is the
/// weaker action: it may leave the targets as they are, while a change must alter at least one
/// (CR 115.7b). Legality is rechecked on resolution, so an illegal change is simply not made.
/// </summary>
[RuleRef("115.7")]
public sealed record ChangeTarget(Selector What, Selector? To = null, bool AllTargets = false, PlayerRef? Who = null) : Effect;

/// <summary>
/// "Sacrifice a creature", "pay 3 life" — a cost paid inside a resolving effect with no choice
/// offered, CR 118.12. The cost is paid where it stands rather than as part of casting or
/// activating. Any Cost record works here, including PayManaCost for an amount counted from the
/// game.
/// <para>
/// <paramref name="Then"/> is the "if you do" of a mandatory cost: "sacrifice a creature. If you do,
/// draw two cards." The instruction is not a choice — the player pays if they can — but it can fail,
/// because a resolving effect does as much as it can and nothing more (CR 608.2c), and the printed
/// second half runs only when the whole cost was actually paid. A partly paid cost is not paid: an
/// empty board pays no sacrifice, so Then does not run. Leave it null for the bare instruction with
/// nothing hanging on it.
/// </para>
/// <para>
/// Use MayPay for "you may pay {4}. If you do, ..." and Unless for "unless that player pays": both
/// are one record each and neither is spelled as a May or an If wrapped around this one. A PayCost
/// inside a May with a StoreChoiceAs key is the shape MayPay replaced, and a canonical pass
/// collapses it. The two spellings never both apply: a May around a PayCost that carries Then is
/// MayPay's whole sentence written out, and lints as one (L034). Which record a card gets is which
/// half it prints — "you may pay" is MayPay, a bare imperative with an "if you do" is this one.
/// </para>
/// </summary>
[RuleRef("118.12")]
public sealed record PayCost(Cost Cost, PlayerRef? Who = null, Effect? Then = null) : Effect;

[RuleRef("121")]
public sealed record Draw(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("119")]
public sealed record GainLife(Amount N, PlayerRef? Who = null) : Effect;

/// <summary>
/// "Each opponent loses 3 life." <paramref name="StoreAs"/> is how "the life lost this way" is said,
/// CR 119.4: the life actually lost is stored under that key — the total across every player this
/// one instruction took life from — and a later step reads it as a
/// <see cref="Stored"/> amount, so "you gain life equal to the life lost this way" is a GainLife
/// whose N is Stored(that key).
/// <para>
/// It is the life the game took, not the number the card asked for: a player at 2 who is asked for
/// 3 loses 2, and CR 119.4 says the effect refers to what was lost. So the key is never a second
/// copy of the amount — writing GainLife with the same Amount the LoseLife carried is a different
/// card, and a Devotion or an X counted twice can differ between the two readings anyway. Leave the
/// key out when nothing on the card refers back.
/// </para>
/// </summary>
[RuleRef("119")]
public sealed record LoseLife(Amount N, PlayerRef? Who = null, string? StoreAs = null) : Effect;

[RuleRef("119.5")]
public sealed record SetLife(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("122")]
public sealed record PutCounters(Selector On, string Counter, Amount N) : Effect;

[RuleRef("122")]
public sealed record RemoveCounters(Selector From, string? Counter, Amount? N = null, bool All = false) : Effect;

public sealed record MoveCounters(Selector From, Selector To, string? Counter, Amount? N = null, bool All = false) : Effect;

/// <summary>Poison, energy, experience, rad and ticket counters on a player; negative amounts remove.</summary>
[RuleRef("122.1")]
public sealed record PutPlayerCounters(PlayerRef Who, string Counter, Amount N) : Effect;

/// <summary>
/// The one zone-change verb: "return target creature to its owner's hand", "put it onto the
/// battlefield tapped", "put that card on the bottom of your library". Exile and sacrifice have
/// their own records because the rules name them.
/// </summary>
[RuleRef("400")]
public sealed record Move(
    Selector What,
    Zone To,
    Placement Where = Placement.Default,
    PlayerRef? UnderControlOf = null,
    bool Tapped = false,
    bool FaceDown = false,
    bool Attacking = false,
    bool Transformed = false,
    IReadOnlyList<CounterSpec>? WithCounters = null,
    Selector? AttachedTo = null,
    string? RememberAs = null,
    bool OwnersChoiceOfOrder = true) : Effect;

public sealed record CounterSpec(string Counter, Amount N);

/// <summary>
/// "Add {G}{G}", "add one mana of any color", "add X mana of the chosen color". Set
/// <paramref name="SpendOnlyOn"/> for a restricted mana: "add {C}. Spend this mana only to cast
/// artifact spells" (CR 106.6a) — the filter describes what the mana may be spent on, and mana with
/// a restriction is otherwise ordinary mana that empties from the pool as usual (CR 106.6b). Say
/// "only to cast creature spells" as ObjectFilter(Spell: true, Types: CardType.Creature) and "only
/// to activate abilities" with an ability filter; a restriction the filter cannot express goes to a
/// primitive request rather than being dropped, because unrestricted mana is a different card.
/// <para>
/// Set <paramref name="Key"/> when a later step has to say "that mana": CR 106.6 lets a mana
/// instruction create a delayed triggered ability that fires when the mana it produced is spent,
/// and the ManaSpent trigger names the mana by this key. Mana is not an object, so a Remembered
/// selector cannot hold it and there is nothing else to point at. Leave it null otherwise — a key
/// nothing reads is noise in the file.
/// </para>
/// </summary>
[RuleRef("106")]
public sealed record AddMana(ManaProduction Mana, PlayerRef? Who = null, Amount? N = null, ObjectFilter? SpendOnlyOn = null, string? Key = null) : Effect;

/// <summary>
/// What a mana instruction produces. <paramref name="Fixed"/> is printed mana ("{G}{G}");
/// otherwise one of the described choices.
/// <para>
/// <paramref name="LandCouldProduce"/> is "of any type that a land could produce" (CR 106.1b: a
/// land's mana ability names the types it could produce). It surveys the lands you control unless
/// <paramref name="LandCouldProduceOf"/> describes a different set — "a land an opponent controls
/// could produce" is <c>LandCouldProduce: true</c> with a filter of lands an opponent controls, and
/// the survey is over exactly the lands that filter admits. Leave the filter null for the ordinary
/// "a land you control could produce"; a filter that admits no land produces no mana, which is what
/// an empty board means and not an error.
/// </para>
/// <para>
/// <paramref name="ChosenColorKey"/> is "mana of the chosen colour": the key of the earlier step
/// that made the choice, and the colour travels with the key (CR 700.5, a choice a card made is
/// remembered by the object). One firing of a TappedForMana trigger stores the colour the land was
/// actually tapped for under the key <c>"tapped-for-mana-color"</c>, so "add one mana of any type
/// that land produced" is a ManaProduction whose ChosenColorKey is exactly that string and nothing
/// else — no second AddMana, no Choose step, and no ColorOfAttachedOrTarget, which is about an
/// attached object's colour rather than a colour a trigger observed. It is the one key the engine
/// writes for a card rather than the card naming its own.
/// </para>
/// <para>
/// <paramref name="ColorsOfRemembered"/> is "one mana of any of that card's colours": the key of an
/// earlier step that remembered the object whose colours are surveyed (CR 105.2, an object's colours
/// are its own), typically a card exiled by this permanent. The player chooses among those colours
/// when the mana is added; a remembered object with no colour produces no mana. Use it instead of
/// AnyColor, which is every colour and would let a colourless card pay for anything.
/// </para>
/// </summary>
[RuleRef("106.1")]
public sealed record ManaProduction(
    string? Fixed = null,
    bool AnyColor = false,
    Color? OneOf = null,
    bool CommanderIdentity = false,
    bool ChosenColor = false,
    string? ChosenColorKey = null,
    bool ColorOfAttachedOrTarget = false,
    bool LandCouldProduce = false,
    bool AnyType = false,
    bool Restriction = false,
    string? RestrictedTo = null,
    ObjectFilter? LandCouldProduceOf = null,
    string? ColorsOfRemembered = null);

/// <summary>"target creature gets +3/+3 until end of turn", "gains flying until end of turn", "becomes a copy".</summary>
[RuleRef("611.2")]
public sealed record Apply(ContinuousEffect What, Selector To, Duration Until) : Effect;

/// <summary>"You have no maximum hand size until end of turn": a continuous effect on a player.</summary>
public sealed record ApplyToPlayer(ContinuousEffect What, PlayerRef Who, Duration Until) : Effect;

[RuleRef("500.7")]
public sealed record ExtraTurn(PlayerRef? Who = null, Amount? N = null) : Effect;

[RuleRef("724")]
public sealed record EndTheTurn : Effect;

[RuleRef("104.2")]
public sealed record WinGame(PlayerRef? Who = null) : Effect;

[RuleRef("104.3")]
public sealed record LoseGame(PlayerRef? Who = null) : Effect;

[RuleRef("104.4")]
public sealed record DrawGame : Effect;

/// <summary>"Look at target player's hand", "look at the top four cards of your library".</summary>
public sealed record LookAt(Selector What, PlayerRef? Who = null, string? RememberAs = null) : Effect;

/// <summary>Cascade-shaped: reveal from the top until a match; remember the match and the rest separately.</summary>
public sealed record RevealUntil(PlayerRef Who, ObjectFilter Until, string RememberHitAs, string RememberRestAs, Amount? MaxCards = null) : Effect;

/// <summary>"Put them back in any order", "in a random order".</summary>
public sealed record Reorder(Selector What, Placement Where, PlayerRef? By = null) : Effect;

[RuleRef("707.10")]
public sealed record CopySpell(Selector What, Amount? N = null, bool MayChooseNewTargets = true, string? RememberAs = null) : Effect;

/// <summary>"You may cast it without paying its mana cost", "you may cast that card this turn".</summary>
[RuleRef("601")]
public sealed record MayCast(Selector What, bool WithoutPayingManaCost = false, Duration? Until = null, PlayerRef? Who = null, bool AsCopy = false, Cost? AlternativeCost = null) : Effect;

/// <summary>
/// "You may play that card this turn" (lands included). With <paramref name="WithoutPayingManaCost"/>,
/// "you may play the exiled card without paying its mana cost" — hideaway's play, CR 702.75a. A
/// card played this way is played as its type says (CR 305.1 for a land, CR 601 for a spell): a
/// spell is cast without paying its mana cost (CR 118.9), and a land played this way is still the
/// turn's land play and needs a land play left (CR 305.2), because the permission is to play, not
/// an extra play. This is the one spelling of a play permission that waives the cost: MayCast
/// carries the same waiver and cannot play a land, so hideaway's play is never MayCast.
/// </summary>
[RuleRef("702.75a")]
public sealed record MayPlay(Selector What, Duration? Until = null, PlayerRef? Who = null, bool WithoutPayingManaCost = false) : Effect;

[RuleRef("701.3")]
public sealed record Attach(Selector What, Selector To) : Effect;

public sealed record Unattach(Selector What) : Effect;

[RuleRef("708")]
public sealed record TurnFaceUp(Selector What) : Effect;

[RuleRef("708")]
public sealed record TurnFaceDown(Selector What) : Effect;

[RuleRef("702.26")]
public sealed record PhaseOut(Selector What) : Effect;

[RuleRef("706")]
public sealed record RollDie(int Sides, string StoreAs, PlayerRef? Who = null, Amount? Times = null) : Effect;

[RuleRef("705")]
public sealed record FlipCoin(string StoreAs, PlayerRef? Who = null, bool CallIt = true) : Effect;

[RuleRef("724")]
public sealed record BecomeMonarch(PlayerRef? Who = null) : Effect;

[RuleRef("725")]
public sealed record TakeInitiative(PlayerRef? Who = null) : Effect;

/// <summary>
/// A player gets an emblem (CR 114): an object in the command zone with no characteristics but
/// the abilities the card gave it. Use this only when the card's own text prints those abilities,
/// which is nearly always a planeswalker's ultimate ("You get an emblem with …"). An emblem the
/// rules define rather than the card — the Ring, CR 701.54b — is <c>RingTemptsYou</c>, and
/// the engine supplies its abilities; see that record's remarks.
/// </summary>
[RuleRef("114")]
public sealed record CreateEmblem(EmblemSpec Emblem, PlayerRef? Who = null) : Effect;

/// <summary>The emblem an effect creates, CR 114.1: a name for the log and the abilities it has.</summary>
public sealed record EmblemSpec(string Name, IReadOnlyList<Ability> Abilities);

[RuleRef("506.4")]
public sealed record RemoveFromCombat(Selector What) : Effect;

/// <summary>Inside a replacement: the entering object enters tapped.</summary>
[RuleRef("614.1c")]
public sealed record EntersTapped : Effect;

/// <summary>Inside a replacement: the entering object enters with counters.</summary>
[RuleRef("614.1c")]
public sealed record EntersWithCounters(string Counter, Amount N) : Effect;

/// <summary>Inside a replacement: the entering object enters as a copy, transformed, face down, or under another's control.</summary>
[RuleRef("614.1c")]
public sealed record EntersAs(Selector? CopyOf = null, bool Transformed = false, bool FaceDown = false, PlayerRef? UnderControlOf = null, IReadOnlyList<ContinuousEffect>? Except = null) : Effect;

/// <summary>Inside a replacement: the whole event is replaced by this effect; "instead".</summary>
[RuleRef("614.1a")]
public sealed record ReplaceWith(Effect Do) : Effect;

/// <summary>Inside a damage replacement: prevent that damage, all of it or an amount.</summary>
[RuleRef("615")]
public sealed record PreventThatDamage(Amount? N = null) : Effect;

/// <summary>Inside a replacement: the event happens with its number changed: "draw twice that many", "gain that much life plus 1".</summary>
public sealed record ModifyAmount(int Multiply = 1, int Add = 0) : Effect;
