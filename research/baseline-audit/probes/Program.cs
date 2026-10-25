using System.Collections.Immutable;
using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;

var results = new List<ProbeResult>();
var p0 = new PlayerId(0);
var p1 = new PlayerId(1);
var systems = Subsystems.Wave2();
var game = new Game(systems, new EmptyStore());

// These audit probes intentionally keep the real subsystem composition. They supplement,
// rather than change, the production suite and its established regression expectations.

void Probe(string id, string rule, Func<(bool Passed, string Observed)> run)
{
    try
    { var r = run(); results.Add(new(id, rule, r.Passed, r.Observed)); }
    catch (Exception ex) { results.Add(new(id, rule, false, "PROBE ERROR: " + ex.GetType().Name + ": " + ex.Message)); }
}

GameState Table(Step step = Step.PrecombatMain) => new()
{
    Players = [new(p0, "One", 20), new(p1, "Two", 20)],
    Objects = ImmutableDictionary<ObjectId, GameObject>.Empty,
    Zones = ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>>.Empty,
    Turn = new(1, p0, step, 0),
    Priority = new(p0, []),
    Format = "test",
};

CardDefinition Definition(string id, CardType type, params Ability[] abilities) =>
    new(id, id, Layout.Normal, [new Face(id, FaceRole.Main, new(type), ManaCost: "{0}",
        Power: type.HasFlag(CardType.Creature) ? new("5") : null,
        Toughness: type.HasFlag(CardType.Creature) ? new("10") : null, Abilities: abilities)]);

(GameState State, ObjectId Id) Add(GameState s, CardDefinition d, Zone zone, PlayerId owner)
{
    var id = new ObjectId(s.NextObjectId);
    var key = zone == Zone.Stack ? StackZone.Key : ZoneKey.Of(zone, owner);
    var obj = new GameObject(id, d, 0, owner, owner, key, s.NextTimestamp) { SummoningSick = false };
    return (s with
    {
        Objects = s.Objects.Add(id, obj),
        Zones = s.Zones.SetItem(key, s.In(key).Add(id)),
        NextObjectId = s.NextObjectId + 1,
        NextTimestamp = s.NextTimestamp + 1
    }, id);
}

GameState Apply(GameState s, Command c) => game.Apply(s, c) switch
{
    Applied a => a.State,
    Refused r => throw new InvalidOperationException("Refused " + c.GetType().Name + ": " + r.Reason),
    _ => throw new InvalidOperationException("Unknown result"),
};

Probe("ordinary_spell_waits_for_both_players", "117.4;608.2", () =>
{
    var (s, id) = Add(Table(), Definition("probe-basic", CardType.Instant, new SpellAbility(new GainLife(new Fixed(1)))), Zone.Hand, p0);
    s = Apply(s, new CastSpell(p0, id));
    var waiting = s.In(StackZone.Key).Count == 1 && s.Player(p0).Life == 20;
    s = Apply(s, new PassPriority(p0));
    waiting &= s.In(StackZone.Key).Count == 1 && s.Player(p0).Life == 20;
    s = Apply(s, new PassPriority(p1));
    return (waiting && s.In(StackZone.Key).Count == 0 && s.Player(p0).Life == 21,
        $"Waited through first pass {waiting}; final stack {s.In(StackZone.Key).Count}; controller life {s.Player(p0).Life}");
});

Probe("two_spells_resolve_last_in_first_out_without_response_pass_history", "405.2;117.4", () =>
{
    var (s, a) = Add(Table(), Definition("probe-bottom", CardType.Instant, new SpellAbility(new GainLife(new Fixed(1)))), Zone.Hand, p0);
    var (s2, b) = Add(s, Definition("probe-top", CardType.Instant, new SpellAbility(new GainLife(new Fixed(2)))), Zone.Hand, p0);
    s = Apply(s2, new CastSpell(p0, a));
    s = Apply(s, new CastSpell(p0, b));
    s = Apply(s, new PassPriority(p0));
    s = Apply(s, new PassPriority(p1));
    return (s.In(StackZone.Key).Count == 1 && s.Player(p0).Life == 22,
        $"After both pass: stack {s.In(StackZone.Key).Count}; controller life {s.Player(p0).Life}; expected top spell only");
});

