using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// One instruction of a resolving spell or ability. The structural records combine
/// instructions; the verbs in <c>Effects.Verbs.cs</c> and the keyword actions in
/// <c>Effects.Actions.cs</c> do things. This is the whole list, and it is the name table.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
// structure
[JsonDerivedType(typeof(Sequence), nameof(Sequence))]
[JsonDerivedType(typeof(ChooseMode), nameof(ChooseMode))]
[JsonDerivedType(typeof(If), nameof(If))]
[JsonDerivedType(typeof(May), nameof(May))]
[JsonDerivedType(typeof(Unless), nameof(Unless))]
[JsonDerivedType(typeof(MayPay), nameof(MayPay))]
[JsonDerivedType(typeof(ForEach), nameof(ForEach))]
[JsonDerivedType(typeof(ForEachPlayer), nameof(ForEachPlayer))]
[JsonDerivedType(typeof(Repeat), nameof(Repeat))]
[JsonDerivedType(typeof(ChooseValue), nameof(ChooseValue))]
[JsonDerivedType(typeof(ChooseNumberSecretly), nameof(ChooseNumberSecretly))]
[JsonDerivedType(typeof(DelayedTrigger), nameof(DelayedTrigger))]
[JsonDerivedType(typeof(Unsupported), nameof(Unsupported))]
// verbs
[JsonDerivedType(typeof(DealDamage), nameof(DealDamage))]
[JsonDerivedType(typeof(DealDividedDamage), nameof(DealDividedDamage))]
[JsonDerivedType(typeof(DistributeCounters), nameof(DistributeCounters))]
[JsonDerivedType(typeof(ChangeTarget), nameof(ChangeTarget))]
[JsonDerivedType(typeof(PayCost), nameof(PayCost))]
[JsonDerivedType(typeof(Draw), nameof(Draw))]
[JsonDerivedType(typeof(GainLife), nameof(GainLife))]
[JsonDerivedType(typeof(LoseLife), nameof(LoseLife))]
[JsonDerivedType(typeof(SetLife), nameof(SetLife))]
[JsonDerivedType(typeof(PutCounters), nameof(PutCounters))]
[JsonDerivedType(typeof(RemoveCounters), nameof(RemoveCounters))]
[JsonDerivedType(typeof(MoveCounters), nameof(MoveCounters))]
[JsonDerivedType(typeof(PutPlayerCounters), nameof(PutPlayerCounters))]
[JsonDerivedType(typeof(Move), nameof(Move))]
[JsonDerivedType(typeof(AddMana), nameof(AddMana))]
[JsonDerivedType(typeof(Apply), nameof(Apply))]
[JsonDerivedType(typeof(ApplyToPlayer), nameof(ApplyToPlayer))]
[JsonDerivedType(typeof(ExtraTurn), nameof(ExtraTurn))]
[JsonDerivedType(typeof(EndTheTurn), nameof(EndTheTurn))]
[JsonDerivedType(typeof(WinGame), nameof(WinGame))]
[JsonDerivedType(typeof(LoseGame), nameof(LoseGame))]
[JsonDerivedType(typeof(DrawGame), nameof(DrawGame))]
[JsonDerivedType(typeof(LookAt), nameof(LookAt))]
[JsonDerivedType(typeof(RevealUntil), nameof(RevealUntil))]
[JsonDerivedType(typeof(Reorder), nameof(Reorder))]
[JsonDerivedType(typeof(CopySpell), nameof(CopySpell))]
[JsonDerivedType(typeof(MayCast), nameof(MayCast))]
[JsonDerivedType(typeof(MayPlay), nameof(MayPlay))]
[JsonDerivedType(typeof(Attach), nameof(Attach))]
[JsonDerivedType(typeof(Unattach), nameof(Unattach))]
[JsonDerivedType(typeof(TurnFaceUp), nameof(TurnFaceUp))]
[JsonDerivedType(typeof(TurnFaceDown), nameof(TurnFaceDown))]
[JsonDerivedType(typeof(PhaseOut), nameof(PhaseOut))]
[JsonDerivedType(typeof(RollDie), nameof(RollDie))]
[JsonDerivedType(typeof(FlipCoin), nameof(FlipCoin))]
[JsonDerivedType(typeof(BecomeMonarch), nameof(BecomeMonarch))]
[JsonDerivedType(typeof(TakeInitiative), nameof(TakeInitiative))]
[JsonDerivedType(typeof(CreateEmblem), nameof(CreateEmblem))]
[JsonDerivedType(typeof(RemoveFromCombat), nameof(RemoveFromCombat))]
[JsonDerivedType(typeof(Remember), nameof(Remember))]
[JsonDerivedType(typeof(Store), nameof(Store))]
[JsonDerivedType(typeof(EntersTapped), nameof(EntersTapped))]
[JsonDerivedType(typeof(EntersWithCounters), nameof(EntersWithCounters))]
[JsonDerivedType(typeof(EntersAs), nameof(EntersAs))]
[JsonDerivedType(typeof(ReplaceWith), nameof(ReplaceWith))]
[JsonDerivedType(typeof(PreventThatDamage), nameof(PreventThatDamage))]
[JsonDerivedType(typeof(ModifyAmount), nameof(ModifyAmount))]
[JsonDerivedType(typeof(DoNothing), nameof(DoNothing))]
// keyword actions, CR 701
[JsonDerivedType(typeof(AttachAction), nameof(AttachAction))]
[JsonDerivedType(typeof(Behold), nameof(Behold))]
[JsonDerivedType(typeof(Counter), nameof(Counter))]
[JsonDerivedType(typeof(CreateToken), nameof(CreateToken))]
[JsonDerivedType(typeof(Destroy), nameof(Destroy))]
[JsonDerivedType(typeof(Discard), nameof(Discard))]
[JsonDerivedType(typeof(Double), nameof(Double))]
[JsonDerivedType(typeof(Triple), nameof(Triple))]
[JsonDerivedType(typeof(Exchange), nameof(Exchange))]
[JsonDerivedType(typeof(Exile), nameof(Exile))]
[JsonDerivedType(typeof(Fight), nameof(Fight))]
[JsonDerivedType(typeof(Goad), nameof(Goad))]
[JsonDerivedType(typeof(Investigate), nameof(Investigate))]
[JsonDerivedType(typeof(Mill), nameof(Mill))]
[JsonDerivedType(typeof(Regenerate), nameof(Regenerate))]
[JsonDerivedType(typeof(Reveal), nameof(Reveal))]
[JsonDerivedType(typeof(Sacrifice), nameof(Sacrifice))]
[JsonDerivedType(typeof(Scry), nameof(Scry))]
[JsonDerivedType(typeof(Search), nameof(Search))]
[JsonDerivedType(typeof(Shuffle), nameof(Shuffle))]
[JsonDerivedType(typeof(Surveil), nameof(Surveil))]
[JsonDerivedType(typeof(Tap), nameof(Tap))]
[JsonDerivedType(typeof(Untap), nameof(Untap))]
[JsonDerivedType(typeof(Transform), nameof(Transform))]
[JsonDerivedType(typeof(Convert), nameof(Convert))]
[JsonDerivedType(typeof(Flip), nameof(Flip))]
[JsonDerivedType(typeof(Prepare), nameof(Prepare))]
[JsonDerivedType(typeof(Unprepare), nameof(Unprepare))]
[JsonDerivedType(typeof(Fateseal), nameof(Fateseal))]
[JsonDerivedType(typeof(Clash), nameof(Clash))]
[JsonDerivedType(typeof(Proliferate), nameof(Proliferate))]
[JsonDerivedType(typeof(Detain), nameof(Detain))]
[JsonDerivedType(typeof(Populate), nameof(Populate))]
[JsonDerivedType(typeof(Monstrosity), nameof(Monstrosity))]
[JsonDerivedType(typeof(Vote), nameof(Vote))]
[JsonDerivedType(typeof(Bolster), nameof(Bolster))]
[JsonDerivedType(typeof(Manifest), nameof(Manifest))]
[JsonDerivedType(typeof(Support), nameof(Support))]
[JsonDerivedType(typeof(Meld), nameof(Meld))]
[JsonDerivedType(typeof(Exert), nameof(Exert))]
[JsonDerivedType(typeof(Explore), nameof(Explore))]
[JsonDerivedType(typeof(Adapt), nameof(Adapt))]
[JsonDerivedType(typeof(Amass), nameof(Amass))]
[JsonDerivedType(typeof(Learn), nameof(Learn))]
[JsonDerivedType(typeof(VentureIntoTheDungeon), nameof(VentureIntoTheDungeon))]
[JsonDerivedType(typeof(Connive), nameof(Connive))]
[JsonDerivedType(typeof(Incubate), nameof(Incubate))]
[JsonDerivedType(typeof(RingTemptsYou), nameof(RingTemptsYou))]
[JsonDerivedType(typeof(VillainousChoice), nameof(VillainousChoice))]
[JsonDerivedType(typeof(TimeTravel), nameof(TimeTravel))]
[JsonDerivedType(typeof(Discover), nameof(Discover))]
[JsonDerivedType(typeof(Cloak), nameof(Cloak))]
[JsonDerivedType(typeof(CollectEvidence), nameof(CollectEvidence))]
[JsonDerivedType(typeof(Suspect), nameof(Suspect))]
[JsonDerivedType(typeof(Forage), nameof(Forage))]
[JsonDerivedType(typeof(ManifestDread), nameof(ManifestDread))]
[JsonDerivedType(typeof(Endure), nameof(Endure))]
[JsonDerivedType(typeof(Harness), nameof(Harness))]
[JsonDerivedType(typeof(Airbend), nameof(Airbend))]
[JsonDerivedType(typeof(Earthbend), nameof(Earthbend))]
[JsonDerivedType(typeof(Blight), nameof(Blight))]
[JsonDerivedType(typeof(Heal), nameof(Heal))]
[JsonDerivedType(typeof(Recruit), nameof(Recruit))]
[JsonDerivedType(typeof(Planeswalk), nameof(Planeswalk))]
[JsonDerivedType(typeof(SetInMotion), nameof(SetInMotion))]
[JsonDerivedType(typeof(Abandon), nameof(Abandon))]
[JsonDerivedType(typeof(Assemble), nameof(Assemble))]
[JsonDerivedType(typeof(OpenAttraction), nameof(OpenAttraction))]
[JsonDerivedType(typeof(RollToVisitAttractions), nameof(RollToVisitAttractions))]
public abstract record Effect;

