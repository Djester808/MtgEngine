using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Writes one line per complete card describing what it compiled to, for a before/after diff.
/// </summary>
/// <remarks>
/// Scaffolding, not a check: it asserts nothing and does nothing unless <c>INERT_DUMP</c> names a
/// file to write. A change to the compiler is only honest if the cards it moved are known by
/// name, and the complete count alone cannot say that — a fix and a regression of the same size
/// cancel in it.
/// </remarks>
public sealed class InertAuditDumpTests
{
    [Fact]
    public void Dump_compiled_effects_when_asked()
    {
        var into = Environment.GetEnvironmentVariable("INERT_DUMP");
        if (string.IsNullOrWhiteSpace(into))
            return;

        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
            return;

        var lines = new List<string>(20_000);
        var complete = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            complete++;

            var parts = new List<string>();

            foreach (var trigger in compiled.Triggers)
                parts.Add($"T[{trigger.FunctionsFrom}]{trigger.Text}");

            foreach (var modifier in compiled.CostModifiers)
                parts.Add($"C[{modifier.FilterId}:{modifier.Amount}:{modifier.Change}]");

            foreach (var effect in Effects(compiled))
                parts.Add(effect.ToString() ?? string.Empty);

            lines.Add(card.Name + "\t" + string.Join(" | ", parts));
        }

        lines.Sort(StringComparer.Ordinal);
        lines.Insert(0, $"#complete\t{complete}");
        File.WriteAllLines(into, lines);
    }

    /// <summary>Every effect the card runs, from every slice, nested ones included.</summary>
    private static IEnumerable<IEffect> Effects(CompiledCard compiled)
    {
        foreach (var property in typeof(CompiledCard).GetProperties())
        {
            if (property.PropertyType == typeof(SpellDefinition)
                && property.GetValue(compiled) is SpellDefinition spell)
            {
                foreach (var one in EffectTree.Flatten(spell.Effects))
                    yield return one;

                foreach (var mode in spell.Modes)
                {
                    foreach (var one in EffectTree.Flatten(mode.Effects))
                        yield return one;
                }
            }
        }

        foreach (var half in compiled.Halves)
        {
            if (half.Spell is not { } face)
                continue;

            foreach (var one in EffectTree.Flatten(face.Effects))
                yield return one;
        }

        foreach (var ability in compiled.Activated)
        {
            foreach (var one in EffectTree.Flatten(ability.Effects))
                yield return one;
        }

        foreach (var trigger in compiled.Triggers)
        {
            foreach (var one in EffectTree.Flatten(trigger.Effects))
                yield return one;

            foreach (var mode in trigger.Modes)
            {
                foreach (var one in EffectTree.Flatten(mode.Effects))
                    yield return one;
            }
        }
    }
}
