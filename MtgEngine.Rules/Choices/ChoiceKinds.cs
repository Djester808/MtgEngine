namespace MtgEngine.Rules.Choices;

/// <summary>
/// The kinds of question the game stops to ask. A kind is a string in the state so that a
/// pending choice survives a replay of the log, and resumption is a branch on this value —
/// never a captured continuation, which could not be replayed (CR 101.4: the game stops and
/// waits for the answer, and everything after it follows from the answer alone).
/// <para>
/// Every kind the engine raises anywhere has a constant here, and nothing raises or routes a
/// kind by spelling the string again: two spellings of one kind is a question that is asked and
/// never routed, which under CR 101.4 stops the game for good
/// (<c>ChoiceResumptionTests.Every_choice_kind_spelled_in_the_rules_engine_is_a_declared_constant</c>
/// sweeps the source for a literal that is not one of these).
/// </para>
/// <para>
/// Each constant says which subsystem raises it and which finishes it. Two answer it:
/// <see cref="ChoiceSystem"/> finishes the kinds a rule of the game raised, and every other kind
/// goes back through <see cref="Engine.IResumable"/> to the interpreter, which is the only
/// subsystem that knows where in an effect tree the question came from — and which hands the
/// replacement and trigger orderings on to the subsystems that own those loops.
/// </para>
/// </summary>
public static class ChoiceKinds
{
    /// <summary>CR 903.9a: owner may move a newly buried or exiled commander to command.</summary>
    public const string CommanderReturn = "commander-return";

    /// <summary>
    /// CR 514.1: discard down to maximum hand size. Raised by the turn system's cleanup step and
    /// by an effect that instructs a discard; finished by the choice system.
    /// </summary>
    public const string Discard = "discard";

    /// <summary>
    /// CR 704.5j: which of the duplicate legends to keep. Raised by the state-based actions;
    /// finished by the choice system.
    /// </summary>
    public const string LegendRule = "legend-rule";

    /// <summary>
    /// CR 103.5: keep this hand or take a mulligan. Raised by the turn system's opening step;
    /// finished by the choice system, which asks the next declaration or hands back to the turn.
    /// </summary>
    public const string Mulligan = "mulligan";

    /// <summary>
    /// CR 103.5: after keeping, put one card per mulligan taken on the bottom. Raised and
    /// finished by the choice system.
    /// </summary>
    public const string MulliganBottom = "mulligan-bottom";

    /// <summary>
    /// CR 603.3b: the order this player's simultaneous triggers go on the stack. Raised by the
    /// trigger system; finished by the interpreter's resumption, which reorders the stack.
    /// </summary>
    public const string TriggerOrder = "trigger-order";

    /// <summary>
    /// CR 616.1: which applicable replacement effect applies first. Raised by the replacement
    /// system; handed back to it, unchanged, by the interpreter's resumption.
    /// </summary>
    public const string ReplacementOrder = "replacement-order";

    /// <summary>
    /// CR 608.2: an optional instruction the controller may decline. Raised by the interpreter
    /// while following an effect; finished by the interpreter.
    /// </summary>
    public const string May = "may";

    /// <summary>
    /// CR 608.2: an "unless" clause — pay the cost, or the rest of the instruction happens.
    /// Raised and finished by the interpreter.
    /// </summary>
    public const string Unless = "unless";

    /// <summary>
    /// CR 608.2: an optional cost inside a resolution — "you may pay". Raised and finished by
    /// the interpreter, which spends the answer through the mana pool.
    /// </summary>
    public const string MayPay = "may-pay";

    /// <summary>
    /// CR 608.2 with 601.2: "you may cast that card". Raised by the interpreter; finished by the
    /// interpreter, which casts through the casting system when the answer is yes.
    /// </summary>
    public const string MayCast = "may-cast";

    /// <summary>
    /// CR 601.2c: the second question of a may-cast pair — which objects pay the additional cost
    /// the card prints. Raised and finished by the interpreter; the kind is what tells the
    /// resumed walk which of the two answers it is holding.
    /// </summary>
    public const string CastCost = "cast-cost";

    /// <summary>
    /// CR 601.2b: a number or a value chosen while an effect is being followed. Raised and
    /// finished by the interpreter.
    /// </summary>
    public const string Value = "value";

    /// <summary>
    /// CR 700.2: which mode of a modal effect is chosen. Raised and finished by the interpreter.
    /// </summary>
    public const string Mode = "mode";

    /// <summary>
    /// CR 701.21: which permanents the controller sacrifices. Raised and finished by the
    /// interpreter.
    /// </summary>
    public const string Sacrifice = "sacrifice";

    /// <summary>CR 701.23: which card a search found. Raised and finished by the interpreter.</summary>
    public const string Search = "search";

    /// <summary>
    /// CR 700.2: which objects a "choose" among a set names, the vocabulary's Choose selector.
    /// Raised and finished by the interpreter (wave 16, E9m).
    /// </summary>
    public const string Choose = "choose";

    /// <summary>
    /// CR 701.22: which of the scried cards go to the bottom. Raised and finished by the
    /// interpreter.
    /// </summary>
    public const string Scry = "scry";

    /// <summary>
    /// CR 701.25: which of the surveilled cards go to the graveyard. Raised and finished by the
    /// interpreter.
    /// </summary>
    public const string Surveil = "surveil";

    /// <summary>CR 701.22/701.25: order the cards remaining on top after looking.</summary>
    public const string LibraryTopOrder = "library-top-order";

    /// <summary>The owner orders cards being placed on the bottom of their library.</summary>
    public const string LibraryBottomOrder = "library-bottom-order";

    /// <summary>
    /// CR 601.2c: the targets chosen for a spell or ability. Raised by the casting system and by
    /// the interpreter for an ability that retargets; finished by the interpreter.
    /// </summary>
    public const string Targets = "targets";

    /// <summary>
    /// CR 115.7: which new targets a spell or ability on the stack is given. Raised by the
    /// interpreter for an effect that changes targets; finished by the interpreter, which writes
    /// them onto the stack entry. Distinct from <see cref="Targets"/>, the targets a spell is cast
    /// with (CR 601.2c) — a resumed walk has to tell the two answers apart.
    /// </summary>
    public const string NewTargets = "target";

    /// <summary>
    /// CR 701.54a: which creature becomes this player's Ring-bearer when the Ring tempts them.
    /// Raised and finished by the interpreter.
    /// </summary>
    public const string RingBearer = "ring-bearer";

    /// <summary>The option id that keeps the opening hand, CR 103.5.</summary>
    public const string Keep = "keep";

    /// <summary>
    /// CR 510.2 and 616.1: which combat damage step a replacement-order question halted —
    /// <see cref="FirstStrikeDamage"/> or <see cref="RegularDamage"/>. A mark on the question and
    /// not a continuation, so the answer alone says where the batch has to pick up. Written by the
    /// combat system when it halts and by the choice system when the answer asks again.
    /// </summary>
    public const string CombatDamageStep = "combat-damage-step";

    /// <summary>CR 510.2: the index, in that step's batch, of the hit the question was asked about.</summary>
    public const string CombatDamageIndex = "combat-damage-index";

    /// <summary>CR 510.4: the first-strike combat damage step.</summary>
    public const string FirstStrikeDamage = "first";

    /// <summary>CR 510.4: the regular combat damage step.</summary>
    public const string RegularDamage = "regular";
}