/// <summary>Instructions in order. "Draw two cards, then discard a card."</summary>
public sealed record Sequence(IReadOnlyList<Effect> Steps) : Effect;

/// <summary>
/// "Choose one —", "choose two", "choose one or more". Entwine and escalate set <paramref name="Max"/> to the mode count.
/// <para>
/// <paramref name="MaxIf"/> is a mode count that depends on the game as the spell is cast: "choose
/// one. If you control a commander as you cast this spell, you may choose both instead" is Max 1
/// with MaxIf a Controls condition over a filter of isCommander and <paramref name="MaxIfMet"/> 2.
/// The modes are announced as the spell is cast (CR 601.2b, which sends the count to CR 700.2), so
/// the condition is read at that announcement and never again: a commander that leaves later
/// changes nothing. MaxIfMet is the ceiling while the condition holds and must exceed Max, or the
/// rider says nothing (L024); Min is the same either way. Never an If around a ChooseMode, which
/// would read the condition on resolution, and never two ChooseModes for one printed list.
/// </para>
/// <para>
/// <paramref name="Budget"/> is a mode count spent rather than counted: "choose modes with total
/// cost {5} or less" over modes that each print a price. The modes are still announced as the spell
/// is cast (CR 601.2b sends the choice to CR 700.2), and the announcement is legal only while the
/// prices of the modes chosen total no more than the budget. Every mode then needs its own
/// <see cref="Mode.Cost"/> and a priced mode needs a budget to spend against, so the two fields are
/// set together or not at all (L032). <paramref name="Max"/> stops meaning the number of modes when
/// a budget rules them — leave it at its default and let the prices do the counting — and
/// <paramref name="SameModeTwice"/> is what "you may choose the same mode more than once" says here,
/// each repeat paying its price again.
/// </para>
/// </summary>
[RuleRef("700.2")]
public sealed record ChooseMode(IReadOnlyList<Mode> Modes, int Min = 1, int Max = 1, bool SameModeTwice = false, Condition? MaxIf = null, int? MaxIfMet = null, Amount? Budget = null) : Effect;

