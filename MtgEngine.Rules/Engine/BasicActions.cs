using MtgEngine.Cards;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// Deliberately closed rules subset. Registry and continuations are trusted server data.
/// Authenticate actors externally and serialize operations on the latest continuation.
/// </summary>
public sealed class BasicActions
{
    private readonly ImmutableDictionary<string, BasicCard> _cards;

    public BasicActions(IEnumerable<BasicCard> cards)
    {
        _cards = cards.Select(card => card with
        {
            Definition = card.Definition with
            {
                Faces = card.Definition.Faces.Select(face => face with
                {
                    Types = face.Types with { Subtypes = face.Types.SubtypeList.ToImmutableList() },
                }).ToImmutableList(),
            },
        }).ToImmutableDictionary(card => card.Definition.OracleId, StringComparer.Ordinal);
        foreach (var card in _cards.Values)
            ValidateSupport(card);
    }

    public TurnExecution PlayLand(TurnExecution execution, PlayerId actor, ObjectId id) => execution.Act(actor, state =>
    {
        ValidateWorld(state);
        RequireMain(state, actor);
        var card = HandCard(state, actor, id);
        if (Support(card).Behavior != BasicBehavior.Land || state.Player(actor).LandsPlayedThisTurn != 0)
            throw new InvalidOperationException("A land cannot be played now.");
        state = ZoneTransitions.Move(state, new(id, ZoneKey.Shared(Zone.Battlefield), Controller: actor)).State;
        return SetPlayer(state, state.Player(actor) with { LandsPlayedThisTurn = 1 });
    });

    /// <summary>Immediate tap-for-one mana ability while holding priority; no stack object is created.</summary>
    public TurnExecution ActivateMana(TurnExecution execution, PlayerId actor, ObjectId source) => execution.Act(actor, state =>
    {
        ValidateWorld(state);
        return Produce(state, actor, source);
    });

    public TurnExecution Cast(TurnExecution execution, PlayerId actor, CastChoice choice) => execution.Act(actor, state =>
    {
        ValidateWorld(state);
        var card = HandCard(state, actor, choice.Card);
        var support = Support(card);
        if (support.Behavior == BasicBehavior.Land)
            throw new InvalidOperationException("Lands are played, not cast.");
        if (card.Face.Types.Types != CardType.Instant)
            RequireMain(state, actor);
        ValidateTarget(state, support.Behavior, choice.Target, choice.TargetPlayer, requireLegal: true);
        var sources = choice.ManaSources ?? [];
        if (sources.Distinct().Count() != sources.Count)
            throw new InvalidOperationException("A mana source cannot be chosen twice.");
        foreach (var source in sources)
            state = Produce(state, actor, source);
        var player = state.Player(actor);
        state = SetPlayer(state, player with { Mana = BasicMana.Pay(player.Mana, choice.Payment, card.Face.ManaCost) });
        var move = ZoneTransitions.Move(state, new(card.Id, ZoneKey.Shared(Zone.Stack), Controller: actor));
        var spell = move.State.Objects[move.Current];
        return move.State with
        {
            Objects = move.State.Objects.SetItem(spell.Id, spell with
            {
                Stack = new(null, null, choice.Target is { } target ? [target] : [],
                    choice.TargetPlayer is { } targetPlayer ? [targetPlayer] : [], [], null, false),
            }),
        };
    });

    public TurnExecution Resolve(TurnExecution execution) => execution.Resolve((state, id) =>
    {
        ValidateWorld(state);
        var spell = state.Objects[id];
        var behavior = Support(spell);
        var choices = spell.Stack ?? throw new NotSupportedException("A spell requires casting metadata.");
        if (choices.Source is not null || choices.AbilityIndex is not null || choices.IsCopy || choices.X is not null
            || !choices.Modes.IsEmpty || choices.Targets.Count > 1 || choices.TargetPlayers.Count > 1)
            throw new NotSupportedException("Unsupported stack choices.");
        ObjectId? target = choices.Targets.Count == 1 ? choices.Targets[0] : null;
        PlayerId? player = choices.TargetPlayers.Count == 1 ? choices.TargetPlayers[0] : null;
        var legal = ValidateTarget(state, behavior.Behavior, target, player, requireLegal: false);
        if (legal)
        {
            if (behavior.Behavior == BasicBehavior.GainLife)
            {
                var recipient = state.Player(player!.Value);
                state = SetPlayer(state, recipient with { Life = checked(recipient.Life + behavior.Amount) });
            }
            else if (behavior.Behavior == BasicBehavior.ReturnCreature)
                state = ZoneTransitions.Move(state, new(target!.Value, ZoneKey.Of(Zone.Hand, state.Objects[target.Value].Owner))).State;
        }
        var destination = behavior.Behavior == BasicBehavior.Creature ? ZoneKey.Shared(Zone.Battlefield)
            : ZoneKey.Of(Zone.Graveyard, spell.Owner);
        return ZoneTransitions.Move(state, new(id, destination)).State;
    });

