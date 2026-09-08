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
                Returns(board, effect);
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
}
