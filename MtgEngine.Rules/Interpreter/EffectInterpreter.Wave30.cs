using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Wave 30. Two readings the walk needs when it stops in the middle of something: the replacement
/// effects that have already applied to an event the walk is about to propose again (CR 614.5), and
/// the names a card-name choice may offer when the card printed none (CR 201.3).
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// The prefix an applied replacement's key rides under in a question's context, CR 614.5. Like
    /// <c>RememberedPrefix</c> it names a key inside one question's own extras and is never a kind
    /// of question: the key itself follows it, so <c>used:o12:a0</c> is the entry for the effect
    /// <c>ReplacementSystem</c> knows as <c>o12:a0</c>.
    /// </summary>
    private const string UsedPrefix = "used:";

    /// <summary>
    /// The key <c>ReplacementSystem.Applicable</c> skips an effect by: its source object and the
    /// index of the ability inside that object. Written here in the same shape so that an
    /// "instead" the interpreter is running can say which effect it is (CR 614.5).
    /// </summary>
    [RuleRef("614.5")]
    private static string ReplacementKey(ObjectId source, int index) =>
        string.Concat(
            "o",
            source.Value.ToString(CultureInfo.InvariantCulture),
            ":a",
            index.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// CR 614.5: an effect that has replaced an event may not replace that event again. When the
    /// walk being started is a replacement's own <c>With</c> (CR 614.1a) the effect has already
    /// applied, so its key is carried in the context from the first step; every event the walk then
    /// proposes is offered with it, and the "instead" no longer matches the event it itself made.
    /// <para>
    /// Without this the re-proposal is a fresh offer: a paid "instead" whose payment question halted
    /// the walk is offered its own replacement again on every resumption, and asks once per way of
    /// paying rather than once.
    /// </para>
    /// </summary>
    [RuleRef("614.5")]
    private static EffectContext Seeded(GameState state, EffectContext context, int? abilityIndex)
    {
        if (context.Source is not { } source
            || abilityIndex is not { } index
            || !state.Objects.TryGetValue(source, out var obj))
            return context;

        var abilities = obj.Face.AbilityList;
        if (index < 0 || index >= abilities.Count || abilities[index] is not ReplacementAbility)
            return context;

        var used = context.UsedReplacements ?? ImmutableHashSet<string>.Empty;
        return context with { UsedReplacements = used.Add(ReplacementKey(source, index)) };
    }

    /// <summary>
    /// Every applied replacement the question carried, put back on the resumed walk's context so the
    /// events after the answer are proposed with it (CR 614.5). A question that carried none leaves
    /// the context alone.
    /// </summary>
    [RuleRef("614.5")]
    private static EffectContext Restored(EffectContext context, ChoiceContext asked)
    {
        var used = context.UsedReplacements ?? ImmutableHashSet<string>.Empty;
        foreach (var (key, _) in asked.Extra)
        {
            if (key.StartsWith(UsedPrefix, StringComparison.Ordinal))
                used = used.Add(key[UsedPrefix.Length..]);
        }

        return used.IsEmpty ? context : context with { UsedReplacements = used };
    }

    /// <summary>
    /// CR 201.3 with 201.2: the names a "choose a card name" may be answered with when the card
    /// printed no options. A card's name is what is printed on it, so the set the game can offer is
    /// the names of the definitions it holds — every object in every zone, one entry per name.
    /// <see cref="ChooseValue.OfType"/> narrows it to the names of cards whose type line carries
    /// that card type (CR 205.2a), which is a check on the named card and never on the board.
    /// <para>
    /// A face-down object is not offered: CR 708.2 leaves it with no name at all, so there is no
    /// name of its to name. The full catalogue of names the game defines is wider than this — it is
    /// the definition store, which no subsystem here can reach (see <c>seam-requests/E9y.md</c>) —
    /// and this is the widest set the state itself knows.
    /// </para>
    /// </summary>
    [RuleRef("201.3")]
    private static ImmutableList<ChoiceOption> Names(GameState state, CardType? ofType) =>
        Offer(state.Objects.Values
            .Where(o => !o.FaceDown)
            .Where(o => ofType is not { } type || (o.Face.Types.Types & type) != 0)
            .Select(o => o.Face.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal));
}
