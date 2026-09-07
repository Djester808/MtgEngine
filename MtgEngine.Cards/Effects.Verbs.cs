namespace MtgEngine.Cards;

/// <summary>"deals 3 damage to any target": to objects, players, or both.</summary>
[RuleRef("120")]
public sealed record DealDamage(Amount N, Selector? To = null, PlayerRef? ToPlayer = null, Selector? Source = null, bool CantBePrevented = false) : Effect;

/// <summary>"deals 4 damage divided as you choose among one, two, three, or four targets".</summary>
[RuleRef("601.2d")]
public sealed record DealDividedDamage(Amount Total, Selector Among, Selector? Source = null) : Effect;

[RuleRef("121")]
public sealed record Draw(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("119")]
public sealed record GainLife(Amount N, PlayerRef? Who = null) : Effect;

[RuleRef("119")]
public sealed record LoseLife(Amount N, PlayerRef? Who = null) : Effect;

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

/// <summary>"Add {G}{G}", "add one mana of any color", "add X mana of the chosen color".</summary>
[RuleRef("106")]
public sealed record AddMana(ManaProduction Mana, PlayerRef? Who = null, Amount? N = null) : Effect;

/// <summary>
/// What a mana instruction produces. <paramref name="Fixed"/> is printed mana ("{G}{G}");
/// otherwise one of the described choices.
/// </summary>
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
    string? RestrictedTo = null);

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

/// <summary>"You may play that card this turn" (lands included).</summary>
public sealed record MayPlay(Selector What, Duration? Until = null, PlayerRef? Who = null) : Effect;

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

[RuleRef("114")]
public sealed record CreateEmblem(EmblemSpec Emblem, PlayerRef? Who = null) : Effect;

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
