using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// What the analysis pass calls a commander's tribe.
/// </summary>
/// <remarks>
/// It used to prepend the commander's own creature types unconditionally, on the grounds
/// that they were "a tribe by definition". They are not, and the prompt already said so —
/// "not the commander's own types unless the text names them too" — so the code was
/// overriding the rule it had just given the model.
/// <para>
/// Measured: a "wolf tribal" build on Varis, Silverymoon Ranger — a Human Elf Ranger whose
/// text mentions none of those and whose only tribal line creates a Wolf token — was handed
/// an 806-name Elf and Human list and built a mono-green elf deck. Its own assessment said
/// so: "a well-built mono-green creature-value deck that happens to run Varis, rather than
/// a deck built to break him."
/// </para>
/// </remarks>
public sealed class CommanderTribeTests
{
    private sealed class NoCache : IAiCacheService
    {
        public Task<T> GetOrCreateAsync<T>(
            string kind, string modelVersion, IEnumerable<string?> keyParts,
            Func<Task<T>> factory, TimeSpan? ttl = null) => factory();
    }

    /// <summary>Returns whatever tribes the test says the model extracted.</summary>
    private sealed class Extracts(params string[] tribes) : IAnthropicClient
    {
        public Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default) =>
            Task.FromResult(JsonSerializer.Serialize(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(new
                        {
                            thresholds = Array.Empty<object>(),
                            tribes,
                            keywords = Array.Empty<string>(),
                        }),
                    },
                },
            }));

        public Task<string> StreamTextAsync(
            AnthropicRequest request, Func<string, string, Task> onText,
            Func<int, Task>? onThinking = null, CancellationToken ct = default) =>
            SendAsync(request, ct);
    }

    private static CardDefinition Commander(string name, string text, params string[] subtypes) => new()
    {
        OracleId = "oracle-" + name,
        Name = name,
        OracleText = text,
        Subtypes = subtypes,
        CardTypes = CardType.Creature,
        Supertypes = ["Legendary"],
        ColorIdentity = [ManaColor.Green],
    };

    private static CommanderAnalysis Sut(params string[] extracted) =>
        new(new NoCache(), new Extracts(extracted), NullLogger<CommanderAnalysis>.Instance);

    [Fact]
    public async Task A_commanders_own_types_are_not_a_tribe_unless_its_text_says_so()
    {
        // Varis, in miniature. Nothing in the text is Human, Elf or Ranger.
        var varis = Commander(
            "Varis",
            "Whenever you cast a creature spell, venture into the dungeon. "
            + "Whenever you complete a dungeon, create a 2/2 green Wolf creature token.",
            "Human", "Elf", "Ranger");

        var result = await Sut().AnalyseAsync(varis);

        Assert.Empty(result.Tribes);
    }

    [Fact]
    public async Task A_lord_keeps_the_type_its_text_names()
    {
        // Chief of the Wilds is a Wolf whose text says "another Wolf you control", so both
        // signals agree and nothing is lost by dropping the type-line shortcut.
        var chief = Commander(
            "Chief of the Wilds",
            "Whenever another Wolf you control enters, put two +1/+1 counters on this creature.",
            "Wolf");

        var result = await Sut("Wolf").AnalyseAsync(chief);

        Assert.Equal(["Wolf"], result.Tribes);
    }

    [Fact]
    public async Task A_tribe_the_text_never_mentions_is_dropped()
    {
        // Grounded rather than taken on trust, the same way the build checks the model's
        // card names before adding them (doctrine §9.7).
        var chief = Commander(
            "Chief of the Wilds",
            "Whenever another Wolf you control enters, put two +1/+1 counters on this creature.",
            "Wolf");

        var result = await Sut("Wolf", "Sliver").AnalyseAsync(chief);

        Assert.Equal(["Wolf"], result.Tribes);
    }

    [Fact]
    public async Task The_plural_in_the_text_still_grounds_the_singular_tribe()
    {
        // Rules text says "Wolves" far more often than "Wolf".
        var lord = Commander(
            "Pack Leader",
            "Wolves you control get +1/+1.",
            "Human");

        var result = await Sut("Wolf").AnalyseAsync(lord);

        Assert.Equal(["Wolf"], result.Tribes);
    }

    [Fact]
    public async Task A_type_named_only_inside_a_longer_word_does_not_count()
    {
        // The substring trap that made "Battle" match every card saying "battlefield".
        var c = Commander(
            "Battlefield Watcher",
            "Whenever a creature enters the battlefield under your control, draw a card.",
            "Human");

        var result = await Sut("Battle").AnalyseAsync(c);

        Assert.Empty(result.Tribes);
    }
}
