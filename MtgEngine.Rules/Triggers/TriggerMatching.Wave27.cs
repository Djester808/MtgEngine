using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// What a prevention effect being applied looks like to the trigger system, CR 615.13. No event in
/// <c>Events/**</c> records a prevention today — <c>ReplacementSystem.Prevent</c> takes the damage
/// out of the <c>DamageDealt</c> event and, when it prevents all of it, produces no event at all,
/// so the one happening a card asks about is the one the log does not say. The event is asked for
/// in <c>seam-requests/E6j.md</c>; this interface is the shape the matcher reads, so the record
/// that <c>Replacement/**</c> emits implements it and the arm below needs no change when it lands.
/// <para>
/// It is an interface and not a record here because the event belongs to the replacement system
/// that raises it, and an event declared in this folder would be a second owner of
/// <c>Events/**</c>'s hierarchy — one that the log's own snapshot (<c>events.txt</c>) would have to
/// learn about from the wrong side.
/// </para>
/// </summary>
[RuleRef("615.13")]
public interface IDamagePrevented
{
    /// <summary>The object that would have dealt the damage, CR 615.1 — the <c>By</c> half of the trigger.</summary>
    ObjectId Source { get; }

    /// <summary>The permanent or player-shaped recipient: the object that would have been damaged, if it was an object.</summary>
    ObjectId? ToObject { get; }

    /// <summary>The player that would have been damaged, if it was a player.</summary>
    PlayerId? ToPlayer { get; }

    /// <summary>How much damage this application of the prevention effect actually prevented, CR 615.13.</summary>
    int Prevented { get; }

    /// <summary>
    /// The object whose prevention or replacement effect was applied, CR 615.13 — what "this way"
    /// names. Null for a prevention no object is behind, which "this way" can never match.
    /// </summary>
    ObjectId? PreventedBy { get; }
}

/// <summary>
/// Wave 27's arms: the prevention trigger of CR 615.13 with the amount it carries, the Nth spell a
/// player casts in a turn (CR 603.2), and the payment made for the triggering spell (CR 400.7d).
/// </summary>
internal sealed partial class TriggerMatching
{
    /// <summary>The trigger kinds added after wave 25's pass of the matcher, CR 603.2.</summary>
    [RuleRef("603.2")]
    private ImmutableList<TriggerSubject> Wave27(GameState state, Trigger trigger, Ev.GameEvent happened, EffectContext ctx) =>
        trigger switch
        {
            DamagePrevented prevented => Prevented(state, prevented, happened, ctx),
            _ => [],
        };

    /// <summary>
    /// CR 615.13: an ability of this shape triggers each time a prevention effect is applied to one
    /// or more simultaneous damage events and prevents some or all of that damage. So a prevention
    /// that prevented nothing — an effect applied to unpreventable damage, CR 615.12 — fires it
    /// not at all, and one application that prevents damage to several objects at once is one
    /// firing per event the replacement system applied to, which is what its own event count says.
    /// <para>
    /// <c>ThisWay</c> is the printed "this way": the prevention watched is the one <b>this same
    /// object</b> applied, so the trigger's own source must be what
    /// <see cref="IDamagePrevented.PreventedBy"/> names. Without that scope a shield anywhere on
    /// the board would fire every card of this shape in the game; with it, and with no source at
    /// all (a delayed ability, CR 603.7), nothing matches, because "this way" cannot name a
    /// prevention that no object applied.
    /// </para>
    /// <para>
    /// <c>To</c> is an <see cref="ObjectFilter"/>, so a prevention of damage that would have been
    /// dealt to a <b>player</b> cannot match a trigger that narrows the recipient — there is no
    /// object for the filter to answer about. Both halves left null admit any prevention, which is
    /// the plain "whenever damage is prevented".
    /// </para>
    /// </summary>
    [RuleRef("615.13")]
    private ImmutableList<TriggerSubject> Prevented(GameState state, DamagePrevented trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not IDamagePrevented prevented || prevented.Prevented <= 0)
            return [];
        if (trigger.ThisWay && (ctx.Source is not { } self || prevented.PreventedBy != self))
            return [];
        if (trigger.By is { } by && !_query.Matches(state, by, prevented.Source, ctx))
            return [];
        if (trigger.To is { } to && (prevented.ToObject is not { } hit || !_query.Matches(state, to, hit, ctx)))
            return [];

