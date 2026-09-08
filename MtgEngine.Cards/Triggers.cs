using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// The event a triggered ability watches for, CR 603. Each record names the event and
/// describes the objects or players it must involve. The interpreter binds
/// <see cref="Triggering"/> selectors to the matched objects.
/// </summary>
[RuleRef("603")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntersBattlefield), nameof(EntersBattlefield))]
[JsonDerivedType(typeof(LeavesBattlefield), nameof(LeavesBattlefield))]
[JsonDerivedType(typeof(Dies), nameof(Dies))]
[JsonDerivedType(typeof(ZoneChange), nameof(ZoneChange))]
[JsonDerivedType(typeof(SpellCast), nameof(SpellCast))]
[JsonDerivedType(typeof(AtStep), nameof(AtStep))]
[JsonDerivedType(typeof(Attacks), nameof(Attacks))]
[JsonDerivedType(typeof(AttacksAlone), nameof(AttacksAlone))]
[JsonDerivedType(typeof(AttacksWithCreatures), nameof(AttacksWithCreatures))]
[JsonDerivedType(typeof(Blocks), nameof(Blocks))]
[JsonDerivedType(typeof(BecomesBlocked), nameof(BecomesBlocked))]
[JsonDerivedType(typeof(DealsDamage), nameof(DealsDamage))]
[JsonDerivedType(typeof(IsDealtDamage), nameof(IsDealtDamage))]
[JsonDerivedType(typeof(PlayerIsDealtDamage), nameof(PlayerIsDealtDamage))]
[JsonDerivedType(typeof(BecomesTapped), nameof(BecomesTapped))]
[JsonDerivedType(typeof(BecomesUntapped), nameof(BecomesUntapped))]
[JsonDerivedType(typeof(BecomesTarget), nameof(BecomesTarget))]
[JsonDerivedType(typeof(Draws), nameof(Draws))]
[JsonDerivedType(typeof(Discards), nameof(Discards))]
[JsonDerivedType(typeof(GainsLife), nameof(GainsLife))]
[JsonDerivedType(typeof(LosesLife), nameof(LosesLife))]
[JsonDerivedType(typeof(Sacrifices), nameof(Sacrifices))]
[JsonDerivedType(typeof(CounterPlaced), nameof(CounterPlaced))]
[JsonDerivedType(typeof(CounterRemoved), nameof(CounterRemoved))]
[JsonDerivedType(typeof(AbilityActivated), nameof(AbilityActivated))]
[JsonDerivedType(typeof(LandPlayed), nameof(LandPlayed))]
[JsonDerivedType(typeof(TokenCreated), nameof(TokenCreated))]
[JsonDerivedType(typeof(Searches), nameof(Searches))]
[JsonDerivedType(typeof(Scries), nameof(Scries))]
[JsonDerivedType(typeof(Surveils), nameof(Surveils))]
[JsonDerivedType(typeof(Mills), nameof(Mills))]
[JsonDerivedType(typeof(Transforms), nameof(Transforms))]
[JsonDerivedType(typeof(TurnsFaceUp), nameof(TurnsFaceUp))]
[JsonDerivedType(typeof(Cycles), nameof(Cycles))]
[JsonDerivedType(typeof(SpellCountered), nameof(SpellCountered))]
[JsonDerivedType(typeof(Fights), nameof(Fights))]
[JsonDerivedType(typeof(Exerted), nameof(Exerted))]
[JsonDerivedType(typeof(Crewed), nameof(Crewed))]
[JsonDerivedType(typeof(Explores), nameof(Explores))]
[JsonDerivedType(typeof(ChapterReached), nameof(ChapterReached))]
[JsonDerivedType(typeof(LevelReached), nameof(LevelReached))]
[JsonDerivedType(typeof(RoomUnlocked), nameof(RoomUnlocked))]
[JsonDerivedType(typeof(DieRolled), nameof(DieRolled))]
[JsonDerivedType(typeof(CoinFlipped), nameof(CoinFlipped))]
[JsonDerivedType(typeof(Voted), nameof(Voted))]
[JsonDerivedType(typeof(RingTempted), nameof(RingTempted))]
[JsonDerivedType(typeof(BecomesMonarch), nameof(BecomesMonarch))]
[JsonDerivedType(typeof(TakesInitiative), nameof(TakesInitiative))]
[JsonDerivedType(typeof(PhasesOut), nameof(PhasesOut))]
[JsonDerivedType(typeof(PhasesIn), nameof(PhasesIn))]
[JsonDerivedType(typeof(Regenerated), nameof(Regenerated))]
[JsonDerivedType(typeof(Kicked), nameof(Kicked))]
[JsonDerivedType(typeof(Foretold), nameof(Foretold))]
[JsonDerivedType(typeof(Investigated), nameof(Investigated))]
[JsonDerivedType(typeof(EnergyPaid), nameof(EnergyPaid))]
[JsonDerivedType(typeof(DungeonCompleted), nameof(DungeonCompleted))]
[JsonDerivedType(typeof(Learns), nameof(Learns))]
[JsonDerivedType(typeof(CommanderCast), nameof(CommanderCast))]
[JsonDerivedType(typeof(TappedForMana), nameof(TappedForMana))]
[JsonDerivedType(typeof(ManaSpent), nameof(ManaSpent))]
[JsonDerivedType(typeof(AnyOfTriggers), nameof(AnyOfTriggers))]
public abstract record Trigger;

