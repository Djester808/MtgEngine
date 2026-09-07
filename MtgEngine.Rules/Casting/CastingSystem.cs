using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using AbilityActivated = MtgEngine.Rules.Events.AbilityActivated;
using CardZone = MtgEngine.Cards.Zone;
using LandPlayed = MtgEngine.Rules.Events.LandPlayed;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// Playing lands, casting spells, activating abilities and resolving the stack: CR 305, 601,
/// 602, 605 and 608. Every path here either produces the events the rules describe or throws a
/// <see cref="RefusedException"/> naming the rule that stops it, and <see cref="Check"/> answers
/// with the same code that performs the action so the two can never disagree.
/// </summary>
[RuleRef("601")]
public sealed partial class CastingSystem(IZoneSystem zones, IQuery query, ICharacteristics characteristics, IInterpreter interpreter) : ICastingSystem
{
    /// <summary>CR 305.2: one land a turn. Effects that raise it are the layer system's to add.</summary>
    private const int LandsPerTurn = 1;

    /// <summary>The subtype of CR 205.3h that makes an enchantment an Aura, CR 303.4.</summary>
    private const string AuraSubtype = "Aura";

    public string Name => "mana, costs, casting and the stack";

    /// <summary>
    /// CR 305.1: a player may play a land from their hand during a main phase of their turn when
    /// they have priority and the stack is empty. It is a special action, so it never uses the
    /// stack, CR 116.2a.
    /// </summary>
    [RuleRef("305.1")]
    public Outcome PlayLand(GameState state, PlayLand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var card = Playable(state, command.Card, command.Player, "305.1");
        var face = LandFace(state, card, command.FaceIndex);
        RequireSorcerySpeed(state, command.Player, "305.1");

        // CR 305.2a: compare lands played with lands playable; 305.2b refuses when they are equal.
        if (state.Player(command.Player).LandsPlayedThisTurn >= LandsPerTurn)
            throw new RefusedException("You have already played a land this turn.", "305.2");

        var battlefield = ZoneKey.Of(CardZone.Battlefield, command.Player);

        // CR 712.12: a modal double-faced card's land face is played, not cast, and the permanent
        // enters showing that face — only the move can say so, so the face-index form is used when
        // the face being played is not the one the card is already showing.
        var moved = face == card.FaceIndex
            ? zones.Move(state, card.Id, battlefield, controller: command.Player)
            : zones.Move(state, card.Id, battlefield, Placement.Default, false, command.Player, face);
        var played = new LandPlayed(NewIdOf(moved.Events, card.Id), command.Player);
        return new Outcome(played.Apply(moved.State), moved.Events.Add(played));
    }

