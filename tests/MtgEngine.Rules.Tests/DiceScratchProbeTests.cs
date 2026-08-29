using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Rules.Tests;

// Temporary diagnostic scaffolding for the dice round. Deleted before commit.
public sealed class DiceScratchProbeTests
{
    private static CardDefinition Card(string name, string text, CardType types = CardType.Instant) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant().Replace(' ', '-'),
        Name = name,
        OracleText = text,
        CardTypes = types,
    };

    [Fact]
    public void Dump()
    {
        var cards = new[]
        {
            Card(
                "Recruitment Drive Test",
                "Roll a d20.\n1—9 | Create two 1/1 white Soldier creature tokens.\n10—19 | Create two 2/2 white Knight creature tokens.\n20 | Create three 2/2 white Knight creature tokens.",
                CardType.Sorcery),
            Card(
                "Contact Other Plane Test",
                "Roll a d20.\n1—9 | Draw two cards.\n10—19 | Scry 2, then draw two cards.\n20 | Scry 3, then draw three cards."),
            Card(
                "Station Probe",
                "Station (Tap another creature you control: Put charge counters equal to its power on this Spacecraft. Station only as a sorcery. It's an artifact creature at 10+.)\n10+ | Flying",
                CardType.Artifact),
        };

        var lines = new List<string>();

        foreach (var card in cards)
        {
            lines.Add("==== " + card.Name);
            foreach (var line in CardCompiler.Lines(card))
                lines.Add("LINE: [" + line + "]");

            var compiled = CardCompiler.Compile(card);
            lines.Add("complete: " + compiled.IsComplete);
            foreach (var unread in compiled.Unhandled)
                lines.Add("UNREAD: [" + unread + "]");
        }

        foreach (var text in new[]
        {
            "Roll a d20. 1\u20149 | Draw two cards. 10\u201419 | Scry 2, then draw two cards. 20 | Scry 3, then draw three cards.",
            "Roll a d20. 1\u20149 | Draw two cards.",
            "Roll a d20. 20 | Draw two cards.",
            "Roll a d20.",
            "Draw two cards.",
            "Create two 1/1 white Soldier creature tokens.",
            "Scry 2, then draw two cards.",
        })
        {
            var read = EffectPhrase.TryParse(text, out var parsed);
            lines.Add(
                "PARSE " + (read ? "ok  " : "FAIL") + " [" + text + "] -> "
                + string.Join(",", parsed.Effects.Select(e => e.GetType().Name)));
        }

        File.WriteAllLines(
            @"C:\Users\John\AppData\Local\Temp\claude\c--Users-John-Documents-Projects-MtgEngine\514e07c2-5ca1-4bff-8466-7501b523b021\scratchpad\dice_probe_out.txt",
            lines);
    }
}