    /// <summary>Certifies a quiet checkpoint only in this closed subset; rejects states needing unsupported SBAs.</summary>
    public TurnExecution CompleteCheckpoint(TurnExecution execution) => execution.CompleteCheckpoint(state =>
    {
        ValidateWorld(state);
        if (state.Objects.Values.Any(o => !Support(o).Keywords.IsEmpty))
            throw new NotSupportedException("Keyword games require BasicCombat.CompleteCheckpoint throughout play.");
        if (state.Players.Any(p => p.Life <= 0 || p.DrewFromEmptyLibrary)
            || state.Objects.Values.Any(o => o.IsPermanent && o.Face.Types.Types.HasFlag(CardType.Creature)
                && (o.Face.Toughness!.Number <= 0 || o.Damage >= o.Face.Toughness.Number || o.DealtDeathtouchDamage)))
            throw new NotSupportedException("This state requires a state-based-action provider.");
        return state;
    });

    private GameState Produce(GameState state, PlayerId actor, ObjectId id)
    {
        if (!state.Objects.TryGetValue(id, out var card) || card.Zone.Zone != Zone.Battlefield
            || card.Controller != actor || card.Tapped || card.PhasedOut || Support(card).Behavior != BasicBehavior.Land)
            throw new InvalidOperationException("The chosen mana source is unavailable.");
        var color = LandColor(card.Face);
        state = state with { Objects = state.Objects.SetItem(id, card with { Tapped = true }) };
        return SetPlayer(state, state.Player(actor) with { Mana = BasicMana.Add(state.Player(actor).Mana, color) });
    }

    private static GameObject HandCard(GameState state, PlayerId actor, ObjectId id)
    {
        if (!state.Objects.TryGetValue(id, out var card) || card.Zone != ZoneKey.Of(Zone.Hand, actor))
            throw new InvalidOperationException("The chosen card is unavailable.");
        return card;
    }

    private static void RequireMain(GameState state, PlayerId actor)
    {
        if (state.Turn.ActivePlayer != actor || state.Turn.Step is not (Step.PrecombatMain or Step.PostcombatMain)
            || !state.In(ZoneKey.Shared(Zone.Stack)).IsEmpty)
            throw new InvalidOperationException("This action requires your main phase and an empty stack.");
    }

    private static bool ValidateTarget(GameState state, BasicBehavior behavior, ObjectId? target, PlayerId? player, bool requireLegal)
    {
        var shape = behavior switch
        {
            BasicBehavior.Creature => target is null && player is null,
            BasicBehavior.GainLife => target is null && player is not null,
            BasicBehavior.ReturnCreature => target is not null && player is null,
            _ => false,
        };
        if (!shape)
            throw new InvalidOperationException("Supply exactly the targets required by the spell.");
        var legal = behavior switch
        {
            BasicBehavior.GainLife => state.Players.Any(p => p.Id == player && !p.HasLost),
            BasicBehavior.ReturnCreature => state.Objects.TryGetValue(target!.Value, out var obj)
                && obj.IsPermanent && !obj.PhasedOut && obj.Face.Types.Types.HasFlag(CardType.Creature),
            _ => true,
        };
        if (requireLegal && !legal)
            throw new InvalidOperationException("The chosen target is illegal.");
        return legal;
    }

    private BasicCard Support(GameObject obj)
    {
        if (!_cards.TryGetValue(obj.Definition.OracleId, out var support) || !Matches(support.Definition, obj.Definition))
            throw new NotSupportedException("The game contains an unsupported card definition.");
        return support;
    }

    internal bool Has(GameObject obj, BasicKeyword keyword) => Support(obj).Keywords.Contains(keyword);