    /// <summary>
    /// CR 602.2: the ability goes on the stack, then its costs are paid. A mana ability is the
    /// exception, CR 605.1a and 605.3a: it never uses the stack and its mana is added at once.
    /// </summary>
    [RuleRef("602")]
    public Outcome Activate(GameState state, ActivateAbility command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        var source = Object(state, command.Source);
        var chars = characteristics.Of(state, command.Source);
        if (command.AbilityIndex < 0 || command.AbilityIndex >= chars.Abilities.Count)
            throw new RefusedException("There is no ability there to activate.", "602.1");
        if (chars.Abilities[command.AbilityIndex] is not ActivatedAbility ability)
            throw new RefusedException("That ability is not an activated ability.", "602.1");

        // CR 113.6: an ability functions only in the zone its text says it does.
        if (source.Zone.Zone != ability.Where)
            throw new RefusedException("That ability does not function in that zone.", "113.6");
        if (chars.Controller != command.Player && !ability.AnyPlayerMayActivate)
            throw new RefusedException("That ability is not yours to activate.", "602.1a");
        if (state.Priority.Holder != command.Player)
            throw new RefusedException("You do not have priority.", "602.2a");
        if (ability.Timing == Timing.SorcerySpeed)
            RequireSorcerySpeed(state, command.Player, "602.2a");

        // CR 106.6b: both printed riders on which mana may be spent travel with the payment.
        var paid = CostPayment.Pay(
            state,
            new CostContext(source, chars, command.Player, command.X, zones, query, ability.PayOnlyWith, new ChosenObjects(command.CostObjects ?? []), ability.PayOnlyFrom),
            ability.Costs);
        // CR 106.6a: what the activation cost was spent on is the ability's source, so a
        // "when that mana is spent" trigger can tell this payment from any other.
        var spent = SpentOn(paid.Events, source.Id);
        var activated = new AbilityActivated(null, source.Id, command.AbilityIndex, command.Player);
        if (IsManaAbility(ability, command))
        {
            // CR 605.3a: activating a mana ability does not use the stack, so it resolves here,
            // and the event's stack object stays null to say so.
            var mana = ManaFrom((AddMana)ability.Do, state, command, chars.Controller);
            ImmutableList<GameEvent> immediate = [.. spent, activated, mana];
            return new Outcome(GameReducer.Apply(state, immediate), immediate);
        }

        var onStack = StackZone.CreateAbilityOnStack(
            paid.State,
            source,
            chars.Controller,
            new StackEntry(source.Id, command.AbilityIndex, command.Targets ?? [], command.TargetPlayers ?? [], command.Modes ?? [], command.X, IsCopy: false, ManaProvenanceSpent: ProvenanceSpent(paid.Events)));
        ImmutableList<GameEvent> events = [.. spent, onStack, activated with { StackObject = onStack.Object.Id }];
        return new Outcome(GameReducer.Apply(state, events), events);
    }

    /// <summary>
    /// CR 608.2: the top object of the stack resolves. A permanent spell becomes a permanent on
    /// the battlefield under its controller's control, CR 608.3, summoning sick until their next
    /// turn begins, CR 302.6. An instant, a sorcery or an ability does what it says and then
    /// goes to its owner's graveyard or ceases to exist, CR 608.2m and 113.7a.
    /// </summary>
    [RuleRef("608.2")]
    public Outcome ResolveTop(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var top = StackZone.Top(state) ?? throw new RefusedException("There is nothing on the stack to resolve.", "608.1");
        var obj = state.Objects[top];
        var controller = characteristics.ControllerOf(state, top);
        var resolved = new StackObjectResolved(top);

        // An ability on the stack keeps its source's definition (CR 602.2a), so it is asked
        // before the permanent-spell test reads that definition's types.
        var chars = StackZone.IsAbility(obj) ? null : characteristics.Of(state, top);
        if (chars is not null && IsPermanentSpell(chars.Types))
        {
            ObjectId? attach = null;
            PlayerId? attachPlayer = null;
            if (IsAura(chars))
            {
                var (to, toPlayer, illegal) = AuraAttachment(state, obj, chars);

                // CR 608.2b: a spell all of whose targets are illegal as it resolves does not
                // resolve; it is removed from the stack and put where CR 608.2m sends it.
                if (illegal)
                {
                    var away = FinishResolving(state, top);
                    return new Outcome(GameReducer.Apply(away.State, [resolved]), away.Events.Add(resolved));
                }

                attach = to;
                attachPlayer = toPlayer;
            }

            var moved = zones.Move(state, top, ZoneKey.Of(CardZone.Battlefield, controller), controller: controller);

            // CR 303.4f: an Aura enters attached to the object or the player it was cast at (the
            // two exclude each other), so the attachment is part of the permanent's arrival.
            var sick = new ObjectChanged(NewIdOf(moved.Events, top), SummoningSick: true, AttachedTo: attach, AttachedToPlayer: attachPlayer);
            ImmutableList<GameEvent> events = [.. moved.Events, sick, resolved];
            return new Outcome(GameReducer.Apply(moved.State, [sick, resolved]), events);
        }

        // CR 101.4: a resolution that stopped to ask leaves its object on the stack; the answer
        // finishes the walk and the choice system then asks for the leaving (FinishResolving).
        var ran = interpreter.Resolve(state, top);
        if (ran.State.Pending is not null)
            return ran;

        var left = FinishResolving(ran.State, top);
        return new Outcome(left.State, ran.Events.AddRange(left.Events));
    }

