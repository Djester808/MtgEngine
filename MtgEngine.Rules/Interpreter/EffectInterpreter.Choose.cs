using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// A non-targeted choice, CR 700.2 with 608.2: "choose a creature you control", "put any number
/// of permanent cards with mana value X or less from among them onto the battlefield". The
/// chooser is asked as the node that reads the <see cref="Choose"/> is reached, from exactly the
/// set the card names, and the answer is remembered under the selector's key so the node — and
/// every later step that says "the chosen cards" — reads it back through the query layer, which
/// only ever reads a choice and never makes one.
/// </summary>
[RuleRef("700.2")]
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// The key a <see cref="Choose"/> that named none is remembered under: its own place in the
    /// tree, which is the same on every walk of the same resolution (CR 101.4).
    /// </summary>
    private const string ChooseKeyPrefix = "choose:";

    /// <summary>
    /// The kind a choice from among a set is asked with. It is a search's kind because
    /// <see cref="ChoiceKinds"/> declares none for a plain choice yet (seam-requests/E9m.md); the
    /// two questions have the same shape — which of these objects — and the same road back.
    /// </summary>
    private const string ChooseKind = ChoiceKinds.Search;

    /// <summary>
    /// Ask every choice this node makes before it runs, one question at a time, CR 700.2. A key
    /// already remembered was chosen on an earlier pass of this walk; one already answered rides
    /// on the resumption as text (CR 608.2) and is read back into the memory; a set with nothing
    /// in it is a choice of nothing, remembered as such, and no question — a question with no
    /// options could never be answered and the game would stop on it for ever (CR 101.4).
    /// </summary>
    [RuleRef("700.2")]
    private EffectRun Choosing(EffectRun run, Effect effect)
    {
        foreach (var choose in EffectWalk.ChoicesIn(effect))
        {
            var key = ChooseKey(choose, run);
            if (run.Context.Remembered.ContainsKey(key))
                continue;

            if (run.Context.Chosen.TryGetValue(key, out var answered))
            {
                run = Recall(run, key, Numbers(answered).Select(n => new ObjectId(n)).Where(run.State.Objects.ContainsKey));
                continue;
            }

            var candidates = ChoiceCandidates(run, choose);
            if (candidates.Count == 0)
            {
                run = Recall(run, key, []);
                continue;
            }

            // CR 700.2 with 608.2: "any number" is up to as many as there are; "choose one" is
            // exactly one; "up to two" is none to two, and never more than are offered.
            var count = choose.Count is null
                ? (choose.UpTo ? candidates.Count : 1)
                : Math.Max(0, Number(run, choose.Count));
            var max = Math.Min(count, candidates.Count);
            var min = choose.UpTo ? 0 : max;
            var chooser = FirstPlayer(run, choose.Chooser);
            return Ask(run, chooser, ChooseKind, "Choose from among these.", OptionsFor(run.State, candidates), min, max, key);
        }

        return run;
    }

    /// <summary>
    /// What may be chosen. Without <see cref="Choose.Among"/> it is every object the filter admits
    /// in the filter's zone. With it, the candidates are whatever the selector names — revealed
    /// cards still in the library, cards exiled a step ago — wherever each of them is: the filter
    /// is asked of each one in its own zone, so the filter's <c>In</c> is never read (L022 refuses
    /// the pair) and a remembered set that has since changed zones is still the set.
    /// </summary>
    [RuleRef("700.2")]
    private ImmutableList<ObjectId> ChoiceCandidates(EffectRun run, Choose choose)
    {
        if (choose.Among is not { } among)
            return query.Resolve(run.State, new Each(choose.Filter), run.Context);

        return
        [
            .. Objects(run, among)
                .Distinct()
                .Where(id => run.State.Objects.TryGetValue(id, out var obj)
                    && query.Matches(run.State, choose.Filter with { In = obj.Zone.Zone }, id, run.Context)),
        ];
    }

    private static string ChooseKey(Choose choose, EffectRun run) => choose.Key ?? ChooseKeyPrefix + run.Path;
}
