using System.Reflection;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Every AI pass that exercises deckbuilding judgement must be given the doctrine.
/// </summary>
/// <remarks>
/// This exists because the claim was true in the documentation and false in the code for
/// months. CLAUDE.md states the doctrine is "the deck-building standard every AI pass
/// reasons from (suggestions, synergy scoring, reason writing, deck building)" — and the
/// deck builder, the one pass that assembles an entire 99, never received it. Nor did the
/// service that tunes mana bases, against a doctrine whose §3 is entirely about mana bases.
/// <para>
/// The cause was ordinary: <c>AiBuildService</c> predates the doctrine, which arrived with
/// the card-suggestions work and was wired into the two services that commit happened to
/// touch. Nothing was wrong with anyone's intent — there was simply no gate, so a service
/// could call the model without the standard and no build, test or hook would notice.
/// </para>
/// <para>
/// The repo's own rule is that an instruction nobody enforces is not a control. The
/// <c>require-docs</c> hook makes a person read the doctrine before editing; only this makes
/// the code send it.
/// </para>
/// </remarks>
public class DoctrineReachTests
{
    private static readonly Assembly Api = typeof(AiBuildService).Assembly;

    /// <summary>
    /// Services that call the model but genuinely have no deckbuilding judgement to make.
    /// </summary>
    /// <remarks>
    /// An escape hatch that has to be written down, in the same spirit as the client's
    /// <c>ui-coverage-allow.json</c>. Adding a name here is a claim that the service decides
    /// nothing the doctrine governs — be able to defend it.
    /// </remarks>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["CardVisionService"] =
            "Reads card names off a photograph. It identifies cards; it never judges whether " +
            "one belongs in a deck.",

        ["CommanderAnalysis"] =
            "Extracts structured facts from the commander's text — thresholds, tribes, " +
            "keywords. Doctrine §0.1 draws exactly this line: the facts block is what code " +
            "checks and states, and judgement is applied against it elsewhere. This service " +
            "produces the facts, so it applies no standard of its own.",
    };

    /// <summary>Concrete services whose constructor takes the Anthropic client.</summary>
    private static IEnumerable<Type> ModelCallingServices() =>
        Api.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Namespace == "MtgEngine.Api.Services"
                        && t.GetConstructors().Any(c =>
                            c.GetParameters().Any(p => p.ParameterType == typeof(IAnthropicClient))));

    [Fact]
    public void Every_model_calling_service_is_given_the_doctrine_or_is_a_written_down_exception()
    {
        var offenders = new List<string>();

        foreach (var type in ModelCallingServices())
        {
            if (Exempt.ContainsKey(type.Name))
                continue;

            bool takesDoctrine = type.GetConstructors().Any(c =>
                c.GetParameters().Any(p => p.ParameterType == typeof(ICommanderDoctrine)));

            if (!takesDoctrine)
                offenders.Add(type.Name);
        }

        Assert.True(
            offenders.Count == 0,
            "These services call the model without the deckbuilding doctrine:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nEither inject ICommanderDoctrine and send it as a cached system prefix, or "
            + "add the service to Exempt with a reason it makes no deckbuilding judgement.");
    }

    [Fact]
    public void The_services_that_build_and_tune_decks_are_not_exempt()
    {
        // Naming them explicitly, because the failure this guards against was not a missing
        // check — it was these two being quietly outside the set nobody had written down.
        Assert.DoesNotContain(nameof(AiBuildService), Exempt.Keys);
        Assert.DoesNotContain(nameof(ManaFineTuneService), Exempt.Keys);
        Assert.DoesNotContain(nameof(SynergyService), Exempt.Keys);
        Assert.DoesNotContain(nameof(DeckSuggestionsService), Exempt.Keys);
        Assert.DoesNotContain(nameof(CommanderSuggestionService), Exempt.Keys);
    }

    [Fact]
    public void Every_exemption_carries_a_reason()
    {
        Assert.All(Exempt, entry =>
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Value),
                $"{entry.Key} is exempt with no reason written down."));
    }
}