Probe("stack_lifo_and_response_resets_passes", "117.4;405.2", () =>
{
    var (s, a) = Add(Table(), Definition("probe-a", CardType.Instant, new SpellAbility(new GainLife(new Fixed(1)))), Zone.Hand, p0);
    var (s2, b) = Add(s, Definition("probe-b", CardType.Instant, new SpellAbility(new GainLife(new Fixed(2)))), Zone.Hand, p1);
    s = Apply(s2, new CastSpell(p0, a));
    s = Apply(s, new PassPriority(p0));
    s = Apply(s, new CastSpell(p1, b));
    s = Apply(s, new PassPriority(p1));
    var beforeLastPass = s.In(StackZone.Key).Count;
    s = Apply(s, new PassPriority(p0));
    return (beforeLastPass == 2 && s.In(StackZone.Key).Count == 1 && s.Player(p1).Life == 22 && s.Player(p0).Life == 20,
        $"Before final pass: {beforeLastPass} objects; afterward {s.In(StackZone.Key).Count}; life {s.Player(p0).Life}/{s.Player(p1).Life}");
});

Probe("mana_ability_resets_previous_passes", "117.3c;117.4;605.3", () =>
{
    var (s, land) = Add(Table(), Definition("probe-mana", CardType.Land,
        new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))), Zone.Battlefield, p1);
    s = Apply(s, new PassPriority(p0));
    s = Apply(s, new ActivateAbility(p1, land, 0));
    var stackEmpty = s.In(StackZone.Key).Count == 0;
    s = Apply(s, new PassPriority(p1));
    return (stackEmpty && s.Turn.Step == Step.PrecombatMain && s.Priority.Holder == p0,
        $"Stack empty {stackEmpty}; step {s.Turn.Step}; priority {s.Priority.Holder}; green {s.Player(p1).Mana.Green}");
});

Probe("modal_spell_requires_modes_before_stack", "601.2b;700.2", () =>
{
    var effect = new ChooseMode([new Mode("gain one", new GainLife(new Fixed(1))), new Mode("gain two", new GainLife(new Fixed(2)))]);
    var (s, id) = Add(Table(), Definition("probe-modal", CardType.Instant, new SpellAbility(effect)), Zone.Hand, p0);
    var r = game.Apply(s, new CastSpell(p0, id));
    return (r is Refused || r is Applied { State.Pending: not null },
        r is Applied a ? $"Accepted; stack {a.State.In(StackZone.Key).Count}; pending {a.State.Pending?.Kind ?? "none"}; announced modes {a.State.Objects.Values.FirstOrDefault(o => o.Stack is not null)?.Stack?.Modes.Count}" : "Refused missing modes");
});

Probe("legal_offer_includes_required_target_spell", "601.2c;117.1a", () =>
{
    var (s, victim) = Add(Table(), Definition("probe-victim", CardType.Creature), Zone.Battlefield, p1);
    var (s2, spell) = Add(s, Definition("probe-target", CardType.Instant,
        new SpellAbility(new DealDamage(new Fixed(1), new Target(new ObjectFilter(AnyTypes: CardType.Creature))))), Zone.Hand, p0);
    var offered = systems.Legal.For(s2, p0).Castable.Contains(spell);
    var executable = game.Apply(s2, new CastSpell(p0, spell, Targets: [victim])) is Applied;
    return (!executable || offered, $"Explicit targeted command accepted {executable}; offer lists spell {offered}");
});

Probe("trample_after_all_blockers_leave", "702.19;510.1c", () =>
{
    var (s, attacker) = Add(Table(Step.CombatDamage), Definition("probe-trample", CardType.Creature, new KeywordAbility(KeywordKind.Trample)), Zone.Battlefield, p0);
    s = s with
    {
        Combat = new(p0, ImmutableDictionary<ObjectId, CombatTarget>.Empty.Add(attacker, new(p1, null)),
        ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty.Add(attacker, [new ObjectId(999)]), true, true)
    };
    var o = systems.Combat.DealCombatDamage(s, false);
    return (o.State.Player(p1).Life == 15, $"Defender life {o.State.Player(p1).Life}; expected 15 from unopposed trample");
});

