using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using AbilityActivated = MtgEngine.Rules.Events.AbilityActivated;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// The mana-ability half of activation, CR 605: which activated abilities are mana abilities
/// (605.1a) and how one resolves — at once, without the stack, and without anyone being able to
/// respond (605.3a). An ability that only adds mana is added by the casting system itself; one
/// that adds mana and does more is walked by the interpreter on the spot, with the colours its
/// activation announced in its context, the way a stack entry would have carried them.
/// </summary>
public sealed partial class CastingSystem
{
    /// <summary>The key the interpreter reads chosen modes under; kept in step with its stack-entry context.</summary>
    private const string ModesKey = "modes";

    /// <summary>The key the interpreter remembers an ability's triggering objects under, CR 603.3d.</summary>
    private const string TriggeringKey = "triggering";

    /// <summary>
    /// CR 605.1a, read in one place: <see cref="MtgEngine.Rules.Query.ManaAbilities.Is"/> is the
    /// engine's single reading of the rule's four criteria — no target (CR 115.6), could add mana
    /// as it resolves, not a loyalty ability (CR 606.1), and neither cost nor effect moves a card
    /// to or from a library. This system's own copy checked three of them and is deleted (E5k § 4).
    /// <para>
    /// The announcement is still consulted beside it: an activation that names targets is not this
    /// ability resolving without the stack whatever its text says, so a command carrying any is
    /// held to CR 605.1a's target criterion the same way.
    /// </para>
    /// </summary>
    [RuleRef("605.1a")]
    private static bool IsManaAbility(ActivatedAbility ability, ActivateAbility command) =>
        Query.ManaAbilities.Is(ability)
        && command.Targets is null or { Count: 0 }
        && command.TargetPlayers is null or { Count: 0 };

    /// <summary>
    /// CR 605.3a: a mana ability resolves as soon as it is activated, never using the stack, so
    /// the activation event carries no stack object and the effect's own events follow it at
    /// once. A bare <see cref="AddMana"/> is added here; anything more is walked by the
    /// interpreter with <see cref="ManaAbilityContext"/>. An ability that stops to ask cannot be
    /// a mana ability resolving on the spot, so a halted walk is refused rather than left pending.
    /// CR 605.3b lets this happen while a cost is being paid, which is why nothing here reads
    /// priority beyond what <see cref="Activate"/> already did.
    /// </summary>
    [RuleRef("605.3a")]
    private Outcome ResolveManaAbility(GameState state, ActivatedAbility ability, ActivateAbility command, PlayerId controller, ImmutableList<GameEvent> spent, AbilityActivated activated)
    {
        if (ability.Do is AddMana add)
        {
            var mana = ManaFrom(add, state, command, controller);
            ImmutableList<GameEvent> immediate = [.. spent, activated, mana];
            return new Outcome(GameReducer.Apply(state, immediate), immediate);
        }

        ImmutableList<GameEvent> announced = [.. spent, activated];
        var paid = GameReducer.Apply(state, announced);
        var ran = interpreter.Run(paid, ability.Do, ManaAbilityContext(paid, command, controller), command.AbilityIndex);
        if (ran.State.Pending is not null)
            throw new RefusedException("A mana ability resolves as it is activated and cannot stop to ask.", "605.3a");
        return new Outcome(ran.State, announced.AddRange(ran.Events));
    }

