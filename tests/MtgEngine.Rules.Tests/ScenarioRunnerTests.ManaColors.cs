using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 608.2d: "add one mana of any color" is a choice announced with the activation, and the engine
/// refuses an activation that did not bring the answer with it. A generated scenario cannot answer a
/// question — there is none to answer, the refusal happens while the command is built — so the
/// choice rides on the step (seam-requests/P12.md § 1) and the runner has to hand it to the command.
/// These tests are about that handover: the colour the scenario wrote is the colour the engine was
/// asked for.
/// </summary>
public sealed class ScenarioRunnerTestsManaColors
{
    private const string Prism = "runner-any-colour-source";

    [Fact]
    [RuleRef("608.2d")]
    public void The_colour_on_the_step_is_the_colour_on_the_command()
    {
        var command = Assert.Single(Record(Activation("W")).Activations);

        Assert.Equal("W", command.ManaColors);
    }

    /// <summary>A step that chooses nothing still means what it always meant: no colour on the command.</summary>
    [Fact]
    [RuleRef("608.2d")]
    public void A_step_that_names_no_colour_leaves_the_command_without_one()
    {
        Assert.Null(Assert.Single(Record(Activation(null)).Activations).ManaColors);
    }

    /// <summary>
    /// The other half of CR 608.2d, played in a real game. The casting system reads the colour off
    /// the command in E3h (wave 10); until it does, the activation is refused for a choice the
    /// scenario in fact made, and this test says which of the two worlds it is looking at rather
    /// than freezing today's gap into an assertion that has to be deleted when the gap closes.
    /// </summary>
    [Fact]
    [RuleRef("608.2d")]
    public void The_activation_is_refused_for_the_unread_colour_until_the_casting_system_reads_it()
    {
        var result = Runner(Subsystems.Wave2()).Run(Activation("W"));

        if (result.Error is { Length: > 0 } refusal)
        {
            Assert.Contains("colour of mana", refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("has no command", refusal, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(result.Passed, string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}")));
        }
    }

    /// <summary>Plays the scenario through the wave-2 engine with its own casting system wrapped, and hands back the notebook.</summary>
    private static ScenarioRunnerTestsCastingRecorder Record(Scenario scenario)
    {
        var wave2 = Subsystems.Wave2();
        var recorder = new ScenarioRunnerTestsCastingRecorder(wave2.Casting);
        Runner(wave2 with { Casting = recorder }).Run(scenario);
        return recorder;
    }

    private static ScenarioRunner Runner(Subsystems subsystems) =>
        new(new Game(subsystems, new ScenarioRunnerManaColorsDefinitions(PrismCard())));

    /// <summary>A permanent that taps for one mana of any colour, and a scenario that activates it.</summary>
    private static Scenario Activation(string? colors) => new(
        "runner-any-colour",
        "an activation says which colour of mana it adds",
        new GeneratedSource("test"),
        new ScenarioSetup([new PlayerSetup("A", Battlefield: [new CardRef(Prism)]), new PlayerSetup("B")], "A"),
        [new ActivateStep("A", Prism, 0, ManaColors: colors), new ResolveAllStep()],
        [new ManaPoolIs("A", "W"), new TappedIs(Prism, true)]);

    private static CardDefinition PrismCard() =>
        new(Prism, Prism, Layout.Normal,
            [new Face(Prism, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}",
                Abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(AnyColor: true)))])]);

    private sealed class ScenarioRunnerManaColorsDefinitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }

    /// <summary>
    /// The real casting system with a notebook: every command the game handed it, kept so a test can
    /// read what the runner built rather than inferring it from what the game did with it.
    /// </summary>
    private sealed class ScenarioRunnerTestsCastingRecorder(ICastingSystem inner) : ICastingSystem
    {
        public List<ActivateAbility> Activations { get; } = [];

        public string Name => inner.Name;

        public Outcome Cast(GameState state, CastSpell command) => inner.Cast(state, command);

        public Outcome Activate(GameState state, ActivateAbility command)
        {
            Activations.Add(command);
            return inner.Activate(state, command);
        }

        public Outcome PlayLand(GameState state, PlayLand command) => inner.PlayLand(state, command);


        public Outcome ResolveTop(GameState state) => inner.ResolveTop(state);

        public Outcome FinishResolving(GameState state, ObjectId stackObject, bool resolved = true) => inner.FinishResolving(state, stackObject, resolved);

        public Refusal? Check(GameState state, Command command) => inner.Check(state, command);

        public ImmutableList<CostChoice> CostChoices(GameState state, Command command) => inner.CostChoices(state, command);

        public Outcome CastInstructed(GameState state, CastSpell command) => inner.CastInstructed(state, command);

        // Every remaining member, forwarded because a wrapper that misses one does not fail to
        // compile: it quietly inherits the seam's throwing default. SeamDecoratorTests is what
        // says so; these four were found by it.
        public Refusal? CheckInstructed(GameState state, CastSpell command) => inner.CheckInstructed(state, command);

        public Outcome PayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null) =>
            inner.PayForRule(state, player, cost, source, x);

        public Refusal? CanPayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null) =>
            inner.CanPayForRule(state, player, cost, source, x);
    }
}
