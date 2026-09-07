using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// An Aura resolves attached, CR 303.4f: the object it was cast at is what it enchants when it
/// enters, and CR 608.2b counters it on resolution when that object is no longer there. Before
/// this it entered attached to nothing and CR 704.5m buried it, which is the same graveyard by
/// the wrong road: the permanent existed for a moment and the enchanted creature never was.
/// </summary>
public sealed class AuraResolutionTests
{
    private readonly CastingSystem casting = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    public void An_Aura_resolves_attached_to_the_creature_it_was_cast_at()
    {
        // CR 303.4f: "the Aura enters the battlefield attached to the object or player it was
        // cast at". The target chosen on casting is read off the stack entry as it resolves.
        var table = new CastingTable();
        var host = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var aura = table.Add(AuraResolutionCards.EnchantCreature(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, aura, Targets: [host]));

        var resolved = casting.ResolveTop(cast.State);

        var permanent = resolved.State.Objects[Moved(resolved)];
        Assert.Equal(CardZone.Battlefield, permanent.Zone.Zone);
        Assert.Equal(host, permanent.AttachedTo);
    }

    [Fact]
    public void An_Aura_whose_target_left_before_it_resolved_goes_to_the_graveyard()
    {
        // CR 608.2b: every target is illegal as the spell resolves, so the spell does not resolve
        // and is put into its owner's graveyard. It never enters the battlefield at all.
        var table = new CastingTable();
        var host = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var aura = table.Add(AuraResolutionCards.EnchantCreature(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, aura, Targets: [host]));
        var gone = new CastingFakeZones().Move(cast.State, host, ZoneKey.Of(CardZone.Graveyard, CastingTable.A));

        var resolved = casting.ResolveTop(gone.State);

        Assert.Equal(CardZone.Graveyard, resolved.State.Objects[Moved(resolved)].Zone.Zone);
        Assert.DoesNotContain(resolved.State.Objects.Values, o => o.Zone.Zone == CardZone.Battlefield && o.Definition.Name.StartsWith("Aura Test", StringComparison.Ordinal));
        Assert.Contains(resolved.Events, e => e is StackObjectResolved);
    }

    [Fact]
    public void An_Aura_cast_at_a_player_still_enters_attached_to_nothing()
    {
        // KNOWN GAP, CR 303.4f. An Aura cast at a player enters attached to that player, but
        // GameObject has nowhere to keep a player attachment (seam-requests/E3e.md, and the
        // wave-1 decision that deferred AttachedToPlayer). Its target is legal, so it is not
        // countered here; it enters unattached and CR 704.5m buries it, as it did before.
        var table = new CastingTable();
        var aura = table.Add(AuraResolutionCards.EnchantPlayer(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, aura, TargetPlayers: [CastingTable.B]));

        var resolved = casting.ResolveTop(cast.State);

        var permanent = resolved.State.Objects[Moved(resolved)];
        Assert.Equal(CardZone.Battlefield, permanent.Zone.Zone);
        Assert.Null(permanent.AttachedTo);
    }

    [Fact]
    public void A_permanent_spell_that_is_not_an_Aura_enters_attached_to_nothing()
    {
        // CR 608.3: every other permanent spell becomes a permanent the way it always did; the
        // attachment is the Aura's rule and nothing else reads the stack entry's targets here.
        var table = new CastingTable();
        var host = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var beast = table.Add(TestFaces.Creature("{2}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, beast, Targets: [host]));

        var resolved = casting.ResolveTop(cast.State);

        var permanent = resolved.State.Objects[Moved(resolved)];
        Assert.Equal(CardZone.Battlefield, permanent.Zone.Zone);
        Assert.Null(permanent.AttachedTo);
        Assert.True(permanent.SummoningSick);
    }

    /// <summary>A zone change makes a new object, CR 400.7; the last move's event carries its id.</summary>
    private static ObjectId Moved(Outcome outcome) => outcome.Events.OfType<ObjectMoved>().Last().To.Id;
}

/// <summary>
/// Cards as data for <see cref="AuraResolutionTests"/>, built here rather than read from
/// <c>cards/</c>. The names are deliberately not any printed card's, as CARD_ENGINE_PLAN.md
/// § "A card is data or it does not exist" requires of the engine and of its tests.
/// </summary>
internal static class AuraResolutionCards
{
    /// <summary>"Enchant creature", CR 702.5b: an Aura whose enchant ability names a filter.</summary>
    public static CardDefinition EnchantCreature() =>
        Aura("Aura Test Wrap", new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature)));

    /// <summary>"Enchant player", CR 702.5b: the filter describes a player, which an ObjectFilter cannot.</summary>
    public static CardDefinition EnchantPlayer() =>
        Aura("Aura Test Curse", new KeywordAbility(KeywordKind.Enchant));

    private static CardDefinition Aura(string name, KeywordAbility enchant) => new(
        name.ToUpperInvariant(),
        name,
        Layout.Normal,
        [new Face(name, FaceRole.Main, new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]), "{2}", Abilities: [enchant])]);
}