    /// <summary>
    /// What a mana ability's effect runs inside when it never reaches the stack: the same context
    /// the interpreter builds for an activated ability's stack entry — the source's own choices,
    /// the colours the activation announced under <see cref="ManaSymbols.AnnouncedColorsKey"/>
    /// (CR 608.2d), any modes, X, and the source as what the ability is about — so an effect reads
    /// the same answers on either road.
    /// </summary>
    [RuleRef("605.3a")]
    private static EffectContext ManaAbilityContext(GameState state, ActivateAbility command, PlayerId controller)
    {
        var chosen = state.Objects.TryGetValue(command.Source, out var source)
            ? ImmutableDictionary<string, string>.Empty.SetItems(source.Chosen)
            : ImmutableDictionary<string, string>.Empty;
        if (!string.IsNullOrEmpty(command.ManaColors))
            chosen = chosen.SetItem(ManaSymbols.AnnouncedColorsKey, command.ManaColors);
        if (command.Modes is { Count: > 0 } modes)
            chosen = chosen.SetItem(ModesKey, string.Join(",", modes.Select(m => m.ToString(CultureInfo.InvariantCulture))));

        ImmutableList<ObjectId> triggering = [command.Source];
        return CostPayment.Context(command.Source, controller, command.X) with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem(TriggeringKey, triggering),
            Chosen = chosen,
            Triggering = triggering,
        };
    }

    /// <summary>What an "add mana" instruction adds, CR 106.1: printed mana, or a colour chosen with the activation.</summary>
    [RuleRef("106.1")]
    private ManaChanged ManaFrom(AddMana add, GameState state, ActivateAbility command, PlayerId controller)
    {
        var who = add.Who is null
            ? controller
            : query.Players(state, add.Who, CostPayment.Context(command.Source, controller, command.X)).FirstOrDefault(controller);
        var times = add.N is null ? 1 : query.Evaluate(state, add.N, CostPayment.Context(command.Source, controller, command.X));
        // CR 608.2d: each mana added is its own choice, so the nth one reads the nth letter of
        // the activation's colours rather than repeating the first.
        var total = ManaPool.Empty;
        for (var i = 0; i < Math.Max(0, times); i++)
            total = ManaSymbols.Add(total, OneProduction(add.Mana, command, i));

        // CR 106.6a: "spend this mana only on ..." adds a restricted block rather than ordinary
        // mana, so only a cost the filter matches can be paid with it; CR 106.6: either way the
        // mana remembers the permanent that produced it and the key the card gave it.
        return new ManaChanged(who, ManaSources.Added(total, add.SpendOnlyOn, command.Source, add.Key), "mana ability");
    }

    private static ManaPool OneProduction(ManaProduction production, ActivateAbility command, int index)
    {
        if (production.Fixed is { Length: > 0 })
            return ManaSymbols.Produced(production.Fixed);

        // CR 608.2d: a choice the effect offers is announced as it is applied — here, with the
        // activation, so no payment is ever suspended (CR 601.2h). ManaColors is the channel the
        // command has for it, one letter per mana; CostChoices stays readable behind it for an
        // activation that names its colour there.
        if (production.AnyColor || production.ChosenColor || production.OneOf is not null)
        {
            var chosen = ManaSymbols.ColorAt(command.ManaColors, index)
                ?? command.CostChoices?.Select(ManaSymbols.ColorFromChoice).FirstOrDefault(c => c is not null)
                ?? (production.OneOf is { } only && ManaSymbols.IsSingleColor(only) ? (Color?)only : null);
            return chosen is null
                ? throw new RefusedException("The colour of mana to add was not chosen.", "608.2d")
                : ManaSymbols.OfColor(chosen.Value);
        }

        throw new RefusedException("That kind of mana production is not supported yet.", "106.1");
    }

    /// <summary>
    /// CR 608.2d: an ability that adds mana of any colour and is not a mana ability — it targets,
    /// or is a loyalty ability (CR 605.1a) — uses the stack, and the colours its activation
    /// announced travel on the entry under the mana key, one letter per mana, so the resolution
    /// adds what was chosen. Nothing announced, nothing carried.
    /// </summary>
    [RuleRef("608.2d")]
    private static ImmutableDictionary<string, string>? AnnouncedColors(string? colors) =>
        string.IsNullOrEmpty(colors) ? null : ImmutableDictionary<string, string>.Empty.Add(ManaSymbols.AnnouncedColorsKey, colors);
}