/// <summary>"When this creature enters", "Whenever another creature you control enters".</summary>
[RuleRef("603.6a")]
public sealed record EntersBattlefield(ObjectFilter What, PlayerRef? UnderControlOf = null) : Trigger;

[RuleRef("603.6c")]
public sealed record LeavesBattlefield(ObjectFilter What) : Trigger;

/// <summary>"Whenever a creature dies": put into a graveyard from the battlefield, CR 700.4.</summary>
[RuleRef("700.4")]
public sealed record Dies(ObjectFilter What) : Trigger;

/// <summary>
/// Any other zone change: "whenever a card is put into your graveyard from anywhere", CR 603.6.
/// Every field but <paramref name="What"/> is optional and each one set must hold.
/// <para>
/// <paramref name="From"/> names the zone the object left ("from your graveyard"), and
/// <paramref name="NotFrom"/> is the printed exception: "put into a graveyard from anywhere other
/// than the battlefield" is To Graveyard with NotFrom Battlefield, and it is ONE trigger, never a
/// ZoneChange wrapped in a condition about where the object was — the object is gone by the time
/// the ability's condition is read (CR 603.10), so a condition there tests nothing. Set one or the
/// other, never both: a card names the zone it cares about or the one it excludes.
/// </para>
/// <para>
/// <paramref name="To"/> is optional so that a trigger about leaving one zone for anywhere is one
/// trigger too: "whenever a card leaves your graveyard" is From Graveyard with no To. Leave To out
/// only then — a trigger with no From, no NotFrom and no To fires on every zone change in the game
/// and is a lost sentence (L025). "Leaves the battlefield" keeps its own record,
/// <see cref="LeavesBattlefield"/>, and "dies" keeps <see cref="Dies"/>.
/// </para>
/// </summary>
[RuleRef("603.6")]
public sealed record ZoneChange(ObjectFilter What, Zone? From = null, Zone? To = null, Zone? NotFrom = null) : Trigger;

[RuleRef("603.2")]
public sealed record SpellCast(ObjectFilter Spell, PlayerRef By) : Trigger;

/// <summary>"At the beginning of your upkeep", "at the beginning of each end step".</summary>
[RuleRef("603.2")]
public sealed record AtStep(Step Step, PlayerRef Whose) : Trigger;

/// <summary>"Whenever this creature attacks", "whenever a creature you control attacks a planeswalker".</summary>
[RuleRef("508.2")]
public sealed record Attacks(ObjectFilter Who, PlayerRef? Defending = null, ObjectFilter? AttackedPermanent = null) : Trigger;

