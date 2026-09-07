using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// The seams between subsystems. Each wave-1 builder implements one of these in its own
/// folder; <see cref="Engine"/> composes them; nothing else crosses a folder boundary. A
/// builder who needs a seam changed writes a seam request rather than editing this file.
/// </summary>
public interface ISubsystem
{
    string Name { get; }
}

/// <summary>What a subsystem hands back: the new state and the events that made it.</summary>
public sealed record Outcome(GameState State, ImmutableList<GameEvent> Events)
{
    public static Outcome Unchanged(GameState state) => new(state, []);

    public Outcome Then(Func<GameState, Outcome> next)
    {
        var following = next(State);
        return new Outcome(following.State, Events.AddRange(following.Events));
    }
}

/// <summary>A command the rules do not allow, and which rule says so.</summary>
public sealed record Refusal(string Reason, string? Rule = null);

/// <summary>Zones and objects, CR 400: creating the game, moving objects, drawing, shuffling, tokens.</summary>
[RuleRef("400")]
public interface IZoneSystem : ISubsystem
{
    Outcome Create(GameSetup setup, IDefinitionStore store);

    Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null);

    /// <summary>
    /// The same move showing a chosen face, CR 712.12: a modal double-faced card's land face is
    /// played rather than cast, and only the move can say which face the new object has. Every
    /// parameter is required here so a three-argument call still binds to the form above.
    /// </summary>
    [RuleRef("712.12")]
    Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex) =>
        throw new NotImplementedException("Moving with a chosen face is not built in this zone system.");

    /// <summary>
    /// Several objects change zones at once, CR 400.7 and 704.5m: one board for all of them, so
    /// leaves-the-battlefield triggers see the board as it was before any left. The default is
    /// the loop, which is right for everything until the trigger system needs the simultaneity.
    /// </summary>
    Outcome MoveAll(GameState state, IReadOnlyList<MoveRequest> moves)
    {
        var outcome = Outcome.Unchanged(state);
        foreach (var m in moves)
            outcome = outcome.Then(s => Move(s, m.What, m.To, m.Where, m.Tapped, m.Controller));
        return outcome;
    }

    Outcome Draw(GameState state, PlayerId player, int count);

    Outcome Shuffle(GameState state, PlayerId player);

    Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking);

    /// <summary>A token that is a copy, CR 707.2 and 111.4: it arrives with the copiable values it was handed, through the same door as every other object.</summary>
    [RuleRef("707.2")]
    Outcome CreateToken(GameState state, Faces.CopiableValues values, PlayerId controller, bool tapped, bool attacking) =>
        throw new NotImplementedException("Copy tokens are not built in this zone system.");
}

public sealed record MoveRequest(ObjectId What, ZoneKey To, Placement Where = Placement.Default, bool Tapped = false, PlayerId? Controller = null);

/// <summary>Turn structure and priority, CR 500 and 117: steps, untap, draw, cleanup, passing, N players.</summary>
[RuleRef("500")]
public interface ITurnSystem : ISubsystem
{
    /// <summary>Start the first turn after creation: mulligans are the choice system's; this begins turn 1.</summary>
    Outcome Begin(GameState state);

    /// <summary>
    /// CR 103.4–103.5: before the first turn, each player in turn order declares whether they
    /// will take a mulligan. Raises the first declaration; the choice system answers it, takes
    /// the mulligans, asks the next player and begins turn 1 after the last keep. The default
    /// offers none, which is what a scenario with a fixed opening hand wants; the turn system
    /// overrides it (wave 3) and <see cref="GameSetup.OfferMulligans"/> turns it on.
    /// </summary>
    [RuleRef("103.5")]
    Outcome OpenHands(GameState state) => Outcome.Unchanged(state);

    /// <summary>
    /// CR 103.5: one player's declaration is finished — they kept, and any cards their mulligans
    /// cost them are on the bottom. The next player in turn order declares; when the turn order
    /// has come back round to the starting player, every player has kept and turn 1 begins.
    /// The choice system calls this after a finished declaration; the default is for a turn
    /// system that offers no mulligans.
    /// </summary>
    [RuleRef("103.5")]
    Outcome AfterMulligans(GameState state, PlayerId finished) => Outcome.Unchanged(state);

    /// <summary>A player passes priority. When all pass in succession, the stack resolves or the step advances.</summary>
    Outcome Pass(GameState state, PlayerId player);

    /// <summary>Advance to the next step or turn, performing turn-based actions, CR 703.</summary>
    Outcome Advance(GameState state);
}

/// <summary>Mana, costs, casting, activating and the stack, CR 601–608 and 106.</summary>
[RuleRef("601")]
public interface ICastingSystem : ISubsystem
{
    Outcome Cast(GameState state, CastSpell command);

    Outcome Activate(GameState state, ActivateAbility command);

    Outcome PlayLand(GameState state, PlayLand command);

    /// <summary>Resolve the top object of the stack, CR 608.</summary>
    Outcome ResolveTop(GameState state);

