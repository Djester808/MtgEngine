using System.Collections.Immutable;
using MtgEngine.Rules.Mana;

namespace MtgEngine.Rules.State;

/// <summary>
/// One player, and the three zones that belong to them (CR 400.1).
/// </summary>
/// <remarks>
/// The zone lists hold ids, not objects; the objects themselves live in one dictionary on
/// <see cref="GameState"/> so that "where is this object" has a single answer.
/// <para>
/// <b>Index 0 is the top of every ordered zone.</b> Drawing takes <c>Library[0]</c>, a card put
/// into a graveyard goes to <c>Graveyard[0]</c> (CR 404.1: "put on top of its owner's
/// graveyard"), and the stack resolves <c>Stack[0]</c>. One convention everywhere beats a
/// per-zone rule nobody can recall at the call site.
/// </para>
/// </remarks>
public sealed record PlayerState
{
    public required Guid PlayerId { get; init; }

    public required string Name { get; init; }

    /// <summary>CR 119. Starting total is set by the format, not by this record.</summary>
    public int Life { get; init; }

    /// <summary>CR 122.1a. Ten or more is a loss, checked as a state-based action (CR 704.5c).</summary>
    public int PoisonCounters { get; init; }

    /// <summary>How many times the Ring has tempted this player (CR 701.54c).</summary>
    /// <remarks>
    /// A count and not a set of flags, because the emblem's abilities arrive in a fixed order
    /// and each is "as long as the Ring has tempted you N or more times". Capped at four: a
    /// fifth temptation still chooses a Ring-bearer, and CR 701.54d says it counts as tempting
    /// even so, but there is no fifth ability for it to turn on.
    /// </remarks>
    public int RingTemptations { get; init; }

    /// <summary>This player's Ring-bearer, if they have one (CR 701.54b).</summary>
    /// <remarks>
    /// Not a copiable value and not an ability, so it is a designation held here rather than
    /// anything computed onto the creature. It survives that creature leaving - the id simply
    /// names nothing then, which is what "until another creature becomes your Ring-bearer"
    /// leaves behind.
    /// </remarks>
    public ObjectId? RingBearer { get; init; }

    /// <summary>Whether this player declared one or more attackers this turn (CR 508.1).</summary>
    /// <remarks>
    /// A fact about the turn rather than about any creature, which is why it is here and not on
    /// the combat: the creatures that attacked may all have died, and "if you attacked this
    /// turn" is still true. Eighty-odd corpus lines ask the question.
    /// </remarks>
    public bool AttackedThisTurn { get; init; }

    /// <summary>Damage still to be prevented from this player this turn (CR 615.1).</summary>
    /// <remarks>
    /// The permanent's shield lives on <see cref="GameObject"/> and this is its other half. Only
    /// the object had one, so "prevent the next N damage that would be dealt to any target" - a
    /// phrase that has always included a player - resolved and did nothing whenever it was
    /// pointed at one.
    /// </remarks>
    public int DamageToPrevent { get; init; }

    /// <summary>
    /// Unspent mana (CR 106.4). Empties as each step and phase ends (CR 500.5), which is a
    /// turn-based action rather than anything the player does.
    /// </summary>
    public ManaPool ManaPool { get; init; } = ManaPool.Empty;

    /// <summary>Face-down, order fixed, top at index 0 (CR 401.1, 401.2).</summary>
    public ImmutableList<ObjectId> Library { get; init; } = [];

    /// <summary>Hidden from every other player (CR 400.2, 402.3).</summary>
    public ImmutableList<ObjectId> Hand { get; init; } = [];

    /// <summary>Face-up, most recently added at index 0 (CR 404.1, 404.2).</summary>
    public ImmutableList<ObjectId> Graveyard { get; init; } = [];

