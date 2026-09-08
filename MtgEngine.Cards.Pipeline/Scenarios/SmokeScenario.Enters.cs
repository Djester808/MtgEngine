namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// What a permanent's own "when this enters" trigger does, for a play whose whole assertion would
/// otherwise be the zone the card arrived in. A land is played rather than cast (CR 305.9), so the
/// generated scenario for one is a single step — and a land that bounces something as it enters
/// was asserting only half of itself, and the wrong half: the trigger's choice took the land that
/// had just arrived, because it was the only one its controller had.
/// </summary>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 603.6b: a trigger on the card's own arrival goes on the stack as the play resolves, so
    /// what it does is part of what the play shows. Nothing is refused here — a trigger the board
    /// cannot stage leaves the play asserting the arrival, which is all it asserted before there
    /// was a road for any of them.
    /// </summary>
    [RuleRef("603.6b")]
    private static void Enters(SmokeBoard board, Face face)
    {
        foreach (var triggered in face.AbilityList.OfType<TriggeredAbility>())
        {
            if (triggered is not { When: EntersBattlefield { What.Also: IsThisObject, UnderControlOf: null }, If: null, Optional: false, Where: Zone.Battlefield })
                continue;
            foreach (var effect in Flatten(triggered.Do))
            {
                Returns(board, effect);
                Edicts(board, effect);
                Sacrifices(board, effect);
                EachIsAsked(board, effect);
            }
        }
    }

    /// <summary>
    /// CR 400.1 with CR 603.3: "when this enters, return a land you control to its owner's hand".
    /// The choice belongs to the resolution and a generated scenario answers with the first legal
    /// option (<c>ScenarioAnswers</c>), so the board stands a filler of the kind the filter admits
    /// on the caster's battlefield first and the choice takes that rather than the card under test
    /// — which is the difference between a play that shows the bounce and one that reports the
    /// card putting itself back in hand.
    /// </summary>
    [RuleRef("603.3")]
    private static void Returns(SmokeBoard board, Effect effect)
    {
        if (effect is not Move
            {
                What: Choose { Chooser: You, Count: null, UpTo: false, Among: null } chosen,
                To: Zone.Hand,
                Where: Placement.Default,
                UnderControlOf: null,
                Tapped: false,
                FaceDown: false,
                Attacking: false,
                Transformed: false,
                WithCounters: null,
                AttachedTo: null,
            })
        {
            return;
        }

        // The filter has to name what the caster controls: a choice among the opponent's
        // permanents is answered off their battlefield, and a filter that says nothing about a
        // controller would offer both. Its owner's hand is the caster's, so the count reads.
        if (chosen.Filter.ControlledBy is not You || chosen.Filter.OwnedBy is not null
            || SmokeFillers.Bodies(chosen.Filter) is not [var body, ..])
        {
            return;
        }

        board.Expect(new InZone(board.Bystander(mine: true, body), Zone.Hand, Caster));
        board.Caster(hand: 1);
    }

    /// <summary>
    /// CR 701.16 with CR 603.3: "when this enters, each player sacrifices a creature of their
    /// choice". Each player chooses, and a generated scenario answers with the first legal option,
    /// so each battlefield needs a body older than the card under test standing on it — otherwise
    /// the caster's only creature is the one that just arrived and the trigger eats it, which is a
    /// play that reports the board the scenario built rather than what the card does. The bodies
    /// are stood on both sides because the trigger names every player (CR 102.1).
    /// </summary>
    [RuleRef("701.16")]
    private static void Edicts(SmokeBoard board, Effect effect)
    {
        if (effect is not Sacrifice { What: null, Choice: { } offered, Who: EachPlayer, N: null or Fixed { N: 1 } })
            return;

        // The filter has to name a kind the board can stand, and nothing about who controls it: a
        // sacrifice each player makes is made off their own battlefield (CR 701.16a).
        if (offered.ControlledBy is not null || SmokeFillers.Bodies(offered) is not [var body, ..])
            return;

        board.Expect(new InZone(board.Bystander(mine: true, body), Zone.Graveyard, Caster));
        board.Bystander(mine: false, body);
        board.Caster(graveyard: 1);
    }

    /// <summary>
    /// CR 701.21 with CR 603.3: "when this enters, each player sacrifices a creature or
    /// planeswalker of their choice; each player who can't discards a card". The record is a
    /// <see cref="ForEachPlayer"/> around an <see cref="If"/> — every player is asked in turn and
    /// the branch taken depends on what that player controls — rather than the flat
    /// <see cref="Sacrifice"/> over <see cref="EachPlayer"/> that <see cref="Edicts"/> reads, so
    /// the same board is wanted for a shape the earlier arm cannot see. Without it the caster's
    /// only creature is the one that just arrived: the trigger eats the card under test and the
    /// arrival the scenario asserts cannot hold, which is what the play reported.
    /// </summary>
    /// <remarks>
    /// The <see cref="If"/> is not evaluated here. The board stands a body the filter admits on
    /// <em>both</em> battlefields, which is exactly what makes the test true for each player, so
    /// the then-branch is the branch every player takes and the else-branch never runs. A filter
    /// the board cannot stand leaves the trigger unstaged, as every other arm does.
    /// </remarks>
    [RuleRef("701.21")]
    private static void EachIsAsked(SmokeBoard board, Effect effect)
    {
        if (effect is not ForEachPlayer { Over: EachPlayer, Do: var each })
            return;

        var sacrifice = each switch
        {
            If { Then: Sacrifice inner } => inner,
            Sacrifice inner => inner,
            _ => null,
        };

        if (sacrifice is not { What: null, Choice: { } offered, Who: TriggeringPlayer or EachPlayer, N: null or Fixed { N: 1 } })
            return;

        // Each player sacrifices off their own battlefield, so a filter that names a controller
        // describes something only one of them could do and the board says nothing about it.
        if (offered.ControlledBy is not null || SmokeFillers.Bodies(offered) is not [var body, ..])
            return;

        board.Expect(new InZone(board.Bystander(mine: true, body), Zone.Graveyard, Caster));
        board.Bystander(mine: false, body);
        board.Caster(graveyard: 1);
    }

    /// <summary>
    /// CR 701.16 with CR 603.3: "when this enters, sacrifice two lands". The sacrifice is the
    /// controller's own choice on resolution and a generated scenario answers with the first legal
    /// options, which are in object order, so the board stands as many bodies of the kind the
    /// filter admits as the record asks for — placed by the setup, so they are older than the card
    /// under test (CR 400.7) and the choice takes them. Without them the only land its controller
    /// has is the one that just arrived, CR 701.16b makes it sacrifice as many as it can, and the
    /// card eats itself: the play then reports the board the scenario built rather than the card.
    /// The bodies are the caster's alone, because "sacrifice" with no player named is the
    /// controller's (CR 701.16a) — the each-player shape is <see cref="Edicts"/>.
    /// </summary>
    [RuleRef("701.16")]
    private static void Sacrifices(SmokeBoard board, Effect effect)
    {
        if (effect is not Sacrifice { What: null, Choice: { } offered, Who: null or You, N: Fixed { N: > 0 and var n } })
            return;

        // A filter that names someone else's permanents describes a sacrifice its controller cannot
        // make, and one the board has no card for is not staged at all.
        if (offered.ControlledBy is not (null or You) || SmokeFillers.Bodies(offered) is not [var body, ..])
            return;

        for (var i = 0; i < n; i++)
            board.Expect(new InZone(board.Bystander(mine: true, body), Zone.Graveyard, Caster));
        board.Caster(graveyard: n);
    }
}
