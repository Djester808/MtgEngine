using System.Collections;
using System.Reflection;

namespace MtgEngine.Cards;

/// <summary>The reflective walk over a definition every lint reads, split from the checks by responsibility.</summary>
public static partial class Linter
{
    /// <summary>Every vocabulary record reachable from a root, depth first, including the root.</summary>
    public static IEnumerable<object> Walk(object root)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0)
                    continue;
                var value = prop.GetValue(node);
                foreach (var child in Children(value))
                    stack.Push(child);
            }
        }
    }

    private static IEnumerable<object> Children(object? value)
    {
        switch (value)
        {
            case null:
            case string:
                yield break;
            case IEnumerable list:
                foreach (var item in list)
                {
                    foreach (var child in Children(item))
                        yield return child;
                }

                yield break;
            default:
                if (value.GetType().Namespace == typeof(Linter).Namespace && !value.GetType().IsEnum && !value.GetType().IsPrimitive)
                    yield return value;
                yield break;
        }
    }
}
