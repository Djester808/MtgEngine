using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A selector said inside a filter (<see cref="IsOneOf"/>, V9) and the direction the
/// <see cref="AttachedTo"/> predicate reads, pinned against each other: the two are the two ends
/// of one link (CR 301.5c). The world these are built in is the one QueryTests declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("301.5c")]
    public void Is_one_of_attached_names_the_equipped_creature_and_nothing_else()
    {
        // "Whenever equipped creature dies": the Equipment (2) is attached to creature 1 and the
        // filter is read as the Equipment, so Attached names creature 1 and IsOneOf admits it —
        // not the other creature, and not the Equipment itself.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 1, 1, types: CardType.Artifact, subtypes: ["Equipment"])
            .Creature(3, Alice, 2, 2)
            .Attach(2, 1)
            .As(2);
        var equipped = new ObjectFilter(Also: new IsOneOf(new Attached()));

        Assert.True(world.Query.Matches(world.State, equipped, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, equipped, new ObjectId(3), world.Context));
        Assert.False(world.Query.Matches(world.State, equipped, new ObjectId(2), world.Context));
        Assert.Equal(1, world.Count(equipped));

        // The filter's other fields still narrow it: the equipped creature is a creature, and it
        // is not an artifact.
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, Also: new IsOneOf(new Attached()))));
        Assert.Equal(0, world.Count(new ObjectFilter(Types: CardType.Artifact, Also: new IsOneOf(new Attached()))));
    }

    [Fact]
    [RuleRef("301.5c")]
    public void Is_one_of_a_selector_that_names_nothing_matches_nothing()
    {
        // Three shapes of nothing: the source is attached to nothing, there is no source at all,
        // and a remembered key no step wrote. None is a refusal — the selector answers an empty
        // list and the filter admits no object, as IsRemembered does off a resolution.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 1, 1, types: CardType.Artifact, subtypes: ["Equipment"])
            .Attach(2, 1);
        var equipped = new ObjectFilter(Also: new IsOneOf(new Attached()));

        Assert.Equal(0, world.As(1).Count(equipped));
        Assert.Equal(0, world.Count(equipped));
        Assert.Equal(0, world.As(2).Count(new ObjectFilter(Also: new IsOneOf(new Remembered("nothing-wrote-this")))));
    }

    [Fact]
    [RuleRef("301.5c")]
    public void Is_one_of_attached_and_attached_to_self_are_the_two_ends_of_one_link()
    {
        // Read as the Equipment: IsOneOf(Attached) is the creature it is on, AttachedTo(Self) is
        // the Auras and Equipment hanging on the Equipment, of which there are none. Read as the
        // creature: AttachedTo(Self) is the Equipment, IsOneOf(Attached) is nothing, because a
        // creature is attached to nothing. A trigger that wants "equipped creature" and writes
        // AttachedTo(Self) therefore fires for the wrong objects, which is why V9 added the record.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 1, 1, types: CardType.Artifact, subtypes: ["Equipment"])
            .Attach(2, 1);
        var equipped = new ObjectFilter(Also: new IsOneOf(new Attached()));
        var attachedToThis = new ObjectFilter(Also: new AttachedTo(new Self()));

        var asEquipment = world.As(2);
        Assert.Equal(new ObjectId(1), Assert.Single(asEquipment.Query.Resolve(asEquipment.State, new Each(equipped), asEquipment.Context)));
        Assert.Equal(0, asEquipment.Count(attachedToThis));

        var asCreature = world.As(1);
        Assert.Equal(0, asCreature.Count(equipped));
        Assert.Equal(new ObjectId(2), Assert.Single(asCreature.Query.Resolve(asCreature.State, new Each(attachedToThis), asCreature.Context)));
    }

    [Fact]
    [RuleRef("301.5c")]
    public void Attached_to_is_candidate_first_whichever_selector_names_the_host()
    {
        // The direction without Self in the picture: the Aura (2) on creature 1 matches
        // AttachedTo(the creature), and the creature does not match AttachedTo(the Aura) — a
        // creature is not attached to its Aura. Nor does the Aura match AttachedTo(itself).
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 1, 1, types: CardType.Enchantment, subtypes: ["Aura"])
            .Attach(2, 1)
            .Remember("creature", new ObjectId(1))
            .Remember("aura", new ObjectId(2));
        var onTheCreature = new ObjectFilter(Also: new AttachedTo(new Remembered("creature")));
        var onTheAura = new ObjectFilter(Also: new AttachedTo(new Remembered("aura")));

        Assert.True(world.Query.Matches(world.State, onTheCreature, new ObjectId(2), world.Context));
        Assert.False(world.Query.Matches(world.State, onTheCreature, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, onTheAura, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, onTheAura, new ObjectId(2), world.Context));
        Assert.Equal(1, world.Count(onTheCreature));
        Assert.Equal(0, world.Count(onTheAura));
    }

    [Fact]
    [RuleRef("603.6")]
    public void Is_one_of_triggering_names_the_firing_objects_in_the_filters_context()
    {
        // "Whenever that creature attacks" inside a delayed trigger: the filter is read in the
        // context that carries the firing's objects, so IsOneOf(Triggering) is the subject and
        // IsOneOf(Triggering(Other)) is the other one; neither admits a bystander.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Bob, 2, 2)
            .Creature(3, Bob, 2, 2)
            .Triggered(new ObjectId(1), new ObjectId(2));
        var subject = new ObjectFilter(Also: new IsOneOf(new Triggering()));
        var other = new ObjectFilter(Also: new IsOneOf(new Triggering(TriggerRole.Other)));

        Assert.True(world.Query.Matches(world.State, subject, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, subject, new ObjectId(2), world.Context));
        Assert.True(world.Query.Matches(world.State, other, new ObjectId(2), world.Context));
        Assert.False(world.Query.Matches(world.State, other, new ObjectId(3), world.Context));
        Assert.Equal(0, World.New().Creature(1, Alice, 2, 2).Count(subject));
    }

    [Fact]
    [RuleRef("603.10")]
    public void Is_remembered_and_is_one_of_remembered_are_one_reading()
    {
        // "That creature" said inside a filter: IsRemembered(key) is the spelling for a key and
        // IsOneOf(Remembered(key)) its general form, so the two admit the same objects, and a key
        // no step wrote admits nothing for either rather than being refused — off a resolution
        // there is no memory to read, and a static filter that reads one describes nothing.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 2, 2)
            .Remember("chosen", new ObjectId(1));
        var remembered = new ObjectFilter(Also: new IsRemembered("chosen"));
        var oneOf = new ObjectFilter(Also: new IsOneOf(new Remembered("chosen")));

        Assert.True(world.Query.Matches(world.State, remembered, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, remembered, new ObjectId(2), world.Context));
        Assert.Equal(world.Count(remembered), world.Count(oneOf));
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, Also: new NotPredicate(new IsRemembered("chosen")))));

        Assert.Equal(0, world.Count(new ObjectFilter(Also: new IsRemembered("nothing-wrote-this"))));
        Assert.Equal(0, World.New().Creature(1, Alice, 2, 2).Count(remembered));
    }
}
