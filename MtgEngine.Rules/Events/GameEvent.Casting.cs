using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

// What a player cast, activated, played and resolved. Split out of GameEvent.cs when that file
// reached the 500-line ceiling tools/check-file-size.js enforces.
[RuleRef("601.2i")]
public sealed record SpellCast(ObjectId Spell, PlayerId Caster, Zone From, int? Permission = null, StaticPlayPermission? StaticPermission = null) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var next = state with
        {
            History = state.History with { SpellsCast = state.History.SpellsCast.Add(new SpellCastRecord(Spell, Caster, From, Permission, StaticPermission)) },
        };
        if (From != Zone.Command || !state.Objects.TryGetValue(Spell, out var card) || !card.IsCommander)
            return next;

        var key = (CommanderIdentity.Of(card), Caster);
        return next with { CommanderCasts = state.CommanderCasts.SetItem(key, state.CommandZoneCasts(card, Caster) + 1) };
    }
}

/// <summary><paramref name="StackObject"/> is null for a mana ability, which does not use the stack, CR 605.3a.</summary>
[RuleRef("602.2")]
public sealed record AbilityActivated(ObjectId? StackObject, ObjectId Source, int AbilityIndex, PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) => state;
}

[RuleRef("305")]
public sealed record LandPlayed(ObjectId Land, PlayerId Player, Zone From = Zone.Hand, int? Permission = null, StaticPlayPermission? StaticPermission = null) : GameEvent
{
    /// <summary>
    /// The turn remembers the land play as well as counting it. Only the count was kept, and a
    /// land play is not a cast, so a land played under a once-per-turn permission (CR 116.2a)
    /// left no trace the permission could find and the permission granted a second one.
    /// </summary>
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        return state with
        {
            Players = state.Players.SetItem(Player.Value, p with { LandsPlayedThisTurn = p.LandsPlayedThisTurn + 1 }),
            History = state.History with
            {
                LandsPlayed = state.History.LandsPlayed.Add(new LandPlayRecord(Land, Player, From, Permission, StaticPermission)),
            },
        };
    }
}

[RuleRef("608.2")]
public sealed record StackObjectResolved(ObjectId StackObject) : GameEvent
{
    /// <summary>The object is still on the stack (FinishResolving does the leaving, CR 608.2m), so its entry names the source and ability this resolution counts for: "the first time this turn" and once-a-turn abilities read the tally.</summary>
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(StackObject, out var obj) || obj.Stack is not { } entry)
            return state;
        var key = (entry.Source ?? obj.Id, entry.AbilityIndex ?? -1);
        var tally = state.History.AbilitiesResolvedThisTurn;
        return state with
        {
            History = state.History with { AbilitiesResolvedThisTurn = tally.SetItem(key, tally.GetValueOrDefault(key) + 1) },
        };
    }
}