public sealed record AttacksAlone(ObjectFilter Who) : Trigger;

/// <summary>
/// "Whenever an opponent attacks with creatures": one firing per declaration of attackers,
/// CR 508.1, not one per attacking creature. It triggers once for the player
/// <paramref name="By"/> names when that player declares one or more attackers, whatever they
/// attack and however many there are.
/// <para>
/// It is not <see cref="Attacks"/>, which is CR 508.2 and fires once for each creature: a card that
/// says "whenever an opponent attacks with creatures ... draw a card" draws one card for a
/// three-creature attack, and written as Attacks it would draw three. It is not
/// <see cref="AttacksAlone"/> either, which is the one-creature case.
/// </para>
/// <para>
/// The "if two or more of those creatures are attacking you and/or planeswalkers you control" half
/// is the ability's own <c>If</c> — a Compare over the <see cref="AttackersAgainst"/> amount, which
/// counts this declaration's attackers aimed at one player and the permanents that player controls.
/// The two are read at the same moment and neither is written as the other.
/// </para>
/// </summary>
[RuleRef("508.1")]
public sealed record AttacksWithCreatures(PlayerRef By) : Trigger;

[RuleRef("509.2")]
public sealed record Blocks(ObjectFilter Blocker, ObjectFilter? Blocked = null) : Trigger;

[RuleRef("509.2")]
public sealed record BecomesBlocked(ObjectFilter Attacker, ObjectFilter? By = null) : Trigger;

/// <summary>"Whenever this creature deals combat damage to a player", "whenever a source you control deals damage".</summary>
[RuleRef("120")]
public sealed record DealsDamage(ObjectFilter Source, ObjectFilter? ToObject = null, PlayerFilter? ToPlayer = null, bool? Combat = null) : Trigger;

[RuleRef("120")]
public sealed record IsDealtDamage(ObjectFilter To, ObjectFilter? By = null, bool? Combat = null) : Trigger;

[RuleRef("120")]
public sealed record PlayerIsDealtDamage(PlayerRef Who, ObjectFilter? By = null, bool? Combat = null) : Trigger;

public sealed record BecomesTapped(ObjectFilter What) : Trigger;

public sealed record BecomesUntapped(ObjectFilter What) : Trigger;

/// <summary>"Whenever this creature becomes the target of a spell or ability".</summary>
[RuleRef("115.7")]
public sealed record BecomesTarget(ObjectFilter What, ObjectFilter? By = null, PlayerRef? ByController = null) : Trigger;

/// <summary>
/// "Whenever a player draws a card" (CR 121.1 — a draw is one event per card, so a player drawing
/// two cards fires this twice). <paramref name="FirstEachTurn"/> is "the first card they draw each
/// turn"; <paramref name="DuringDrawStep"/> pins the trigger to that step or excludes it.
/// <para>
/// <paramref name="ExceptFirstInDrawStep"/> is the printed exception "except the first one they draw
/// in each of their draw steps": every draw fires the ability but that one, and the exception is
/// about the player's own draw step, so a draw taken in someone else's draw step is not spared. It
/// is the turn-based draw the exception is written to skip (CR 504.1), so a player who skips their
/// draw step, or whose first draw of it is a second card, spares only the one draw that came first.
/// </para>
/// <para>
/// It is the opposite of <paramref name="FirstEachTurn"/> and the two never both hold (L033): one
/// admits that draw alone, the other admits every draw but it. It is also not
/// <paramref name="DuringDrawStep"/> false, which refuses the whole step rather than its first
/// draw.
/// </para>
/// </summary>
[RuleRef("121.1")]
public sealed record Draws(PlayerRef Who, bool FirstEachTurn = false, bool? DuringDrawStep = null, bool ExceptFirstInDrawStep = false) : Trigger;

public sealed record Discards(PlayerRef Who, ObjectFilter? What = null) : Trigger;

public sealed record GainsLife(PlayerRef Who) : Trigger;

public sealed record LosesLife(PlayerRef Who) : Trigger;

