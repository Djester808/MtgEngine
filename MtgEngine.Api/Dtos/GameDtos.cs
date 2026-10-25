using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Dtos;

/// <summary>
/// What a client asks to do, over the wire.
/// </summary>
/// <remarks>
/// <b>One hub method, one discriminated payload.</b> The previous hub had a method per action
/// with optional parameters bolted on as the engine grew, and the result was that kicker,
/// buyback, dash, morph and delve were all implemented in the engine and unreachable from any
/// client: the wire shape and the engine shape had drifted and nothing compared them. A closed
/// hierarchy discriminated by <c>kind</c> cannot drift silently — a command the engine gains
/// either appears here or fails to compile against <see cref="ToCommand"/>.
/// <para>
/// <b>No payload carries a player id.</b> The seat comes from the caller's token; a client that
/// could name its own player could act as its opponent, which is a cheat and not a bug.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PassPriorityDto), "pass")]
[JsonDerivedType(typeof(PlayLandDto), "playLand")]
[JsonDerivedType(typeof(CastSpellDto), "cast")]
[JsonDerivedType(typeof(ActivateAbilityDto), "activate")]
[JsonDerivedType(typeof(DeclareAttackersDto), "attack")]
[JsonDerivedType(typeof(DeclareBlockersDto), "block")]
[JsonDerivedType(typeof(AnswerChoiceDto), "answer")]
[JsonDerivedType(typeof(ConcedeDto), "concede")]
[JsonDerivedType(typeof(ManualMoveDto), "manualMove")]
[JsonDerivedType(typeof(ManualTapDto), "manualTap")]
[JsonDerivedType(typeof(ManualCountersDto), "manualCounters")]
[JsonDerivedType(typeof(ManualLifeDto), "manualLife")]
[JsonDerivedType(typeof(ManualLibraryDrawDto), "manualLibraryDraw")]
[JsonDerivedType(typeof(ManualLibraryShuffleDto), "manualLibraryShuffle")]
[JsonDerivedType(typeof(ManualLibraryLookDto), "manualLibraryLook")]
[JsonDerivedType(typeof(ManualLibraryStopLookingDto), "manualLibraryStopLooking")]
[JsonDerivedType(typeof(ManualLibraryRepositionDto), "manualLibraryReposition")]
[JsonDerivedType(typeof(ManualLibrarySearchDto), "manualLibrarySearch")]
[JsonDerivedType(typeof(ManualLibraryArrangeDto), "manualLibraryArrange")]
public abstract record GameCommandDto
{
    /// <summary>Builds the engine command for the seat the caller actually holds.</summary>
    public abstract Command ToCommand(PlayerId player);

    /// <summary>The object this command handles, when it handles one. The hub asks so it can refuse a manual action on an enforced card.</summary>
    public virtual ObjectId? Subject => null;

    /// <summary>Whether this is an honour-system action, which only a table that allows them accepts.</summary>
    public virtual bool IsManual => false;
}

public sealed record PassPriorityDto : GameCommandDto
{
    public override Command ToCommand(PlayerId player) => new PassPriority(player);
}

public sealed record ConcedeDto : GameCommandDto
{
    public override Command ToCommand(PlayerId player) => new Concede(player);
}

public sealed record PlayLandDto([Range(1, int.MaxValue)] int Card) : GameCommandDto
{
    public override ObjectId? Subject => new(Card);

    public override Command ToCommand(PlayerId player) => new PlayLand(player, new ObjectId(Card));
}

