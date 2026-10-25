using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

/// <summary>
/// The pending question and its answer. CR 101.4: when a rule or effect requires a player to
/// make a choice, the game waits for it; nothing else happens until it is made, which is why
/// <see cref="GameState.Pending"/> is state and not a callback. An answer is validated against
/// the question that is actually open, recorded as <see cref="ChoiceAnswered"/> — the event
/// that clears <see cref="GameState.Pending"/> — and only then resumed, by kind.
/// </summary>
[RuleRef("101.4")]
public sealed partial class ChoiceSystem(IZoneSystem zones, ITurnSystem turn, IResumable? interpreter = null, ICastingSystem? casting = null, ICombatSystem? combat = null) : IChoiceSystem
{
    private readonly IResumable resumable = interpreter ?? new UnbuiltResumable();

    public string Name => "choices";

    public Outcome Answer(GameState state, AnswerChoice command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        var pending = state.Pending
            ?? throw new RefusedException("Nothing is waiting on an answer.", "101.4");
        if (pending.Context.RemainingPayment is not null || pending.Context.EffectPaymentContinuation is not null)
            throw new RefusedException("This saved game has an unsupported legacy payment. Start a new game.", "601.2h");
        var chosen = Validate(pending, command);

        // CR 101.4 again: the answer is a fact of the game, so it is an event before it is a
        // consequence. Replaying the log to here reproduces the same board without replaying
        // whatever the asker was doing.
        var answered = new ChoiceAnswered(pending.Id, pending.Player, command.Answer);
        return new Outcome(answered.Apply(state), [answered])
            .Then(after => Resume(after, pending, chosen));
    }

    /// <summary>
    /// The answer against the question: the right question, the right player, ids that are
    /// actually on offer, no id twice, and a count within the min and max the asker set. A
    /// wrong answer is refused with the rule the kind cites, so the client can say why.
    /// <para>
    /// A question that carries a <see cref="NumberRange"/> instead of an option list is checked
    /// against its bounds instead (CR 601.2b, <see cref="ChoiceNumbers"/>): that is the channel an
    /// unbounded or a very wide numeric choice is asked through, and the number comes back as the
    /// chosen option's id, which is the same string the arm that asked reads back.
    /// </para>
    /// </summary>
    [RuleRef("101.4")]
    private static ImmutableList<ChoiceOption> Validate(PendingChoice pending, AnswerChoice command)
    {
        var rule = RuleFor(pending.Kind);
        if (command.Choice != pending.Id)
            throw new RefusedException("That answer is for a question that is no longer open.", "101.4");
        if (command.Player != pending.Player)
            throw new RefusedException("That choice is not yours to make.", "101.4");

        var answer = command.Answer ?? [];
        if (ChoiceNumbers.RangeOf(pending) is { } range)
        {
            var number = ChoiceNumbers.Answered(range, answer, rule);
            return [new ChoiceOption(number.ToString(CultureInfo.InvariantCulture), answer[0])];
        }

        if (answer.Count < pending.Min)
            throw new RefusedException($"Choose at least {pending.Min}.", rule);
        if (answer.Count > pending.Max)
            throw new RefusedException($"Choose at most {pending.Max}.", rule);
        if (answer.Distinct(StringComparer.Ordinal).Count() != answer.Count)
            throw new RefusedException("The same option was chosen twice.", rule);

        var chosen = ImmutableList.CreateBuilder<ChoiceOption>();
        foreach (var id in answer)
        {
            var option = pending.Options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal))
                ?? throw new RefusedException("That is not one of the options.", rule);
            chosen.Add(option);
        }

        return chosen.ToImmutable();
    }

    /// <summary>The rule a refusal of this kind cites; CR 101.4 when the kind names none.</summary>
    internal static string RuleFor(string kind) => kind switch
    {
        ChoiceKinds.Discard => "514.1",
        ChoiceKinds.LegendRule => "704.5j",
        ChoiceKinds.CommanderReturn => "903.9a",
        ChoiceKinds.Mulligan or ChoiceKinds.MulliganBottom => "103.5",
        ChoiceKinds.TriggerOrder => "603.3b",
        ChoiceKinds.ReplacementOrder => "616.1",
        ChoiceKinds.May or ChoiceKinds.Unless or ChoiceKinds.MayPay or ChoiceKinds.MayCast => "608.2",
        ChoiceKinds.CastCost => "601.2c",
        ChoiceKinds.Value => "601.2b",
        ChoiceKinds.Mode => "700.2",
        ChoiceKinds.Sacrifice => "701.21",
        ChoiceKinds.Search => "701.23",
        ChoiceKinds.Scry => "701.22",
        ChoiceKinds.Surveil => "701.25",
        ChoiceKinds.Targets => "601.2c",
        ChoiceKinds.NewTargets => "115.7",
        ChoiceKinds.RingBearer => "701.54a",
        _ => "101.4",
    };
}
