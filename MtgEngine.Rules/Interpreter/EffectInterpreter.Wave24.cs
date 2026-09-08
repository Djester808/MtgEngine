using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Two keyword-shaped arms wave 23 left unbuilt: amass (CR 701.47) and the secret simultaneous
/// number choice (CR 700.2 with 101.4). Both are written from the rule and never from a card: the
/// token amass creates has the spec the rule gives it, and the numbers are asked for in turn order
/// with nothing read back until every player has answered.
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// The creature type every token amass creates carries, CR 701.47a. It is rules data — one of
    /// the creature types CR 205.3m lists — and not a card name; the rule itself is what names it.
    /// </summary>
    private const string ArmyType = "Army";

    /// <summary>
    /// The key one player's secret number is remembered under, CR 700.2 with 101.4. It is
    /// deliberately public: the <c>HighestChosen</c>, <c>LowestChosen</c> and <c>PlayerWhoChose</c>
    /// readers live in the query layer, which must look under exactly the keys this arm writes and
    /// must not spell them a second time (seam-requests/E9s.md).
    /// </summary>
    public const string SecretNumberPrefix = "secret-number:";

    /// <summary>
    /// How wide a span of numbers one question may offer, CR 101.4. Every answer is validated
    /// against the options the question carries, so a bounded choice is a list; a span wider than
    /// this is refused rather than turned into a list no client could render, and closing it is the
    /// same free-form numeric answer the unbounded case wants (seam-requests/E9s.md).
    /// </summary>
    private const int SecretNumberSpan = 256;

    /// <summary>Every secret number of one choice, CR 700.2: the prefix its per-player keys share.</summary>
    public static string SecretNumbersUnder(string key) => SecretNumberPrefix + key + ":";

    /// <summary>The key that player's secret number is remembered under, CR 700.2.</summary>
    public static string SecretNumberKey(string key, PlayerId player) =>
        SecretNumbersUnder(key) + player.Value.ToString(CultureInfo.InvariantCulture);

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.47 — amass.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// "Amass Orcs N", CR 701.47a: if the player controls no Army creature, create a 0/0 black
    /// token of the named type and Army; then put N +1/+1 counters on an Army they control. The
    /// creation is not conditional on N — a card that amasses 0 still leaves the token behind — and
    /// the counters go on one Army, never on each of them.
    /// <para>
    /// The two halves cannot both need a question: a player who controlled none has exactly the one
    /// token afterwards and is not asked, and a player who controlled some had no token created, so
    /// the resumed walk has nothing to undo. That is why the answer is read first and returns.
    /// </para>
    /// </summary>
    [RuleRef("701.47")]
    private EffectRun Amassing(EffectRun run, Amass amass)
    {
        var count = Math.Max(0, Number(run, amass.N, 0));
        if (Answered(run) is { } answered)
            return Amassed(run, Named(run.State, answered), count);

        var player = FirstPlayer(run, amass.Who);
        var armies = Armies(run.State, player);
        if (armies.Count == 0)
            (run, armies) = Recruited(run, amass.CreatureType, player);

        // CR 101.4: nothing is asked when the answer could change nothing — no Army to add to, or
        // no counters to add. The token, where one was needed, has already been created.
        if (armies.Count == 0 || count == 0)
            return run;

        return armies.Count > 1
            ? Ask(run, player, ChoiceKinds.Choose, "Choose an Army you control.", OptionsFor(run.State, armies), 1, 1)
            : Amassed(run, armies, count);
    }

    /// <summary>
    /// CR 701.47a: the Army the keyword action creates when there is none to add to. The token that
    /// arrives is the Army — the rule just made it — so it is taken from what the zone system
    /// created rather than looked for on the board a second time.
    /// </summary>
    [RuleRef("701.47")]
    private (EffectRun Run, ImmutableList<ObjectId> Armies) Recruited(EffectRun run, string creatureType, PlayerId player)
    {
        var before = run.State;
        run = Merge(run, zones.CreateToken(run.State, ArmyToken(creatureType), player, false, false));
        return (run, [.. run.State.Objects.Keys
            .Where(id => !before.Objects.ContainsKey(id)
                && run.State.Objects[id].Zone.Zone == Cards.Zone.Battlefield)
            .OrderBy(id => id.Value)]);
    }

    /// <summary>
    /// The token's spec, CR 701.47a with 111.2: 0/0, black, a creature of the named type and Army.
    /// CR 111.4 names a token the rules create for its subtypes, so "Orc" amasses an Orc Army.
    /// </summary>
    [RuleRef("701.47")]
    private static TokenSpec ArmyToken(string creatureType)
    {
        var type = Subtype(creatureType);
        ImmutableList<string> subtypes = type is null ? [ArmyType] : [type, ArmyType];
        return new TokenSpec(
            string.Join(" ", subtypes),
            Color.Black,
            CardType.Creature,
            subtypes,
            Power: 0,
            Toughness: 0);
    }

    /// <summary>
    /// The creature type the record named, CR 205.3m. Printed amass text is plural ("amass Orcs
    /// 1") and the type is singular, so a trailing s is dropped when that is what makes it a type.
    /// A word that is no creature type either way is refused: a token subtyped with it would be
    /// found by no filter and the amass would silently do nothing later.
    /// </summary>
    [RuleRef("205.3m")]
    private static string? Subtype(string creatureType)
    {
        var written = (creatureType ?? string.Empty).Trim();
        if (written.Length == 0 || string.Equals(written, ArmyType, StringComparison.OrdinalIgnoreCase))
            return null;
        if (CreatureTypes.All.Contains(written))
            return written;

        var singular = written.EndsWith('s') ? written[..^1] : written;
        if (CreatureTypes.All.Contains(singular))
            return singular;

        throw new RefusedException(
            "amass names a creature type the game does not define, so the token it creates could not be an Army of anything",
            "205.3m");
    }

    /// <summary>
    /// CR 701.47a: the N +1/+1 counters, on one Army. They are proposed like every other counter
    /// event (CR 614.1), so "enters with an additional counter" and its kin apply to them.
    /// </summary>
    [RuleRef("701.47")]
    private EffectRun Amassed(EffectRun run, ImmutableList<ObjectId> armies, int count)
    {
        if (count <= 0 || armies.Count == 0 || !run.State.Objects.ContainsKey(armies[0]))
            return run;

        return Propose(
            run,
            new CountersChanged(armies[0], Counters.PlusOnePlusOne, count),
            characteristics.ControllerOf(run.State, armies[0]));
    }

    /// <summary>
    /// The Army creatures that player controls, CR 701.47a. Read through the layers, so a permanent
    /// whose subtypes an effect set counts and one that has every creature type (CR 205.1b) is an
    /// Army too.
    /// </summary>
    [RuleRef("701.47")]
    private ImmutableList<ObjectId> Armies(GameState state, PlayerId player) =>
    [
        .. state.Objects.Values
            .Where(o => o.Zone.Zone == Cards.Zone.Battlefield && characteristics.ControllerOf(state, o.Id) == player)
            .Where(o => IsArmy(characteristics.Of(state, o.Id)))
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id),
    ];

    [RuleRef("205.1b")]
    private static bool IsArmy(Characteristics what) =>
        what.Types.HasFlag(CardType.Creature)
        && (what.EveryCreatureType || what.Subtypes.Contains(ArmyType, StringComparer.OrdinalIgnoreCase));

    /* ---------------------------------------------------------------------------------------- *
     * CR 700.2 with 101.4 — the secret simultaneous choice.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// "Each player secretly chooses a number", CR 700.2 with 101.4. Every player the reference
    /// admits is asked, in turn order (CR 101.4), and the walk stops on each question, so no step
    /// of the effect can read any number until the last player has answered: that halt is the
    /// reveal, and there is nothing for a later step to reveal. Each number is remembered under
    /// <see cref="SecretNumberKey"/>, which is where the amounts and the player reference read them.
    /// <para>
    /// An omitted <c>Who</c> is the controller alone, CR 109.5, as everywhere else in this
    /// vocabulary; a card that means every player says so.
    /// </para>
    /// </summary>
    [RuleRef("700.2")]
    private EffectRun ChoosingSecretly(EffectRun run, ChooseNumberSecretly choice)
    {
        var min = Math.Max(0, Number(run, choice.Min, 0));
        foreach (var player in InTurnOrder(run.State, PlayersOrYou(run, choice.Who)))
        {
            var key = SecretNumberKey(choice.Key, player);
            if (run.Context.Chosen.ContainsKey(key))
                continue;

            // CR 601.2b: a number with no printed ceiling, or too many to list, is announced through
            // the numeric channel — the question carries no options and its range instead
            // (seam-requests/E4h.md ask 2); a short printed range is still offered as options.
            if (choice.Max is null)
                return Ask(run, player, ChoiceKinds.Value, "Choose a number, in secret.", [], 1, 1, key, context => ChoiceNumbers.Ranged(context, min, null));

            var max = Math.Max(min, Number(run, choice.Max, min));
            if (max - min >= SecretNumberSpan)
                return Ask(run, player, ChoiceKinds.Value, "Choose a number, in secret.", [], 1, 1, key, context => ChoiceNumbers.Ranged(context, min, max));

            return Ask(run, player, ChoiceKinds.Value, "Choose a number, in secret.", NumberOptions(min, max), 1, 1, key);
        }

        return run;
    }

    /// <summary>CR 601.2b: the numbers a bounded choice offers, one option each, low to high.</summary>
    [RuleRef("601.2b")]
    private static ImmutableList<ChoiceOption> NumberOptions(int min, int max) =>
    [
        .. Enumerable.Range(min, max - min + 1)
            .Select(n => n.ToString(CultureInfo.InvariantCulture))
            .Select(n => new ChoiceOption(n, n)),
    ];

    /// <summary>
    /// The players in turn order, CR 101.4: the active player first, then each of the others in
    /// the order they take turns. It is the order every simultaneous instruction is carried out in,
    /// and the order these questions are asked in.
    /// </summary>
    [RuleRef("101.4")]
    private static ImmutableList<PlayerId> InTurnOrder(GameState state, IEnumerable<PlayerId> players)
    {
        var seats = state.Players.Count;
        if (seats == 0)
            return [.. players];

        var active = state.Turn.ActivePlayer.Value;
        return [.. players.OrderBy(p => ((p.Value - active) % seats + seats) % seats)];
    }
}
