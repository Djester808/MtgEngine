using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Commands;

/// <summary>
/// What a player asks to do. <see cref="Engine.Game.Apply"/> turns one into events or a
/// refusal naming the rule. Choices that a cost needs are taken with the command, CR 601.2h,
/// so no payment is ever suspended.
/// </summary>
public abstract record Command(PlayerId Player);

[RuleRef("601")]
public sealed record CastSpell(
    PlayerId Player,
    ObjectId Card,
    int FaceIndex = 0,
    ImmutableList<ObjectId>? Targets = null,
    ImmutableList<PlayerId>? TargetPlayers = null,
    ImmutableList<int>? Modes = null,
    int? X = null,
    ImmutableList<string>? CostChoices = null,
    KeywordKind? AlternativeCost = null,
    ImmutableList<KeywordKind>? OptionalCosts = null,
    ImmutableList<ObjectId>? CostObjects = null) : Command(Player);

[RuleRef("602")]
public sealed record ActivateAbility(
    PlayerId Player,
    ObjectId Source,
    int AbilityIndex,
    ImmutableList<ObjectId>? Targets = null,
    ImmutableList<PlayerId>? TargetPlayers = null,
    ImmutableList<int>? Modes = null,
    int? X = null,
    ImmutableList<string>? CostChoices = null,
    ImmutableList<ObjectId>? CostObjects = null) : Command(Player);

[RuleRef("305")]
/// <summary>CR 305.1; <paramref name="FaceIndex"/> is the land face of a modal double-faced card being played, CR 712.12.</summary>
public sealed record PlayLand(PlayerId Player, ObjectId Card, int? FaceIndex = null) : Command(Player);

[RuleRef("508")]
public sealed record DeclareAttackers(PlayerId Player, ImmutableList<AttackDeclaration> Attacks) : Command(Player);

public sealed record AttackDeclaration(ObjectId Attacker, CombatTarget Target);

[RuleRef("509")]
public sealed record DeclareBlockers(PlayerId Player, ImmutableList<BlockDeclaration> Blocks) : Command(Player);

public sealed record BlockDeclaration(ObjectId Blocker, ObjectId Attacker);

[RuleRef("117.4")]
public sealed record PassPriority(PlayerId Player) : Command(Player);

public sealed record AnswerChoice(PlayerId Player, ChoiceId Choice, ImmutableList<string> Answer) : Command(Player);

[RuleRef("104.3a")]
public sealed record Concede(PlayerId Player) : Command(Player);

/// <summary>Honour-system commands: accepted only on objects the table admitted that way. The engine applies no rule, only the log.</summary>
public sealed record ManualMove(PlayerId Player, ObjectId Object, ZoneKey To, int Position = -1, bool Tapped = false) : Command(Player);

public sealed record ManualTap(PlayerId Player, ObjectId Object, bool Tapped) : Command(Player);

public sealed record ManualCounters(PlayerId Player, ObjectId Object, string Kind, int Delta) : Command(Player);

public sealed record ManualLife(PlayerId Player, PlayerId Target, int Delta) : Command(Player);