/// <summary>
/// One printed mode: the line as printed in <paramref name="Text"/> and what it does in
/// <paramref name="Do"/>.
/// <para>
/// <paramref name="Cost"/> is the price a mode carries when its list is spent against a
/// <see cref="ChooseMode.Budget"/> rather than counted — the mana the printed line puts in front of
/// the mode, whose symbols are what the budget counts (CR 700.2). Nothing is paid for it: choosing
/// the mode adds nothing to the spell's cost, and the price is read against the budget and nowhere
/// else. A cost a player really pays to choose a mode is an additional cost on the face, not this.
/// Leave it null for an ordinary numbered list of modes.
/// </para>
/// <para>
/// <paramref name="PaidCost"/> is spree, CR 702.172: "choose one or more. As an additional cost to
/// cast this spell, pay the costs associated with those modes." Here the price in front of the mode
/// is <b>really paid</b> — it is an additional cost of the spell (CR 601.2f–601.2h), announced with
/// the modes and paid with the rest of the total cost, and a mode nobody can pay for cannot be
/// chosen. That is the opposite of <paramref name="Cost"/>, which is spent against a
/// <see cref="ChooseMode.Budget"/> and paid to nobody, so a mode carries one or the other and never
/// both (L046), and a spree spell's <see cref="ChooseMode"/> has no budget to spend (L046): its
/// modes are counted, from one up to all of them, and priced in the real cost.
/// </para>
/// </summary>
[RuleRef("700.2")]
public sealed record Mode(string Text, Effect Do, Cost? Cost = null, Cost? PaidCost = null);

