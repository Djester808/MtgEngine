using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.Query;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The arms the earlier waves left throwing. Each one either does the whole of what its record
/// says, or refuses naming the record and the rule, so a table is told what it cannot do rather
/// than seeing the engine's front door raise. A refusal here is always a seam that has been asked
/// for in <c>seam-requests/E9d.md</c>, never a shrug.
/// </summary>
public sealed partial class EffectInterpreter
{
    private EffectRun? Backlog(EffectRun run, Effect effect)
    {
        switch (effect)
        {
            // CR 118.12: a cost paid where it stands, inside a resolving effect.
            case PayCost pay:
                return Paid(run, pay);

            // CR 118.12: "[a player] may [do something]. If [they] [do or don't], [effect]."
            case MayPay may:
                return MayPaid(run, may);

            // CR 601.2d: the division was announced as the spell was cast; every named target gets
            // at least one, and the split is not re-chosen now unless nobody recorded one.
            case DistributeCounters distribute:
                return Distributed(run, distribute);

            // CR 710.2: a flip permanent turns upside down, once (CR 710.4).
            case Cards.Flip flip:
                return Flipped(run, Objects(run, flip.What));

            // CR 702.26: a permanent phases out; it is treated as though it did not exist.
            case Cards.PhaseOut phase:
                return Phased(run, Objects(run, phase.What));

            // CR 401.4: a library's order only changes when an effect says so; this is that effect.
            case Reorder reorder:
                return Reordered(run, reorder);

            // CR 701.14a: each creature deals damage equal to its power to the other, at once.
            case Cards.Fight fight:
                return Fought(run, fight);

            // CR 701.55a: the named player chooses one of the two, then it happens.
            case Cards.VillainousChoice villainous:
                return Villainous(run, villainous);

            // CR 115.7: a spell on the stack is made to point somewhere else.
            case ChangeTarget change:
                return Retargeted(run, change);

            // CR 724.1: the monarch is a designation the game keeps, not one an object carries, so
            // it is not an ObjectDesignated and there is no event for it — GameState.Monarch has
            // nothing that writes it, and a designation the log cannot carry would be lost on the
            // first replay. Refused by rule until the seam lands rather than silently doing nothing,
            // which would leave a court's card looking as though it worked.
            case BecomeMonarch:
                throw new RefusedException(
                    "Becoming the monarch needs a MonarchChanged event that writes GameState.Monarch; asked for in seam-requests/E9r.md",
                    "724.1");

            // CR 702.180: prepared is a designation, put on and taken off by these two.
            case Cards.Prepare prepare:
                return Designated(run, Objects(run, prepare.What), Designations.Prepared, true);

            case Cards.Unprepare unprepare:
                return Designated(run, Objects(run, unprepare.What), Designations.Prepared, false);

            // CR 701.60a: a suspected creature has menace and can't block, which the query reads.
            case Cards.Suspect suspect:
                return Designated(run, Objects(run, suspect.What), Designations.Suspected, true);

            // CR 701.64a: harnessed, which the query reads the same way.
            case Cards.Harness harness:
                return Designated(run, Objects(run, harness.What), Designations.Harnessed, true);

            // CR 701.15a: goaded by a named player, so the designation carries that seat.
            case Cards.Goad goad:
                return Goaded(run, goad);

            // CR 701.37a: counters and the monstrous designation, once.
            case Cards.Monstrosity monstrosity:
                return Monstrous(run, monstrosity);

            // CR 701.54: the tempted player chooses their Ring-bearer.
            case Cards.RingTemptsYou ring:
                return Tempted(run, ring);

            // CR 601: casting from inside a resolution, through the casting system.
            case MayCast may:
                return MayCastCard(run, may);

            // CR 601.3a with 305.1: a permission to play is a lasting effect, never a play on the spot.
            case MayPlay play:
                return MayPlayCards(run, play);

            default:
                return null;
        }
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 118.12 — paying a cost inside a resolution.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// Pay the cost where it stands, CR 118.12. A cost that cannot be paid is simply not paid: the
    /// resolution carries on and the "if you do" half sees false, because CR 118.12 makes an
    /// unpayable cost a cost that was not paid and not an error the table has to see.
    /// <para>
    /// CR 608.2c: "Pay [cost]. If you do, [effect]" is one instruction with two halves, and the
    /// second half runs only when the first was carried out — so <see cref="PayCost.Then"/> is a
    /// child of the payment, not the next step of the sequence, and an unpayable cost skips it. It
    /// is the mandatory twin of <see cref="MayPay"/>'s <c>Then</c>: no question is asked here,
    /// because the player was never given a choice (CR 118.12's "may" is the other record).
    /// </para>
    /// </summary>
    [RuleRef("608.2c")]
    private EffectRun Paid(EffectRun run, PayCost pay) =>
        Pay(run, pay.Cost, FirstPlayer(run, pay.Who)) is { } paid
            ? pay.Then is null ? paid : Child(paid, 0, pay.Then)
            : Unpaid(run);

    /// <summary>
    /// Pay one cost where it stands, CR 118.12, through the same <see cref="CostPayment"/> the
    /// activation path uses. Null is a cost that was not paid — there is no source to pay from, or
    /// CR 118.4 says the player cannot pay it — which is not an error the table has to see.
    /// </summary>
    [RuleRef("118.12")]
    private EffectRun? Pay(EffectRun run, Cost cost, PlayerId player)
    {
        if (run.Context.Source is not { } id || !run.State.Objects.TryGetValue(id, out var source))
            return null;

        try
        {
            var paid = CostPayment.Pay(
                run.State,
                new Casting.CostContext(source, characteristics.Of(run.State, id), player, run.Context.X, zones, query),
                [cost]);
            return Merge(run, paid);
        }
        catch (RefusedException)
        {
            return null;
        }
    }

    /// <summary>
    /// "[A player] may [pay a cost]. If [they] do, [effect]", CR 118.12: the action is a cost, paid
    /// as the spell or ability resolves, so the walk stops and asks the player first (CR 101.4).
    /// Answered yes the cost is paid and <c>Then</c> runs; answered no, or paid by a player who
    /// cannot pay it (the CR 118.12 example where the permanent to sacrifice has gone), <c>Else</c>
    /// runs where the card prints one. CR 118.12's last sentence is why the branch is decided on
    /// the choice and the payment and never on what events came out of it — a sacrifice replaced
    /// into an exile still paid. This is the mirror of <see cref="Unless"/> (CR 118.12a) and stays
    /// a separate arm, because which half a card prints is the half its file keeps. The answer is
    /// stored under this node's own key, so a bare <see cref="Resumption"/> puts the walk back here.
    /// </summary>
    [RuleRef("118.12")]
    private EffectRun MayPaid(EffectRun run, MayPay may)
    {
        var who = FirstPlayer(run, may.Who);
        var key = "may-pay:" + run.Path;
        if (!run.Context.Chosen.TryGetValue(key, out var answer))
            return Ask(run, who, "may-pay", "You may pay that cost. Do you?", YesNo, 1, 1, key);

        if (!Yes(answer))
            return Otherwise(run, may.Else);

        return Pay(run, may.Paid, who) is { } paid
            ? Child(paid, 0, may.Then)
            : Otherwise(run, may.Else);
    }

    /// <summary>The "if you don't" half, CR 118.12: nothing at all when the card prints none.</summary>
    [RuleRef("118.12")]
    private EffectRun Otherwise(EffectRun run, Effect? otherwise) =>
        otherwise is null ? run : Child(run, 1, otherwise);

    /// <summary>
    /// The cost was not paid, so the enclosing "you may" reads no. Without this a player who said
    /// yes and then could not pay would still run the "if you do" branch (CR 118.12).
    /// </summary>
    [RuleRef("118.12")]
    private static EffectRun Unpaid(EffectRun run) => run.PayKey is { } key
        ? run with { Context = run.Context with { Chosen = run.Context.Chosen.SetItem(key, "no") } }
        : run;

    /* ---------------------------------------------------------------------------------------- *
     * CR 601.2d — a division among targets.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// Distribute the counters, CR 601.2d. The division belongs to the announcement, so a split the
    /// casting system recorded — under this node's path, or under the plain key a mode carries — is
    /// used as announced. When nothing recorded one, the division is asked for here as a single
    /// value question over the legal splits, every one of which gives each target at least one.
    /// </summary>
    [RuleRef("601.2d")]
    private EffectRun Distributed(EffectRun run, DistributeCounters distribute)
    {
        var targets = run.Context.Targets
            .Where(run.State.Objects.ContainsKey)
            .Take(Math.Max(1, distribute.MaxTargets))
            .ToList();
        if (targets.Count == 0)
            return run;

        var total = Math.Max(0, Number(run, distribute.N));
        if (total < targets.Count)
            return run;

        var key = "distribute:" + run.Path;
        if (!run.Context.Chosen.TryGetValue(key, out var split)
            && !run.Context.Chosen.TryGetValue("distribute", out split))
        {
            var options = Splits(total, targets.Count).ToList();
            if (options.Count == 0)
                return run;
            if (options.Count > 1)
                return Ask(
                    run,
                    run.Context.Controller,
                    ChoiceKinds.Value,
                    "Divide the counters among the targets.",
                    [.. options.Select(o => new ChoiceOption(o, o))],
                    1,
                    1,
                    key);
            split = options[0];
        }

        var shares = Numbers(split);
        for (var i = 0; i < targets.Count; i++)
        {
            var share = i < shares.Count ? shares[i] : 0;
            if (share <= 0)
                continue;
            run = Propose(
                run,
                new CountersChanged(targets[i], distribute.Counter, share),
                characteristics.ControllerOf(run.State, targets[i]));
            if (run.Halted)
                return run;
        }

        return run;
    }

    /// <summary>
    /// Every way of splitting <paramref name="total"/> among <paramref name="parts"/> targets so
    /// that each gets at least one, CR 601.2d. A division wide enough to have more than sixty-four
    /// readings is refused rather than listed: that one was announced on casting or not at all.
    /// </summary>
    [RuleRef("601.2d")]
    private static IEnumerable<string> Splits(int total, int parts)
    {
        if (parts <= 0 || total < parts)
            yield break;
        if (Ways(total, parts) > 64)
            throw new RefusedException(
                $"{nameof(DistributeCounters)} of {total} among {parts} targets must be announced as the spell is cast; asked for in seam-requests/E9d.md",
                "601.2d");
        if (parts == 1)
        {
            yield return total.ToString(CultureInfo.InvariantCulture);
            yield break;
        }

        for (var first = 1; first <= total - (parts - 1); first++)
        {
            foreach (var rest in Splits(total - first, parts - 1))
                yield return first.ToString(CultureInfo.InvariantCulture) + "," + rest;
        }
    }

    /// <summary>How many divisions there are: C(total - 1, parts - 1), CR 601.2d.</summary>
    private static long Ways(int total, int parts)
    {
        long ways = 1;
        for (var i = 1; i <= parts - 1; i++)
        {
            ways = ways * (total - i) / i;
            if (ways > 4096)
                return ways;
        }

        return ways;
    }

    /* ---------------------------------------------------------------------------------------- *
     * The permanents these verbs turn, phase and hurt.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// Flip a permanent, CR 710.2. <see cref="FaceRules.Flip"/> answers whether it turns at all — a
    /// card that is not a flip card and one already flipped both turn nothing (CR 710.1, 710.4) —
    /// and when it answers no, no event is emitted, so nothing triggers.
    /// </summary>
    [RuleRef("710.2")]
    private EffectRun Flipped(EffectRun run, IEnumerable<ObjectId> ids)
    {
        foreach (var id in ids)
        {
            if (!run.State.Objects.TryGetValue(id, out var obj))
                continue;
            var turned = FaceRules.Flip(obj.Definition, obj.FaceIndex);
            if (!turned.Turned)
                continue;
            run = Emit(run, new ObjectTurnedOver(id, turned.FaceIndex, TurnKind.Flip));
        }

        return run;
    }

    /// <summary>
    /// CR 702.26b: a permanent phases out and is treated as though it did not exist. One already
    /// phased out does not phase out again, so nothing happens and nothing triggers.
    /// </summary>
    [RuleRef("702.26")]
    private static EffectRun Phased(EffectRun run, IEnumerable<ObjectId> ids)
    {
        foreach (var id in ids)
        {
            if (!run.State.Objects.TryGetValue(id, out var obj) || obj.PhasedOut)
                continue;
            run = Emit(run, new ObjectChanged(id, PhasedOut: true));
        }

        return run;
    }

    /// <summary>
    /// Put the named cards at the top or the bottom of the zone they are in, in the order the
    /// effect names them (CR 401.4: nothing else may change a library's order). Cards in the zone
    /// that were not named keep the order they had.
    /// </summary>
    [RuleRef("401.4")]
    private EffectRun Reordered(EffectRun run, Reorder reorder)
    {
        var named = Objects(run, reorder.What).Where(run.State.Objects.ContainsKey).ToList();
        foreach (var zone in named.Select(id => run.State.Objects[id].Zone).Distinct().ToList())
        {
            var here = named.Where(id => run.State.Objects[id].Zone == zone).ToList();
            var rest = run.State.In(zone).Where(id => !here.Contains(id));
            ImmutableList<ObjectId> order = reorder.Where == Placement.Bottom
                ? [.. rest, .. here]
                : [.. here, .. rest];
            run = Emit(run, new ZoneReordered(zone, order));
        }

        return run;
    }

    /// <summary>
    /// CR 701.14a: each of the two creatures deals damage equal to its power to the other. Both
    /// amounts are read before either is dealt, because the damage is simultaneous, and CR 701.14c
    /// means neither deals damage if either has left the battlefield first.
    /// </summary>
    [RuleRef("701.14")]
    private EffectRun Fought(EffectRun run, Cards.Fight fight)
    {
        var attackers = Objects(run, fight.A);
        var defenders = Objects(run, fight.B);
        if (attackers.Count == 0 || defenders.Count == 0)
            return run;

        var first = attackers[0];
        var second = defenders[0];
        if (!run.State.Objects.ContainsKey(first) || !run.State.Objects.ContainsKey(second))
            return run;

        var power = characteristics.Of(run.State, first).Power ?? 0;
        var back = characteristics.Of(run.State, second).Power ?? 0;
        var deathtouch = Has(run.State, first, KeywordKind.Deathtouch);
        var deathtouchBack = Has(run.State, second, KeywordKind.Deathtouch);

        if (power > 0)
        {
            run = Propose(
                run,
                new DamageDealt(first, second, null, power, Combat: false, FromCommander: false, deathtouch),
                characteristics.ControllerOf(run.State, second));
            if (run.Halted)
                return run;
        }

        if (back > 0)
        {
            run = Propose(
                run,
                new DamageDealt(second, first, null, back, Combat: false, FromCommander: false, deathtouchBack),
                characteristics.ControllerOf(run.State, first));
        }

        return run;
    }

    /// <summary>
    /// A villainous choice, CR 701.55a: the named player chooses one of the two and it happens.
    /// The answer is stored under this node's path, so resuming runs the branch rather than asking
    /// again (CR 101.4).
    /// </summary>
    [RuleRef("701.55")]
    private EffectRun Villainous(EffectRun run, Cards.VillainousChoice villainous)
    {
        var who = FirstPlayer(run, villainous.Who);
        var key = "villainous:" + run.Path;
        if (run.Context.Chosen.TryGetValue(key, out var chosen))
        {
            var second = string.Equals(chosen, "1", StringComparison.Ordinal);
            return Child(run, second ? 1 : 0, second ? villainous.OptionB.Do : villainous.OptionA.Do);
        }

        return Ask(
            run,
            who,
            "mode",
            "Choose one.",
            [new ChoiceOption("0", villainous.OptionA.Text), new ChoiceOption("1", villainous.OptionB.Text)],
            1,
            1,
            key);
    }
}
