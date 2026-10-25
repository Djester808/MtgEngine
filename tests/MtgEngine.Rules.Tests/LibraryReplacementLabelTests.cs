using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryReplacementLabelTests
{
    [Fact]
    public void Hypothetical_entry_probe_does_not_publish_a_hidden_source_name()
    {
        var table = new InterpreterTable();
        var ability = new ReplacementAbility(new WouldEnterBattlefield(ObjectFilter.Any), new EntersTapped(), SelfOnly: true);
        var definition = new CardDefinition("private-entry", "Unrevealed library source", Layout.Normal,
            [new Face("Unrevealed library source", FaceRole.Main, new TypeLine(CardType.Creature), Abilities: [ability, ability])]);
        var card = table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), definition);
        var result = Subsystems.Wave2().Zones.Move(table.State, card, ZoneKey.Shared(Zone.Battlefield));
        var pending = Assert.IsType<PendingChoice>(result.State.Pending);
        Assert.Equal(2, pending.Options.Count);
        Assert.All(pending.Options, option => Assert.Equal("Apply replacement effect", option.Label));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Library_replacement_labels_are_readable_without_naming_hidden_sources(bool hidden, bool suppliedText)
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        var zone = hidden ? Zone.Library : Zone.Battlefield;
        var ability = new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Any with { In = Zone.Library }, Zone.Graveyard),
            new ModifyAmount(Add: 0), Where: zone)
        {
            Text = suppliedText ? "Use the supplied replacement text" : null,
        };
        var definition = new CardDefinition("label-source", "Label source", Layout.Normal,
            [new Face("Label source", FaceRole.Main, new TypeLine(CardType.Enchantment), Abilities: [ability, ability])]);
        table.Put(table.A, hidden ? ZoneKey.Of(zone, table.A) : ZoneKey.Shared(zone), definition);
        var result = Subsystems.Wave2().Zones.Move(table.State, card, ZoneKey.Of(Zone.Graveyard, table.A));
        var pending = Assert.IsType<PendingChoice>(result.State.Pending);
        Assert.Equal(2, pending.Options.Count);
        foreach (var option in pending.Options)
        {
            Assert.DoesNotContain("WouldBePutIntoZone", option.Label, StringComparison.Ordinal);
            if (suppliedText)
                Assert.Equal(ability.Text, option.Label);
            else if (hidden)
                Assert.Equal("Replace a move to the graveyard", option.Label);
            else
                Assert.Contains("Label source — Replace a move to the graveyard", option.Label, StringComparison.Ordinal);
        }
        if (!hidden && !suppliedText)
        {
            Assert.EndsWith("(ability 1)", pending.Options[0].Label, StringComparison.Ordinal);
            Assert.EndsWith("(ability 2)", pending.Options[1].Label, StringComparison.Ordinal);
        }
    }
}