/// <summary>"If you do, ...", "if it's a creature card, ...".</summary>
public sealed record If(Condition Test, Effect Then, Effect? Else = null) : Effect;

/// <summary>"You may ...": the player decides on resolution. <paramref name="Who"/> defaults to you.</summary>
public sealed record May(Effect Do, PlayerRef? Who = null, string? StoreChoiceAs = null) : Effect;

/// <summary>"Counter target spell unless its controller pays {3}."</summary>
public sealed record Unless(Effect Do, Cost Paid, PlayerRef Who) : Effect;

/// <summary>
/// "You may pay {4}. If you do, ...": a cost offered to a player while the effect resolves,
/// CR 118.12 — "[A player] may [do something]. If [that player] [does, doesn't, or can't],
/// [effect]. The action [do something] is a cost, paid when the spell or ability resolves."
/// <paramref name="Paid"/> is that cost, <paramref name="Then"/> the "if you do" half,
/// <paramref name="Else"/> the "if you don't" half when the card prints one, and
/// <paramref name="Who"/> the player offered the choice, defaulting to you.
/// <para>
/// This is the one spelling of the whole sentence. Do not write it as May wrapping PayCost with a
/// StoreChoiceAs key followed by If(ChoiceIs(key, "yes"), ...): that reads the same to a person and
/// is four records the translator has to agree on instead of one. PayCost stays for a cost the
/// card makes a player pay outright, with no choice and no "if you do".
/// </para>
/// <para>
/// It is the mirror of Unless, not a synonym of it. CR 118.12a: "[do something] unless [a player
/// does something else]" means "[a player] may [do something else]. If [that player] doesn't, [do
/// something]". Unless carries the effect that happens when the cost goes unpaid, which is how
/// "counter it unless its controller pays {3}" is written; MayPay carries the effect that happens
/// when it is paid instead. Which half the card prints is the half the translator keeps,
/// so a card is never rewritten from one into the other.
/// </para>
/// </summary>
[RuleRef("118.12")]
public sealed record MayPay(Cost Paid, Effect Then, Effect? Else = null, PlayerRef? Who = null) : Effect;