public sealed record Sacrifices(PlayerRef Who, ObjectFilter What) : Trigger;

/// <summary>"Whenever one or more +1/+1 counters are put on a creature you control"; <paramref name="Nth"/> for "the third".</summary>
[RuleRef("122.7")]
public sealed record CounterPlaced(ObjectFilter On, string? Counter = null, int? Nth = null) : Trigger;

public sealed record CounterRemoved(ObjectFilter From, string? Counter = null) : Trigger;

public sealed record AbilityActivated(ObjectFilter Source, AbilityKind Ability = AbilityKind.Any, PlayerRef? By = null) : Trigger;

/// <summary>Landfall: "whenever a land enters under your control" is <see cref="EntersBattlefield"/>; this is the play itself.</summary>
[RuleRef("305")]
public sealed record LandPlayed(PlayerRef By) : Trigger;

public sealed record TokenCreated(PlayerRef By, ObjectFilter? What = null) : Trigger;

/// <summary>
/// "Whenever an opponent searches their library", CR 701.19: the search itself is the event, and it
/// fires once per search however many cards that search finds and whether it finds any. The search
/// a card, spell or ability instructed is the one this watches (CR 701.19a); a player looking at
/// their own library with no instruction to search is not searching.
/// <para>
/// <paramref name="Who"/> describes the searcher, so "an opponent" is a PlayerFilter with Opponent
/// true — a filter and not a PlayerRef, because the trigger matches whoever searched rather than
/// naming a player in advance, the same reading DealsDamage takes of ToPlayer. The library searched
/// is the searcher's own, which is every printed sentence of this shape; a card that watches a
/// search of someone else's library is a different trigger and does not exist yet.
/// </para>
/// <para>
/// It is not the Search effect, which is this card telling a player to search, and it is not a
/// ZoneChange or a Mills: nothing has left the library when this fires, and CR 701.19c shuffles
/// only if the search moved a card.
/// </para>
/// </summary>
[RuleRef("701.19")]
public sealed record Searches(PlayerFilter Who) : Trigger;

public sealed record Scries(PlayerRef Who) : Trigger;

public sealed record Surveils(PlayerRef Who) : Trigger;

public sealed record Mills(PlayerRef Who, ObjectFilter? What = null) : Trigger;

public sealed record Transforms(ObjectFilter What) : Trigger;

[RuleRef("708")]
public sealed record TurnsFaceUp(ObjectFilter What) : Trigger;

[RuleRef("702.29")]
public sealed record Cycles(PlayerRef Who, ObjectFilter? What = null) : Trigger;

public sealed record SpellCountered(ObjectFilter Spell, ObjectFilter? By = null) : Trigger;

public sealed record Fights(ObjectFilter Who) : Trigger;

[RuleRef("701.43")]
public sealed record Exerted(ObjectFilter What) : Trigger;

[RuleRef("702.122")]
public sealed record Crewed(ObjectFilter Vehicle) : Trigger;

[RuleRef("701.44")]
public sealed record Explores(ObjectFilter What) : Trigger;

/// <summary>A Saga chapter, CR 714. Chapter abilities are triggered abilities with this trigger.</summary>
[RuleRef("714.2")]
public sealed record ChapterReached(int Chapter, int? ThroughChapter = null) : Trigger;

[RuleRef("702.87")]
public sealed record LevelReached(int Level) : Trigger;

/// <summary>A Room's door is unlocked, CR 709.5: the half gains its unlocked designation.</summary>
[RuleRef("709.5")]
public sealed record RoomUnlocked(ObjectFilter What) : Trigger;

public sealed record DieRolled(PlayerRef By, Comparison? Result = null) : Trigger;

public sealed record CoinFlipped(PlayerRef By, bool? Won = null) : Trigger;

public sealed record Voted(PlayerRef By) : Trigger;

[RuleRef("701.54")]
public sealed record RingTempted(PlayerRef Who) : Trigger;

[RuleRef("724")]
public sealed record BecomesMonarch(PlayerRef Who) : Trigger;

