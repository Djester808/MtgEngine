using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Guards the property the AI prompts depend on: the injected card subset must be a
/// pure function of the request, otherwise temperature=0 buys nothing and the
/// Anthropic prompt cache can never hit.
/// </summary>
public class DeterministicSampleTests
{
    private static string[] Pool(int n) =>
        Enumerable.Range(0, n).Select(i => $"Card {i:D4}").ToArray();

    [Fact]
    public void SameSeed_ProducesIdenticalSample()
    {
        var pool = Pool(500);

        var a = DeterministicSample.Take(pool, 60, "Atraxa|3|budget");
        var b = DeterministicSample.Take(pool, 60, "Atraxa|3|budget");

        Assert.Equal(a, b);
    }

    [Fact]
    public void SameSeed_IsStableAcrossManyRuns()
    {
        var pool = Pool(500);
        var first = DeterministicSample.Take(pool, 40, "seed");

        for (int i = 0; i < 25; i++)
            Assert.Equal(first, DeterministicSample.Take(pool, 40, "seed"));
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentSample()
    {
        var pool = Pool(500);

        var a = DeterministicSample.Take(pool, 60, "Atraxa|3|budget");
        var b = DeterministicSample.Take(pool, 60, "Krenko|3|budget");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BracketAndPrice_ChangeTheSample()
    {
        var pool = Pool(500);

        var bracket2 = DeterministicSample.Take(pool, 60, "Atraxa|2|budget");
        var bracket4 = DeterministicSample.Take(pool, 60, "Atraxa|4|budget");
        var midPrice = DeterministicSample.Take(pool, 60, "Atraxa|2|mid");

        Assert.NotEqual(bracket2, bracket4);
        Assert.NotEqual(bracket2, midPrice);
    }

    [Fact]
    public void Sample_IsSpreadAcrossPool_NotAlphabeticallyBiased()
    {
        // The regression this guards: sorting the source and taking the first N would
        // feed the model only cards starting with "A".
        var pool = Pool(1000);
        var sample = DeterministicSample.Take(pool, 100, "spread");

        var indices = sample.Select(s => int.Parse(s.Split(' ')[1])).ToArray();

        Assert.True(indices.Max() > 800, $"expected spread into the tail, max index was {indices.Max()}");
        Assert.True(indices.Min() < 200, $"expected spread into the head, min index was {indices.Min()}");
    }

    [Fact]
    public void Sample_ReturnsRequestedCount_WithNoDuplicates()
    {
        var sample = DeterministicSample.Take(Pool(500), 60, "seed");

        Assert.Equal(60, sample.Length);
        Assert.Equal(60, sample.Distinct().Count());
    }

    [Fact]
    public void Sample_DrawsOnlyFromSource()
    {
        var pool = Pool(200);
        var sample = DeterministicSample.Take(pool, 50, "seed");

        Assert.All(sample, s => Assert.Contains(s, pool));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCount_ReturnsEmpty(int count) =>
        Assert.Empty(DeterministicSample.Take(Pool(10), count, "seed"));

    [Fact]
    public void EmptySource_ReturnsEmpty() =>
        Assert.Empty(DeterministicSample.Take([], 10, "seed"));

    [Fact]
    public void SourceSmallerThanCount_ReturnsWholeSource()
    {
        var pool = Pool(5);
        Assert.Equal(pool, DeterministicSample.Take(pool, 60, "seed"));
    }
}