    /// <summary>
    /// Set the moment a draw from an empty library is attempted, and read by state-based
    /// actions at the next check (CR 104.3c, 704.5b).
    /// </summary>
    /// <remarks>
    /// It is a flag and not a computed property because the rule is about an <em>attempt</em>
    /// that already happened, not about the library being empty now — a player at zero cards
    /// who has not yet been asked to draw has not lost. The engine this replaces wrote
    /// <c>Library.IsEmpty &amp;&amp; false</c> with a comment saying the real check lived in the
    /// rules engine. It did not live anywhere.
    /// </remarks>
    public bool HasAttemptedDrawFromEmptyLibrary { get; init; }

    /// <summary>Set by state-based actions once this player has lost (CR 104.2, 704.5a-c).</summary>
    public bool HasLost { get; init; }

    /// <summary>
    /// This player's commander, in whichever zone it currently is (CR 903.3).
    /// </summary>
    /// <remarks>
    /// Held as the card's oracle id rather than an object id, because being a commander is an
    /// attribute of the <em>card</em> and survives every zone change (CR 903.3) — while an
    /// object id deliberately does not (CR 400.7). Null outside a Commander game.
    /// </remarks>
    public string? CommanderOracleId { get; init; }

    /// <summary>
    /// How many times this player has cast their commander from the command zone (CR 903.8).
    /// </summary>
    /// <remarks>
    /// Each previous cast adds {2}, informally the commander tax. Counted rather than derived,
    /// because it counts casts from the command zone specifically — a commander cast from hand
    /// after being bounced there does not add to it.
    /// </remarks>
    public int CommanderCastsFromCommandZone { get; init; }