    /// <summary>
    /// The end of a resolution that the interpreter has finished: an ability ceases to exist
    /// (CR 608.2n), a spell goes where CR 608.2m and its costs say. Called by ResolveTop when the
    /// walk finished at once, and by the choice system when an answer finished it later.
    /// </summary>
    [RuleRef("608.2m")]
    public Outcome FinishResolving(GameState state, ObjectId stackObject)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Objects.TryGetValue(stackObject, out var obj) || obj.Zone.Zone != CardZone.Stack)
            return Outcome.Unchanged(state);

        if (StackZone.IsAbility(obj))
        {
            var gone = new ObjectCeasedToExist(stackObject);
            return new Outcome(gone.Apply(state), [gone]);
        }

        var destination = Destination(obj);
        var away = zones.Move(state, stackObject, ZoneKey.Of(destination, obj.Owner));

        // CR 720.3d: an Omen goes into its owner's library and that library is shuffled.
        var shuffled = destination == CardZone.Library ? zones.Shuffle(away.State, obj.Owner) : Outcome.Unchanged(away.State);
        return new Outcome(shuffled.State, away.Events.AddRange(shuffled.Events));
    }

    /// <summary>
    /// Where a spell goes as it finishes resolving. CR 608.2m puts it into its owner's graveyard;
    /// what it was cast for comes first — a spell cast for its flashback cost is exiled instead
    /// (CR 702.34a) and one whose buyback was paid returns to its owner's hand (CR 702.26a) — and
    /// then the face it was cast as: an Adventure is exiled and may be cast from there later
    /// (CR 715.3d), an Omen is shuffled into its owner's library (CR 720.3d).
    /// </summary>
    [RuleRef("608.2m")]
    private static CardZone Destination(GameObject spell)
    {
        var paid = spell.Stack?.OptionalCostsPaid ?? [];
        if (paid.Contains(nameof(KeywordKind.Flashback)))
            return CardZone.Exile;
        if (paid.Contains(nameof(KeywordKind.Buyback)))
            return CardZone.Hand;
        return FaceRules.ResolvesTo(spell.Definition, spell.FaceIndex).Zone;
    }

    /// <summary>The refusal a command would be given now, or null if the rules allow it.</summary>
    public Refusal? Check(GameState state, Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            _ = command switch
            {
                CastSpell c => Cast(state, c),
                ActivateAbility a => Activate(state, a),
                PlayLand p => PlayLand(state, p),
                _ => Outcome.Unchanged(state),
            };
            return null;
        }
        catch (RefusedException refused)
        {
            return new Refusal(refused.Reason, refused.Rule);
        }
    }

    /// <summary>
    /// CR 106.4: each player's mana pool empties at the end of each step and phase. The turn
    /// system calls this as a step ends; nothing else does.
    /// </summary>
    [RuleRef("106.4")]
    public Outcome EmptyPools(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ImmutableList<GameEvent> events =
        [
            .. state.Players
                .Where(p => p.Mana.Total > 0)
                .Select(p => new ManaPoolEmptied(p.Id)),
        ];
        return events.Count == 0 ? Outcome.Unchanged(state) : new Outcome(GameReducer.Apply(state, events), events);
    }

    /// <summary>
    /// CR 605.1a: an activated ability is a mana ability when it needs no target, could add mana,
    /// is not a loyalty ability, and neither its cost nor its effect moves a card to or from a
    /// library. In this vocabulary that is an <see cref="AddMana"/> effect with no target taken.
    /// </summary>
    [RuleRef("605.1a")]
    private static bool IsManaAbility(ActivatedAbility ability, ActivateAbility command) =>
        ability.Do is AddMana
        && command.Targets is null or { Count: 0 }
        && command.TargetPlayers is null or { Count: 0 }
        && !ability.Costs.Any(c => c is LoyaltyCost);

    /// <summary>CR 303.4: an Aura is an enchantment with the Aura subtype, CR 205.3h.</summary>
    [RuleRef("303.4")]
    private static bool IsAura(Characteristics chars) =>
        chars.Subtypes.Any(s => string.Equals(s, AuraSubtype, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What an Aura spell enters attached to, CR 303.4f: the object it was cast at. That target is
    /// re-checked as the spell resolves, CR 608.2b — an object that has left the battlefield, or
    /// that no longer matches the enchant ability's filter (CR 702.5b), is illegal, and a spell
    /// all of whose targets are illegal does not resolve.
    /// <para>
    /// An Aura cast at a player enters attached to that player, the other half of CR 303.4f, kept
    /// on <see cref="GameObject.AttachedToPlayer"/>. A player is not an object, so there is no
    /// CR 608.2b re-check of the enchant filter against one; whether such an Aura may stay is
    /// CR 704.5m's, in the state-based actions.
    /// </para>
    /// </summary>
    [RuleRef("303.4f")]
    private (ObjectId? To, PlayerId? ToPlayer, bool Illegal) AuraAttachment(GameState state, GameObject spell, Characteristics chars)
    {
        if (spell.Stack is not { } entry)
            return (null, null, false);
        if (entry.Targets.Count == 0)
            return (null, entry.TargetPlayers.Count > 0 ? entry.TargetPlayers[0] : null, false);

        var host = entry.Targets[0];
        if (!state.Objects.TryGetValue(host, out var target) || target.Zone.Zone != CardZone.Battlefield)
            return (null, null, true);

        // CR 702.5b: "enchant [object]" is what a legal object is for this Aura. With no enchant
        // ability there is nothing to be illegal about, so the attachment stands.
        var enchant = chars.Abilities.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == KeywordKind.Enchant);
        var context = CostPayment.Context(spell.Id, chars.Controller, entry.X);
        return enchant?.Of is { } filter && !query.Matches(state, filter, host, context)
            ? (null, null, true)
            : (host, null, false);
    }

    /// <summary>CR 608.3: a permanent spell becomes a permanent as it resolves.</summary>
    [RuleRef("608.3")]
    private static bool IsPermanentSpell(CardType types) =>
        (types & (CardType.Artifact | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0;

    /// <summary>What an "add mana" instruction adds, CR 106.1: printed mana, or a colour chosen with the activation.</summary>
    [RuleRef("106.1")]
    private ManaChanged ManaFrom(AddMana add, GameState state, ActivateAbility command, PlayerId controller)
    {
        var who = add.Who is null
            ? controller
            : query.Players(state, add.Who, CostPayment.Context(command.Source, controller, command.X)).FirstOrDefault(controller);
        var times = add.N is null ? 1 : query.Evaluate(state, add.N, CostPayment.Context(command.Source, controller, command.X));
        // CR 608.2d: each mana added is its own choice, so the nth one reads the nth letter of
        // the activation's colours rather than repeating the first.
        var total = ManaPool.Empty;
        for (var i = 0; i < Math.Max(0, times); i++)
            total = ManaSymbols.Add(total, OneProduction(add.Mana, command, i));

        // CR 106.6a: "spend this mana only on ..." adds a restricted block rather than ordinary
        // mana, so only a cost the filter matches can be paid with it; CR 106.6: either way the
        // mana remembers the permanent that produced it and the key the card gave it.
        return new ManaChanged(who, ManaSources.Added(total, add.SpendOnlyOn, command.Source, add.Key), "mana ability");
    }

    private static ManaPool OneProduction(ManaProduction production, ActivateAbility command, int index)
    {
        if (production.Fixed is { Length: > 0 })
            return ManaSymbols.Produced(production.Fixed);

        // CR 608.2d: a choice the effect offers is announced as it is applied — here, with the
        // activation, so no payment is ever suspended (CR 601.2h). ManaColors is the channel the
        // command has for it, one letter per mana; CostChoices stays readable behind it for an
        // activation that names its colour there.
        if (production.AnyColor || production.ChosenColor || production.OneOf is not null)
        {
            var chosen = ColorAt(command.ManaColors, index)
                ?? command.CostChoices?.Select(ManaSymbols.ColorFromChoice).FirstOrDefault(c => c is not null)
                ?? (production.OneOf is { } only && IsSingleColor(only) ? (Color?)only : null);
            return chosen is null
                ? throw new RefusedException("The colour of mana to add was not chosen.", "608.2d")
                : ManaSymbols.OfColor(chosen.Value);
        }

        throw new RefusedException("That kind of mana production is not supported yet.", "106.1");
    }

    private static bool IsSingleColor(Color color) =>
        color is Color.White or Color.Blue or Color.Black or Color.Red or Color.Green;

    private static void RequireSorcerySpeed(GameState state, PlayerId player, string rule)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Turn.ActivePlayer != player)
            throw new RefusedException("Only during your own turn.", rule);
        if (state.Turn.Step is not (Step.PrecombatMain or Step.PostcombatMain))
            throw new RefusedException("Only during a main phase.", rule);
        if (!StackZone.IsEmpty(state))
            throw new RefusedException("Only while the stack is empty.", rule);
    }

    private static GameObject Object(GameState state, ObjectId id) =>
        state.Objects.TryGetValue(id, out var obj) ? obj : throw new RefusedException("There is no such object.", "109.1");

    /// <summary>
    /// Which face is played as a land, CR 305.1 and 712.12. The face the command names when it
    /// names one, and it must be a land face this card may be played as; otherwise the card's own
    /// face when that is a land, or the one land face among the faces it may be played as
    /// (CR 601.3b). Two land faces and no choice on the command is refused rather than picked.
    /// </summary>
    [RuleRef("712.12")]
    private int LandFace(GameState state, GameObject card, int? chosen)
    {
        var lands = FaceRules.CastableFaces(card.Definition, card.Zone.Zone).Where(f => f.IsLandFace).ToList();
        if (chosen is { } index)
        {
            return lands.Any(f => f.FaceIndex == index)
                ? index
                : throw new RefusedException("That face cannot be played as a land.", "712.12");
        }

        if (characteristics.Of(state, card.Id).Types.HasFlag(CardType.Land))
            return card.FaceIndex;

        return lands.Count switch
        {
            1 => lands[0].FaceIndex,
            0 => throw new RefusedException("That card is not a land.", "305.1"),
            _ => throw new RefusedException("Which land face to play was not chosen.", "712.12"),
        };
    }

    /// <summary>
    /// The card a player is trying to cast. CR 601.2: a player casts a card they own, and which
    /// zone it may leave is the plan's business (CR 601.3) — a card in someone else's hand is not
    /// castable whatever permission it carries, because that permission is its owner's.
    /// </summary>
    [RuleRef("601.2")]
    private static GameObject Castable(GameState state, ObjectId id, PlayerId player)
    {
        var obj = Object(state, id);
        if (obj.Zone.Zone == CardZone.Hand && obj.Zone != ZoneKey.Of(CardZone.Hand, player))
            throw new RefusedException("That card is not in your hand.", "601.2a");
        return obj.Owner == player ? obj : throw new RefusedException("That card is not yours to cast.", "601.3");
    }

    /// <summary>
    /// Where a land may be played from, CR 305.1: its owner's hand, or the zone a
    /// <see cref="MayPlayFromZone"/> names — CR 601.3a's permission, read by the same code
    /// (<see cref="CastPlan.Permission"/>) so a land and a spell cannot disagree about it.
    /// </summary>
    [RuleRef("305.1")]
    private GameObject Playable(GameState state, ObjectId id, PlayerId player, string rule)
    {
        ArgumentNullException.ThrowIfNull(state);
        var obj = Object(state, id);
        return obj.Zone == ZoneKey.Of(CardZone.Hand, player)
            || (obj.Owner == player && CastPlan.Permission(state, obj, player, query) is not null)
            ? obj
            : throw new RefusedException("That card is not in your hand.", rule);
    }

    /// <summary>A zone change makes a new object, CR 400.7; the move's event carries the new id.</summary>
    private static ObjectId NewIdOf(IEnumerable<GameEvent> events, ObjectId from) =>
        events.OfType<ObjectMoved>().Where(m => m.From == from).Select(m => m.To.Id).LastOrDefault(from);
}
