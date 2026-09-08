using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 26: the edict every player answers (CR 701.16 with 102.1), what a destruction actually
/// destroyed (CR 701.8), the player a token enters attacking (CR 508.4) and the permission a
/// continuous effect grants a player (CR 702.8).
/// </summary>
public sealed partial class InterpreterTests
{
    private static readonly ObjectFilter Wave26Creatures = new(Types: CardType.Creature);

    [Fact]
    [RuleRef("701.16")]
    public void An_edict_on_each_player_asks_every_player_and_moves_every_answer()
    {
        var table = new InterpreterTable();
        var mine = table.Permanent(table.A);
        var theirs = table.Permanent(table.B);
        var edict = table.OnStack(table.A, new Sacrifice(Choice: Wave26Creatures, Who: new EachPlayer()));

        table.Resolve(edict);

        // CR 101.4: the active player is asked first and the walk stops on their question.
        Assert.Equal(table.A, table.Pending.Player);
        table.Answer(mine.Value.ToString(CultureInfo.InvariantCulture));

        // CR 701.16 with 102.1: "each player sacrifices" reaches the other player too, and it is
        // their own choice — before this wave the walk asked the caster alone and the opponent's
        // permanent never moved (seam-requests/P24.md § 1).
        Assert.Equal(table.B, table.Pending.Player);
        table.Answer(theirs.Value.ToString(CultureInfo.InvariantCulture));

        Assert.Empty(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Single(table.In(Zone.Graveyard, table.A));
        Assert.Single(table.In(Zone.Graveyard, table.B));
    }

    [Fact]
    [RuleRef("701.16")]
    public void An_edict_skips_a_player_who_controls_nothing_it_admits()
    {
        var table = new InterpreterTable();
        var theirs = table.Permanent(table.B);
        var edict = table.OnStack(table.A, new Sacrifice(Choice: Wave26Creatures, Who: new EachPlayer()));

        table.Resolve(edict);

        // CR 701.16b with 101.4: the caster controls no creature, so they are not asked a question
        // no answer could satisfy; the walk goes straight on to the player who does.
        Assert.Equal(table.B, table.Pending.Player);
        table.Answer(theirs.Value.ToString(CultureInfo.InvariantCulture));
        Assert.Single(table.In(Zone.Graveyard, table.B));
    }

    [Fact]
    [RuleRef("701.8")]
    public void A_destruction_remembers_what_it_destroyed_and_not_what_it_named()
    {
        var table = new InterpreterTable();
        var soft = table.Permanent(table.A);
        var hard = table.Permanent(table.A, keywords: [KeywordKind.Indestructible]);

        // "Destroy all creatures. Exile each creature destroyed this way": the second step reads the
        // key, so what is in it is what moved.
        table.Run(new Sequence(
        [
            new Destroy(new Each(Wave26Creatures), RememberAs: "d"),
            new Cards.Exile(new Remembered("d")),
        ]));

        // CR 701.8b: the indestructible creature is not destroyed and so is not in the key — the
        // exile finds one card, the one the destruction actually put in a graveyard.
        Assert.Equal(hard, Assert.Single(table.State.In(ZoneKey.Shared(Zone.Battlefield))));
        Assert.Empty(table.In(Zone.Graveyard, table.A));
        var exiled = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Exile)));
        Assert.NotEqual(soft, exiled);
    }

    [Fact]
    [RuleRef("508.4")]
    public void A_token_created_attacking_a_named_player_attacks_that_player()
    {
        var table = new InterpreterTable();

        // Combat is already happening and the declared attacker is attacking the caster: the token
        // must not simply copy that, which is what "attacking" alone would give it (CR 508.1).
        var theirs = table.Permanent(table.B);
        table.Declaring(table.B, table.A, theirs);

        table.Run(new CreateToken(
            Wave26Token,
            Attacking: true,
            AttackingWhom: new EachOpponent(),
            RememberAs: "t"));

        var combat = table.State.Combat ?? throw new InvalidOperationException("no combat");
        var token = Assert.Single(combat.Attackers, a => a.Key != theirs);
        Assert.Equal(table.B, token.Value.Player);
        Assert.Null(token.Value.Permanent);

        // CR 508.4 with 506.3c: it was never declared, so the player who declared the attack is
        // still the one the combat records.
        Assert.Equal(table.B, combat.AttackingPlayer);
    }

    [Fact]
    [RuleRef("508.4")]
    public void A_token_attacking_a_player_who_could_not_be_attacked_is_refused()
    {
        var table = new InterpreterTable();
        var theirs = table.Permanent(table.B);
        table.Declaring(table.B, table.A, theirs);

        // CR 506.2: the token's own controller is never a defending player, so the reference names
        // nobody it could enter attacking, and that is refused with the rule and not played wrong.
        var refused = Assert.Throws<RefusedException>(() => table.Run(new CreateToken(
            Wave26Token,
            Attacking: true,
            AttackingWhom: new You())));
        Assert.Equal("508.4", refused.Rule);
    }

    [Fact]
    [RuleRef("702.8")]
    public void A_flash_permission_is_granted_to_the_player_the_effect_names()
    {
        var table = new InterpreterTable();

        // CR 702.8: the permission names its own player, so it reaches the opponent even though the
        // Apply record around it names objects — there is no object in a hand for it to reach.
        var events = table.Run(new Cards.Apply(
            new MayCastAsThoughFlash(new EachOpponent(), new ObjectFilter(Types: CardType.Creature)),
            new Each(Wave26Creatures),
            new Permanently()));

        var created = Assert.Single(events.OfType<ContinuousEffectCreated>()).Effect;
        Assert.Equal(table.B, Assert.Single(created.AppliesToPlayers));
        Assert.Empty(created.AppliesTo);
    }

    /// <summary>A one-face token to create: the spec is rules data, not a card (CR 111.2).</summary>
    private static TokenSpec Wave26Token => new("engine test token", Color.Colorless, CardType.Creature, [], Supertype.None, 1, 1);
}
