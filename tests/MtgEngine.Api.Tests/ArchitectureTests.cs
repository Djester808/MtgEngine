using System.Reflection;
using MtgEngine.Api.Services;
using MtgEngine.Domain.ValueObjects;
using NetArchTest.Rules;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Executable layering rules. These encode the invariants documented in the repo
/// CLAUDE.md so a drift fails the build instead of a review. If one of these fails,
/// the design regressed — fix the code, not the test.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(ManaCost).Assembly;
    private static readonly Assembly Api = typeof(AiExceptionHandler).Assembly;

    private static void AssertPasses(TestResult result)
    {
        Assert.True(
            result.IsSuccessful,
            "Offending types:\n  " + string.Join("\n  ", result.FailingTypeNames ?? []));
    }

    // ---- The domain is the leaf: nothing infrastructural leaks into it ----

    [Fact]
    public void Domain_does_not_depend_on_the_Api()
    {
        AssertPasses(Types.InAssembly(Domain)
            .ShouldNot().HaveDependencyOn("MtgEngine.Api")
            .GetResult());
    }

    [Fact]
    public void Domain_does_not_depend_on_EntityFrameworkCore()
    {
        AssertPasses(Types.InAssembly(Domain)
            .ShouldNot().HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult());
    }

    [Fact]
    public void Domain_does_not_depend_on_AspNetCore()
    {
        AssertPasses(Types.InAssembly(Domain)
            .ShouldNot().HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult());
    }

    // ---- Layering inside the Api: services sit below controllers ----

    [Fact]
    public void Services_do_not_depend_on_Controllers()
    {
        AssertPasses(Types.InAssembly(Api)
            .That().ResideInNamespace("MtgEngine.Api.Services")
            .ShouldNot().HaveDependencyOn("MtgEngine.Api.Controllers")
            .GetResult());
    }

    // ---- The EF context is service-layer infrastructure ----
    // (Controllers Auth/Preferences/Users still touch it directly — legacy, tracked
    //  in CLAUDE.md; don't add more. Mapping/DTO layers, however, must stay clean.)

    [Fact]
    public void Mapping_does_not_depend_on_the_DbContext()
    {
        AssertPasses(Types.InAssembly(Api)
            .That().ResideInNamespace("MtgEngine.Api.Mapping")
            .ShouldNot().HaveDependencyOn("MtgEngine.Api.Data")
            .GetResult());
    }
}
