using System.Reflection;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A decorator that stopped decorating, and the build that said nothing about it.
///
/// The seam interfaces in <c>Seams.cs</c> carry default implementations that throw, so a partly
/// built subsystem still compiles (that is what they are for). A wrapper is the one shape the
/// default is wrong for: <c>ScenarioRunnerTestsCastingRecorder</c> forwarded
/// <c>FinishResolving(GameState, ObjectId)</c>, the member grew a third parameter with a default
/// value, and a two-parameter method does not implement a three-parameter member — so the class
/// silently stopped overriding it, kept compiling with no warning, and every resolution through
/// the recorder reached the throwing default instead of the real casting system.
///
/// This finds that shape by what it is: a type that takes the interface it implements as a
/// constructor parameter is a decorator, and a decorator that inherits ANY member from the
/// interface is a hole. Nothing here knows about <c>FinishResolving</c>, so the next member to
/// change signature is caught the same way.
/// </summary>
public sealed class SeamDecoratorTests
{
    [Fact]
    [RuleRef("101.4")]
    public void Every_seam_decorator_implements_every_member_it_wraps()
    {
        var seams = typeof(ICastingSystem).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace == typeof(ICastingSystem).Namespace)
            .ToList();
        Assert.NotEmpty(seams);

        var holes = new List<string>();
        foreach (var assembly in new[] { typeof(ICastingSystem).Assembly, typeof(SeamDecoratorTests).Assembly })
        {
            foreach (var type in Concrete(assembly))
            {
                foreach (var seam in seams.Where(s => s.IsAssignableFrom(type) && Wraps(type, s)))
                {
                    holes.AddRange(Inherited(type, seam).Select(m => $"{type.Name} inherits {seam.Name}.{m} instead of forwarding it"));
                }
            }
        }

        Assert.True(holes.Count == 0, string.Join("; ", holes));
    }

    private static IEnumerable<Type> Concrete(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            types = [.. partial.Types.OfType<Type>()];
        }

        return types.Where(t => t is { IsClass: true, IsAbstract: false });
    }

    /// <summary>A decorator: it holds one of the thing it is, handed to it to forward to.</summary>
    private static bool Wraps(Type type, Type seam) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == seam));

    /// <summary>
    /// The members this type did not write itself. A default interface method shows up in the
    /// interface map with the INTERFACE as its declaring type; a member the class implements
    /// declares the class.
    /// </summary>
    private static IEnumerable<string> Inherited(Type type, Type seam)
    {
        var map = type.GetInterfaceMap(seam);
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.TargetMethods[i].DeclaringType == seam)
                yield return map.InterfaceMethods[i].Name;
        }
    }
}
