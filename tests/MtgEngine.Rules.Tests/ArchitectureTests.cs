using System.Reflection;
using MtgEngine.Rules.Engine;
using NetArchTest.Rules;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Executable layering rules for the engine, matching the ones the Api tests place on the
/// domain. If one fails the design drifted — fix the code, not the test.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Rules = typeof(Game).Assembly;

    private static void AssertPasses(TestResult result)
    {
        Assert.True(
            result.IsSuccessful,
            "Offending types:\n  " + string.Join("\n  ", result.FailingTypeNames ?? []));
    }

    // ---- The engine is a leaf, like the domain ----

    [Fact]
    public void Rules_do_not_depend_on_the_Api()
    {
        AssertPasses(Types.InAssembly(Rules)
            .ShouldNot().HaveDependencyOn("MtgEngine.Api")
            .GetResult());
    }

    [Fact]
    public void Rules_do_not_depend_on_EntityFrameworkCore()
    {
        AssertPasses(Types.InAssembly(Rules)
            .ShouldNot().HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult());
    }

    [Fact]
    public void Rules_do_not_depend_on_AspNetCore()
    {
        AssertPasses(Types.InAssembly(Rules)
            .ShouldNot().HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult());
    }

    /// <summary>
    /// The rules must not reach for a clock. Timestamps order continuous effects (CR 613.7) and
    /// have to be a total order the log can reproduce; a wall clock is neither, and a game that
    /// consults one cannot be replayed. Randomness is confined to <see cref="GameRandom"/>,
    /// whose results are recorded as events.
    /// </summary>
    [Fact]
    public void Rules_do_not_read_the_clock()
    {
        AssertPasses(Types.InAssembly(Rules)
            .ShouldNot().HaveDependencyOn("System.DateTime")
            .GetResult());
    }
}