    /// <summary>
    /// CR 608.2m and 608.2n: where a resolved spell or ability goes once its walk is finished.
    /// <see cref="ResolveTop"/> calls it when the resolution finished at once; a resolution that
    /// stopped to ask (CR 101.4) leaves its object on the stack, and the choice system calls this
    /// when the answer finished the walk.
    /// </summary>
    [RuleRef("608.2m")]
    Outcome FinishResolving(GameState state, ObjectId stackObject) =>
        throw new NotImplementedException("FinishResolving is not built in this casting system.");

    /// <summary>Whether the command would be legal now, with the rule when it would not.</summary>
    Refusal? Check(GameState state, Command command);

    /// <summary>
    /// CR 601.2c: for each cost of the command that needs objects chosen, the objects that could
    /// legally pay it and how many it takes, so a question can offer them. Empty when nothing
    /// needs choosing; the default is for a casting system that cannot say.
    /// </summary>
    [RuleRef("601.2c")]
    ImmutableList<CostChoice> CostChoices(GameState state, Command command) => [];
}

/// <summary>One cost's ask, CR 601.2c: the cost by the name the casting system refuses it by, the objects that could legally pay it, and how many of them it takes.</summary>
[RuleRef("601.2c")]
public sealed record CostChoice(string Cost, ImmutableList<ObjectId> Legal, int Count);

[RuleRef("506")]
public interface ICombatSystem : ISubsystem
{
    Outcome DeclareAttackers(GameState state, DeclareAttackers command);

    Outcome DeclareBlockers(GameState state, DeclareBlockers command);

    Outcome DealCombatDamage(GameState state, bool firstStrike);

    Outcome EndCombat(GameState state);

    /// <summary>CR 508.1a: whether this creature could be declared as an attacker now — untapped, not summoning sick unless it has haste (302.6, 702.10b), no defender (702.3b). One reading, shared with the legal-actions view.</summary>
    [RuleRef("508.1a")]
    bool CouldAttack(GameState state, ObjectId creature) =>
        throw new NotImplementedException("CouldAttack is not built in this combat system.");

    /// <summary>CR 509.1a: whether this creature could block at all — untapped and a creature; which attacker it may block is the declaration's question.</summary>
    [RuleRef("509.1a")]
    bool CouldBlock(GameState state, ObjectId creature) =>
        throw new NotImplementedException("CouldBlock is not built in this combat system.");

    /// <summary>
    /// CR 510.2 and 616.1: deal the rest of a combat damage batch that a replacement-order
    /// question stopped. The batch is a pure function of the state the step began on, so the
    /// question carries the step and the index of the assignment it was asked about, and the
    /// step is re-run from <paramref name="from"/> once the choice system has resumed that one
    /// event through <see cref="IReplacementSystem.Resume"/>.
    /// </summary>
    [RuleRef("510.2")]
    Outcome ResumeCombatDamage(GameState state, bool firstStrike, int from) =>
        throw new NotImplementedException("Resuming a halted combat damage step is not built in this combat system: wave 8, agent E4c.");

    /// <summary>
    /// CR 510.2 with 702.15b: the same resumption told what the halted hit finally dealt
    /// (<paramref name="dealt"/>, the events the replacement system finished it with), so that
    /// hit's lifelink and deathtouch follow the damage actually dealt. The default ignores them.
    /// </summary>
    [RuleRef("702.15b")]
    Outcome ResumeCombatDamage(GameState state, bool firstStrike, int from, ImmutableList<GameEvent> dealt) =>
        ResumeCombatDamage(state, firstStrike, from);
}

[RuleRef("704")]
public interface IStateBasedActions : ISubsystem
{
    /// <summary>Perform all state-based actions that apply, repeatedly, until none do, CR 704.3.</summary>
    Outcome Check(GameState state);
}

[RuleRef("603")]
public interface ITriggerSystem : ISubsystem
{
    /// <summary>Find abilities that triggered on these events and put them on the stack in APNAP order, CR 603.3.</summary>
    Outcome Collect(GameState state, IReadOnlyList<GameEvent> since);
}

/// <summary>The layer system, CR 613. Characteristics are computed, never stored; controller included.</summary>
[RuleRef("613")]
public interface ICharacteristics : ISubsystem
{
    Characteristics Of(GameState state, ObjectId id);

    PlayerId ControllerOf(GameState state, ObjectId id);

    /// <summary>Every player-scoped prohibition in force on this player right now, CR 613.10: can't lose, can't be dealt damage, a life total that can't change, protection from everything. The default is none, for a characteristics source that computes no player effects.</summary>
    [RuleRef("613.10")]
    PlayerLimits PlayerRestrictions(GameState state, PlayerId player) => PlayerLimits.None;
}