[RuleRef("725")]
public sealed record TakesInitiative(PlayerRef Who) : Trigger;

[RuleRef("702.26")]
public sealed record PhasesOut(ObjectFilter What) : Trigger;

[RuleRef("702.26")]
public sealed record PhasesIn(ObjectFilter What) : Trigger;

[RuleRef("701.19")]
public sealed record Regenerated(ObjectFilter What) : Trigger;

[RuleRef("702.33")]
public sealed record Kicked(ObjectFilter Spell) : Trigger;

[RuleRef("702.143")]
public sealed record Foretold(PlayerRef By) : Trigger;

[RuleRef("701.16")]
public sealed record Investigated(PlayerRef Who) : Trigger;

public sealed record EnergyPaid(PlayerRef Who) : Trigger;

[RuleRef("701.49")]
public sealed record DungeonCompleted(PlayerRef Who) : Trigger;

[RuleRef("701.48")]
public sealed record Learns(PlayerRef Who) : Trigger;

[RuleRef("903")]
public sealed record CommanderCast(PlayerRef By) : Trigger;

/// <summary>
/// "Whenever a land you control is tapped for mana", CR 106.12a: the trigger fires when a mana
/// ability of the permanent whose activation cost includes {T} resolves and produces mana
/// (CR 106.12). <paramref name="What"/> describes the permanent tapped.
/// <para>
/// Set <paramref name="OfColor"/> for "tapped for mana of a specified type" — "tapped for green
/// mana" — and set <paramref name="OfChosenColorKey"/> instead when the card reads "tapped for mana
/// of the chosen color", naming the key an earlier ChooseValue(ChooseWhat.Color, key) stored the
/// colour under. Set neither for "tapped for mana" of any type. Never set both: a card prints one
/// colour restriction, and the fixed colour and the chosen one are different cards.
/// </para>
/// <para>
/// This is the trigger half only. What the ability then does — "add an additional one mana of that
/// color" — is an AddMana whose ManaProduction carries the same ChosenColorKey, so the two halves
/// name the same choice.
/// </para>
/// <para>
/// Every firing of this trigger stores the colour the permanent was actually tapped for under the
/// key <c>"tapped-for-mana-color"</c>, whatever <paramref name="OfColor"/> or
/// <paramref name="OfChosenColorKey"/> say. That is how "add one mana of any type that land
/// produced" is written: the ability's AddMana carries a ManaProduction whose ChosenColorKey is the
/// literal string "tapped-for-mana-color", and there is no other spelling of it — the key names a
/// colour the game observed, so no step on the card produces it and none has to.
/// </para>
/// </summary>
[RuleRef("106.12a")]
public sealed record TappedForMana(ObjectFilter What, Color? OfColor = null, string? OfChosenColorKey = null) : Trigger;

/// <summary>
/// "When that mana is spent to cast a spell, ...", CR 106.6: a mana instruction may create a
/// delayed triggered ability that triggers when the mana it produced is spent (CR 603.7a).
/// <paramref name="ManaKey"/> is the Key of the AddMana in the same ability that produced that
/// mana, which is how "that mana" is said without pointing at an object — mana is not an object.
/// One delayed ability is created for each mana that AddMana produced (CR 106.6a).
/// <paramref name="OnSpell"/> narrows what the mana was spent on when the card does ("spent to cast
/// a creature spell"); leave it null for "spent to cast a spell".
/// <para>
/// Use it as the When of a DelayedTrigger the same resolution creates, never as a printed
/// TriggeredAbility's own trigger: the mana it watches does not exist until the AddMana has run,
/// and a standing trigger would have no key to match.
/// </para>
/// </summary>
[RuleRef("106.6")]
public sealed record ManaSpent(string ManaKey, ObjectFilter? OnSpell = null) : Trigger;

/// <summary>"Whenever you cast or copy an instant or sorcery": several events, one ability.</summary>
public sealed record AnyOfTriggers(IReadOnlyList<Trigger> Of) : Trigger;