public sealed record CastSpellDto(
    [Range(1, int.MaxValue)] int Card,
    [Range(0, 8)] int FaceIndex,
    IReadOnlyList<int>? Targets,
    IReadOnlyList<int>? TargetPlayers,
    IReadOnlyList<int>? Modes,
    [Range(0, 999)] int? X,
    [MaxLength(16)] IReadOnlyList<string>? CostChoices) : GameCommandDto
{
    public override ObjectId? Subject => new(Card);

    public override Command ToCommand(PlayerId player) => new CastSpell(
        player,
        new ObjectId(Card),
        FaceIndex,
        Targets is null ? null : [.. Targets.Select(t => new ObjectId(t))],
        TargetPlayers is null ? null : [.. TargetPlayers.Select(p => new PlayerId(p))],
        Modes is null ? null : [.. Modes],
        X,
        CostChoices is null ? null : [.. CostChoices]);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActivateAbilityDto(
    [Range(1, int.MaxValue)] int Source,
    [Range(0, 64)] int AbilityIndex,
    IReadOnlyList<int>? Targets,
    IReadOnlyList<int>? TargetPlayers,
    IReadOnlyList<int>? Modes,
    [Range(0, 999)] int? X,
    [MaxLength(16)] IReadOnlyList<string>? CostChoices) : GameCommandDto
{
    [MaxLength(128)] public IReadOnlyList<int>? CostObjects { get; init; }

    public override ObjectId? Subject => new(Source);

    public override Command ToCommand(PlayerId player) => new ActivateAbility(
        player,
        new ObjectId(Source),
        AbilityIndex,
        Targets is null ? null : [.. Targets.Select(t => new ObjectId(t))],
        TargetPlayers is null ? null : [.. TargetPlayers.Select(p => new PlayerId(p))],
        Modes is null ? null : [.. Modes],
        X,
        CostChoices is null ? null : [.. CostChoices],
        CostObjects is null ? null : [.. CostObjects.Select(id => new ObjectId(id))]);
}

/// <summary>One attacker and what it is attacking: a player, or a permanent they control, CR 508.1.</summary>
public sealed record AttackDto([Range(1, int.MaxValue)] int Attacker, int? DefendingPlayer, int? DefendingPermanent);

public sealed record DeclareAttackersDto([Required][MaxLength(64)] IReadOnlyList<AttackDto> Attacks) : GameCommandDto
{
    public override Command ToCommand(PlayerId player) => new DeclareAttackers(
        player,
        [.. Attacks.Select(a => new AttackDeclaration(
            new ObjectId(a.Attacker),
            new CombatTarget(
                a.DefendingPlayer is { } p ? new PlayerId(p) : null,
                a.DefendingPermanent is { } o ? new ObjectId(o) : null)))]);
}

public sealed record BlockDto([Range(1, int.MaxValue)] int Blocker, [Range(1, int.MaxValue)] int Attacker);

public sealed record DeclareBlockersDto([Required][MaxLength(64)] IReadOnlyList<BlockDto> Blocks) : GameCommandDto
{
    public override Command ToCommand(PlayerId player) => new DeclareBlockers(
        player,
        [.. Blocks.Select(b => new BlockDeclaration(new ObjectId(b.Blocker), new ObjectId(b.Attacker)))]);
}

public sealed record AnswerChoiceDto(
    [Range(1, int.MaxValue)] int Choice,
    [Required][MaxLength(64)] IReadOnlyList<string> Answer) : GameCommandDto
{
    public override Command ToCommand(PlayerId player) => new AnswerChoice(player, new ChoiceId(Choice), [.. Answer]);
}

/// <summary>Where a manually moved object is going. A zone and, for a hidden zone, whose it is.</summary>
public sealed record ZoneRefDto([Required][StringLength(20)] string Zone, int? Player);

public sealed record ManualMoveDto(
    [Range(1, int.MaxValue)] int Object,
    [Required] ZoneRefDto To,
    [Range(-1, 999)] int Position,
    bool Tapped) : GameCommandDto
{
    public override ObjectId? Subject => new(Object);

    public override bool IsManual => true;

    public override Command ToCommand(PlayerId player) => new ManualMove(
        player,
        new ObjectId(Object),
        new ZoneKey(ParseZone(To.Zone), To.Player is { } p ? new PlayerId(p) : null),
        Position,
        Tapped);

    /// <summary>The caller names a zone; an unknown name is a bad request, never an unhandled parse.</summary>
    internal static MtgEngine.Cards.Zone ParseZone(string zone) =>
        Enum.TryParse<MtgEngine.Cards.Zone>(zone, ignoreCase: true, out var parsed)
            ? parsed
            : throw new Services.InvalidRequestException("No such zone.");
}

public sealed record ManualTapDto([Range(1, int.MaxValue)] int Object, bool Tapped) : GameCommandDto
{
    public override ObjectId? Subject => new(Object);

    public override bool IsManual => true;

    public override Command ToCommand(PlayerId player) => new ManualTap(player, new ObjectId(Object), Tapped);
}

public sealed record ManualCountersDto(
    [Range(1, int.MaxValue)] int Object,
    [property: JsonPropertyName("counterKind")][Required][StringLength(40)] string Kind,
    [Range(-999, 999)] int Delta) : GameCommandDto
{
    public override ObjectId? Subject => new(Object);

    public override bool IsManual => true;

    public override Command ToCommand(PlayerId player) => new ManualCounters(player, new ObjectId(Object), Kind, Delta);
}

public sealed record ManualLifeDto([Range(0, 15)] int Target, [Range(-999, 999)] int Delta) : GameCommandDto
{
    public override bool IsManual => true;

    public override Command ToCommand(PlayerId player) => new ManualLife(player, new PlayerId(Target), Delta);
}

public sealed record ManualLibraryDrawDto(Guid RequestId) : GameCommandDto
{
    public override bool IsManual => true;
    public override Command ToCommand(PlayerId player) => new ManualLibraryDraw(player, RequestId);
}

public sealed record ManualLibraryShuffleDto(Guid RequestId) : GameCommandDto
{
    public override bool IsManual => true;
    public override Command ToCommand(PlayerId player) => new ManualLibraryShuffle(player, RequestId);
}

public sealed record ManualLibraryLookDto(Guid RequestId) : GameCommandDto
{
    public override bool IsManual => true;
    public override Command ToCommand(PlayerId player) => new ManualLibraryLook(player, RequestId);
}

public sealed record ManualLibraryStopLookingDto(Guid RequestId) : GameCommandDto
{
    public override bool IsManual => true;
    public override Command ToCommand(PlayerId player) => new ManualLibraryStopLooking(player, RequestId);
}

public sealed record ManualLibraryArrangeDto(Guid RequestId, [Required] IReadOnlyList<int>? Order) : GameCommandDto
{
    public override bool IsManual => true;
    public override Command ToCommand(PlayerId player) =>
        new ManualLibraryArrange(player, RequestId, Order is null ? null : [.. Order.Select(id => new ObjectId(id))]);
}

public sealed record ManualLibrarySearchDto(Guid RequestId, [Range(0, int.MaxValue)] int Count,
    MtgEngine.Cards.Zone Destination, MtgEngine.Cards.ObjectFilter? Filter = null,
    bool Reveal = false, bool ShuffleAfter = true, bool Tapped = false,
    MtgEngine.Cards.Placement Where = MtgEngine.Cards.Placement.Default) : GameCommandDto
{
    public override bool IsManual => true;
    public override Command ToCommand(PlayerId player) =>
        new ManualLibrarySearch(player, RequestId, Count, Destination, Filter, Reveal, ShuffleAfter, Tapped, Where);
}

public sealed record ManualLibraryRepositionDto(Guid RequestId, int Card, int Position) : GameCommandDto
{
    public override bool IsManual => true;
    public override Command ToCommand(PlayerId player) => new ManualLibraryReposition(player, RequestId, new ObjectId(Card), Position);
}

/// <summary>A player saying a card did not play as printed, from a position in the log.</summary>
public sealed record GameReportRequest(
    [Range(0, int.MaxValue)] int EventIndex,
    [Required][StringLength(64, MinimumLength = 1)] string OracleId,
    [StringLength(500)] string? Note);

public sealed record GameReportDto(Guid Id, Guid GameId, int EventIndex, string OracleId, string? Note, DateTime CreatedAt);
