using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A player's own limits, CR 613.10, read where the replacement system still only has a proposed
/// event: damage to a player who can't be dealt damage (CR 120.3, 702.16j) and a life change a
/// player's life total forbids (CR 119.3) never happen at all.
/// <para>
/// Everything here runs against the real <see cref="LayerSystem"/>, so the limit is computed from
/// a static ability on the battlefield exactly as a game would compute it, and — for the life
/// tests — against the real interpreter, so the dropped event is one the engine itself proposed.
/// Every definition is invented: no card name reaches the engine or its tests.
/// </para>
/// </summary>
public sealed class PlayerLimitReplacementTests
{
    /// <summary>
    /// CR 119.3 with 101.2: "your life total can't change" stops the gain the interpreter's
    /// <see cref="GainLife"/> proposed, so no <see cref="LifeChanged"/> is logged and the total
    /// stands. The unrestricted player, running the same effect, gains.
    /// </summary>
    [Fact]
    public void A_gain_from_the_interpreter_is_dropped_while_a_life_total_cant_change()
    {
        var board = new Board();
        board.Put(Static("frozen ledger", new PlayerCant(PlayerRestriction.LifeTotalChanges)));
        var engine = new Wiring();

        var restricted = engine.Interpreter.Run(board.State, new GainLife(new Fixed(3)), Context(0));

        Assert.DoesNotContain(restricted.Events, e => e is LifeChanged);
        Assert.Equal(20, restricted.State.Player(new PlayerId(0)).Life);

        var free = engine.Interpreter.Run(board.State, new GainLife(new Fixed(3)), Context(1));

        Assert.Equal(3, Assert.IsType<LifeChanged>(Assert.Single(free.Events)).Delta);
        Assert.Equal(23, free.State.Player(new PlayerId(1)).Life);
    }

    /// <summary>
    /// CR 120.3: damage that would be dealt to a player who can't be dealt damage is prevented.
    /// The static names its controller only, so the other player is still dealt their damage —
    /// the limit is per player, not a global switch.
    /// </summary>
    [Fact]
    public void Damage_to_a_player_who_cant_be_dealt_damage_is_dropped_and_the_other_player_is_not()
    {
        var board = new Board();
        board.Put(Static("ward of stillness", new PlayerCant(PlayerRestriction.BeDealtDamage)));
        var source = board.Put(Board.Creature(3, 3, "burner"), owner: 1);
        var system = new Wiring().Replacements;

        var warded = system.Replace(board.State, Damage(source, 0, 3), new PlayerId(0));

        Assert.Empty(warded.Events);
        Assert.Equal(20, warded.State.Player(new PlayerId(0)).Life);

        var other = system.Replace(board.State, Damage(source, 1, 3), new PlayerId(1));

        Assert.Equal(3, Assert.IsType<DamageDealt>(Assert.Single(other.Events)).Amount);
        Assert.Equal(17, other.State.Player(new PlayerId(1)).Life);
    }

    /// <summary>
    /// CR 702.16j: "all damage that would be dealt to such a player is prevented". The player-scoped
    /// protection is not an object's keyword, so it cannot reach the loop as card text; it has to
    /// be the same reading as the flag, which is what <see cref="PlayerLimits.CantBeDealtDamage"/>
    /// makes it.
    /// </summary>
    [Fact]
    public void Protection_from_everything_prevents_all_damage_to_that_player()
    {
        var board = new Board();
        board.Put(Static("aegis of nothing", new PlayerProtectionFromEverything()));
        var source = board.Put(Board.Creature(9, 9, "burner"), owner: 1);

        var outcome = new Wiring().Replacements.Replace(board.State, Damage(source, 0, 9), new PlayerId(0));

        Assert.Empty(outcome.Events);
        Assert.Equal(20, outcome.State.Player(new PlayerId(0)).Life);
    }

