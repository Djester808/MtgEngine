using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// Which player or players an instruction means. "You" is the controller of the ability;
/// everything else is relative to that or to the objects in play.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(You), nameof(You))]
[JsonDerivedType(typeof(EachOpponent), nameof(EachOpponent))]
[JsonDerivedType(typeof(AnOpponent), nameof(AnOpponent))]
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
[JsonDerivedType(typeof(PlayerWhoChose), nameof(PlayerWhoChose))]
[JsonDerivedType(typeof(AttachedPlayer), nameof(AttachedPlayer))]
public abstract record PlayerRef;

/// <summary>"Enchanted player": the player this Aura is attached to, CR 303.4f. Nobody when the source is attached to an object or to nothing.</summary>
[RuleRef("303.4f")]
public sealed record AttachedPlayer : PlayerRef;

/// <summary>The controller of the spell or ability.</summary>
public sealed record You : PlayerRef;

public sealed record EachOpponent : PlayerRef;

/// <summary>
/// "if an opponent controls three or more creatures", "as long as an opponent has no cards in
/// hand", CR 102.2: **some** opponent, not every one of them. A condition around it is true when at
/// least one opponent satisfies it, which is what the printed word "an" says and what
/// <see cref="EachOpponent"/> cannot say — that reading is true only when every opponent satisfies
/// it, so in a four-player game the two readings are different cards, and in a duel they agree,
/// which is why the wrong one survives a two-player test.
/// <para>
/// It reads the game and nothing else: it names no single player, so an instruction cannot be aimed
/// at it and nothing can be remembered about it (L041). A card that makes an opponent do something
/// names them with <see cref="ChoosePlayer"/> or <see cref="TargetPlayer"/>, and one that makes all
/// of them do it says <see cref="EachOpponent"/>.
/// </para>
/// </summary>
[RuleRef("102.2")]
public sealed record AnOpponent : PlayerRef;

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

[RuleRef("725.1")]
public sealed record Monarch : PlayerRef;

/// <summary>
/// "each player who chose the highest number", CR 700.2 with 101.4: every player whose secret choice
/// under <paramref name="Key"/> was the greatest of those revealed, or the least when
/// <paramref name="Lowest"/> stands. It names every one of them, because two players may choose the
/// same number and the printed line reads "each player who chose".
/// <para>
/// <paramref name="Key"/> is the ChooseNumberSecretly step of the same ability that asked for the
/// numbers, and nothing else produces it (L035). It is not <see cref="PlayerWithMostOf"/>, which
/// reads an amount off the game for every player and knows nothing of a choice, and it is not
/// <see cref="ChosenPlayer"/>, which is a player somebody picked.
/// </para>
/// </summary>
[RuleRef("700.2")]
public sealed record PlayerWhoChose(string Key, bool Lowest = false) : PlayerRef;

/// <summary>"the player with the most life", "the player who controls the most creatures".</summary>
public sealed record PlayerWithMostOf(Amount Of, bool Fewest = false) : PlayerRef;

/// <summary>Who qualifies for a player choice or target.</summary>
public sealed record PlayerFilter(bool? Opponent = null, bool? IsYou = null, Condition? Also = null)
{
    public static PlayerFilter Any => new();

    public static PlayerFilter Opponents => new(Opponent: true);
}
