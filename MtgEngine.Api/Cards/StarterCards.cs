using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Cards;

/// <summary>
/// The first cards the engine implements.
/// </summary>
/// <remarks>
/// Chosen to cover the shapes rather than to be a format: a burn spell, a counterspell, removal,
/// a draw spell, a pump spell, a lord, a mana rock, a pinger, an enters-with-counters creature,
/// and a death trigger. Between them they exercise every primitive, which is what makes it
/// obvious whether the next card needs new machinery or is only more data.
/// <para>
/// Keyed by card name rather than oracle id. Oracle id is the better key — it is what a deck
/// stores and it survives a rename — but the ids cannot be checked without the card database in
/// front of me, and a card pool full of plausible-looking wrong GUIDs would bind real cards to
/// the wrong behaviour and look right while doing it. Names are exact, verifiable, and unique per
/// oracle card. <see cref="CardPool"/> resolves by name and can be moved to ids in one place once
/// they can be read from the database.
/// </para>
/// </remarks>
public static class StarterCards
{
    /// <summary>
    /// Continuous effects a resolving spell creates, by the id it names them with (CR 613.7b).
    /// </summary>
    /// <remarks>
    /// These outlive their spell — "target creature gets +3/+3 until end of turn" keeps applying
    /// after the card is in the graveyard — so the game records that the effect exists and looks
    /// up what it does here.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, ContinuousEffectDefinition> FloatingEffects =
        new Dictionary<string, ContinuousEffectDefinition>(StringComparer.Ordinal)
        {
            ["giant-growth"] = new()
            {
                Id = "giant-growth",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = (_, _, _) => true,
                Apply = builder => builder.Modify(3, 3),
            },
        };

    public static IReadOnlyList<CardScript> All { get; } =
    [
        new CardScript
        {
            Name = "Lightning Bolt",
            Spell = new SpellDefinition
            {
                Targets = [Targets.AnyTarget],
                Effects = [new DealDamage(3)],
            },
        },

        new CardScript
        {
            Name = "Shock",
            Spell = new SpellDefinition
            {
                Targets = [Targets.AnyTarget],
                Effects = [new DealDamage(2)],
            },
        },

        new CardScript
        {
            Name = "Counterspell",
            Spell = new SpellDefinition
            {
                Targets = [Targets.TargetSpell],
                Effects = [new CounterTargetSpell()],
            },
        },

        new CardScript
        {
            Name = "Murder",
            Spell = new SpellDefinition
            {
                Targets = [Targets.TargetCreature],
                Effects = [new DestroyTarget()],
            },
        },

        new CardScript
        {
            Name = "Divination",
            Spell = new SpellDefinition { Effects = [new DrawCards(2)] },
        },

        new CardScript
        {
            Name = "Giant Growth",
            Spell = new SpellDefinition
            {
                Targets = [Targets.TargetCreature],
                Effects = [new PumpUntilEndOfTurn("giant-growth")],
            },
        },

        new CardScript
        {
            Name = "Sol Ring",
            Activated =
            [
                new ActivatedAbilityDefinition
                {
                    Id = "mana",
                    Text = "{T}: Add {C}{C}.",
                    RequiresTap = true,
                    Produces = [ManaProduction.Colorless(2)],
                },
            ],
        },

        new CardScript
        {
            Name = "Prodigal Pyromancer",
            Activated =
            [
                new ActivatedAbilityDefinition
                {
                    Id = "ping",
                    Text = "{T}: This creature deals 1 damage to any target.",
                    RequiresTap = true,
                    Targets = [Targets.AnyTarget],
                    Effects = [new DealDamage(1)],
                },
            ],
        },

        new CardScript
        {
            Name = "Elvish Archdruid",
            Statics =
            [
                new ContinuousEffectDefinition
                {
                    Id = "elf-lord",
                    Layer = EffectLayer.PowerToughnessModify,
                    // CR 613.4c, and "other" means not itself.
                    Applies = (state, source, target) =>
                        source is not null
                        && target.Subject.Id != source.Id
                        && target.Subject.Zone == Zone.Battlefield
                        && target.ControllerId == source.ControllerId
                        && target.Subject.Card.Subtypes.Contains("Elf", StringComparer.OrdinalIgnoreCase),
                    Apply = builder => builder.Modify(1, 1),
                },
            ],
        },

        new CardScript
        {
            Name = "Kalonian Hydra",
            Replacements =
            [
                new ReplacementEffectDefinition
                {
                    Id = "enters-with-counters",
                    // CR 614.1c: "as this enters" is a replacement applied to the move itself, and
                    // it functions from the stack, where the card still is at that moment.
                    FunctionsFrom = Zone.Stack,
                    Applies = (e, state, source) =>
                        e is ObjectMoved { To: Zone.Battlefield } m && m.OldId == source.Id,
                    Replace = (e, state, source) =>
                    {
                        var move = (ObjectMoved)e;
                        return [move, new CountersChanged(move.NewId, CounterKinds.PlusOnePlusOne, 4)];
                    },
                },
            ],
        },

        new CardScript
        {
            Name = "Wall of Blossoms",
            Triggers =
            [
                new TriggeredAbilityDefinition
                {
                    Id = "etb-draw",
                    Text = "When this creature enters, draw a card.",
                    // CR 603.6a: an enters-the-battlefield trigger looks at the game as it is
                    // just after the permanent arrived, so it triggers from the battlefield.
                    Triggers = (e, state, source) =>
                        e is ObjectMoved { To: Zone.Battlefield } m && m.NewId == source.Id,
                    Effects = [new DrawCards(1)],
                },
            ],
        },

        new CardScript
        {
            Name = "Solemn Simulacrum",
            Triggers =
            [
                new TriggeredAbilityDefinition
                {
                    Id = "dies-draw",
                    Text = "When this creature dies, draw a card.",
                    Triggers = (e, state, source) =>
                        e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } m
                        && m.OldId == source.Id,
                    Effects = [new DrawCards(1)],
                },
            ],
        },
    ];
}