        // The firing is about what the damage would have been dealt to, so "that creature" and
        // "that player" both have something to bind; a prevention that names neither still fires,
        // with nothing to name (CR 615.13 asks only that some damage was prevented).
        if (prevented.ToObject is { } damaged && state.Objects.ContainsKey(damaged))
            return [TriggerSubject.Of(damaged, Controller(state, damaged))];
        return prevented.ToPlayer is { } player ? [TriggerSubject.OfPlayer(player)] : [TriggerSubject.Nothing];
    }

    /// <summary>
    /// CR 615.13 with CR 603.10: the firing carries how much was prevented, the way a damage firing
    /// carries how much was dealt. The number is fixed as the ability triggers and is not recomputed
    /// when it resolves — by then the prevention effect is spent and the shield may be gone, and
    /// there is nothing on the board that still says what it stopped.
    /// </summary>
    [RuleRef("615.13")]
    private static ImmutableList<TriggerSubject> WithPreventedDamage(ImmutableList<TriggerSubject> subjects, IDamagePrevented prevented)
    {
        if (subjects.IsEmpty || prevented.Prevented <= 0)
            return subjects;
        var amount = prevented.Prevented.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return [.. subjects.Select(s => s with { Chosen = s.Chosen.SetItem(TriggerSystem.TriggeringPreventedDamageAmount, amount) })];
    }

    /// <summary>
    /// CR 400.7d: what was paid to cast a spell is information a later ability may reference, and
    /// the ability that watched the cast resolves after the spell may have resolved and ceased to
    /// exist (CR 400.7). So a firing on a cast carries the payment off the spell's stack entry
    /// while the spell is still on the stack — the total always, including zero, and one key per
    /// colour actually paid. The readers are named in <c>seam-requests/E6j.md</c>.
    /// <para>
    /// The payment read is the entry's own <c>ManaSpent</c>, which is what <c>ManaSpentOnThis</c>
    /// sums for the spell that is asking about itself; carrying the same dictionary is what keeps
    /// the two amounts answering the same way about one payment. A cast with no entry at all — a
    /// fake in a test, or a spell put on the stack by something that recorded no payment — carries
    /// a total of zero, which is the honest reading of "no mana was spent".
    /// </para>
    /// </summary>
    [RuleRef("400.7d")]
    private static ImmutableList<TriggerSubject> WithManaSpentOnTriggering(GameState state, ImmutableList<TriggerSubject> subjects, Ev.SpellCast cast)
    {
        if (subjects.IsEmpty)
            return subjects;
        var spent = state.Objects.TryGetValue(cast.Spell, out var spell) ? spell.Stack?.ManaSpent : null;
        var carried = ImmutableDictionary<string, string>.Empty.SetItem(
            TriggerSystem.ManaSpentOnTriggeringAmount,
            (spent?.Values.Sum() ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (color, amount) in spent ?? ImmutableDictionary<Color, int>.Empty)
        {
            if (amount != 0)
            {
                carried = carried.SetItem(
                    TriggerSystem.ManaSpentOnTriggeringColorPrefix + color,
                    amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return [.. subjects.Select(s => s with { Chosen = s.Chosen.SetItems(carried) })];
    }

    /// <summary>
    /// "Whenever a player casts a spell", CR 603.2. The caster and the spell are asked first, and
    /// only then the place this cast has in the turn: <see cref="Cards.SpellCast.Nth"/> names one
    /// cast and no other, so a cast at any other place is not this trigger's event at all. The arm
    /// lives beside <see cref="NthSpellThisTurn"/> because the two are one reading.
    /// </summary>
    [RuleRef("603.2")]
    private ImmutableList<TriggerSubject> Cast(GameState state, Cards.SpellCast trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.SpellCast cast)
            return [];
        if (!_query.Players(state, trigger.By, ctx).Contains(cast.Caster))
            return [];
        if (!_query.Matches(state, trigger.Spell, cast.Spell, ctx))
            return [];
        if (trigger.Nth is { } nth && NthSpellThisTurn(state, trigger, cast, ctx) != nth)
            return [];
        return [TriggerSubject.Of(cast.Spell, cast.Caster)];
    }

    /// <summary>
    /// Which of this player's spells this turn this cast is, CR 603.2, counted over the spells this
    /// trigger's own filter admits. "Their second spell each turn" triggers on the cast that is
    /// exactly the second one and on no later one, which is why the number is compared for equality
    /// here rather than being read as "two or more spells this turn" — that condition beside an
    /// ungated trigger fires again on the third spell and still passes a two-spell test.
    /// <para>
    /// The turn's record (<c>TurnHistory.SpellsCast</c>) is in cast order and this event's spell is
    /// in it, so counting stops at this cast and a later cast in the same batch cannot change the
    /// answer. A spell cast earlier this turn that has already resolved is a different object by
    /// now (CR 400.7) and its filter can no longer be asked, so it is counted: not counting it
    /// would make "your second spell" stop firing the moment the first one resolved, which is every
    /// real game. That over-counts a narrow filter whose earlier spell would not have been admitted
    /// — the record would have to say what the spell was for that to be exact, which is a
    /// <c>State/**</c> ask in <c>seam-requests/E6j.md</c>.
    /// </para>
    /// </summary>
    [RuleRef("603.2")]
    private int NthSpellThisTurn(GameState state, Cards.SpellCast trigger, Ev.SpellCast cast, EffectContext ctx)
    {
        var records = state.History.SpellsCast;
        var upTo = records.FindIndex(r => r.Spell == cast.Spell);
        if (upTo < 0)
            return 0;

        var count = 0;
        for (var i = 0; i <= upTo; i++)
        {
            var record = records[i];
            if (record.By != cast.Caster)
                continue;
            if (state.Objects.ContainsKey(record.Spell) && !_query.Matches(state, trigger.Spell, record.Spell, ctx))
                continue;
            count++;
        }

        return count;
    }
}