/// <summary>"For each creature you control, ...". Inside, <see cref="Triggering"/> is the current object.</summary>
public sealed record ForEach(Selector Over, Effect Do) : Effect;

/// <summary>"Each player ...", in APNAP order. Inside, <see cref="TriggeringPlayer"/> is the current player.</summary>
public sealed record ForEachPlayer(PlayerRef Over, Effect Do) : Effect;

/// <summary>"Repeat this process N times."</summary>
public sealed record Repeat(Amount Times, Effect Do) : Effect;

/// <summary>"Choose a color", "choose a creature type", "choose a number": the choice is stored under a key.</summary>
public sealed record ChooseValue(ChooseWhat What, string Key, PlayerRef? Who = null, IReadOnlyList<string>? Options = null) : Effect;

/// <summary>
/// "Each player secretly chooses a number, then those numbers are revealed", CR 700.2 with 101.4:
/// one step in which every player <paramref name="Who"/> admits chooses a number without seeing what
/// the others chose, and the choices are revealed as the step finishes. The reveal is part of this
/// step and never a second one — there is nothing for a later step to reveal, and a choice that was
/// never secret is a different card.
/// <para>
/// The numbers are stored under <paramref name="Key"/>, and the ability reads them back with
/// <see cref="HighestChosen"/>, <see cref="LowestChosen"/> and the <see cref="PlayerWhoChose"/>
/// player reference. <paramref name="Min"/> and <paramref name="Max"/> are the printed bounds where
/// a card prints them; leave them out for an unbounded choice.
/// </para>
/// <para>
/// It is not ChooseValue with ChooseWhat.Number, which is one player choosing in the open and read
/// back as a ChosenNumber, and it is not a ForEachPlayer around one: choices made in APNAP order are
/// made in turn and each player would see the ones before theirs (CR 101.4), which is the whole of
/// what this record says. A secret choice nothing reads back is a lost sentence (L035).
/// </para>
/// </summary>
[RuleRef("700.2")]
public sealed record ChooseNumberSecretly(string Key, PlayerRef? Who = null, Amount? Min = null, Amount? Max = null) : Effect;

/// <summary>
/// "At the beginning of the next end step, return it." A trigger created by this effect, CR 603.7.
/// <para>
/// <paramref name="Until"/> is the printed deadline, CR 603.7c: "until end of turn, whenever a
/// creature you control deals damage to a player, draw that many cards" is one DelayedTrigger with
/// Once false and Until UntilEndOfTurn — the ability waits for its event for that long and is gone
/// when the duration ends, whether or not it ever fired. It is the same "this turn" that ends a
/// continuous effect, so it is a <see cref="Duration"/> and not a condition or a turn count.
/// </para>
/// <para>
/// Leave it null for the ordinary one-shot, whose own event is its end ("at the beginning of the
/// next end step"): a deadline there would either repeat what the event already says or contradict
/// it. Set <paramref name="Once"/> false whenever the printed line says "whenever" inside a
/// duration, and keep it true for the single firing; the two fields answer different questions —
/// how many times, and for how long — and a card that prints a duration and "whenever" prints both.
/// </para>
/// </summary>
[RuleRef("603.7")]
public sealed record DelayedTrigger(Trigger When, Effect Do, bool Once = true, Duration? Until = null) : Effect;

/// <summary>
/// The translator could not express something in this vocabulary. A file containing this fails
/// the linter and never reaches a game; the pipeline ranks these to find the next primitive.
/// </summary>
public sealed record Unsupported(string Reason, string? Text = null) : Effect;

/// <summary>A choice a player is asked to make, or a value an effect records for later steps.</summary>
public sealed record Store(string Key, Amount Value) : Effect;

/// <summary>Remember the objects a selector resolves to under a key, for later steps.</summary>
public sealed record Remember(string Key, Selector What) : Effect;

public sealed record DoNothing : Effect;
