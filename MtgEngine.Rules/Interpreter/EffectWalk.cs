using System.Reflection;
using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Two passes over a vocabulary tree that the interpreter needs and no card should have to spell
/// out: which targets an effect chooses, and turning every number in a continuous effect into the
/// number it works out to now. Both walk the records generically, so a primitive added later is
/// covered without a second edit here.
/// </summary>
internal static class EffectWalk
{
    private const string Vocabulary = "MtgEngine.Cards";

    /// <summary>
    /// The target selectors the effect names, in the order the text reads them: a
    /// <see cref="Target"/> with its filter, and an <see cref="AnyTarget"/>, which CR 115.4 makes
    /// one target chosen from among creatures, players, planeswalkers and battles. Both count, so
    /// the slot a chosen target fills is the slot the tree named, and resolution re-checks the
    /// chosen targets against these, CR 608.2b. An "any target" carries no filter of its own,
    /// because the announcement already put an object in Targets or a player in TargetPlayers.
    /// </summary>
    [RuleRef("608.2b")]
    public static ImmutableList<Selector> TargetsIn(Effect effect) =>
    [
        .. Nodes(effect, new HashSet<object>(ReferenceEqualityComparer.Instance))
            .Where(n => n is Target or AnyTarget)
            .Cast<Selector>(),
    ];

    /// <summary>
    /// The <see cref="TargetPlayer"/> references the effect names, in the order the text reads
    /// them: the same pre-order walk as <see cref="TargetsIn"/>, so the n-th keyed player target
    /// is the n-th player announced (CR 601.2c).
    /// </summary>
    [RuleRef("601.2c")]
    public static ImmutableList<TargetPlayer> TargetPlayersIn(Effect effect) =>
        [.. Nodes(effect, new HashSet<object>(ReferenceEqualityComparer.Instance)).OfType<TargetPlayer>()];

    /// <summary>
    /// The same tree with every unkeyed <see cref="Target"/> and <see cref="AnyTarget"/> keyed by
    /// its slot: the n-th target selector in <see cref="TargetsIn"/> order is the n-th announced
    /// (CR 601.2c), so it is given <see cref="Query.Query.TargetSlot"/>(n) and from then on reads
    /// exactly what was announced for it, the way a selector the card keyed itself does. Two
    /// unkeyed targets used to read the whole announced list each — "target creature you control
    /// fights target creature you don't control" fought the first creature against itself. A
    /// selector the card keyed is left alone, and a tree with no unkeyed target is returned as it
    /// is, so a single unkeyed target and a keyed one both behave as they always did.
    /// </summary>
    [RuleRef("601.2c")]
    public static Effect KeyTargets(Effect effect)
    {
        var slots = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var at = 0;
        foreach (var selector in TargetsIn(effect))
        {
            if (selector is Target { Key: null } or AnyTarget { Key: null })
                slots[selector] = Query.Query.TargetSlot(at);
            at++;
        }

        if (slots.Count == 0)
            return effect;

        return (Effect)Rewrite(effect, node => node switch
        {
            Target target when slots.TryGetValue(target, out var slot) => target with { Key = slot },
            AnyTarget any when slots.TryGetValue(any, out var slot) => any with { Key = slot },
            _ => node,
        })!;
    }

    /// <summary>
    /// Every <see cref="Amount"/> in the effect, worked out now and replaced by a
    /// <see cref="Fixed"/>. CR 611.2b: the value is locked in when the effect is created and is
    /// never recalculated, so the layer system must never see a countable amount.
    /// </summary>
    [RuleRef("611.2b")]
    public static ContinuousEffect Freeze(ContinuousEffect effect, Func<Amount, int> evaluate) =>
        (ContinuousEffect)Rewrite(effect, node => node is Amount amount and not Fixed ? new Fixed(evaluate(amount)) : node)!;

    /// <summary>
    /// The <see cref="Choose"/> selectors this one node reads, in reading order, CR 700.2: every
    /// one reachable from it without entering another <see cref="Effect"/>. A child effect is its
    /// own node of the walk and asks its own choices when the walk reaches it, which is what keeps
    /// "reveal, then choose from among them" asking after the reveal has been remembered.
    /// </summary>
    [RuleRef("700.2")]
    public static ImmutableList<Choose> ChoicesIn(Effect effect) =>
        [.. Own(effect, effect, new HashSet<object>(ReferenceEqualityComparer.Instance)).OfType<Choose>()];

