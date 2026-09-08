using MtgEngine.Cards;

namespace MtgEngine.Rules.Keywords;

/// <summary>
/// CR 702.1: a keyword ability is shorthand for one or more abilities. This rewrites a face's
/// <see cref="KeywordAbility"/> records into the concrete <see cref="TriggeredAbility"/>,
/// <see cref="ActivatedAbility"/> and <see cref="StaticAbility"/> records the interpreter and
/// the triggers subsystem already understand, so that no subsystem downstream of here has to
/// know what a keyword is.
///
/// Three outcomes, decided by <see cref="KeywordSemantics"/> and never by this file:
/// <list type="bullet">
/// <item>the keyword stays as it is printed, because combat, the layers or casting read it off
/// characteristics or off the face — rewriting it would give one rule two writers;</item>
/// <item>the keyword becomes the abilities it stands for;</item>
/// <item>the keyword becomes a <see cref="SpellAbility"/> carrying <see cref="Unsupported"/>,
/// naming the keyword and the missing primitive. It is never dropped: the pipeline ranks the
/// primitive backlog by counting those markers.</item>
/// </list>
/// </summary>
[RuleRef("702.1")]
public static class KeywordExpander
{
    /// <summary>Rewrites every keyword on a face. Faces with no keywords come back unchanged.</summary>
    public static Face Expand(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        var abilities = face.AbilityList;
        if (!abilities.OfType<KeywordAbility>().Any(k => KeywordSemantics.Of(k.Keyword).Expands
            || KeywordSemantics.Of(k.Keyword).Treatment == KeywordTreatment.Unsupported))
        {
            return face;
        }

        var expanded = new List<Ability>(abilities.Count);
        foreach (var ability in abilities)
        {
            if (ability is KeywordAbility keyword)
                expanded.AddRange(ExpandKeyword(keyword));
            else
                expanded.Add(ability);
        }

        return face with { Abilities = expanded };
    }

    /// <summary>
    /// What one keyword stands for. Returns the keyword itself when its subsystem reads it in
    /// place, so the caller can splice the result in without a special case.
    /// </summary>
    public static IReadOnlyList<Ability> ExpandKeyword(KeywordAbility keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);

        var semantics = KeywordSemantics.Of(keyword.Keyword);
        if (semantics.Treatment == KeywordTreatment.Unsupported)
            return [Marker(keyword, semantics, "no engine treatment yet")];

        if (!semantics.Expands)
            return [keyword];

