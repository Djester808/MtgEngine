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
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// Playing lands, casting spells, activating abilities and resolving the stack: CR 305, 601,
/// 602, 605 and 608. Every path here either produces the events the rules describe or throws a
/// <see cref="RefusedException"/> naming the rule that stops it, and <see cref="Check"/> answers
/// with the same code that performs the action so the two can never disagree.
/// </summary>
[RuleRef("601")]
public sealed class CastingSystem(IZoneSystem zones, IQuery query, ICharacteristics characteristics, IInterpreter interpreter) : ICastingSystem
{
    /// <summary>CR 305.2: one land a turn. Effects that raise it are the layer system's to add.</summary>
    private const int LandsPerTurn = 1;

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
        var card = InHand(state, command.Card, command.Player, "305.1");
        var face = LandFace(state, card);
        RequireSorcerySpeed(state, command.Player, "305.1");

        // CR 712.12: a modal double-faced card's land face is played, not cast — but the zone
        // system has no way to say which face the permanent enters with, so it is refused by name
        // rather than played as the wrong face. seam-requests/E3b.md asks for the two fields.
        if (face != card.FaceIndex)
            throw new RefusedException("A land face on the back of a card cannot be played yet.", "712.12");

        // CR 305.2a: compare lands played with lands playable; 305.2b refuses when they are equal.
        if (state.Player(command.Player).LandsPlayedThisTurn >= LandsPerTurn)
            throw new RefusedException("You have already played a land this turn.", "305.2");