    private static IEnumerable<object> Own(object? node, object root, HashSet<object> seen)
    {
        if (node is null or string)
            yield break;

        if (node is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                foreach (var found in Own(item, root, seen))
                    yield return found;
            yield break;
        }

        var type = node.GetType();
        if (type.Namespace != Vocabulary || type.IsEnum || !type.IsClass || !seen.Add(node))
            yield break;
        if (node is Effect && !ReferenceEquals(node, root))
            yield break;

        yield return node;
        foreach (var property in Readable(type))
            foreach (var found in Own(property.GetValue(node), root, seen))
                yield return found;
    }

    private static IEnumerable<object> Nodes(object? node, HashSet<object> seen)
    {
        if (node is null or string)
            yield break;

        if (node is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                foreach (var found in Nodes(item, seen))
                    yield return found;
            yield break;
        }

        var type = node.GetType();
        if (type.Namespace != Vocabulary || type.IsEnum || !type.IsClass || !seen.Add(node))
            yield break;

        yield return node;
        foreach (var property in Readable(type))
            foreach (var found in Nodes(property.GetValue(node), seen))
                yield return found;
    }

    /// <summary>
    /// The tree with <paramref name="map"/> applied to every vocabulary node, top down: a node the
    /// map replaces is taken as it is, and one it returns unchanged is descended into and rebuilt
    /// only when something under it changed.
    /// </summary>
    private static object? Rewrite(object? node, Func<object, object?> map)
    {
        switch (node)
        {
            case null or string:
                return node;
            case System.Collections.IEnumerable items:
                return RewriteList(items, map);
        }

        var mapped = map(node);
        if (!ReferenceEquals(mapped, node))
            return mapped;

        var type = node.GetType();
        if (type.Namespace != Vocabulary || type.IsEnum || !type.IsClass)
            return node;

        var constructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        var parameters = constructor?.GetParameters() ?? [];
        if (constructor is null || parameters.Length == 0)
            return node;

        var arguments = new object?[parameters.Length];
        var changed = false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var property = type.GetProperty(parameters[i].Name ?? string.Empty, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                return node;
            var value = property.GetValue(node);
            arguments[i] = Rewrite(value, map);
            changed |= !ReferenceEquals(value, arguments[i]);
        }

        return changed ? constructor.Invoke(arguments) : node;
    }

    private static object RewriteList(System.Collections.IEnumerable items, Func<object, object?> map)
    {
        var element = items.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0] ?? typeof(object);
        var original = items.Cast<object?>().ToList();
        var rewritten = original.Select(item => Rewrite(item, map)).ToList();
        if (original.Zip(rewritten).All(pair => ReferenceEquals(pair.First, pair.Second)))
            return items;
        var array = Array.CreateInstance(element, rewritten.Count);
        for (var i = 0; i < rewritten.Count; i++)
            array.SetValue(rewritten[i], i);
        return array;
    }

    private static IEnumerable<PropertyInfo> Readable(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);
}

/// <summary>Reading printed mana symbols, CR 107.4: "{G}{G}" is two green.</summary>
[RuleRef("107.4")]
internal static class ManaSymbols
{
    public static ManaPool Parse(string symbols)
    {
        var counts = new Dictionary<char, int>();
        foreach (var symbol in symbols.Where(char.IsLetterOrDigit))
        {
            var upper = char.ToUpperInvariant(symbol);
            var key = char.IsDigit(upper) ? 'C' : upper;
            counts[key] = counts.GetValueOrDefault(key) + (char.IsDigit(upper) ? upper - '0' : 1);
        }

        return new ManaPool(
            counts.GetValueOrDefault('W'),
            counts.GetValueOrDefault('U'),
            counts.GetValueOrDefault('B'),
            counts.GetValueOrDefault('R'),
            counts.GetValueOrDefault('G'),
            counts.GetValueOrDefault('C'));
    }

    public static ManaPool OfColor(string colour) => colour.Length == 0
        ? ManaPool.Empty
        : Parse(colour[..1]);
}
