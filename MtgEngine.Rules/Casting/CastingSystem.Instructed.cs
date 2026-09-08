using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// The two roads a spell is cast by, CR 601.2 and CR 608.2g. A player's command takes the first
/// and is held to the timing rules; a resolving effect that instructs a player to cast a card
/// takes the second, which is the same announcement without CR 117.1a's priority check because
/// the cast happens inside that resolution. Everything else — the plan, the costs, the stack
/// entry — is one body, so the two can never disagree about what a spell costs.
/// </summary>
[RuleRef("608.2g")]
public sealed partial class CastingSystem
{
    /// <summary>
    /// CR 601.2: the card moves to the stack and becomes a spell carrying its targets, modes and
    /// X (601.2a–d), the total cost is paid (601.2h), and then the spell is cast (601.2i). An
    /// instant or a spell with flash needs only priority, CR 304.1 and 702.8b; everything else
    /// waits for its controller's main phase with an empty stack, CR 302.1 and 307.1.
    /// </summary>
    [RuleRef("601.2")]
    public Outcome Cast(GameState state, CastSpell command) => CastCore(state, command, instructed: false);

    /// <summary>
    /// CR 608.2g: a spell cast because a resolving effect instructs its player to cast it is cast
    /// during that resolution, so CR 117.1a's priority check does not apply — nobody holds priority
    /// while a spell resolves, and holding an instructed cast to that rule would make it impossible.
    /// The instruction is also CR 601.3a's permission to cast the card from wherever the effect
    /// found it, and carries CR 118.9's "without paying its mana cost" when the effect said so.
    /// Only the interpreter reaches this; a command from a player is always <see cref="Cast"/>.
    /// </summary>
    [RuleRef("608.2g")]
    public Outcome CastInstructed(GameState state, CastSpell command) => CastCore(state, command, instructed: true);

