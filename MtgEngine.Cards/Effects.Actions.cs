namespace MtgEngine.Cards;

// The keyword actions of CR 701, one record each, in rule order. "Activate", "Cast" and
// "Play" are not effects a card performs on resolution and live in Effects.Verbs.cs as
// MayCast and MayPlay; "Tap and Untap" is two records.

[RuleRef("701.3")]
public sealed record AttachAction(Selector What, Selector To) : Effect;

/// <summary>"Behold a Dragon": reveal one from hand or choose one you control, remembered under a key.</summary>
[RuleRef("701.4")]
public sealed record Behold(ObjectFilter Quality, string RememberAs, PlayerRef? Who = null) : Effect;

[RuleRef("701.6")]
public sealed record Counter(Selector What, Zone? PutInto = null) : Effect;

[RuleRef("701.7")]
public sealed record CreateToken(TokenSpec Token, Amount? N = null, PlayerRef? Who = null, bool Tapped = false, bool Attacking = false, Selector? CopyOf = null, string? RememberAs = null) : Effect;

[RuleRef("701.8")]
public sealed record Destroy(Selector What, bool CantBeRegenerated = false) : Effect;

[RuleRef("701.9")]
public sealed record Discard(Amount? N = null, PlayerRef? Who = null, ObjectFilter? What = null, DiscardChooser By = DiscardChooser.Owner, bool WholeHand = false, string? RememberAs = null) : Effect;

[RuleRef("701.10")]
public sealed record Double(DoubleWhat What, Selector? Of = null, PlayerRef? Player = null, string? CounterKind = null, Duration? Until = null) : Effect;

[RuleRef("701.11")]
public sealed record Triple(DoubleWhat What, Selector? Of = null, PlayerRef? Player = null, string? CounterKind = null, Duration? Until = null) : Effect;

[RuleRef("701.12")]
public sealed record Exchange(ExchangeWhat What, Selector? A = null, Selector? B = null, PlayerRef? PlayerA = null, PlayerRef? PlayerB = null) : Effect;

[RuleRef("701.13")]
public sealed record Exile(Selector What, Duration? Until = null, bool FaceDown = false, string? RememberAs = null, Selector? ExiledWith = null) : Effect;

[RuleRef("701.14")]
public sealed record Fight(Selector A, Selector B) : Effect;

[RuleRef("701.15")]
public sealed record Goad(Selector What, PlayerRef? By = null) : Effect;

[RuleRef("701.16")]
public sealed record Investigate(Amount? N = null, PlayerRef? Who = null) : Effect;

[RuleRef("701.17")]
public sealed record Mill(Amount N, PlayerRef? Who = null, string? RememberAs = null) : Effect;

[RuleRef("701.19")]
public sealed record Regenerate(Selector What) : Effect;

[RuleRef("701.20")]
public sealed record Reveal(Selector What, PlayerRef? Who = null, string? RememberAs = null) : Effect;

/// <summary>"Sacrifice a creature": the player chooses; "sacrifice it": a selector.</summary>
[RuleRef("701.21")]
public sealed record Sacrifice(Selector? What = null, ObjectFilter? Choice = null, Amount? N = null, PlayerRef? Who = null, string? RememberAs = null) : Effect;

[RuleRef("701.22")]
public sealed record Scry(Amount N, PlayerRef? Who = null) : Effect;

