namespace MtgEngine.Rules.State;

/// <summary>The five phases of a turn, in order (CR 500.1).</summary>
public enum Phase
{
    Beginning,
    PrecombatMain,
    Combat,
    PostcombatMain,
    Ending,
}

/// <summary>
/// Every step of a turn, in the order they occur (CR 501–514).
/// </summary>
/// <remarks>
/// The two main phases appear here even though a main phase has no steps (CR 505.2). Modelling
/// the turn as one ordered list of positions means "what happens next" has a single answer, and
/// the alternative — a phase cursor plus a nullable step cursor — has two cursors that can
/// disagree. <see cref="TurnSteps.PhaseOf"/> recovers the phase, and <see cref="TurnSteps.IsMainPhase"/>
/// is what timing rules actually ask about.
/// <para>
/// The two combat damage steps that first strike creates (CR 506.2, 510.4) are not separate
/// entries: it is the same step repeated, and slice 5 handles the repeat.
/// </para>
/// </remarks>
public enum TurnStep
{
    Untap,
    Upkeep,
    Draw,
    PrecombatMain,
    BeginningOfCombat,
    DeclareAttackers,
    DeclareBlockers,
    CombatDamage,
    EndOfCombat,
    PostcombatMain,
    End,
    Cleanup,
}

/// <summary>Facts about the turn's shape, stated once (CR 500–514).</summary>
public static class TurnSteps
{
    /// <summary>Every step of a turn in order, for walking the turn.</summary>
    public static readonly IReadOnlyList<TurnStep> InOrder = Enum.GetValues<TurnStep>();

    /// <summary>Which phase a step belongs to (CR 500.1).</summary>
    public static Phase PhaseOf(this TurnStep step) => step switch
    {
        TurnStep.Untap or TurnStep.Upkeep or TurnStep.Draw => Phase.Beginning,
        TurnStep.PrecombatMain => Phase.PrecombatMain,
        TurnStep.BeginningOfCombat or TurnStep.DeclareAttackers or TurnStep.DeclareBlockers
            or TurnStep.CombatDamage or TurnStep.EndOfCombat => Phase.Combat,
        TurnStep.PostcombatMain => Phase.PostcombatMain,
        TurnStep.End or TurnStep.Cleanup => Phase.Ending,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    /// <summary>
    /// A main phase: where sorcery-speed spells and the land drop are legal (CR 505.6a, 505.6b).
    /// </summary>
    public static bool IsMainPhase(this TurnStep step) =>
        step is TurnStep.PrecombatMain or TurnStep.PostcombatMain;

    /// <summary>
    /// Whether players receive priority during this step (CR 117.3a).
    /// </summary>
    /// <remarks>
    /// No player receives priority during the untap step (CR 502.4), and players usually do not
    /// during cleanup (CR 514.3) — "usually" because a cleanup step that performs an action gets
    /// one, which <see cref="Engine.Game"/> handles where it happens rather than here.
    /// </remarks>
    public static bool GrantsPriority(this TurnStep step) =>
        step is not (TurnStep.Untap or TurnStep.Cleanup);

    /// <summary>The step after this one, or null at the end of the turn.</summary>
    public static TurnStep? Next(this TurnStep step) =>
        step == TurnStep.Cleanup ? null : (TurnStep)((int)step + 1);
}
