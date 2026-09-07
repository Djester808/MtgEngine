using MtgEngine.Cards;
using MtgEngine.Rules.Commander;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Sba;

/// <summary>
/// The state-based actions of CR 704, and the win-and-loss conditions of CR 104 and 903.10a
/// they decide. Nothing here uses the stack (704.1) and no player controls any of it (704.2):
/// <see cref="Check"/> is asked whenever a player would get priority, performs every action
/// that applies from one snapshot, and repeats until a pass performs nothing (704.3).
/// <para>
/// Two things this deliberately does not do. It never destroys a creature whose toughness is
/// 0 or less — 704.5f puts it into its owner's graveyard, which is why the verb on the log is
/// different and why indestructible does not save it. And it never answers its own question:
/// the legend rule raises a <c>ChoiceRequested</c> and stops, because 704.5j is a choice its
/// controller makes.
/// </para>
/// </summary>
[RuleRef("704")]
public sealed partial class StateBasedActions(IZoneSystem zones, ICharacteristics characteristics, IQuery query) : IStateBasedActions
{
    /// <summary>
    /// A stop on 704.3's repetition. The loop terminates on its own — every action either
    /// removes an object, removes counters or ends the game — so reaching this is a bug in an
    /// action, not a legal game state, and stopping beats hanging the table.
    /// </summary>
    public const int MaxPasses = 100;

    /// <summary>Ten, CR 704.5c. Two-Headed Giant's fifteen (704.6b) is a variant this does not play.</summary>
    private const int PoisonLoss = 10;

    private const string PutIntoGraveyard = "put into graveyard";
    private const string Destroyed = "destroyed";
    private const string Ceased = "ceased";
    private const string LegendRule = "legend-rule";

    public string Name => "state-based actions";

    /// <inheritdoc/>
    [RuleRef("704.3")]
    public Outcome Check(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var all = ImmutableList<GameEvent>.Empty;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            // 101.4 and 104.1: with a question outstanding or the game over, nothing happens.
            if (state.Pending is not null || state.Outcome is not null)
                break;

            var performed = Once(state);
            if (performed.Events.IsEmpty)
                break;

            state = performed.State;
            all = all.AddRange(performed.Events);
        }

        return new Outcome(state, all);
    }

    /// <summary>
    /// One check: every applicable action decided from a single snapshot, so they are
    /// simultaneous (704.3) and last known information is taken from before any of them
    /// (704.8).
    /// </summary>
    [RuleRef("704.3")]
    private Outcome Once(GameState before)
    {
        var events = ImmutableList.CreateBuilder<GameEvent>();
        var state = before;

        foreach (var player in before.Players)
        {
            if (player.HasLost || LossReason(player) is not { } reason)
                continue;

            // CR 101.2 with 104.3: an effect saying this player can't lose the game beats every
            // loss 704.5a-c and 903.10a would perform here. The player stays in the game, and
            // loses the moment the effect ends, because the condition is still true on the next
            // check — 704.5a and 704.5c read the state, not a moment that has passed.
            if (!LimitsOn(before, player.Id).CantLose)
            {
                var lost = new PlayerLost(player.Id, reason);
                events.Add(lost);
                state = lost.Apply(state);
            }

            // 704.5b is the one that does not read the state: "since the last time state-based
            // actions were checked". The flag is consumed by the check that read it, whether or
            // not the loss happened, so neither a replaced loss (CR 614) nor a prevented one
            // repeats on every later check.
            if (player.DrewFromEmptyLibrary)
            {
                var cleared = new PlayerFlagCleared(player.Id, "drewFromEmptyLibrary");
                events.Add(cleared);
                state = cleared.Apply(state);
            }
        }

        if (Ending(state) is { } ended)
        {
            events.Add(ended);
            return new Outcome(ended.Apply(state), events.ToImmutable());
        }

        // Losing a player changed no object, so the permanents are still judged from `before`.
        var permanents = Permanents(state);
        events.AddRange(permanents.Events);
        state = permanents.State;

        // 704.5h is "since the last time state-based actions were checked" too: every deathtouch
        // mark that survived this check is consumed now, so it cannot destroy on a later one.
        foreach (var obj in before.Objects.Values.Where(o => o.DealtDeathtouchDamage))
        {
            if (!state.Objects.ContainsKey(obj.Id))
                continue;
            var consumed = new ObjectChanged(obj.Id, DealtDeathtouchDamage: false);
            events.Add(consumed);
            state = consumed.Apply(state);
        }

        return new Outcome(state, events.ToImmutable());
    }

    /// <summary>
    /// Why this player loses now, or null. The four that are checked here are the ones a
    /// player's own state answers: 704.5a, 704.5b, 704.5c and the Commander variant's
    /// 903.10a, which 704.6c makes a state-based action too.
    /// </summary>
    private static string? LossReason(PlayerState player)
    {
        // 704.5a If a player has 0 or less life, that player loses the game.
        if (player.Life <= 0)
            return "0 or less life";

        // 704.5b A draw attempted from an empty library since the last check loses, CR 121.4.
        if (player.DrewFromEmptyLibrary)
            return "drew from an empty library";

        // 704.5c Ten or more poison counters lose the game.
        if (player.Counters.GetValueOrDefault(Counters.Poison) >= PoisonLoss)
            return "ten or more poison counters";

        // 903.10a Twenty-one or more combat damage from one commander loses the game.
        return CommanderDamage.LethalSource(player) is { } source
            ? $"{CommanderDamage.Lethal} or more combat damage from {source}"
            : null;
    }

    /// <summary>
    /// The player-scoped continuous effects in force on this player, CR 613.10. The question
    /// belongs on <see cref="ICharacteristics"/> — <c>seam-requests/E7b.md</c> asks for the hoist —
    /// and until it lands the layer system answers it through <see cref="IPlayerLimits"/>. A
    /// composition whose characteristics do not answer reports no restrictions, which is exactly
    /// what these actions did before.
    /// </summary>
    [RuleRef("613.10")]
    private PlayerLimits LimitsOn(GameState state, PlayerId player) =>
        characteristics is IPlayerLimits limits ? limits.PlayerRestrictions(state, player) : PlayerLimits.None;

    /// <summary>
    /// Whether the game is over: 104.2a, the last player who has not lost wins; 104.4a, a game
    /// every remaining player left at once is a draw.
    /// <para>
    /// "You can't win the game" is deliberately not consulted. CR 104.2a says this win "happens
    /// immediately and overrides all effects that would preclude that player from winning the
    /// game", so the last player standing wins with such an effect on them; the prohibition bites
    /// on 104.2b, an effect that states a player wins, which is the interpreter's to honour.
    /// </para>
    /// </summary>
    [RuleRef("104")]
    private static GameEnded? Ending(GameState state)
    {
        if (state.Players.IsEmpty)
            return null;

        var remaining = state.Players.Where(p => !p.HasLost).Select(p => p.Id).ToImmutableList();
        if (remaining.Count > 1)
            return null;

        var draw = remaining.IsEmpty;
        return new GameEnded(new GameOutcome(remaining, draw, draw ? "all players lost" : "last player remaining"));
    }
}