        return keyword.Keyword switch
        {
            KeywordKind.Prowess => [Prowess(keyword)],
            KeywordKind.Exalted => [Exalted(keyword)],
            KeywordKind.Cycling => [Cycling(keyword)],
            KeywordKind.Unearth => [Unearth(keyword)],
            KeywordKind.Devoid => [Devoid(keyword)],
            KeywordKind.Storm => [Storm(keyword)],
            _ => [Marker(keyword, semantics, MissingPrimitive(keyword.Keyword))],
        };
    }

    /// <summary>
    /// CR 702.108a — prowess: "Whenever you cast a noncreature spell, this creature gets +1/+1
    /// until end of turn." The trigger names no object, so it needs nothing the vocabulary
    /// lacks: the spell is any spell that is not a creature, cast by this ability's controller.
    /// </summary>
    [RuleRef("702.108a")]
    private static TriggeredAbility Prowess(KeywordAbility keyword) =>
        new(
            new SpellCast(new ObjectFilter(Spell: true, NotTypes: CardType.Creature), new You()),
            new Apply(new ModifyPt(new Fixed(1), new Fixed(1)), new Self(), new UntilEndOfTurn()))
        {
            Text = keyword.Text,
        };

    /// <summary>
    /// CR 702.90a — exalted: "Whenever a creature you control attacks alone, that creature gets
    /// +1/+1 until end of turn." The bonus goes on the attacker, not on the source, so the
    /// effect selects the triggering object.
    /// </summary>
    [RuleRef("702.90a")]
    private static TriggeredAbility Exalted(KeywordAbility keyword) =>
        new(
            new AttacksAlone(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())),
            new Apply(
                new ModifyPt(new Fixed(1), new Fixed(1)),
                new Triggering(TriggerRole.Subject),
                new UntilEndOfTurn()))
        {
            Text = keyword.Text,
        };

    /// <summary>
    /// CR 702.29a — cycling: "[Cost], Discard this card: Draw a card." An activated ability that
    /// functions only from hand, CR 702.29b, at instant speed.
    /// </summary>
    [RuleRef("702.29a")]
    private static ActivatedAbility Cycling(KeywordAbility keyword) =>
        new(
            [.. PrintedCost(keyword), DiscardThis()],
            new Draw(new Fixed(1)),
            Timing.Any,
            Zone.Hand)
        {
            Text = keyword.Text,
        };

    /// <summary>
    /// CR 702.84a — unearth: "[Cost]: Return this card from your graveyard to the battlefield.
    /// It gains haste. Exile it at the beginning of the next end step or if it would leave the
    /// battlefield. Activate only as a sorcery." The last clause of the exile trigger needs the
    /// replacement subsystem, so this expansion carries the end-step half only, which is the
    /// half the engine can already run.
    /// </summary>
    [RuleRef("702.84a")]
    private static ActivatedAbility Unearth(KeywordAbility keyword) =>
        new(
            PrintedCost(keyword),
            new Sequence(
            [
                new Move(new Self(), Zone.Battlefield),
                new Apply(new GrantKeyword(KeywordKind.Haste), new Self(), new UntilEndOfTurn()),
                new DelayedTrigger(new AtStep(Step.End, new You()), new Move(new Self(), Zone.Exile)),
            ]),
            Timing.SorcerySpeed,
            Zone.Graveyard)
        {
            Text = keyword.Text,
        };

    /// <summary>
    /// CR 702.113a — devoid: "This object is colorless." A characteristic-defining ability, so
    /// the layer system applies it in layer 5 regardless of timestamp, CR 613.4.
    /// </summary>
    [RuleRef("702.113a")]
    private static StaticAbility Devoid(KeywordAbility keyword) =>
        new(new SetColors(Color.Colorless), new Self(), CharacteristicDefining: true)
        {
            Text = keyword.Text,
        };

    /// <summary>
    /// CR 702.40a — storm: "When you cast this spell, copy it for each other spell that was cast
    /// before it this turn. If the spell has any targets, you may choose new targets for any of the
    /// copies." A triggered ability that functions on the stack, so it is an ordinary cast trigger
    /// whose filter is this object (CR 702.40b: two instances trigger separately, which falls out
    /// of two keyword records expanding to two abilities).
    /// <para>
    /// The count is the turn's spells by every player (CR 702.40a says "each other spell", not
    /// "each other spell you cast") less this one: the spell is already in the turn's record when
    /// the trigger resolves, so <c>Difference</c> against <see cref="Fixed"/> 1 is "each
    /// <em>other</em>". The copies are put on the stack by CR 707.10's road, which the interpreter
    /// already owns, with new targets allowed — <c>MayChooseNewTargets</c> is the record's default
    /// and is written out here because the rule is the reason for it.
    /// </para>
    /// </summary>
    [RuleRef("702.40a")]
    private static TriggeredAbility Storm(KeywordAbility keyword) =>
        new(
            new SpellCast(new ObjectFilter(Spell: true, Also: new IsThisObject()), new You()),
            new CopySpell(
                new Triggering(TriggerRole.Subject),
                new Difference(new SpellsCastThisTurn(new EachPlayer()), new Fixed(1)),
                MayChooseNewTargets: true))
        {
            Text = keyword.Text,
        };

    /// <summary>The keyword's printed cost, CR 702.1b. A keyword with no printed cost pays nothing.</summary>
    private static IReadOnlyList<Cost> PrintedCost(KeywordAbility keyword) =>
        string.IsNullOrWhiteSpace(keyword.Cost) ? [] : [new ManaCost(keyword.Cost)];

    /// <summary>
    /// Discarding the card the ability is printed on, CR 702.29a. The vocabulary has no
    /// "discard this" cost yet — <c>SacrificeSelf</c> and <c>ExileSelfCost</c> have no discard
    /// sibling — so this asks for one card and the gap is filed in seam-requests/E12.md.
    /// </summary>
    [RuleRef("702.29a")]
    private static DiscardCost DiscardThis() => new(new Fixed(1));

    /// <summary>
    /// The marker left where a keyword cannot be rewritten. It carries the keyword's name and
    /// its rule so the pipeline can rank the backlog, and the printed line so the dispute view
    /// can show what was lost.
    /// </summary>
    private static SpellAbility Marker(KeywordAbility keyword, KeywordSemantic semantics, string why) =>
        new(new Unsupported($"keyword {keyword.Keyword} (CR {semantics.Rule}): {why}", keyword.Text))
        {
            Text = keyword.Text,
        };

    /// <summary>Why an expanding keyword has no arm yet: the primitive the vocabulary is missing.</summary>
    private static string MissingPrimitive(KeywordKind keyword) => keyword switch
    {
        KeywordKind.Afflict or KeywordKind.Mentor or KeywordKind.Undying or KeywordKind.Persist =>
            "the trigger is about this object, and ObjectFilter has no way to say \"this\"",
        KeywordKind.Cascade =>
            "needs an exile-until-condition over the library with a cast permission, CR 702.85a",
        KeywordKind.Riot =>
            "enters with a choice, CR 702.136a: a replacement effect, not a trigger",
        KeywordKind.Changeling =>
            "AddTypes has no all-creature-types flag, so layer 4 cannot be told, CR 702.72a",
        KeywordKind.Affinity =>
            "CostModifier has no filter that matches this spell while it is on the stack",
        _ => "no expansion arm yet",
    };
}
