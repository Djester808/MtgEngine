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

/// <summary>"Search your library for a basic land card, put it onto the battlefield tapped, then shuffle."</summary>
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
    string? RememberAs = null) : Effect;

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