    /// <summary>
    /// CR 119.3 read as a prohibition: a total that can't change can't fall either, so a loss is
    /// dropped as readily as a gain.
    /// </summary>
    [Fact]
    public void A_life_total_that_cant_change_stops_a_loss_as_well_as_a_gain()
    {
        var board = new Board();
        board.Put(Static("frozen ledger", new PlayerCant(PlayerRestriction.LifeTotalChanges)));
        var system = new Wiring().Replacements;

        var lost = system.Replace(board.State, new LifeChanged(new PlayerId(0), -4, "test"), new PlayerId(0));

        Assert.Empty(lost.Events);
        Assert.Equal(20, lost.State.Player(new PlayerId(0)).Life);

        var gained = system.Replace(board.State, new LifeChanged(new PlayerId(0), 4, "test"), new PlayerId(0));

        Assert.Empty(gained.Events);
        Assert.Equal(20, gained.State.Player(new PlayerId(0)).Life);
    }

    /// <summary>
    /// The weaker prohibition: "you can't gain life" stops only an increase (CR 119.3), and life
    /// loss goes on happening. This is the pair that would collapse into one reading if the drop
    /// were written on the sign of the delta alone.
    /// </summary>
    [Fact]
    public void Cant_gain_life_stops_a_gain_and_lets_a_loss_through()
    {
        var board = new Board();
        board.Put(Static("bitter ledger", new CantGainLife(new You())));
        var system = new Wiring().Replacements;

        var gained = system.Replace(board.State, new LifeChanged(new PlayerId(0), 4, "test"), new PlayerId(0));

        Assert.Empty(gained.Events);
        Assert.Equal(20, gained.State.Player(new PlayerId(0)).Life);

        var lost = system.Replace(board.State, new LifeChanged(new PlayerId(0), -4, "test"), new PlayerId(0));

        Assert.Equal(-4, Assert.IsType<LifeChanged>(Assert.Single(lost.Events)).Delta);
        Assert.Equal(16, lost.State.Player(new PlayerId(0)).Life);
    }

    /// <summary>
    /// CR 101.2: a "can't" is not a competitor in the ordering of CR 616.1. The event is dropped
    /// before candidates are gathered, so a replacement effect that would otherwise have applied
    /// to it is never offered it and never runs.
    /// </summary>
    [Fact]
    public void A_forbidden_event_is_never_offered_to_a_replacement_effect()
    {
        var board = new Board();
        board.Put(Static("frozen ledger", new PlayerCant(PlayerRestriction.LifeTotalChanges)));
        board.Put(Board.Definition(
            "doubling ledger",
            CardType.Enchantment,
            abilities: [new ReplacementAbility(new WouldGainLife(new You()), new ReplaceWith(new Draw(new Fixed(1)))) { Text = "draw a card instead" }]));

        var watcher = new ReplacementEffectFakeInterpreter();
        var outcome = new Wiring(watcher).Replacements.Replace(board.State, new LifeChanged(new PlayerId(0), 4, "test"), new PlayerId(0));

        Assert.Empty(outcome.Events);
        Assert.Empty(watcher.Ran);
        Assert.Equal(20, outcome.State.Player(new PlayerId(0)).Life);
    }

    /// <summary>
    /// The real subsystems, wired the way a game wires them: the replacement system holds a handle
    /// the interpreter is set on afterwards, because each needs the other (CR 614.1a).
    /// </summary>
    private sealed class Wiring
    {
        public Wiring(IInterpreter? interpreter = null)
        {
            var layers = new LayerSystem();
            var query = new MtgEngine.Rules.Query.Query(layers);
            var handle = new ForwardingInterpreter();
            Replacements = new MtgEngine.Rules.Replacement.ReplacementSystem(query, layers, handle);
            Interpreter = interpreter ?? new EffectInterpreter(query, new MtgEngine.Rules.Zones.ZoneSystem(), Replacements, layers);
            handle.Target = Interpreter;
        }

        public MtgEngine.Rules.Replacement.ReplacementSystem Replacements { get; }

        public IInterpreter Interpreter { get; }
    }

    /// <summary>A permanent whose one static ability restricts the player who controls it.</summary>
    private static CardDefinition Static(string name, ContinuousEffect effect) =>
        Board.Definition(name, CardType.Enchantment, abilities: [new StaticAbility(effect, Players: new You())]);

    private static DamageDealt Damage(ObjectId source, int toPlayer, int amount) =>
        new(source, null, new PlayerId(toPlayer), amount, Combat: false, FromCommander: false);

    private static EffectContext Context(int controller) =>
        new(
            null,
            new PlayerId(controller),
            [],
            [],
            null,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, int>.Empty,
            [],
            null);
}