/// <summary>What an object is right now, after every layer.</summary>
public sealed record Characteristics(
    string Name,
    CardType Types,
    Supertype Supertypes,
    ImmutableList<string> Subtypes,
    Color Colors,
    int? Power,
    int? Toughness,
    int? Loyalty,
    int? Defense,
    string? ManaCost,
    ImmutableList<Ability> Abilities,
    PlayerId Controller,
    bool EveryCreatureType = false)
{
    public bool Has(KeywordKind keyword) => Abilities.OfType<KeywordAbility>().Any(k => k.Keyword == keyword);
}

[RuleRef("614")]
public interface IReplacementSystem : ISubsystem
{
    /// <summary>Offer an event to every applicable replacement and prevention effect; return what actually happens, CR 616.</summary>
    Outcome Replace(GameState state, GameEvent proposed, PlayerId affected);

    /// <summary>
    /// CR 616.1: the affected player chose which of several applicable replacements applies
    /// first. The question carried the event in <see cref="ChoiceContext.Event"/>; this applies
    /// the chosen effect to it and offers the result to the rest. Wave 3 (E8b) implements it;
    /// the interpreter's <see cref="IResumable"/> hands the "replacement-order" kind here.
    /// </summary>
    [RuleRef("616.1")]
    Outcome Resume(GameState state, Resumption resumption) =>
        throw new NotImplementedException("Resuming a replacement-order choice is not built yet: wave 3, agent E8b.");
}

/// <summary>Executes effect trees. Owns selector resolution, remembered objects and the keyword actions.</summary>
public interface IInterpreter : ISubsystem
{
    Outcome Resolve(GameState state, ObjectId stackObject);

    /// <summary>Run an effect outside the stack: a replacement's With, a static ability's setup, a manual scenario step.</summary>
    Outcome Run(GameState state, Effect effect, EffectContext context);

    /// <summary>
    /// The same walk, told which ability of the context's source the effect belongs to, so a
    /// question it asks (CR 101.4) can be found again in that ability — a replacement's "instead"
    /// (CR 614.1a) or a delayed trigger. The default forgets the index; the interpreter keeps it.
    /// </summary>
    [RuleRef("101.4")]
    Outcome Run(GameState state, Effect effect, EffectContext context, int? abilityIndex) => Run(state, effect, context);
}

/// <summary>What an effect runs inside: its source, controller, chosen targets and the memory earlier steps left.</summary>
public sealed record EffectContext(
    ObjectId? Source,
    PlayerId Controller,
    ImmutableList<ObjectId> Targets,
    ImmutableList<PlayerId> TargetPlayers,
    int? X,
    ImmutableDictionary<string, ImmutableList<ObjectId>> Remembered,
    ImmutableDictionary<string, string> Chosen,
    ImmutableDictionary<string, int> Stored,
    ImmutableList<ObjectId> Triggering,
    PlayerId? TriggeringPlayer,
    ImmutableDictionary<string, ImmutableList<PlayerId>>? RememberedPlayers = null);

/// <summary>
/// The read-only query layer every subsystem shares: does this object match a filter, is this
/// condition true, what number is this amount, which objects does a selector name. Built on
/// <see cref="ICharacteristics"/>; never changes state. Targeting legality and choices are not
/// here, only the answers the rules text asks for.
/// </summary>
public interface IQuery : ISubsystem
{
    bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context);

    bool Holds(GameState state, Condition condition, EffectContext context);

    int Evaluate(GameState state, Amount amount, EffectContext context);

    /// <summary>Resolve a non-targeting selector to the objects it names now. Targets come from the context.</summary>
    ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context);

    ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context);
}

/// <summary>Pending questions and their answers, resumed by kind, never by continuation.</summary>
/// <summary>
/// An answered question handed back to whoever asked it: the kind it asked under, who answered,
/// the options they chose (the object or player behind each one travels with it) and the
/// <see cref="ChoiceContext"/> that located the asking. Resumption is by kind and locator, never
/// a captured continuation, so it survives a reload of the log (CR 101.4).
/// </summary>
public sealed record Resumption(
    string Kind,
    PlayerId Player,
    ImmutableList<ChoiceOption> Chosen,
    ChoiceContext Context);

/// <summary>
/// The seam a choice resumes through when the question came from a card rather than from a rule
/// the choice system performs itself. The interpreter implements it: it is the only subsystem
/// that knows what an effect was in the middle of doing.
/// </summary>
public interface IResumable : ISubsystem
{
    Outcome Resume(GameState state, Resumption resumption);
}

public interface IChoiceSystem : ISubsystem
{
    Outcome Answer(GameState state, AnswerChoice command);
}

/// <summary>Per-player projection: what a viewer may see, and what they may do.</summary>
public interface IViewProjector : ISubsystem
{
    Views.GameView Project(GameState state, PlayerId viewer);
}

public interface ILegalActions : ISubsystem
{
    Views.LegalActions For(GameState state, PlayerId player);
}

/// <summary>How a game is created.</summary>
public sealed record GameSetup(ImmutableList<PlayerStart> Players, ulong Seed, int StartingLife = 20, string Format = "commander", int OpeningHandSize = 7, int StartingPlayer = 0, bool OfferMulligans = false);
