using System.Collections.ObjectModel;
using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Layers;

/// <summary>Keeps origin metadata aligned through layer-six additions and removals.</summary>
internal sealed class LayerAbilityList : Collection<Ability>
{
    private readonly List<AbilityOrigin?> origins = [];

    internal ImmutableList<AbilityOrigin?> Origins => [.. origins];

    internal void Add(Ability ability, AbilityOrigin? origin)
    {
        Add(ability);
        origins[^1] = origin;
    }

    internal static LayerAbilityList Printed(IReadOnlyList<Ability> abilities, int face)
    {
        var result = new LayerAbilityList();
        for (var index = 0; index < abilities.Count; index++)
            result.Add(abilities[index], new AbilityOrigin("printed", index, Face: face));
        return result;
    }

    internal LayerAbilityList Copied(AbilityOrigin? effect, string definition, int face)
    {
        var result = new LayerAbilityList();
        for (var index = 0; index < Count; index++)
            result.Add(this[index], new AbilityOrigin("copy", index, Definition: definition, Face: face, Parent: effect));
        return result;
    }

    internal void RemoveAll(Predicate<Ability> predicate)
    {
        for (var index = Count - 1; index >= 0; index--)
            if (predicate(this[index]))
                RemoveAt(index);
    }

    protected override void InsertItem(int index, Ability item)
    {
        base.InsertItem(index, item);
        origins.Insert(index, null);
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        origins.RemoveAt(index);
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        origins.Clear();
    }
}
