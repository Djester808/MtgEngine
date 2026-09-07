using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The two collapses wave 5 added, and what each one refuses to touch. Both close a spelling the
/// pilot found the models split on, and both are only honest while the negative half holds: a
/// collapse that fires one step wider than the phrase it is named after decides a disagreement
/// instead of removing one. Every negative here is a card that means something else.
/// </summary>
public sealed partial class CanonicalTests
{
    /// <summary>Canonicalising a canonical reading changes nothing: the pass is a normal form, not a step.</summary>
    private static void StaysCanonical(CardDefinition definition)
    {
        var once = CardFileSerializer.WriteDefinition(Canonical.Of(definition));
        var twice = CardFileSerializer.WriteDefinition(Canonical.Of(Canonical.Of(definition)));
        Assert.Equal(once, twice);
    }

    private static ObjectFilter AnyTargetTypes =>
        new(AnyTypes: CardType.Creature | CardType.Planeswalker | CardType.Battle);

    private static CardDefinition Bolt(Selector to, PlayerRef? toPlayer = null) =>
        Spell(new DealDamage(new Fixed(3), to, toPlayer));

    [Fact]
    public void The_two_ways_of_writing_any_target_both_become_the_selector()
    {
        var said = Bolt(new AnyTarget());

        // CR 115.4 as it reads today: creature, player, planeswalker or battle.
        Same(Bolt(new Target(AnyTargetTypes), new TargetPlayer(PlayerFilter.Any)), said);

        // The same phrase as it was printed before battles were a permanent type.
        Same(Bolt(new Target(new ObjectFilter(AnyTypes: CardType.Creature | CardType.Planeswalker)), new TargetPlayer(PlayerFilter.Any)), said);

        var collapsed = (DealDamage)Do(Canonical.Of(Bolt(new Target(AnyTargetTypes), new TargetPlayer(PlayerFilter.Any))));
        Assert.IsType<AnyTarget>(collapsed.To);
        Assert.Null(collapsed.ToPlayer);
        StaysCanonical(Bolt(new Target(AnyTargetTypes), new TargetPlayer(PlayerFilter.Any)));
        StaysCanonical(said);
    }

    [Fact]
    public void The_count_the_up_to_and_the_key_travel_onto_the_selector()
    {
        var written = Bolt(
            new Target(AnyTargetTypes, Count: new Fixed(2), UpTo: true, Key: "hit"),
            new TargetPlayer(PlayerFilter.Any));

        var any = Assert.IsType<AnyTarget>(((DealDamage)Do(Canonical.Of(written))).To);
        Assert.Equal(2, Assert.IsType<Fixed>(any.Count).N);
        Assert.True(any.UpTo);
        Assert.Equal("k1", any.Key); // keys are renumbered by the first pass; the selector keeps one.

        Same(written, Bolt(new AnyTarget(new Fixed(2), UpTo: true, Key: "any"), null));
    }

    [Fact]
    public void A_reading_that_already_says_any_target_loses_the_repeated_player_half()
    {
        // AnyTarget carries the player, so a toPlayer beside it is the phrase written twice.
        Same(Bolt(new AnyTarget(), new TargetPlayer(PlayerFilter.Any)), Bolt(new AnyTarget()));
        StaysCanonical(Bolt(new AnyTarget(), new TargetPlayer(PlayerFilter.Any)));
    }

    [Fact]
    public void Damage_that_is_not_any_target_is_left_exactly_as_written()
    {
        var said = Bolt(new AnyTarget());

        // Only creatures, and only a player: each is one half of the phrase and means less than it.
        Differs(Bolt(new Target(ObjectFilter.Creature)), said);
        Differs(Bolt(new Target(AnyTargetTypes)), said);
        Differs(Spell(new DealDamage(new Fixed(3), ToPlayer: new TargetPlayer(PlayerFilter.Any))), said);

        // An open filter beside a player target reaches a land too.
        Differs(Bolt(new Target(ObjectFilter.Any), new TargetPlayer(PlayerFilter.Any)), said);

        // A filter that says one more thing about the permanent is not "any target".
        Differs(Bolt(new Target(AnyTargetTypes with { Tapped = true }), new TargetPlayer(PlayerFilter.Any)), said);

        // "Any target an opponent controls" is not a phrase; a restricted player half is a real choice.
        Differs(Bolt(new Target(AnyTargetTypes), new TargetPlayer(new PlayerFilter(Opponent: true))), said);

        // Damage divided among targets that may be the same object twice has nowhere to keep that.
        Differs(Bolt(new Target(AnyTargetTypes, Count: new Fixed(2), Distinct: false), new TargetPlayer(PlayerFilter.Any)), said);

        // And the player half must be a target, not "each opponent".
        Differs(Bolt(new Target(AnyTargetTypes), new EachOpponent()), said);
    }

    [Fact]
    public void Can_t_lose_the_game_is_the_record_that_says_it()
    {
        Same(
            Card(new StaticAbility(new PlayerCant(PlayerRestriction.Lose), Players: new You())),
            Card(new StaticAbility(new CantLoseGame(new You()), Players: new You())));

        // Absent means you, the controller of the ability, as it does everywhere else in this pass.
        var collapsed = Card(new StaticAbility(new PlayerCant(PlayerRestriction.Lose)));
        var effect = Assert.IsType<CantLoseGame>(Assert.IsType<StaticAbility>(Canonical.Of(collapsed).Faces[0].AbilityList[0]).Effect);
        Assert.IsType<You>(effect.Who);
        StaysCanonical(collapsed);

        // The same for a duration, where the player is the one ApplyToPlayer names.
        Same(
            Spell(new ApplyToPlayer(new PlayerCant(PlayerRestriction.Lose), new EachOpponent(), new UntilEndOfTurn())),
            Spell(new ApplyToPlayer(new CantLoseGame(new EachOpponent()), new EachOpponent(), new UntilEndOfTurn())));
    }

    [Fact]
    public void A_restriction_no_single_record_says_stays_a_player_cant()
    {
        // CR 119.1: "your life total can't change" forbids losing life and setting it too, so it is
        // not CantGainLife. PlayerRestriction has no Win or GainLife member for the same reason —
        // nothing in it says only those — so those two records have nothing to collapse from.
        Differs(
            Card(new StaticAbility(new PlayerCant(PlayerRestriction.LifeTotalChanges), Players: new You())),
            Card(new StaticAbility(new CantGainLife(new You()), Players: new You())));

        var kept = Card(new StaticAbility(new PlayerCant(PlayerRestriction.BeTargeted), Players: new You()));
        Assert.IsType<PlayerCant>(Assert.IsType<StaticAbility>(Canonical.Of(kept).Faces[0].AbilityList[0]).Effect);
        StaysCanonical(kept);

        // Several flags are one effect that no single record says; it stays whole.
        var both = Card(new StaticAbility(new PlayerCant(PlayerRestriction.Lose | PlayerRestriction.BeTargeted), Players: new You()));
        var effect = Assert.IsType<PlayerCant>(Assert.IsType<StaticAbility>(Canonical.Of(both).Faces[0].AbilityList[0]).Effect);
        Assert.Equal(PlayerRestriction.Lose | PlayerRestriction.BeTargeted, effect.What);
        Differs(both, Card(new StaticAbility(new CantLoseGame(new You()), Players: new You())));
    }
}