Probe("first_striker_losing_first_strike_does_not_hit_twice", "510.4", () =>
{
    var (s, attacker) = Add(Table(Step.FirstStrikeCombatDamage), Definition("probe-first", CardType.Creature, new KeywordAbility(KeywordKind.FirstStrike)), Zone.Battlefield, p0);
    var (s2, blocker) = Add(s, Definition("probe-blocker", CardType.Creature), Zone.Battlefield, p1);
    s = s2 with
    {
        Combat = new(p0, ImmutableDictionary<ObjectId, CombatTarget>.Empty.Add(attacker, new(p1, null)),
        ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty.Add(attacker, [blocker]), true, true)
    };
    var first = systems.Combat.DealCombatDamage(s, true);
    // Isolate the loss of first strike between damage steps; the mutation substitutes for
    // the equivalent continuous-effect expiry, while calling the real combat subsystem.
    var changed = first.State.Objects[attacker] with { Definition = Definition("probe-first", CardType.Creature) };
    s = first.State with { Objects = first.State.Objects.SetItem(attacker, changed), Turn = first.State.Turn with { Step = Step.CombatDamage } };
    var second = systems.Combat.DealCombatDamage(s, false);
    var again = second.Events.OfType<DamageDealt>().Where(d => d.Source == attacker).Sum(d => d.Amount);
    return (again == 0, $"Attacker assigned {again} damage in second step after losing first strike");
});

Probe("once_per_turn_trigger_stays_spent_after_countering", "603.2h", () =>
{
    var watcher = Definition("probe-once", CardType.Creature,
        new TriggeredAbility(new MtgEngine.Cards.SpellCast(new ObjectFilter(AnyTypes: CardType.Instant, Spell: true), new You()),
            new GainLife(new Fixed(1)), OncePerTurn: true));
    var (s, source) = Add(Table(), watcher, Zone.Battlefield, p0);
    var (s2, first) = Add(s, Definition("probe-cast-one", CardType.Instant, new SpellAbility(new GainLife(new Fixed(1)))), Zone.Hand, p0);
    var (s3, second) = Add(s2, Definition("probe-cast-two", CardType.Instant, new SpellAbility(new GainLife(new Fixed(1)))), Zone.Hand, p0);
    s = Apply(s3, new CastSpell(p0, first));
    var trigger = StackZone.Top(s) ?? throw new InvalidOperationException("Expected a trigger");
    if (s.Objects[trigger].Stack?.Source != source)
        throw new InvalidOperationException("Expected the once-per-turn trigger on top");
    // Isolate removal of a countered trigger without resolving it. The source remains,
    // and no resolution tally should be required to remember that it already triggered.
    s = new ObjectCeasedToExist(trigger).Apply(s);
    var r = game.Apply(s, new CastSpell(p0, second));
    if (r is not Applied applied)
        throw new InvalidOperationException("Second cast unexpectedly refused");
    var newFirings = applied.Events.OfType<MtgEngine.Rules.Triggers.TriggerFired>().Count();
    return (newFirings == 0, $"New firings after first trigger ceased without resolving: {newFirings}");
});

Probe("cleanup_trigger_gets_priority_before_next_turn", "514.3a", () =>
{
    var (s, _) = Add(Table(Step.End), Definition("probe-cleanup", CardType.Creature,
        new TriggeredAbility(new AtStep(Step.Cleanup, new You()), new GainLife(new Fixed(1)))), Zone.Battlefield, p0);
    s = Apply(s, new PassPriority(p0));
    s = Apply(s, new PassPriority(p1));
    return (s.Turn.Number == 1 && s.Turn.Step == Step.Cleanup && s.In(StackZone.Key).Count > 0,
        $"After end-step passes: turn {s.Turn.Number}, step {s.Turn.Step}, stack {s.In(StackZone.Key).Count}");
});

Probe("starting_player_skips_entire_first_draw_step", "103.8a", () =>
{
    var s = Apply(Table(Step.Upkeep), new PassPriority(p0));
    s = Apply(s, new PassPriority(p1));
    return (s.Turn.Step == Step.PrecombatMain, $"After first upkeep: {s.Turn.Step}; expected PrecombatMain without draw-step priority");
});

Probe("empty_combat_skips_blockers_and_damage", "508.8;506.1", () =>
{
    var s = Table(Step.DeclareAttackers);
    s = Apply(s, new DeclareAttackers(p0, []));
    s = Apply(s, new PassPriority(p0));
    s = Apply(s, new PassPriority(p1));
    return (s.Turn.Step == Step.EndOfCombat, $"After empty attacker declaration and passes: {s.Turn.Step}; expected EndOfCombat");
});

var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (args.Length > 0) File.WriteAllText(args[0], json);
return results.Any(r => r.Observed.StartsWith("PROBE ERROR", StringComparison.Ordinal)) ? 2 : 0;

internal sealed record ProbeResult(string Id, string Rules, bool Passed, string Observed);
internal sealed class EmptyStore : IDefinitionStore
{
    public CardDefinition? ByName(string name) => null;
    public CardDefinition? ByOracleId(string oracleId) => null;
}