    private Outcome CastCore(GameState state, CastSpell command, bool instructed)
    {
        ArgumentNullException.ThrowIfNull(command);
        var card = Castable(state, command.Card, command.Player);

        // CR 601.3b: the face and every optional cost are chosen before anything moves, so the
        // plan is what the spell costs and what it will be on the stack.
        var chars = characteristics.Of(state, card.Id);
        var plan = CastPlan.For(state, card, command, query, instructed);
        if (!instructed)
            RequireCastingTiming(state, command.Player, plan, chars);

        // The enchant half is asked first: protection from everything sets both prohibitions, and
        // "that player cannot be enchanted" is the nearer reason an Aura at one is refused.
        RequireLegalAuraTarget(state, chars, command);
        RequireLegalPlayerTargets(state, command, plan);

        // CR 601.2b with 700.2: the modes are announced before the costs are paid (601.2h), held
        // to the ceiling the chosen face prints — a ceiling that depends on the game is read now,
        // once, and travels on the entry so nothing reads it again.
        var announced = ModeAnnouncement.Announce(
            state,
            query,
            plan.Printed.AbilityList.OfType<SpellAbility>().Select(s => s.Do),
            CostPayment.Context(card.Id, command.Player, command.X),
            command.Modes ?? []);

        // 601.2h before 601.2a in code only: nothing is emitted unless the whole cost is payable.
        // The costs that are not mana come first because each one moves objects, and the spell's
        // own new object is numbered from the state they leave behind (CR 400.7).
        // CR 601.2c: the objects that pay a cost naming some come off the command, read in the
        // order the costs are written.
        var costs = new CostContext(
            card,
            chars,
            command.Player,
            command.X,
            zones,
            query,
            Chosen: new ChosenObjects(command.CostObjects ?? []),

            // CR 107.4f with 601.2h: the choice a Phyrexian symbol offers is announced with the
            // cast, on the same channel every other cost choice travels.
            PayPhyrexianWithLife: PaysWithLife(command.CostChoices));
        var others = CostPayment.Pay(state, costs, plan.NonMana);
        var paid = CostPayment.PaySymbols(others.State, costs, plan.Mana, "cast");
        ImmutableList<GameEvent> payments = [.. others.Events, .. paid.Outcome.Events];
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
            OptionalCostsPaid: plan.OptionalCostsPaid,

            // CR 702.174b: the promise is part of the spell, so it travels on the stack entry.
            GiftPromisedTo: plan.GiftPromisedTo,

            // CR 700.2 with 601.2b: the mode ceiling this announcement read, when it was conditional.
            Chosen: announced,

            // CR 106.6a: which mana actually paid is a property of the spell, so it travels too.
            ManaProvenanceSpent: ProvenanceSpent(payments));
        var move = StackZone.MoveSpellToStack(paid.Outcome.State, card, entry, plan.Chosen.FaceIndex);
        var cast = new SpellCast(move.To.Id, command.Player, plan.From);
        ImmutableList<GameEvent> events = [.. SpentOn(payments, move.To.Id), move, cast];
        return new Outcome(GameReducer.Apply(state, events), events);
    }

    /// <summary>
    /// CR 601.2c with 702.5b: the targets a spell requires are chosen as it is cast, and an Aura's
    /// enchant ability is what a legal one is. An Aura whose enchant ability names only objects
    /// cannot be cast at a player at all, so the announcement is refused here rather than accepted
    /// and buried by CR 704.5m on the next state-based action check — a spell that could never
    /// have been announced is not a permanent that briefly existed. An enchant ability with a
    /// player filter admits one, and one with neither filter admits anything (CR 702.5b).
    /// </summary>
    [RuleRef("601.2c")]
    private void RequireLegalAuraTarget(GameState state, Characteristics chars, CastSpell command)
    {
        if (!IsAura(chars))
            return;
        var enchant = chars.Abilities.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == KeywordKind.Enchant);

        // The object half (CR 601.2c with 702.5b): the object an Aura is cast at must be on the
        // battlefield and be what its enchant ability admits, read the way the resolution reads it
        // (AuraAttachment), so a spell with no legal target is refused here rather than cast and
        // countered on resolution by CR 608.2b.
        var targets = command.Targets ?? [];
        if (targets.Count > 0)
        {
            var host = targets[0];
            if (!state.Objects.TryGetValue(host, out var target) || target.Zone.Zone != CardZone.Battlefield)
                throw new RefusedException("An Aura is cast at a permanent on the battlefield.", "601.2c");
            if (enchant?.Of is { } filter && !query.Matches(state, filter, host, CostPayment.Context(command.Card, command.Player, command.X)))
                throw new RefusedException("That Aura cannot enchant that permanent.", "601.2c");
        }

        var players = command.TargetPlayers ?? [];
        if (players.Count == 0)
            return;
        if (enchant is { Of: not null, OfPlayer: null })
            throw new RefusedException("That Aura cannot enchant a player.", "601.2c");

        // CR 702.16c with 702.16j: a player with protection can't be enchanted by an Aura with the
        // stated quality, and protection from everything states every quality. The announcement is
        // where that is answered — an Aura cast at such a player has no legal target (CR 601.2c),
        // so it is refused here rather than cast and buried by CR 704.5m on the settle.
        if (characteristics.PlayerRestrictions(state, players[0]).CantBeEnchanted)
            throw new RefusedException("That player cannot be enchanted.", "702.16c");
    }

    /// <summary>
    /// CR 115.6 with 601.2c and 702.16j: a player the spell's controller may not target is not a
    /// legal choice, so naming one is refused as the spell is announced. The two spellings are read
    /// differently on purpose. <see cref="PlayerRestriction.BeTargeted"/> is the card-text form,
    /// "by spells or abilities your opponents control", so it is read from the caster's point of
    /// view and a player may still name themselves; CR 702.16j's protection from everything carries
    /// no such qualifier and no source targets through it.
    /// <para>
    /// CR 115.10: the opponent a gift is promised to is not a target. That promise is carried on
    /// <see cref="CastSpell.TargetPlayers"/> because the command has no other channel for it
    /// (CR 702.174b), so the player the plan read it as is skipped rather than refused.
    /// </para>
    /// </summary>
    [RuleRef("115.6")]
    private void RequireLegalPlayerTargets(GameState state, CastSpell command, CastPlan plan)
    {
        foreach (var player in command.TargetPlayers ?? [])
        {
            if (player == plan.GiftPromisedTo)
                continue;

            var limits = characteristics.PlayerRestrictions(state, player);
            if (limits.ProtectionFromEverything || (limits.Has(PlayerRestriction.BeTargeted) && player != command.Player))
                throw new RefusedException("That player cannot be targeted.", "115.6");
        }
    }

    /// <summary>
    /// CR 608.2g with 601.3a: the refusal an instructed cast would be given now, which is
    /// <see cref="Check"/> without the two things the instruction itself grants — the permission
    /// for the zone the card is in and CR 117.1a's timing. Every other refusal still stands, so a
    /// cost that has not been named for still comes back by its own rule (CR 601.2c) and the
    /// caller can ask for it instead of dropping the cast.
    /// <para>
    /// Not on <see cref="ICastingSystem"/> yet: <c>seam-requests/E3i.md</c> asks for it, and until
    /// it lands the interpreter's pre-flight is <see cref="Check"/>, which refuses a graveyard
    /// may-cast for the zone before this road is ever reached.
    /// </para>
    /// </summary>
    [RuleRef("608.2g")]
    public Refusal? CheckInstructed(GameState state, CastSpell command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            _ = CastInstructed(state, command);
            return null;
        }
        catch (RefusedException refused)
        {
            return new Refusal(refused.Reason, refused.Rule);
        }
    }

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

    /// <summary>
    /// CR 106.6a: mana spent paying for a spell or an ability was spent on that object, which is
    /// what "when that mana is spent to cast a spell" asks about. A spell is the object on the
    /// stack (CR 400.7), so a cast's payments learn the id only once the move has made it; mana a
    /// payment added rather than spent (a mana ability's own production) keeps no such object.
    /// </summary>
    [RuleRef("106.6a")]
    private static ImmutableList<GameEvent> SpentOn(IEnumerable<GameEvent> events, ObjectId on) =>
        [.. events.Select(e => e is ManaChanged { SpentOn: null } m && m.Delta.Total < 0 ? m with { SpentOn = on } : e)];

    /// <summary>
    /// CR 106.6a: the provenance blocks that actually paid, as the stack entry records them. A
    /// spend reports its blocks negative because the same list empties the pool
    /// (<see cref="ManaSources.Spend"/>); what was spent on the spell is a positive quantity, so
    /// the sign is turned here and nowhere else.
    /// </summary>
    [RuleRef("106.6a")]
    private static ImmutableList<ManaProvenance> ProvenanceSpent(IEnumerable<GameEvent> events) =>
    [
        .. events.OfType<ManaChanged>()
            .SelectMany(m => m.Delta.Provenance)
            .Where(b => b.Amount < 0)
            .Select(b => b with { Amount = -b.Amount }),
    ];
}
