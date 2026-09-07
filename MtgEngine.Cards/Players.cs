using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// Which player or players an instruction means. "You" is the controller of the ability;
/// everything else is relative to that or to the objects in play.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(You), nameof(You))]
[JsonDerivedType(typeof(EachOpponent), nameof(EachOpponent))]
[JsonDerivedType(typeof(EachPlayer), nameof(EachPlayer))]
[JsonDerivedType(typeof(TargetPlayer), nameof(TargetPlayer))]
[JsonDerivedType(typeof(ChoosePlayer), nameof(ChoosePlayer))]
[JsonDerivedType(typeof(ControllerOf), nameof(ControllerOf))]
[JsonDerivedType(typeof(OwnerOf), nameof(OwnerOf))]
[JsonDerivedType(typeof(ActivePlayer), nameof(ActivePlayer))]
[JsonDerivedType(typeof(DefendingPlayer), nameof(DefendingPlayer))]
[JsonDerivedType(typeof(TriggeringPlayer), nameof(TriggeringPlayer))]
[JsonDerivedType(typeof(ChosenPlayer), nameof(ChosenPlayer))]
[JsonDerivedType(typeof(RememberedPlayer), nameof(RememberedPlayer))]
[JsonDerivedType(typeof(Monarch), nameof(Monarch))]
[JsonDerivedType(typeof(PlayerWithMostOf), nameof(PlayerWithMostOf))]
public abstract record PlayerRef;

/// <summary>The controller of the spell or ability.</summary>
public sealed record You : PlayerRef;

public sealed record EachOpponent : PlayerRef;

public sealed record EachPlayer : PlayerRef;

/// <summary>"target player" or "target opponent". Names a key so later steps can refer back.</summary>
public sealed record TargetPlayer(PlayerFilter Filter, Amount? Count = null, bool UpTo = false, string? Key = null) : PlayerRef;

/// <summary>A non-targeted player choice: "choose a player".</summary>
public sealed record ChoosePlayer(PlayerFilter Filter, PlayerRef Chooser, string Key) : PlayerRef;

public sealed record ControllerOf(Selector Of) : PlayerRef;

public sealed record OwnerOf(Selector Of) : PlayerRef;

public sealed record ActivePlayer : PlayerRef;

/// <summary>The player being attacked, or the controller of the planeswalker or battle being attacked.</summary>
public sealed record DefendingPlayer : PlayerRef;

/// <summary>The player the trigger was about: the one who drew, discarded, was dealt damage.</summary>
public sealed record TriggeringPlayer : PlayerRef;

public sealed record ChosenPlayer(string Key) : PlayerRef;

public sealed record RememberedPlayer(string Key) : PlayerRef;

[RuleRef("724")]
public sealed record Monarch : PlayerRef;

/// <summary>"the player with the most life", "the player who controls the most creatures".</summary>
public sealed record PlayerWithMostOf(Amount Of, bool Fewest = false) : PlayerRef;

/// <summary>Who qualifies for a player choice or target.</summary>
public sealed record PlayerFilter(bool? Opponent = null, bool? IsYou = null, Condition? Also = null)
{
    public static PlayerFilter Any => new();

    public static PlayerFilter Opponents => new(Opponent: true);
}