    /// <summary>
    /// Combat damage this player has been dealt by each commander over the game (CR 903.10a).
    /// </summary>
    /// <remarks>
    /// Keyed by the commander's oracle id, because the rule is about "the same commander" across
    /// the whole game and a commander that dies and returns is a new object each time.
    /// Twenty-one from one of them is a loss, checked as a state-based action.
    /// </remarks>
    public ImmutableDictionary<string, int> CommanderDamage { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    /// <summary>
    /// Damage dealt by a source with deathtouch since the last state-based action check
    /// (CR 704.5h) is tracked on the permanent, not here; this records why the player lost so a
    /// client can say so.
    /// </summary>
    public string? LossReason { get; init; }

    /// <summary>
    /// Lands played this turn, against the one-per-turn allowance (CR 305.2, 505.6b). A count
    /// rather than a bool because effects raise the allowance.
    /// </summary>
    public int LandsPlayedThisTurn { get; init; }

    /// <summary>
    /// Land drops granted for this turn only, on top of the one CR 505.6b gives.
    /// </summary>
    /// <remarks>
    /// Kept on the player rather than computed from the battlefield, which is where the standing
    /// "you may play an additional land on each of your turns" permissions come from: this one is
    /// printed on a spell that has already resolved and left, so there is nothing left to read it
    /// off. It resets with the rest of the per-turn counts.
    /// </remarks>
    public int ExtraLandDropsThisTurn { get; init; }

    /// <summary>
    /// How much of this player's mana survives the emptying at each step and phase (CR 500.4).
    /// </summary>
    /// <remarks>
    /// A record of what may stay rather than a second pool to spend from, so paying a cost is
    /// untouched: <see cref="ManaPool"/> stays the one place mana is spent, and this is clamped
    /// down to it whenever mana leaves. That clamp is what makes the accounting exact - mana
    /// spent out of the persistent part stops being persistent, so a Mountain tapped afterwards
    /// does not inherit the permission the spent mana had.
    /// </remarks>
    public ManaPool PersistentMana { get; init; } = ManaPool.Empty;

    /// <summary>When the persistent mana stops being persistent.</summary>
    public ManaPersistence PersistentManaUntil { get; init; }

    /// <summary>
    /// Whether this player has been dealt damage this turn (CR 120.3).
    /// </summary>
    /// <remarks>
    /// Bloodthirst asks it, and so does a run of "if an opponent was dealt damage this turn"
    /// conditions. It has to be state rather than a question asked of the log, because a trigger
    /// predicate and a replacement effect are handed a state and nothing else — and state is what
    /// a replay rebuilds.
    /// </remarks>
    public bool WasDealtDamageThisTurn { get; init; }

    /// <summary>
    /// Energy counters this player has (CR 122.1, 107.4c).
    /// </summary>
    /// <remarks>
    /// A counter on a player, like poison, and kept the same way. It is not mana: it does not
    /// empty between steps, it is not spent by the mana system, and a cost paid with it is paid
    /// with counters rather than with the pool — which is why it is a number here rather than
    /// another symbol in <c>ManaCostSpec</c>.
    /// </remarks>
    public int Energy { get; init; }

    /// <summary>
    /// Spells this player has cast this turn (CR 608.2, 700.11).
    /// </summary>
    /// <remarks>
    /// Kept for the "second spell each turn" family, and it counts <em>every</em> spell rather
    /// than the ones that resolved: a countered spell was still cast, and the cards that care are
    /// about casting.
    /// </remarks>
    public int SpellsCastThisTurn { get; init; }

    /// <summary>
    /// How many spells this player cast during the previous turn.
    /// </summary>
    /// <remarks>
    /// Carried over as the turn changes rather than reconstructed from the log, because the log
    /// is not something the reducer may read - state has to fold forward. Every werewolf turns on
    /// this and on nothing else: "if no spells were cast last turn" and "if a player cast two or
    /// more spells last turn" are the whole of the day-night cycle as those cards print it.
    /// </remarks>
    public int SpellsCastLastTurn { get; init; }

    /// <summary>Cards this player has drawn this turn (CR 121.1).</summary>
    public int CardsDrawnThisTurn { get; init; }

    /// <summary>How fast this player is going, 0 through 4 (CR 702.179).</summary>
    /// <remarks>
    /// Zero means "no speed", which is not the same as speed 1: "Start your engines!" only does
    /// anything to a player who has no speed, and the automatic increase only reaches a player
    /// who already has some. Both halves read this one number, so the distinction has to survive.
    /// </remarks>
    public int Speed { get; init; }

    /// <summary>Whether speed has already gone up this turn (CR 702.179b).</summary>
    public bool SpeedIncreasedThisTurn { get; init; }

    /// <summary>Whether this player has lost any life this turn.</summary>
    /// <remarks>
    /// Life loss rather than damage, which <see cref="WasDealtDamageThisTurn"/> already records
    /// and which is a different fact: paying life, an edict on your own life total and damage all
    /// lose life, and only the last of them is damage (CR 119.3).
    /// </remarks>
    public bool LostLifeThisTurn { get; init; }

    /// <summary>Whether a creature this player controlled has connected this turn.</summary>
    /// <remarks>
    /// Kept on the player rather than on the creature, because the question outlives the
    /// creature: "if you dealt combat damage to a player this turn" is still true after the
    /// creature that did it has died, and a flag on a permanent would go to the graveyard with
    /// it. Combat damage only - a burn spell to the face is damage this player dealt and is not
    /// what any of the twenty-four cards asking this mean.
    /// </remarks>
    public bool DealtCombatDamageToPlayerThisTurn { get; init; }

    /// <summary>
    /// Whether an Assassin or this player's commander has dealt combat damage to a player this
    /// turn, which is what freerunning asks (CR 702.173a).
    /// </summary>
    /// <remarks>
    /// Recorded as the damage lands rather than reconstructed afterwards, because the rule asks
    /// what the creature was <em>at the time it dealt that damage</em>: a creature that has since
    /// stopped being an Assassin still enabled the cost.
    /// </remarks>
    public bool AssassinOrCommanderConnectedThisTurn { get; init; }

    /// <summary>Whether this player has the city's blessing (CR 702.131a).</summary>
    /// <remarks>
    /// The engine's first player designation, and the one with the simplest rule: it is gained
    /// once and kept "for the rest of the game". So unlike every other per-player fact beside it,
    /// this one is <em>not</em> reset when a turn begins - losing it at end of turn would be a
    /// different mechanic, and a quiet one, since nothing about the card would say so.
    /// </remarks>
    public bool HasCitysBlessing { get; init; }

    /// <summary>How much life this player has gained this turn.</summary>
    /// <remarks>
    /// A total rather than a flag, because the cards that ask want both questions - "if you
    /// gained life this turn" and "if you gained 3 or more life this turn" are the same watcher
    /// read to different depths, and a flag can only answer the first. Gains are summed as they
    /// happen rather than compared against a start-of-turn total, so gaining 3 and losing 3
    /// still counts as having gained 3 (CR 118.5).
    /// </remarks>
    public int LifeGainedThisTurn { get; init; }

    /// <summary>How many noncreature spells this player has cast this turn.</summary>
    /// <remarks>
    /// Counted separately rather than derived, because by the time anything asks, the spells are
    /// gone: a resolved instant is in a graveyard as a different object (CR 400.7) and a
    /// countered one is nowhere. The count is the only place the fact survives.
    /// </remarks>
    public int NoncreatureSpellsCastThisTurn { get; init; }

    // Records compare collections by reference; see Structural.
    public bool Equals(PlayerState? other) =>
        other is not null &&
        PlayerId == other.PlayerId &&
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        Life == other.Life &&
        PoisonCounters == other.PoisonCounters &&
        DamageToPrevent == other.DamageToPrevent &&
        AttackedThisTurn == other.AttackedThisTurn &&
        RingTemptations == other.RingTemptations &&
        RingBearer == other.RingBearer &&
        ManaPool == other.ManaPool &&
        HasAttemptedDrawFromEmptyLibrary == other.HasAttemptedDrawFromEmptyLibrary &&
        HasLost == other.HasLost &&
        string.Equals(CommanderOracleId, other.CommanderOracleId, StringComparison.Ordinal) &&
        CommanderCastsFromCommandZone == other.CommanderCastsFromCommandZone &&
        Structural.Same(CommanderDamage, other.CommanderDamage) &&
        string.Equals(LossReason, other.LossReason, StringComparison.Ordinal) &&
        LandsPlayedThisTurn == other.LandsPlayedThisTurn &&
        ExtraLandDropsThisTurn == other.ExtraLandDropsThisTurn &&
        PersistentManaUntil == other.PersistentManaUntil &&
        PersistentMana == other.PersistentMana &&
        WasDealtDamageThisTurn == other.WasDealtDamageThisTurn &&
        Energy == other.Energy &&
        SpellsCastThisTurn == other.SpellsCastThisTurn &&
        SpellsCastLastTurn == other.SpellsCastLastTurn &&
        CardsDrawnThisTurn == other.CardsDrawnThisTurn &&
        Speed == other.Speed &&
        SpeedIncreasedThisTurn == other.SpeedIncreasedThisTurn &&
        LostLifeThisTurn == other.LostLifeThisTurn &&
        DealtCombatDamageToPlayerThisTurn == other.DealtCombatDamageToPlayerThisTurn &&
        AssassinOrCommanderConnectedThisTurn == other.AssassinOrCommanderConnectedThisTurn &&
        HasCitysBlessing == other.HasCitysBlessing &&
        LifeGainedThisTurn == other.LifeGainedThisTurn &&
        NoncreatureSpellsCastThisTurn == other.NoncreatureSpellsCastThisTurn &&
        Structural.Same(Library, other.Library) &&
        Structural.Same(Hand, other.Hand) &&
        Structural.Same(Graveyard, other.Graveyard);

    public override int GetHashCode() =>
        HashCode.Combine(PlayerId, Life, PoisonCounters, Library.Count, Hand.Count, Graveyard.Count);
}
