using MtgEngine.Cards;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

/// <summary>
/// What a pending question is allowed to show, CR 101.4. A resolution that stops more than once
/// carries the answers it has already been given forward on the next question's
/// <see cref="ChoiceContext.Extra"/> — that is how a resumed walk keeps them, and taking them off
/// the question would make the walk ask the earlier players again and never finish. But an answer
/// chosen <em>in secret</em> is a different thing. CR 101.4b: a player ordinarily knows the choices
/// made before theirs, and an effect that has each player choose <em>secretly</em> is the exception
/// — the whole point of the choice is that nobody knows another's number until the reveal. So the
/// second player's question must not hold the first player's number where the second player's own
/// client renders it.
/// <para>
/// The two facts are reconciled the way the hand and the library already are: the state keeps the
/// answer, and no view of the question shows it. <see cref="Withheld(PendingChoice?)"/> is that
/// removal, and it is applied by the view projector on the arm that hands a player their own
/// question — <see cref="ViewSeam"/> in <c>seam-requests/E4h.md</c>. The resumption road itself is
/// untouched, so <c>ChoiceSystem.Answer</c> still hands the interpreter every answer the walk has.
/// </para>
/// <para>
/// Secrecy is read from the key the asking effect wrote, not from a second flag someone has to
/// remember to set: a secret number rides under
/// <see cref="EffectInterpreter.SecretNumberPrefix"/>, which is public for exactly this reason, so
/// the marker and the store are one spelling.
/// </para>
/// </summary>
[RuleRef("101.4")]
public static class ChoiceSecrets
{
    /// <summary>
    /// The seam this rule's other half is filed under, so a reader of either side finds the other.
    /// </summary>
    internal const string ViewSeam = "seam-requests/E4h.md";

    /// <summary>
    /// Whether an entry on a question's context records an answer chosen in secret, CR 700.2 with
    /// 101.4. The answers already given ride under <c>answered:</c> plus the key the arm that asked
    /// stores them by, so a secret one is the key with the secret prefix somewhere inside it rather
    /// than at its start.
    /// </summary>
    [RuleRef("700.2")]
    public static bool IsSecret(string? key) =>
        key is not null && key.Contains(EffectInterpreter.SecretNumberPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The question as a player may see it, CR 101.4: every secret answer already given is taken
    /// off it. Nothing else about the question changes — the same id, the same options, the same
    /// bounds — so a client answers the question it was shown and the answer still validates.
    /// A question carrying no secret is returned unchanged, and so is no question at all.
    /// </summary>
    [RuleRef("101.4")]
    public static PendingChoice? Withheld(PendingChoice? pending)
    {
        if (pending is null)
            return null;

        var extra = pending.Context.Extra;
        var kept = extra;
        foreach (var key in extra.Keys)
        {
            if (IsSecret(key))
                kept = kept.Remove(key);
        }

        return ReferenceEquals(kept, extra)
            ? pending
            : pending with { Context = pending.Context with { Extra = kept } };
    }

    /// <summary>
    /// The secret answers a question is carrying, CR 700.2. The engine reads them — the resumed
    /// walk skips the players who have answered by finding them here — and this is the road that
    /// says so out loud, so a reader of <see cref="Withheld(PendingChoice?)"/> can see that the
    /// numbers are kept and only hidden.
    /// </summary>
    [RuleRef("700.2")]
    public static ImmutableDictionary<string, string> SecretsOf(PendingChoice pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var secrets = ImmutableDictionary<string, string>.Empty;
        foreach (var (key, value) in pending.Context.Extra)
        {
            if (IsSecret(key))
                secrets = secrets.SetItem(key, value);
        }

        return secrets;
    }
}