/// <summary>
/// "Search your library for a basic land card, put it onto the battlefield tapped, then shuffle."
/// One zone goes in <paramref name="From"/>. A card that searches several at once — "search your
/// graveyard, hand, and library for a card named ..." — puts every zone in <paramref name="Zones"/>
/// in printed order and leaves <paramref name="From"/> alone; the whole search is one action over
/// the union, so a single card is found across all of them (CR 701.23a).
/// Shuffling follows CR 701.23c: a library that was searched is shuffled afterwards and other zones
/// are not, so a multi-zone search with <paramref name="ShuffleAfter"/> set shuffles only the
/// libraries among <paramref name="Zones"/>. Set <paramref name="ShuffleOnlyIfFound"/> for the
/// printed rider "if you do, shuffle" / "shuffle only if you searched your library this way", where
/// no shuffle happens when the search found nothing there.
/// <para>
/// <paramref name="OthersTo"/> is one search with two destinations: "search your library for up to
/// two basic land cards, reveal them, put one onto the battlefield tapped and the other into your
/// hand". It is one search of up to <paramref name="UpTo"/> cards (CR 701.23a — the whole search is
/// one action), the first card found goes to <paramref name="To"/> with
/// <paramref name="Where"/> and <paramref name="Tapped"/>, and every other card found goes to
/// <paramref name="OthersTo"/>, tapped when <paramref name="OthersTapped"/> is set. Never write it
/// as two Searches: two searches see the library twice, so a player could find two cards that one
/// search could not, and each would shuffle. Leave <paramref name="OthersTo"/> null when every card
/// found goes to the same zone, and leave <paramref name="OthersTapped"/> false unless
/// <paramref name="OthersTo"/> is the battlefield, because nothing is tapped in another zone.
/// </para>
/// </summary>
[RuleRef("701.23")]
public sealed record Search(
    ObjectFilter For,
    Zone To,
    Amount? UpTo = null,
    PlayerRef? Who = null,
    Zone From = Zone.Library,
    Placement Where = Placement.Default,
    bool Reveal = false,
    bool Tapped = false,
    bool ShuffleAfter = true,
    string? RememberAs = null,
    IReadOnlyList<Zone>? Zones = null,
    bool ShuffleOnlyIfFound = false,
    Zone? OthersTo = null,
    bool OthersTapped = false) : Effect;

[RuleRef("701.24")]
public sealed record Shuffle(PlayerRef? Who = null, Selector? IntoLibrary = null) : Effect;

[RuleRef("701.25")]
public sealed record Surveil(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("701.26")]
public sealed record Tap(Selector What) : Effect;

[RuleRef("701.26")]
public sealed record Untap(Selector What) : Effect;

[RuleRef("701.27")]
public sealed record Transform(Selector What) : Effect;

[RuleRef("701.28")]
public sealed record Convert(Selector What) : Effect;

/// <summary>
/// "Flip it", CR 710.2: a flip permanent is turned upside down and its alternative (FaceRole.Flipped)
/// face's name, types, abilities and power/toughness apply. This is not transform (CR 701.27a),
/// which turns a double-faced card over to its back face, and not FlipCoin, which is CR 705.
/// A flip is one way only — CR 710.4 has no action that unflips — so a card that "returns it to its
/// unflipped state" is a different mechanic and goes to a primitive request. Use this record
/// whenever the printed text says the permanent flips, whatever the layout says.
/// </summary>
[RuleRef("710")]
public sealed record Flip(Selector What) : Effect;

/// <summary>
/// Prepare, CR 702.180: the object gains the Prepared designation, which lasts until something takes
/// it away and is not a counter and not an ability. Ask about it with the IsPrepared predicate.
/// </summary>
[RuleRef("702.180")]
public sealed record Prepare(Selector What) : Effect;

/// <summary>Unprepare, CR 702.180: the object loses the Prepared designation. Nothing happens if it was not prepared.</summary>
[RuleRef("702.180")]
public sealed record Unprepare(Selector What) : Effect;

[RuleRef("701.29")]
public sealed record Fateseal(Amount N, PlayerRef Player, PlayerRef? Who = null) : Effect;

[RuleRef("701.30")]
public sealed record Clash(PlayerRef? Who = null, PlayerRef? With = null, string? StoreAs = null) : Effect;

[RuleRef("701.31")]
public sealed record Planeswalk(PlayerRef? Who = null) : Effect;

[RuleRef("701.32")]
public sealed record SetInMotion(Selector What) : Effect;

[RuleRef("701.33")]
public sealed record Abandon(Selector What) : Effect;

[RuleRef("701.34")]
public sealed record Proliferate(PlayerRef? Who = null) : Effect;

[RuleRef("701.35")]
public sealed record Detain(Selector What) : Effect;

[RuleRef("701.36")]
public sealed record Populate(PlayerRef? Who = null) : Effect;

[RuleRef("701.37")]
public sealed record Monstrosity(Amount N) : Effect;