        var moved = zones.Move(state, card.Id, ZoneKey.Of(CardZone.Battlefield, command.Player), controller: command.Player);
        var played = new LandPlayed(NewIdOf(moved.Events, card.Id), command.Player);
        return new Outcome(played.Apply(moved.State), moved.Events.Add(played));
    }

    /// <summary>
    /// CR 601.2: the card moves to the stack and becomes a spell carrying its targets, modes and
    /// X (601.2a–d), the total cost is paid (601.2h), and then the spell is cast (601.2i). An
    /// instant or a spell with flash needs only priority, CR 304.1 and 702.8b; everything else
    /// waits for its controller's main phase with an empty stack, CR 302.1 and 307.1.
    /// </summary>
    [RuleRef("601.2")]
    public Outcome Cast(GameState state, CastSpell command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var card = Castable(state, command.Card, command.Player);

        // CR 601.3b: the face and every optional cost are chosen before anything moves, so the
        // plan is what the spell costs and what it will be on the stack.
        var plan = CastPlan.For(state, card, command, query);
        if (plan.Printed.AdditionalCosts is { Count: > 0 })
            throw new RefusedException("An additional cost printed on a spell cannot be paid yet.", "601.2f");
        RequireCastingTiming(state, command.Player, plan, characteristics.Of(state, card.Id));

        // 601.2h before 601.2a in code only: nothing is emitted unless the whole cost is payable.
        var paid = CostPayment.PaySymbols(state, command.Player, plan.Mana, command.X, "cast");
        var entry = new StackEntry(
            Source: null,
            AbilityIndex: null,
            Targets: command.Targets ?? [],
            TargetPlayers: command.TargetPlayers ?? [],
            Modes: command.Modes ?? [],
            X: command.X,
            IsCopy: false,
            ManaSpent: CostPayment.ByColor(paid.Spent),
            CastFrom: plan.From,
            OptionalCostsPaid: plan.OptionalCostsPaid);
        var move = StackZone.MoveSpellToStack(state, card, entry, plan.Chosen.FaceIndex);
        var cast = new SpellCast(move.To.Id, command.Player, plan.From);
        ImmutableList<GameEvent> events = [move, .. paid.Outcome.Events, cast];
        return new Outcome(GameReducer.Apply(state, events), events);
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

        var paid = CostPayment.Pay(state, source, chars, ability.Costs, command.Player, command.X, zones, query);
        var activated = new AbilityActivated(null, source.Id, command.AbilityIndex, command.Player);
        if (IsManaAbility(ability, command))
        {
            // CR 605.3a: activating a mana ability does not use the stack, so it resolves here,
            // and the event's stack object stays null to say so.
            var mana = ManaFrom((AddMana)ability.Do, state, command, chars.Controller);
            ImmutableList<GameEvent> immediate = [.. paid.Events, activated, mana];
            return new Outcome(GameReducer.Apply(state, immediate), immediate);
        }

        var onStack = StackZone.CreateAbilityOnStack(
            paid.State,
            source,
            chars.Controller,
            new StackEntry(source.Id, command.AbilityIndex, command.Targets ?? [], command.TargetPlayers ?? [], command.Modes ?? [], command.X, IsCopy: false));
        ImmutableList<GameEvent> events = [.. paid.Events, onStack, activated with { StackObject = onStack.Object.Id }];
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

        if (StackZone.IsAbility(obj))
        {
            var ran = interpreter.Resolve(state, top);
            var gone = new ObjectCeasedToExist(top);
            ImmutableList<GameEvent> events = [.. ran.Events, gone, resolved];
            return new Outcome(GameReducer.Apply(ran.State, [gone, resolved]), events);
        }

        if (IsPermanentSpell(characteristics.Of(state, top).Types))
        {
            var moved = zones.Move(state, top, ZoneKey.Of(CardZone.Battlefield, controller), controller: controller);
            var sick = new ObjectChanged(NewIdOf(moved.Events, top), SummoningSick: true);
            ImmutableList<GameEvent> events = [.. moved.Events, sick, resolved];
            return new Outcome(GameReducer.Apply(moved.State, [sick, resolved]), events);
        }

        var effect = interpreter.Resolve(state, top);
        var destination = Destination(obj);
        var away = zones.Move(effect.State, top, ZoneKey.Of(destination, obj.Owner));

        // CR 720.3d: an Omen goes into its owner's library and that library is shuffled.
        var shuffled = destination == CardZone.Library ? zones.Shuffle(away.State, obj.Owner) : Outcome.Unchanged(away.State);
        ImmutableList<GameEvent> spell = [.. effect.Events, .. away.Events, .. shuffled.Events, resolved];
        return new Outcome(GameReducer.Apply(shuffled.State, [resolved]), spell);
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
        var once = OneProduction(add.Mana, command);
        var total = ManaPool.Empty;
        for (var i = 0; i < Math.Max(0, times); i++)
            total = ManaSymbols.Add(total, once);
        return new ManaChanged(who, total, "mana ability");
    }

    private static ManaPool OneProduction(ManaProduction production, ActivateAbility command)
    {
        if (production.Fixed is { Length: > 0 })
            return ManaSymbols.Produced(production.Fixed);

        // CR 608.2d: a choice the effect offers is announced as it is applied — here, with the
        // activation, so no payment is ever suspended (CR 601.2h).
        if (production.AnyColor || production.ChosenColor || production.OneOf is not null)
        {
            var chosen = command.CostChoices?.Select(ManaSymbols.ColorFromChoice).FirstOrDefault(c => c is not null)
                ?? (production.OneOf is { } only && IsSingleColor(only) ? (Color?)only : null);
            return chosen is null
                ? throw new RefusedException("The colour of mana to add was not chosen.", "608.2d")
                : ManaSymbols.OfColor(chosen.Value);
        }

        throw new RefusedException("That kind of mana production is not supported yet.", "106.1");
    }

    private static bool IsSingleColor(Color color) =>
        color is Color.White or Color.Blue or Color.Black or Color.Red or Color.Green;

    /// <summary>
    /// CR 117.1a and 601.1a: casting needs priority; a sorcery-speed action needs the caster's own
    /// main phase with an empty stack. The types read are the chosen face's, CR 712.11c, so casting
    /// the instant back of a modal card is not held to the front face's timing; a flash granted by
    /// an effect is on the object's computed characteristics rather than the printed face, so both
    /// are consulted.
    /// </summary>
    [RuleRef("117.1a")]
    private static void RequireCastingTiming(GameState state, PlayerId player, CastPlan plan, Characteristics chars)
    {
        if (state.Priority.Holder != player)
            throw new RefusedException("You do not have priority.", "117.1a");

        // CR 304.1 and 702.8b: an instant, or anything with flash, is cast whenever its
        // controller holds priority.
        if (plan.Types.HasFlag(CardType.Instant) || plan.HasFlash || chars.Has(KeywordKind.Flash))
            return;
        RequireSorcerySpeed(state, player, SorceryTimingRule(plan.Types));
    }

    /// <summary>The rule that names the timing for this card type: a creature's is 302.1, a sorcery's 307.1.</summary>
    private static string SorceryTimingRule(CardType types) => types switch
    {
        _ when types.HasFlag(CardType.Sorcery) => "307.1",
        _ when types.HasFlag(CardType.Creature) => "302.1",
        _ when types.HasFlag(CardType.Artifact) => "301.1",
        _ when types.HasFlag(CardType.Enchantment) => "303.1",
        _ when types.HasFlag(CardType.Planeswalker) => "306.1",
        _ => "601.3",
    };

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
    /// Which face is played as a land, CR 305.1 and 712.12. The card's own face when that is a
    /// land; otherwise the one land face among the faces this card may be played as, CR 601.3b.
    /// Two land faces would be a choice, and there is nowhere on the command to make it.
    /// </summary>
    [RuleRef("712.12")]
    private int LandFace(GameState state, GameObject card)
    {
        if (characteristics.Of(state, card.Id).Types.HasFlag(CardType.Land))
            return card.FaceIndex;

        var lands = FaceRules.CastableFaces(card.Definition, card.Zone.Zone).Where(f => f.IsLandFace).ToList();
        return lands.Count switch
        {
            1 => lands[0].FaceIndex,
            0 => throw new RefusedException("That card is not a land.", "305.1"),
            _ => throw new RefusedException("Which land face to play cannot be chosen yet.", "712.12"),
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

    private static GameObject InHand(GameState state, ObjectId id, PlayerId player, string rule)
    {
        ArgumentNullException.ThrowIfNull(state);
        var obj = Object(state, id);
        return obj.Zone == ZoneKey.Of(CardZone.Hand, player)
            ? obj
            : throw new RefusedException("That card is not in your hand.", rule);
    }

    /// <summary>A zone change makes a new object, CR 400.7; the move's event carries the new id.</summary>
    private static ObjectId NewIdOf(IEnumerable<GameEvent> events, ObjectId from) =>
        events.OfType<ObjectMoved>().Where(m => m.From == from).Select(m => m.To.Id).LastOrDefault(from);
}