    internal void ValidateWorld(GameState state)
    {
        // Unknown abilities can function in hidden zones too. Admit only a fully registered deck subset.
        foreach (var obj in state.Objects.Values)
        {
            _ = Support(obj);
            if (obj.FaceIndex != 0 || obj.FaceDown || obj.PhasedOut || obj.IsToken || obj.IsCopy || obj.CardInstanceId is null
                || !obj.Counters.IsEmpty || !obj.Designations.IsEmpty || !obj.Chosen.IsEmpty
                || obj.AttachedTo is not null || obj.AttachedToPlayer is not null)
                throw new NotSupportedException("Unsupported object status.");
        }
        if (state.Players.Any(p => !p.Counters.IsEmpty || !p.Designations.IsEmpty || !p.CommanderDamage.IsEmpty
            || BasicMana.Values(p.Mana).Any(v => v < 0)))
            throw new NotSupportedException("Unsupported player status.");
    }

    private static bool Matches(CardDefinition expected, CardDefinition actual)
    {
        if (actual.Faces.Count != 1 || expected.Name != actual.Name || expected.Layout != actual.Layout)
            return false;
        var a = expected.Faces[0];
        var b = actual.Faces[0];
        return a.Types.Types == b.Types.Types && a.Types.Supertypes == b.Types.Supertypes
            && a.Types.SubtypeList.SequenceEqual(b.Types.SubtypeList) && a with { Types = b.Types } == b;
    }

    private static void ValidateSupport(BasicCard card)
    {
        card.Definition.Validate();
        if (card.Definition.Layout != Layout.Normal || card.Definition.Faces.Count != 1)
            throw new NotSupportedException("Only normal single-face cards are supported.");
        var face = card.Definition.Faces[0];
        var text = face.OracleText ?? "";
        if (card.Keywords.Any(k => !Enum.IsDefined(k)) || (card.Behavior != BasicBehavior.Creature && !card.Keywords.IsEmpty))
            throw new NotSupportedException("Only supported creature keyword declarations are allowed.");
        var valid = card.Behavior switch
        {
            BasicBehavior.Land => face.Types.Types == CardType.Land && face.Types.Supertypes == Supertype.Basic
                && face.ManaCost is null && card.Amount == 0 && LandColor(face) >= 0
                && (text.Length == 0 || text == $"({{T}}: Add {{{"WUBRGC"[LandColor(face)]}}}.)"),
            BasicBehavior.Creature => face.Types.Types is CardType.Creature or (CardType.Artifact | CardType.Creature)
                && face.Types.Supertypes == Supertype.None && KeywordTextMatches(text, card.Keywords) && card.Amount == 0
                && face.Power?.Number is not null && face.Toughness?.Number is not null,
            BasicBehavior.GainLife => face.Types.Types is CardType.Instant or CardType.Sorcery
                && face.Types.Supertypes == Supertype.None && card.Amount > 0 && text == $"Target player gains {card.Amount} life.",
            BasicBehavior.ReturnCreature => face.Types.Types is CardType.Instant or CardType.Sorcery
                && face.Types.Supertypes == Supertype.None && card.Amount == 0 && text == "Return target creature to its owner's hand.",
            _ => false,
        };
        if (!valid || face.Role != FaceRole.Main)
            throw new NotSupportedException("The declaration contains unsupported costs, text or characteristics.");
        if (card.Behavior != BasicBehavior.Land)
            _ = BasicMana.Cost(face.ManaCost);
    }

    // Exact canonical standalone keyword lines only. This checks an authored declaration;
    // it never derives executable abilities from arbitrary Oracle text or reminder text.
    private static bool KeywordTextMatches(string text, ImmutableHashSet<BasicKeyword> keywords)
    {
        if (keywords.IsEmpty)
            return string.IsNullOrWhiteSpace(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var expected = keywords.Select(k => k switch
        {
            BasicKeyword.FirstStrike => "First strike",
            BasicKeyword.DoubleStrike => "Double strike",
            _ => k.ToString(),
        }).ToImmutableHashSet(StringComparer.Ordinal);
        return lines.Length == expected.Count && expected.SetEquals(lines);
    }

    private static int LandColor(Face face)
    {
        if (face.Types.SubtypeList.Count != 1)
            throw new NotSupportedException("Only single-type basic lands are supported.");
        return face.Types.SubtypeList[0] switch
        {
            "Plains" => 0,
            "Island" => 1,
            "Swamp" => 2,
            "Mountain" => 3,
            "Forest" => 4,
            _ => throw new NotSupportedException("Unsupported basic land type."),
        };
    }

    private static GameState SetPlayer(GameState state, PlayerState player) =>
        state with { Players = state.Players.SetItem(player.Id.Value, player) };
}