/// <summary>Voting: each player votes for one option; the effect of the winning option runs, or per-vote effects.</summary>
[RuleRef("701.38")]
public sealed record Vote(IReadOnlyList<Mode> Options, bool EachVoteResolves = false, bool TiesResolveAll = true, string? StoreAs = null) : Effect;

[RuleRef("701.39")]
public sealed record Bolster(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("701.40")]
public sealed record Manifest(Amount? N = null, Selector? What = null, PlayerRef? Who = null, string? RememberAs = null) : Effect;

[RuleRef("701.41")]
public sealed record Support(Amount N, ObjectFilter? Among = null) : Effect;

[RuleRef("701.42")]
public sealed record Meld(Selector With) : Effect;

[RuleRef("701.43")]
public sealed record Exert(Selector What) : Effect;

[RuleRef("701.44")]
public sealed record Explore(Selector What) : Effect;

[RuleRef("701.45")]
public sealed record Assemble(PlayerRef? Who = null) : Effect;

[RuleRef("701.46")]
public sealed record Adapt(Amount N) : Effect;

[RuleRef("701.47")]
public sealed record Amass(string CreatureType, Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("701.48")]
public sealed record Learn(PlayerRef? Who = null) : Effect;

[RuleRef("701.49")]
public sealed record VentureIntoTheDungeon(PlayerRef? Who = null, string? Dungeon = null) : Effect;

[RuleRef("701.50")]
public sealed record Connive(Selector What, Amount? N = null) : Effect;

[RuleRef("701.51")]
public sealed record OpenAttraction(PlayerRef? Who = null) : Effect;

[RuleRef("701.52")]
public sealed record RollToVisitAttractions(PlayerRef? Who = null) : Effect;

[RuleRef("701.53")]
public sealed record Incubate(Amount N, PlayerRef? Who = null) : Effect;

/// <summary>
/// The Ring tempts you (CR 701.54): you get the emblem named The Ring if you do not have it, and
/// then choose a creature you control as your Ring-bearer.
/// </summary>
/// <remarks>
/// The emblem carries no abilities here and must not be given any. CR 701.54b prints the emblem's
/// four abilities in the rules, not on the card — every card that tempts you says only "the Ring
/// tempts you", and which of the four the emblem has depends on how many times you have been
/// tempted, which is game state. So the emblem is the engine's to supply from the rulebook, the
/// same way it supplies the monarch's or the initiative's rules, and a translator that spelled
/// the four abilities out with <c>CreateEmblem</c> would be putting card data in a card
/// file that the printed card does not contain — and would drift the moment the rules change it.
/// <c>CreateEmblem</c> is for the emblems a planeswalker's own text spells out (CR 114.2).
/// </remarks>
[RuleRef("701.54")]
public sealed record RingTemptsYou(PlayerRef? Who = null) : Effect;

[RuleRef("701.55")]
public sealed record VillainousChoice(PlayerRef Who, Mode OptionA, Mode OptionB) : Effect;

[RuleRef("701.56")]
public sealed record TimeTravel(Amount? Times = null, PlayerRef? Who = null) : Effect;

[RuleRef("701.57")]
public sealed record Discover(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("701.58")]
public sealed record Cloak(Amount? N = null, Selector? What = null, PlayerRef? Who = null) : Effect;

[RuleRef("701.59")]
public sealed record CollectEvidence(Amount N, PlayerRef? Who = null, string? StoreChoiceAs = null) : Effect;

[RuleRef("701.60")]
public sealed record Suspect(Selector What) : Effect;

[RuleRef("701.61")]
public sealed record Forage(PlayerRef? Who = null) : Effect;

[RuleRef("701.62")]
public sealed record ManifestDread(PlayerRef? Who = null) : Effect;

[RuleRef("701.63")]
public sealed record Endure(Selector What, Amount N) : Effect;

[RuleRef("701.64")]
public sealed record Harness(Selector What) : Effect;

[RuleRef("701.65")]
public sealed record Airbend(Selector What) : Effect;

[RuleRef("701.66")]
public sealed record Earthbend(Amount N, Selector Land) : Effect;

[RuleRef("701.68")]
public sealed record Blight(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("701.69")]
public sealed record Heal(Selector What, Amount? N = null) : Effect;

[RuleRef("701.70")]
public sealed record Recruit(PlayerRef? Who = null) : Effect;
