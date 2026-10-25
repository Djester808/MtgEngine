using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.Keywords;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// Which face is being cast and what it costs, decided before anything is put on the stack:
/// CR 601.3b picks the face, CR 118.9 lets a keyword replace the mana cost, CR 601.2b adds one
/// to it, and the cost-shaping statics of CR 702.40a take generic mana off it. Everything here
/// is read off the printed <see cref="KeywordAbility"/> on the chosen face — no card name, no
/// special case — and anything this slice cannot pay throws a <see cref="RefusedException"/>
/// naming the keyword and its rule rather than casting the spell for the wrong price.
/// </summary>
[RuleRef("601.2b")]
internal sealed partial record CastPlan(
    CastableFace Chosen,
    Face Printed,
    CardZone From,
    ImmutableList<ManaSymbol> Mana,
    ImmutableList<string> OptionalCostsPaid,
    ImmutableList<Cost> NonMana,
    PlayerId? GiftPromisedTo = null,

    /// <summary>
    /// CR 116.2a: the permission that let this card be cast from where it was, when a permission
    /// is what did. Null when the card was cast from hand, on an adventure, under an announced
    /// alternative cost, or on the instructed road (CR 608.2g) - all of which reach the zone
    /// without one. Recording a permission that merely EXISTS for the zone would spend a
    /// once-per-turn one on a cast it did not allow.
    /// </summary>
    int? Permission = null, StaticPlayPermission? StaticPermission = null)
{
    /// <summary>
    /// How a spell that paid life for its mana value (CR 118.9, a permission's
    /// <see cref="MayPlayFromZone.PayLifeEqualToManaValueInstead"/>) is recorded on its stack
    /// entry's <see cref="StackEntry.OptionalCostsPaid"/>, beside <c>"free"</c> for a waived one, so
    /// "if you cast a spell this way" can be asked of the spell as it resolves.
    /// </summary>
    internal const string PaidLifeForManaValue = "life";

    /// <summary>
    /// How a spell cast for a printed alternative cost (CR 118.9, <see cref="Face.AlternativeCosts"/>)
    /// is recorded on <see cref="StackEntry.OptionalCostsPaid"/>, so <c>PaidAlternativeCost</c> with no
    /// keyword named answers true for that cast. A keyword alternative cost records its own name
    /// instead, which is how a card that asks for a particular one tells them apart.
    /// </summary>
    [RuleRef("118.9")]
    internal const string PaidPrintedAlternativeCost = "alternative";

    /// <summary>The types the spell has on the stack, CR 712.11c: only the chosen face is evaluated.</summary>
    public CardType Types => Printed.Types.Types;

    /// <summary>CR 702.8b: flash on the face being cast widens the window, whichever face that is.</summary>
    public bool HasFlash => Keyword(Printed, KeywordKind.Flash) is not null;

    /// <summary>
    /// CR 601.3b: the player announces which face they are casting before the card moves, and
    /// CR 601.2b–f: the alternative and additional costs are locked in with it, so no payment is
    /// ever suspended.
    /// </summary>
    /// <param name="instructed">
    /// CR 608.2g: this cast is one a resolving effect instructed, so the instruction is itself
    /// CR 601.3a's permission for the zone and carries CR 118.9's cost waiver when the command
    /// says so. False for every cast a player commands.
    /// </param>
    [RuleRef("601.3b")]
    public static CastPlan For(GameState state, GameObject card, CastSpell command, IQuery query, ICharacteristics characteristics, bool instructed = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(command);
        var definition = card.Definition;
        var zone = card.Zone.Zone;
        var options = FaceRules.CastableFaces(definition, zone, OnAdventure(card));
        if (options.Count == 0)
            throw new RefusedException("A card in that zone cannot be cast.", "601.3");

        var chosen = options.FirstOrDefault(o => o.FaceIndex == command.FaceIndex)
            ?? throw new RefusedException("That face cannot be cast from there.", "601.3b");
        if (chosen.IsLandFace)
        {
            // CR 305.9 for an ordinary land; CR 712.12 for the land face of a modal card, which
            // is played as a land and never cast.
            var normal = definition.Layout == Layout.Normal;
            throw new RefusedException(normal ? "A land is played, not cast." : "A land face is played, not cast.", normal ? "305.9" : "712.12");
        }

        var printed = definition.Faces[chosen.FaceIndex];
        var paid = ImmutableList.CreateBuilder<string>();
        var extra = ImmutableList.CreateBuilder<Cost>();

        // CR 601.2b: an additional cost printed on the spell is announced with it and paid along
        // with the mana cost, whether or not the caster wants to — it is not optional.
        extra.AddRange(printed.AdditionalCosts ?? []);
        var mana = Alternative(state, card, printed, command, query, characteristics, paid, extra, instructed) ?? ManaSymbols.Parse(printed.ManaCost);
        mana = Additional(printed, command, paid, extra, mana);
        mana = Offered(state, card, command, query, paid, extra, mana);
        // CR 903.8 and 601.2f: tax is additional even to alternative/free costs, before reductions.
        if (CommanderRoad(card, command.Player) && state.CommandZoneCasts(card, command.Player) is > 0 and var casts)
            mana = mana.Add(new ManaSymbol { Generic = checked(2 * casts) });
        mana = Shaped(state, printed, query, command.Player, mana);
        // The same four conditions the zone check above refuses on (CR 601.3): a permission is
        // the road only when none of the other roads is.
        var allowing = command.AlternativeCost is null && zone != CardZone.Hand && !OnAdventure(card) && !instructed && UsesZonePermission(state, card, command.Player, query, characteristics)
            ? Allowing(state, card, command.Player, query, characteristics)
            : null;

        return new CastPlan(chosen, printed, zone, mana, paid.ToImmutable(), extra.ToImmutable(), Gift(printed, command), allowing?.Id, allowing?.Static);
    }

    /// <summary>
    /// CR 702.174b: the gift is promised to an opponent as the spell is cast, and the promise is
    /// part of the spell — the stack entry keeps it so the spell's own text can ask later. The
    /// opponent promised is the first of the command's <see cref="CastSpell.TargetPlayers"/>: the
    /// gift is not a target (CR 115.10), but the players a cast names is the one channel the
    /// command has for naming one, and a gift promised to nobody is not a promise.
    /// </summary>
    [RuleRef("702.174b")]
    private static PlayerId? Gift(Face printed, CastSpell command)
    {
        if (!(command.OptionalCosts ?? []).Contains(KeywordKind.Gift))
            return null;
        if (Keyword(printed, KeywordKind.Gift) is null)
            throw new RefusedException("That card has no gift to promise.", "702.174b");

        var promised = (command.TargetPlayers ?? []).FirstOrDefault(p => p != command.Player, command.Player);
        return promised == command.Player
            ? throw new RefusedException("A gift is promised to an opponent.", "702.174b")
            : promised;
    }

    /// <summary>
    /// CR 601.3a: a player may cast a card from a zone other than their hand when an effect says
    /// they may. <see cref="MayPlayFromZone"/> is that effect, and this is the only reader of it:
    /// the zone it names is the one the card is in, the objects it applies to hold this card (or
    /// its <see cref="MayPlayFromZone.What"/> filter matches the card), and the players it applies
    /// to hold the caster (or its <see cref="MayPlayFromZone.Who"/> resolves to them). The effect
    /// itself comes back rather than a bool, because CR 118.9's half of the same permission —
    /// "without paying its mana cost" — is read off it.
    /// <para>
    /// A permission limited to the top card of a library (<see cref="MayPlayFromZone.TopOfLibraryOnly"/>,
    /// "from the top of your library") holds for one card: the state keeps every library top-first
    /// (<see cref="GameState.Zones"/>), so it is read as the card being the first in its library
    /// and grants nothing to any card beneath it, or to a card anywhere else.
    /// </para>
    /// </summary>
    [RuleRef("601.3a")]
    /// <summary>The permission that allows this, with the id the turn's records key it by.</summary>
    internal static ZonePlayPermissions.Permission? Allowing(GameState state, GameObject card, PlayerId player, IQuery query, ICharacteristics characteristics) =>
        ZonePlayPermissions.Find(state, card, player, query, characteristics);

    /// <summary>CR 715.3d: the card exiled on an adventure is the one card its owner may cast from exile.</summary>
    [RuleRef("715.3d")]
    private static bool OnAdventure(GameObject card) =>
        card.Zone.Zone == CardZone.Exile && card.Definition.Layout == Layout.Adventure;

    /// <summary>
    /// An alternative cost, CR 118.9: the keyword's printed cost is paid instead of the mana
    /// cost, and several of them also say which zone the card is cast from, CR 601.3. Null means
    /// no alternative cost was announced, so the printed mana cost stands.
    /// <para>
    /// Both halves of CR 601.3a and 118.9 are an <em>effect's</em> to give, and neither is a
    /// player's to assert: <see cref="CastSpell.WithoutPayingManaCost"/> on a command grants
    /// nothing by itself, so the zone check holds and the mana cost is paid unless a
    /// <see cref="MayPlayFromZone"/> permission or an instruction (CR 608.2g, whose caller is the
    /// resolving effect and so carries the flag legitimately) says otherwise. A command that
    /// claims the waiver with neither behind it is refused naming the half it fails.
    /// </para>
    /// </summary>
    [RuleRef("118.9")]
    private static ImmutableList<ManaSymbol>? Alternative(GameState state, GameObject card, Face printed, CastSpell command, IQuery query, ICharacteristics characteristics, ImmutableList<string>.Builder paid, ImmutableList<Cost>.Builder extra, bool instructed)
    {
        var zone = card.Zone.Zone;
        var permission = Allowing(state, card, command.Player, query, characteristics)?.Effect;

        // CR 118.9 with 601.2b: the printed alternative costs of the chosen face are offered as the
        // spell is announced, and the caster takes at most one (CR 118.9c). It is read before any of
        // the other three roads to the same replacement so every pair of them is refused by that rule
        // rather than silently paying two prices or none.
        var printedAlternative = PrintedAlternative(state, card, printed, command, query);
        if (printedAlternative is not null && command.AlternativeCost is not null)
            throw new RefusedException("A spell is cast for only one alternative cost.", "118.9c");

        // CR 601.3: a card is cast from its owner's hand unless an effect says otherwise. The
        // exiled adventurer is one such permission (CR 715.3d), a MayPlayFromZone effect on the
        // state is another (CR 601.3a), and the instruction of a resolving effect is the third
        // (CR 608.2g). The check comes before the mana half, so a free-cast claim can never be
        // the thing that lets a card out of a zone it may not be cast from. An announced
        // alternative cost is checked against the zone its own keyword names instead, below
        // (CR 702.34a: flashback is paid from a graveyard and from nowhere else).
        if (command.AlternativeCost is null && zone != CardZone.Hand && !OnAdventure(card) && permission is null && !instructed && !CommanderRoad(card, command.Player))
            throw new RefusedException("A card in that zone cannot be cast without permission.", "601.3");

        // CR 118.8: "…by sacrificing a land in addition to paying its other costs" is a cost the
        // permission applies to the spell, paid at CR 601.2f with everything else, so it rides with
        // the non-mana costs like every other additional cost. It is refused beside either waiver
        // the same record can carry (CR 118.9's free cast and the life-instead rider): an additional
        // cost is additional to a mana cost, and a permission that has set the mana cost aside has
        // nothing left for this to be added to.
        if (permission is { AdditionalCost: { } addition })
        {
            if (permission.WithoutPayingManaCost || permission.PayLifeEqualToManaValueInstead)
                throw new RefusedException("A permission that waives the mana cost cannot also add to it.", "118.8");
            extra.Add(addition);
        }

        // CR 118.9 and 601.3a: an effect that lets the card be cast without paying its mana cost is
        // both the permission to cast it from where it is and the announcement that no mana cost is
        // paid; every additional cost still is (CR 601.2b), so this is a flag beside the costs.
        if (permission is { WithoutPayingManaCost: true } || (instructed && command.WithoutPayingManaCost))
        {
            if (printedAlternative is not null)
                throw new RefusedException("A spell is cast for only one alternative cost.", "118.9c");
            paid.Add("free");
            return [];
        }

        // CR 118.9: nothing gives this cast the waiver, so the claim on the command is refused
        // rather than honoured — the mana cost is not a player's to set aside.
        if (command.WithoutPayingManaCost)
            throw new RefusedException("Nothing lets that spell be cast without paying its mana cost.", "118.9");

        // CR 118.9 with 601.2f: "if you cast a spell this way, pay life equal to its mana value
        // rather than pay its mana cost" is an alternative cost the permission carries, so a cast
        // the permission allowed sets the mana cost aside and announces a life payment of the
        // spell's mana value among its costs — X as announced (CR 202.3e) — which CostPayment pays
        // at CR 601.2h and refuses when the player has less life than that (CR 119.4). Every
        // additional cost is still paid (CR 601.2b). A keyword alternative cost the command
        // announces is a different cast (CR 118.9c: one alternative cost per spell), and a card in
        // hand has no such permission, so it pays as printed.
        if (permission is { PayLifeEqualToManaValueInstead: true } && command.AlternativeCost is null)
        {
            if (printedAlternative is not null)
                throw new RefusedException("A spell is cast for only one alternative cost.", "118.9c");
            var life = ManaSymbols.ManaValue(ManaSymbols.Parse(printed.ManaCost), command.X ?? 0);
            extra.Add(new PayLife(new Fixed(life)));
            paid.Add(PaidLifeForManaValue);
            return [];
        }

        // CR 118.9: the chosen alternative is paid in place of the mana cost and nothing else — every
        // additional cost the face prints was already added above (CR 601.2b) and is still paid. The
        // cost rides with the non-mana costs whatever shape it is, so one payment road pays it.
        if (printedAlternative is { } chosen)
        {
            extra.Add(chosen.Cost);
            paid.Add(PaidPrintedAlternativeCost);
            return [];
        }

        if (command.AlternativeCost is not { } kind)
            return null;

        var semantic = KeywordSemantics.Of(kind);
        if (semantic.Cost != CostShape.Alternative)
            throw new RefusedException($"{kind} is not an alternative cost.", semantic.Rule);
        var ability = Keyword(printed, kind) ?? throw new RefusedException($"That card has no {kind}.", semantic.Rule);
        if (semantic.CastFrom is { } from && from != zone)
            throw new RefusedException($"{kind} is not paid from there.", semantic.Rule);

        // CR 702.34a: flashback's whole rider is exiling the card as it resolves, which
        // Destination does. CR 702.138a: escape's is the non-mana half printed beside its mana,
        // which KeywordAbility.Costs carries and CostPayment pays. The rest name themselves and
        // their rule rather than casting a spell whose rider the engine would drop.
        if (kind is not (KeywordKind.Flashback or KeywordKind.Escape))
            throw new RefusedException($"{kind} cannot be paid yet.", semantic.Rule);
        if (!ManaSymbols.TryParse(ability.Cost, out var symbols, out _))
            throw new RefusedException($"The {kind} cost cannot be paid yet.", "601.2h");

        extra.AddRange(ability.Costs ?? []);
        paid.Add(kind.ToString());
        return symbols;
    }

    /// <summary>
    /// The additional costs a player chose to pay, CR 601.2b: each is paid on top of the mana
    /// cost and recorded, so "if it was kicked" (CR 702.32c) can be asked when the spell resolves.
    /// </summary>
    [RuleRef("601.2b")]
    private static ImmutableList<ManaSymbol> Additional(Face printed, CastSpell command, ImmutableList<string>.Builder paid, ImmutableList<Cost>.Builder extra, ImmutableList<ManaSymbol> mana)
    {
        var times = new Dictionary<KeywordKind, int>();
        foreach (var kind in command.OptionalCosts ?? [])
        {
            // CR 702.174b: promising a gift costs nothing and is not an additional cost, so it is
            // recorded (Gift) and skipped here rather than priced.
            if (kind == KeywordKind.Gift)
            {
                paid.Add(kind.ToString());
                continue;
            }

            var semantic = KeywordSemantics.Of(kind);
            if (semantic.Cost != CostShape.Additional)
                throw new RefusedException($"{kind} is not an additional cost.", semantic.Rule);
            var ability = Keyword(printed, kind) ?? throw new RefusedException($"That card has no {kind}.", semantic.Rule);

            // CR 702.32b: multikicker may be paid any number of times, and KeywordAbility.Repeatable
            // is how the printed text says so. An ordinary kicker is paid once, CR 702.32a.
            times[kind] = times.GetValueOrDefault(kind) + 1;
            if (times[kind] > 1 && !ability.Repeatable)
                throw new RefusedException($"A {kind} cost cannot be paid more than once.", semantic.Rule);
            if (!ManaSymbols.TryParse(ability.Cost, out var more, out _))
                throw new RefusedException($"The {kind} cost cannot be paid yet.", "601.2h");

            // CR 601.2b: each payment is a separate additional cost, so a cost paid twice is paid
            // twice over and recorded twice — "if it was kicked twice" counts the entries.
            mana = mana.AddRange(more);
            extra.AddRange(ability.Costs ?? []);
            paid.Add(kind.ToString());
        }

        return mana;
    }

    /// <summary>
    /// The statics that shape the cost while the spell is on the stack. Affinity is not optional,
    /// CR 702.40a, so it is applied here and counted from the battlefield. Convoke, improvise and
    /// delve (CR 702.51a, 702.126a, 702.66a) are payments a player <em>may</em> make, and there is
    /// no channel on the command for the permanents or cards they would use, so a spell with one
    /// of them is cast for its full price — which is a legal choice, not a dropped rule.
    /// </summary>
    [RuleRef("702.40a")]
    private static ImmutableList<ManaSymbol> Shaped(GameState state, Face printed, IQuery query, PlayerId player, ImmutableList<ManaSymbol> mana)
    {
        var reduction = 0;
        foreach (var keyword in printed.AbilityList.OfType<KeywordAbility>().Where(k => k.Keyword == KeywordKind.Affinity))
            reduction += Affinity(state, keyword, query, player);
        return reduction == 0 ? mana : ReduceGeneric(mana, reduction);
    }

    /// <summary>
    /// CR 702.40a: "affinity for [text]" costs {1} less to cast for each matching permanent its
    /// controller controls. The filter comes off the printed keyword; the "you control" half is
    /// the rule's, so only the caster's permanents are counted whatever the filter says.
    /// </summary>
    [RuleRef("702.40a")]
    private static int Affinity(GameState state, KeywordAbility keyword, IQuery query, PlayerId player)
    {
        var printed = keyword.Of ?? throw new RefusedException("An affinity with nothing to count cannot be applied.", "702.40a");

        // "its controller controls" is the rule's half, so it is asked of the layer system
        // through the filter (CR 613.1b) rather than off the object's stored initial
        // controller, which a ControlBy effect leaves behind: a permanent gained from an
        // opponent was not counted, and one lost to them still was.
        var filter = printed with { ControlledBy = new You() };
        var context = CostPayment.Context(null, player, null);
        var count = 0;
        foreach (var obj in state.Objects.Values)
        {
            if (obj.Zone.Zone != CardZone.Battlefield)
                continue;
            if (query.Matches(state, filter, obj.Id, context))
                count++;
        }

        return count;
    }

    /// <summary>
    /// CR 601.2f and 118.7: a cost reduction comes off the generic part of the cost and can never
    /// take it below nothing, so the coloured symbols are still paid in full.
    /// </summary>
    [RuleRef("601.2f")]
    private static ImmutableList<ManaSymbol> ReduceGeneric(ImmutableList<ManaSymbol> mana, int reduction)
    {
        var result = mana;
        for (var i = 0; i < result.Count && reduction > 0; i++)
        {
            var symbol = result[i];
            if (symbol.IsX || symbol.IsSnow || !symbol.IsAnyType || symbol.Generic <= 0)
                continue;
            var taken = Math.Min(symbol.Generic, reduction);
            reduction -= taken;
            result = result.SetItem(i, symbol with { Generic = symbol.Generic - taken });
        }

        return result;
    }

    private static KeywordAbility? Keyword(Face printed, KeywordKind kind) =>
        printed.AbilityList.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == kind);
}
